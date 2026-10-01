// Port of KiwiSDR Selcall.js — Copyright (c) 2022 John Seamons, ZL4VO/KF6VO
// Map/NAVTEX dump helpers skipped; decoded text lines still emitted.
// ext_get_freq / ext_get_name replaced with injectable callbacks.

using System.Globalization;
using System.Text;

namespace NeuroSDR.Plugins.Kiwi.Jnx;

internal sealed class SelcallEncoding : IJnxEncoding
{
    const int Dx = 125, Rx5 = 109;
    const int FmtIndivCall = 120, FmtIndivSemiAuto = 123;
    const int Cmd1Position = 121, Cmd2_110 = 110;
    const int Arq = 117, Abq = 122, Eos = 127;
    const int MsgLenMin = 30, MsgLenMax = 80;

    readonly Func<double> _getFreqHz;
    readonly string[] _posS;
    readonly Dictionary<string, int> _pos = new();
    readonly int[] _svec = new int[12];

    int _syncA, _syncB, _syncC, _syncD, _syncE;
    int _seq, _pkt;
    bool _synced;
    int[]? _fullSyms, _syms;
    bool[]? _bcErr;
    int _parityErrors;
    bool _dumpAlways = true, _dumpNonStdLen = true;

    public SelcallEncoding(Func<double>? getFreqHz)
    {
        _getFreqHz = getFreqHz ?? (() => 0);
        _posS =
        [
            "dx", "rx5", "dx", "rx4", "dx", "rx3", "dx", "rx2", "dx", "rx1", "dx", "rx0",
            "fmt", "fmt", "B1", "fmt", "B2", "fmt", "cat", "B1",
            "A1", "B2", "A2", "cat",
            "eos", "A1", "eos", "A2", "eos", "eos"
        ];
        var posN = new Dictionary<string, int>();
        for (int i = 0; i < _posS.Length; i++)
        {
            string ps = _posS[i].Trim();
            if (!posN.ContainsKey(ps)) posN[ps] = 0;
            _pos[ps + "-" + posN[ps]] = i;
            posN[ps]++;
        }

        static int Edc(int v) => BitUtil.BitReverse(BitUtil.BitCount(v ^ 0x7f), 3);
        int dxW = (Edc(Dx) << 7) | Dx;
        int rx = Rx5;
        for (int i = 0; i < 12; i++)
        {
            _svec[i] = (i & 1) != 0 ? ((Edc(rx) << 7) | rx) : dxW;
            if ((i & 1) != 0) rx--;
        }
    }

    public int GetNbits() => 10;
    public int GetMsb() => 0x200;
    public bool CheckBits(int v) => false;

    public void Reset() => _syncA = _syncB = _syncC = _syncD = _syncE = 0;

    public bool SearchSync(int bit)
    {
        int cin = bit;
        int cout = _syncA & 1;
        _syncA = (_syncA >> 1) & 0x3ff;
        _syncA |= cin != 0 ? 0x200 : 0;

        cin = cout; cout = _syncB & 1;
        _syncB = (_syncB >> 1) & 0x3ff;
        _syncB |= cin != 0 ? 0x200 : 0;

        cin = cout; cout = _syncC & 1;
        _syncC = (_syncC >> 1) & 0x3ff;
        _syncC |= cin != 0 ? 0x200 : 0;

        cin = cout; cout = _syncD & 1;
        _syncD = (_syncD >> 1) & 0x3ff;
        _syncD |= cin != 0 ? 0x200 : 0;

        cin = cout; cout = _syncE & 1;
        _syncE = (_syncE >> 1) & 0x3ff;
        _syncE |= cin != 0 ? 0x200 : 0;

        for (int i = 0; i <= 9; i++)
        {
            if (_syncC == _svec[i] && _syncB == _svec[i + 1] && _syncA == _svec[i + 2])
            {
                BeginSync(i + 3);
                return true;
            }
        }
        for (int i = 1; i <= 7; i += 2)
        {
            if (_syncE == _svec[i] && _syncC == _svec[i + 2] && _syncA == _svec[i + 4])
            {
                BeginSync(i + 5);
                return true;
            }
        }
        _synced = false;
        return false;
    }

    void BeginSync(int seq)
    {
        _seq = seq;
        _fullSyms = new int[MsgLenMax + 8];
        _syms = new int[MsgLenMax + 8];
        _bcErr = new bool[MsgLenMax + 8];
        _parityErrors = 0;
        _synced = true;
    }

    (int Code, bool FecErr) Fec(int i)
    {
        if (!_bcErr![i])
            return (_fullSyms![i] & 0x7f, false);
        if (i + 5 < _seq && !_bcErr[i + 5])
            return (_fullSyms![i + 5] & 0x7f, false);
        return (-1, true);
    }

    static string CallDecode(string call)
    {
        return call switch
        {
            "2199" => call + " (Austravel Casino NSW)",
            "3199" => call + " (Austravel Shepparton VIC)",
            "4199" => call + " (Austravel Mareeba North QLD)",
            "5199" => call + " (Austravel Base SA)",
            "6199" => call + " (Austravel Perth WA)",
            "6299" => call + " (Austravel Kununnura WA)",
            "8199" => call + " (Austravel Alice Springs NT)",
            _ => call
        };
    }

    public ProcessCharResult ProcessChar(int codeIn, int fixedStart, Action<string> output, bool showRaw, bool showErrs)
    {
        if (!_synced) return new ProcessCharResult { Resync = false };
        if (_seq == 12) _pkt++;

        _fullSyms![_seq] = codeIn;
        int bcRev = (codeIn >> 7) & 7;
        int code = codeIn & 0x7f;
        _syms![_seq] = code;
        int bcCk = BitUtil.BitReverse(bcRev, 3);
        int bcData = BitUtil.BitCount(code ^ 0x7f);
        bool pe = bcCk != bcData;
        _bcErr![_seq] = pe;
        if (pe) _parityErrors++;
        _seq++;

        if (_seq >= MsgLenMin)
        {
            bool EosCk(int eosSym)
            {
                int n = 0;
                if (!_bcErr[_seq - 1] && _syms[_seq - 1] == eosSym) n++;
                if (!_bcErr[_seq - 2] && _syms[_seq - 2] == eosSym) n++;
                if (!_bcErr[_seq - 4] && _syms[_seq - 4] == eosSym) n++;
                if (!_bcErr[_seq - 6] && _syms[_seq - 6] == eosSym) n++;
                return n >= 3;
            }

            bool eos = EosCk(Eos) || EosCk(Arq) || EosCk(Abq);
            if (eos || _seq > MsgLenMax)
            {
                bool dump = _dumpAlways;
                if (eos)
                {
                    if (_seq != MsgLenMin) dump |= _dumpNonStdLen;
                }
                if (dump)
                    DumpLine(output, showRaw, showErrs);

                _synced = false;
                return new ProcessCharResult { Resync = true };
            }
        }
        return new ProcessCharResult { Resync = false };
    }

    void DumpLine(Action<string> output, bool showRaw, bool showErrs)
    {
        bool fecErrors = false;
        var sym = new List<string>();
        double freq = _getFreqHz() / 1e3;
        var raw = new StringBuilder();
        raw.Append(DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        raw.Append(' ');
        raw.Append(freq.ToString("F2", CultureInfo.InvariantCulture));
        raw.Append(' ');

        int fmt = Fec(12).Code;
        int cat = 0, cmd1 = 0, cmd2 = 0;

        for (int i = 0; i < _seq; i++)
        {
            if ((i & 1) != 0 || i < 12) continue;

            var rv = Fec(i);
            int code = rv.Code;
            if (rv.FecErr) fecErrors = true;

            if (i == 14)
            {
                cat = Fec(18).Code;
                if (cat > 99)
                {
                    sym.Add("  ");
                    raw.Append("__ ");
                }
                else
                    cat = Fec(20).Code;
            }
            if (i == 20)
            {
                cmd1 = Fec(24).Code;
                cmd2 = Fec(26).Code;
                if (cmd1 > 99)
                {
                    sym.Add("  ");
                    raw.Append("__ ");
                }
                else
                {
                    cmd1 = Fec(28).Code;
                    cmd2 = Fec(30).Code;
                }
            }

            string codeS;
            if (rv.FecErr)
            {
                raw.Append("X ");
                codeS = "0";
            }
            else
            {
                codeS = code <= 99 ? BitUtil.LeadingZeros(code, 2) : code.ToString();
                raw.Append(codeS).Append(' ');
            }
            sym.Add(codeS);
        }

        string? deco = null;
        if (!fecErrors)
        {
            bool isCall = fmt == FmtIndivCall || fmt == FmtIndivSemiAuto;
            if (isCall && sym.Count >= 8)
            {
                string callTo, callToS, callFrom, callFromS;
                if (int.TryParse(sym[1], out int s1) && s1 != 0)
                {
                    callTo = sym[1] + sym[2] + sym[3];
                    callToS = callTo;
                }
                else
                {
                    callTo = sym[2] + sym[3];
                    callToS = "__" + callTo;
                }
                if (int.TryParse(sym[5], out int s5) && s5 != 0)
                {
                    callFrom = sym[5] + sym[6] + sym[7];
                    callFromS = callFrom;
                }
                else
                {
                    callFrom = sym[6] + sym[7];
                    callFromS = "__" + callFrom;
                }
                deco = CallDecode(callFrom) + " calling " + CallDecode(callTo);

                if (cmd1 == Cmd1Position && cmd2 == Cmd2_110 && _seq >= 48 && _seq <= 56 && sym.Count >= 17)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i <= 6; i++) sb.Append(sym[i + 10]);
                    string p = sb.ToString();
                    if (p.Length >= 14)
                    {
                        string latD = p.Substring(1, 2);
                        string lonD = p.Substring(5, 3);
                        string latM = p.Substring(3, 2);
                        string lonM = p.Substring(8, 2);
                        string latDd = latD + "." + latM;
                        string lonDd = lonD + "." + lonM;
                        deco += $", position [{latDd},{lonDd}]";
                        deco += $" {latD}°{latM}'/{lonD}°{lonM}'";
                        deco += $" {p.Substring(10, 2)}:{p.Substring(12, 2)}";
                    }
                }
            }
            if (deco != null)
            {
                deco = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " +
                       freq.ToString("F2", CultureInfo.InvariantCulture) + " " + deco;
            }
        }

        if (!fecErrors && deco != null)
            output(deco + "\n");
        if ((!fecErrors && (deco == null || showRaw)) || (fecErrors && showErrs))
            output(raw.ToString().TrimEnd() + "\n");
    }
}
