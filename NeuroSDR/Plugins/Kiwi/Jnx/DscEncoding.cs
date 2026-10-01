// Port of KiwiSDR DSC.js — Copyright (c) 2022 John Seamons, ZL4VO/KF6VO
// Browser helpers (ext_get_freq, map links, ANSI) replaced with injectable callbacks / plain text.

using System.Globalization;
using System.Text;

namespace NeuroSDR.Plugins.Kiwi.Jnx;

internal sealed class DscEncoding : IJnxEncoding
{
    const int Dx = 125, Rx7 = 111;
    const int FmtGeoArea = 102, FmtDistress = 112, FmtCommonInterest = 114, FmtAllShips = 116;
    const int FmtIndivSta = 120, FmtResv = 121, FmtIsSemiAuto = 123;
    const int CatRoutine = 100, CatSafety = 108, CatUrgency = 110, CatDistress = 112;
    const int Cmd1FmCall = 100, Cmd1FmDuplexCall = 101, Cmd1Polling = 103, Cmd1UnableComply = 104;
    const int Cmd1EndOfCall = 105, Cmd1F1bData = 106, Cmd1J3eRt = 109, Cmd1DistressAck = 110;
    const int Cmd1DistressAlertRelay = 112, Cmd1F1bFec = 113, Cmd1F1bArq = 115, Cmd1Test = 118;
    const int Cmd1Position = 121, Cmd1Nop = 126;
    const int Cmd2UnableFirst = 100, Cmd2UnableLast = 109, Cmd2NonConflict = 110, Cmd2MedTransports = 111, Cmd2Nop = 126;
    const int Arq = 117, Abq = 122, Eos = 127, Nop = 126;
    const int MsgLenMin = 62, MsgLenMax = 80;

    readonly Func<double> _getFreqHz;
    readonly string[] _posS;
    readonly Dictionary<string, int> _pos = new();
    readonly int[] _svec = new int[12];
    readonly string[] _symS = new string[128];
    readonly Dictionary<string, string> _formatS = new()
    {
        ["102"] = "geo-area", ["112"] = "*distress*", ["114"] = "com-int", ["116"] = "all-ships",
        ["120"] = "indiv-sta", ["121"] = "resv", ["123"] = "semi-auto"
    };
    readonly Dictionary<string, string> _categoryS = new()
    {
        ["100"] = "routine", ["108"] = "safety", ["110"] = "urgency", ["112"] = "*distress*"
    };
    readonly string[] _eccSym =
    [
        "fmt", "to1", "to2", "to3", "to4", "to5", "cat", "from1", "from2", "from3", "from4", "from5",
        "cmd1", "cmd2", "freq1.1", "freq1.2", "freq1.3", "freq2.1", "freq2.2", "freq2.3", "eos"
    ];

    int _syncA, _syncB, _syncC, _syncD, _syncE;
    int _seq;
    bool _synced;
    int[]? _fullSyms, _syms;
    bool[]? _bcErr;
    int _parityErrors, _fecErrors;
    bool _warned;

    public DscEncoding(bool init, Action<string>? outputCb, Func<double>? getFreqHz)
    {
        _getFreqHz = getFreqHz ?? (() => 0);
        for (int i = 0; i < 128; i++) _symS[i] = "   ";
        _symS[102] = "GEO"; _symS[104] = "Pr0"; _symS[105] = "Pr1"; _symS[106] = "Pr2";
        _symS[107] = "Pr3"; _symS[108] = "Pr4"; _symS[109] = "Pr5"; _symS[110] = "Pr6";
        _symS[111] = "Pr7"; _symS[112] = "*D*"; _symS[114] = "INT"; _symS[116] = "ALL";
        _symS[117] = "ARQ"; _symS[120] = "STA"; _symS[121] = "RSV"; _symS[122] = "ABQ";
        _symS[123] = "ATO"; _symS[125] = "Pdx"; _symS[126] = "  *"; _symS[127] = "EOS";

        _posS =
        [
            " dx", "rx7", " dx", "rx6", " dx", "rx5", " dx", "rx4", " dx", "rx3", " dx", "rx2",
            "Afmt", "rx1", "Afmt", "rx0",
            "Bto1", "Afmt", "Bto2", "Afmt", "Bto3", "Bto1", "Bto4", "Bto2", "Bto5", "Bto3",
            "Cat", "Bto4", "Dfr1", "Bto5", "Dfr2", "Cat",
            "Dfr3", "Dfr1", "Dfr4", "Dfr2", "Dfr5", "Dfr3",
            "E1cmd", "Dfr4", "E2cmd", "Dfr5",
            "Ffreq1", "E1cmd", "Ffreq2", "E2cmd", "Ffreq3", "Ffreq1",
            "Gfreq1", "Ffreq2", "Gfreq2", "Ffreq3", "Gfreq3", "Gfreq1",
            "eos", "Gfreq2", "ecc", "Gfreq3", "eos", "eos", "eos", "ecc"
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
        int rx = Rx7;
        for (int i = 0; i < 12; i++)
        {
            _svec[i] = (i & 1) != 0 ? ((Edc(rx) << 7) | rx) : dxW;
            if ((i & 1) != 0) rx--;
        }

        if (init && outputCb != null && !_warned)
        {
            _warned = true;
            outputCb("WARNING: Do not use the DSC decoder in any life-safety application. Hobby radio monitoring only.\n" +
                     "No distress messages are currently decoded. See the KiwiSDR forum for details.\n\n");
        }
    }

    public int GetNbits() => 10;
    public int GetMsb() => 0x200;
    public bool CheckBits(int v) => false;

    public void Reset()
    {
        _syncA = _syncB = _syncC = _syncD = _syncE = 0;
    }

    string OutputMsg(string s)
    {
        double khz = _getFreqHz() / 1e3;
        return DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " +
               khz.ToString("F2", CultureInfo.InvariantCulture) + " " + s + "\n";
    }

    (int Pos, int Sym) SymByName(string symName)
    {
        int pos = _pos[symName];
        return (pos, _syms![pos]);
    }

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
        _fecErrors = 0;
        _synced = true;
    }

    public ProcessCharResult ProcessChar(int codeIn, int fixedStart, Action<string> output, bool showRaw, bool showErrs)
    {
        if (!_synced) return new ProcessCharResult { Resync = false };
        _fullSyms![_seq] = codeIn;
        int bcRev = (codeIn >> 7) & 7;
        int code = codeIn & 0x7f;
        _syms![_seq] = code;
        int bcCk = BitUtil.BitReverse(bcRev, 3);
        int bcData = BitUtil.BitCount(code ^ 0x7f);
        _bcErr![_seq] = bcCk != bcData;
        if (_bcErr[_seq]) _parityErrors++;
        _seq++;

        if (_seq >= MsgLenMin)
        {
            bool EosCk(int eosSym)
            {
                int n = 0;
                if (!_bcErr[_seq - 2] && _syms[_seq - 2] == eosSym) n++;
                if (!_bcErr[_seq - 3] && _syms[_seq - 3] == eosSym) n++;
                if (!_bcErr[_seq - 4] && _syms[_seq - 4] == eosSym) n++;
                if (!_bcErr[_seq - 8] && _syms[_seq - 8] == eosSym) n++;
                return n >= 3;
            }

            bool eos = EosCk(Eos) || EosCk(Arq) || EosCk(Abq);
            if (eos || _seq > MsgLenMax)
            {
                if (eos)
                {
                    if (_seq != MsgLenMin && showErrs)
                        output(OutputMsg($"non-std len={_seq}"));
                    output(ProcessMsg(showErrs));
                }
                else if (showErrs)
                {
                    string pe = _parityErrors > 0 ? $" {_parityErrors} PE" : "";
                    output(OutputMsg("no EOS" + pe));
                }
                _synced = false;
                return new ProcessCharResult { Resync = true };
            }
        }
        return new ProcessCharResult { Resync = false };
    }

    sealed class SymInfo
    {
        public int N;
        public string SymS = "";
        public string Z = "";
        public bool Nop, Err;
        public string S = "";
        public int Type;
        public bool Fail;
    }

    SymInfo Check(string symName, int repeats, List<int> eccCk)
    {
        var ck = new List<(int Pos, int Sym)>();
        for (int i = 0; i < repeats; i++)
        {
            var sym = SymByName(symName + "-" + i);
            if (!_bcErr![sym.Pos]) ck.Add(sym);
        }
        bool empty = ck.Count == 0;
        bool err = false;
        if (!empty)
        {
            var first = ck[0];
            for (int i = 1; i < ck.Count; i++)
                if (first.Sym != ck[i].Sym) err = true;
        }
        if (empty || err) _fecErrors++;
        if (empty) return new SymInfo { N = -1, Err = true };
        int sn = ck[0].Sym;
        return new SymInfo
        {
            N = err ? -1 : sn,
            SymS = sn.ToString(),
            Z = BitUtil.LeadingZeros(sn, 2),
            Nop = sn == Nop,
            Err = err
        };
    }

    string Mid(string mid) => DscMidMmsi.Mid.TryGetValue(mid, out var s) ? s : "(unknown)";
    string MmsiCoast(string mmsi7) => DscMidMmsi.MmsiCoast.TryGetValue(mmsi7, out var s) ? s : "(unknown)";

    SymInfo Call(string symName, SymInfo fmt, List<int> eccCk)
    {
        var c12 = Check(symName + "1", 2, eccCk);
        var c34 = Check(symName + "2", 2, eccCk);
        var c56 = Check(symName + "3", 2, eccCk);
        var c78 = Check(symName + "4", 2, eccCk);
        var c90 = Check(symName + "5", 2, eccCk);
        eccCk.Add(c12.N); eccCk.Add(c34.N); eccCk.Add(c56.N); eccCk.Add(c78.N); eccCk.Add(c90.N);
        if (c12.Err || c34.Err || c56.Err || c78.Err || c90.Err)
            return new SymInfo { S = "callFEC", Err = true };

        string _12 = c12.Z, _34 = c34.Z, _56 = c56.Z, _78 = c78.Z, _90 = c90.Z;
        string _56789 = _56 + _78 + _90[0];
        string mmsiS = _12 + _34 + _56789;
        int iden1 = _12[0] - '0', iden2 = _12[1] - '0', iden3 = _34[0] - '0';
        bool isMid1 = iden1 >= 2 && iden1 <= 7;
        bool isMid2 = iden2 >= 2 && iden2 <= 7;
        bool isMid3 = iden3 >= 2 && iden3 <= 7;
        int format = fmt.N;
        if (format == FmtGeoArea && symName == "Dfr") format = FmtIndivSta;
        if (format == FmtAllShips && symName == "Dfr") format = FmtIndivSta;
        if (format == FmtCommonInterest && symName == "Dfr") format = FmtIndivSta;

        string s;
        switch (format)
        {
            case FmtIndivSta:
                s = "";
                if (isMid1)
                {
                    string midS = _12 + _34[0];
                    s = $"{mmsiS}[Flag: {Mid(midS)}]";
                }
                else if (_12 == "00" && isMid3)
                {
                    string midS = _34 + _56[0];
                    string mmsi7 = _34 + _56789;
                    s = $"{mmsiS}[Coast Station: {Mid(midS)}, {MmsiCoast(mmsi7)}]";
                }
                else if (iden1 == 0 && isMid2)
                {
                    string midS = _12[1] + _34;
                    s = $"{mmsiS}[Ship group: {Mid(midS)}]";
                }
                else if (_12 == "99" && isMid3)
                {
                    string midS = _34 + _56[0];
                    s = $"{mmsiS}[NAVAID: {Mid(midS)}]";
                }
                else if (_12 == "98" && isMid3)
                {
                    string midS = _34 + _56[0];
                    s = $"{mmsiS}[Associated craft: {Mid(midS)}]";
                }
                else if (_12 == "97")
                {
                    int _3 = _34[0] - '0';
                    if (_3 == 0) s = "[SAR transponder]";
                    else if (_3 == 2) s = "[MOB device]";
                    else if (_3 == 4) s = "[EPIRB]";
                    else s = "";
                    if (s != "") s = mmsiS + s;
                }
                else if (iden1 == 1) s = mmsiS + "[SAR aircraft]";
                else if (iden1 == 8) s = mmsiS + "[Handheld VHF]";
                if (s == "") s = mmsiS + "[Unknown]";
                break;
            case FmtDistress:
            case FmtAllShips:
                s = "[All ships]";
                break;
            case FmtCommonInterest:
                if (iden1 == 0 && isMid2)
                {
                    string midS = _12[1] + _34;
                    s = $"{mmsiS}[Ship group: {Mid(midS)}]";
                }
                else s = mmsiS + "[Ship group]";
                break;
            case FmtGeoArea:
            {
                int quad = iden1;
                string latSgn = (quad == 0 || quad == 1) ? "" : "-";
                string lonSgn = (quad == 0 || quad == 2) ? "" : "-";
                s = $"{latSgn}{_12[1]}{_34[0]}°/{lonSgn}{_34[1]}{_56}° {_78}°V/{_90}°H [Area]";
                break;
            }
            default:
                s = mmsiS + "[Other]";
                break;
        }
        return new SymInfo { S = s, Z = mmsiS + _90[1], Err = false };
    }

    SymInfo Frequency(string symName, bool firstCall, SymInfo cmd1, bool forcePos, List<int> eccCk)
    {
        var f1 = Check(symName + "1", 2, eccCk);
        var f2 = Check(symName + "2", 2, eccCk);
        var f3 = Check(symName + "3", 2, eccCk);
        eccCk.Add(f1.N); eccCk.Add(f2.N); eccCk.Add(f3.N);
        if (f1.Err || f2.Err || f3.Err) return new SymInfo { S = "freqFEC", Err = true };

        int type = 0;
        if (!f1.Nop && !f2.Nop)
        {
            char n = f1.Z[0];
            if (firstCall && cmd1.N == Cmd1Position) type = 5;
            if (forcePos || n == '5' || type == 5)
                return new SymInfo { S = f1.Z + f2.Z + f3.Z, Type = 5, Err = false };
            if (n is '0' or '1' or '2')
                return new SymInfo
                {
                    S = (n == '0' ? f1.Z[1].ToString() : f1.Z) + f2.Z + f3.Z[0] + "." + f3.Z[1],
                    Type = 6, Err = false
                };
            if (n == '4')
            {
                string z1 = f1.Nop ? "*" : f1.Z;
                string z2 = f2.Nop ? "*" : f2.Z;
                string z3 = f3.Nop ? "*" : f3.Z;
                return new SymInfo { S = "7-DIGIT MODE|" + z1 + "|" + z2 + "|" + z3, Type = 7, Err = false };
            }
        }
        {
            string z1 = f1.Nop ? "*" : f1.Z;
            string z2 = f2.Nop ? "*" : f2.Z;
            string z3 = f3.Nop ? "*" : f3.Z;
            return new SymInfo { S = z1 + "|" + z2 + "|" + z3, Type = type, Err = false };
        }
    }

    string ProcessMsg(bool showErrs)
    {
        var eccCk = new List<int>();
        var fmt = Check("Afmt", 4, eccCk);
        eccCk.Add(fmt.N);
        fmt.S = fmt.Err ? "fmtFEC" : (_formatS.TryGetValue(fmt.SymS, out var fs) ? fs : fmt.N + "?");

        var to = Call("Bto", fmt, eccCk);
        var cat = Check("Cat", 2, eccCk);
        eccCk.Add(cat.N);
        cat.S = cat.Err ? "catFEC" : (_categoryS.TryGetValue(cat.SymS, out var cs) ? cs : cat.N + "?");

        var from = Call("Dfr", fmt, eccCk);
        var cmd1 = Check("E1cmd", 2, eccCk);
        eccCk.Add(cmd1.N);
        cmd1.S = cmd1.Err ? "cmdFEC" : (cmd1.Nop ? "*" : cmd1.SymS);
        var cmd2 = Check("E2cmd", 2, eccCk);
        eccCk.Add(cmd2.N);
        cmd2.S = cmd2.Err ? "cmdFEC" : (cmd2.Nop ? "*" : cmd2.SymS);

        var f1 = Frequency("Ffreq", true, cmd1, false, eccCk);
        var f2 = Frequency("Gfreq", false, cmd1, f1.Type == 5, eccCk);

        var eos = Check("eos", 4, eccCk);
        eccCk.Add(eos.N);
        eos.S = eos.Err ? "eosFEC" : (eos.N == Eos ? "EOS" : eos.N == Arq ? "ARQ" : eos.N == Abq ? "ABQ" : "eos?");

        var ecc = Check("ecc", 2, eccCk);
        ecc.Fail = false;
        if (!ecc.Err)
        {
            int xor = 0;
            foreach (var a in eccCk) xor ^= a;
            ecc.Fail = xor != ecc.N;
        }
        ecc.S = ecc.Err ? "eccFEC" : (ecc.Fail ? "eccFAIL" : "");

        string peS = _parityErrors > 0 ? $" {_parityErrors} PE" : "";
        string eccS = ecc.Err ? "ECC FEC" : (ecc.Fail ? "ECC" : "");
        if (eccS != "") eccS = " " + eccS;

        string Ack(string s1, string? s2 = null) => s1 + (eos.N == Abq ? " ack" : "") + (s2 ?? "");
        string Type()
        {
            if (fmt.N == FmtDistress) return "DISTRESS";
            return cat.N switch
            {
                CatDistress => "DISTRESS",
                CatUrgency => "URGENT",
                CatSafety => "SAFETY",
                CatRoutine => "ROUTINE",
                _ => "UNKNOWN"
            };
        }
        string FromTo(string s) => s + ", " + from.S + " => " + to.S;
        string Freq(bool posRequest = false)
        {
            if (f1.Type == 6)
            {
                string s = " " + f1.S;
                if (f2.Type == 6) s += "/" + f2.S;
                return s + " kHz";
            }
            if (f1.Type == 5)
            {
                int i = (posRequest && eos.N == Abq) ? 0 : 2;
                string p = f1.S + f2.S;
                if (p.Length < i + 10) return "";
                int quad = p[i] - '0';
                string latSgn = (quad == 0 || quad == 1) ? "" : "-";
                string lonSgn = (quad == 0 || quad == 2) ? "" : "-";
                return $" {latSgn}{p[i + 1]}{p[i + 2]}°{p[i + 3]}{p[i + 4]}'/{lonSgn}{p[i + 5]}{p[i + 6]}{p[i + 7]}°{p[i + 8]}{p[i + 9]}'";
            }
            return "";
        }

        bool fec = fmt.Err || to.Err || cat.Err || from.Err || cmd1.Err || cmd2.Err || f1.Err || f2.Err || eos.Err;
        string s;
        if (fec) { s = ""; }
        else if (fmt.N == FmtDistress && eos.N == Eos) s = "4.1";
        else if (cat.N == CatDistress && fmt.N == FmtAllShips && cmd1.N == Cmd1DistressAck && eos.N == Eos) s = "4.2";
        else if (cat.N == CatDistress && cmd1.N == Cmd1DistressAlertRelay && (eos.N == Eos || eos.N == Arq)) s = "4.3";
        else if (cat.N == CatDistress && cmd1.N == Cmd1DistressAlertRelay && eos.N == Abq) s = "4.4";
        else if ((cat.N == CatSafety || cat.N == CatUrgency) && fmt.N == FmtAllShips && cmd2.N == Cmd2Nop && eos.N == Eos)
        {
            s = "4.5";
            if (cmd1.N == Cmd1J3eRt) s = FromTo("SSB call" + Freq());
            else if (cmd1.N == Cmd1F1bFec) s = FromTo("FSK-FEC call" + Freq());
        }
        else if ((cat.N == CatSafety || cat.N == CatUrgency) && fmt.N == FmtGeoArea && eos.N == Eos)
        {
            s = "4.6";
            string s2 = cmd2.N == Cmd2Nop ? "" : cmd2.N == Cmd2MedTransports ? " medical" : cmd2.N == Cmd2NonConflict ? " ships/aircraft" : "";
            if (cmd1.N == Cmd1J3eRt) s = FromTo("SSB call" + s2 + Freq());
            else if (cmd1.N == Cmd1F1bFec) s = FromTo("FSK-FEC call" + s2 + Freq());
        }
        else if ((cat.N == CatSafety || cat.N == CatUrgency) && fmt.N == FmtIndivSta)
        {
            s = "4.7";
            if (eos.N == Arq || eos.N == Abq)
            {
                if (cmd2.N == Cmd2Nop)
                {
                    s = cmd1.N switch
                    {
                        Cmd1FmCall => FromTo(Ack("FM call", Freq())),
                        Cmd1FmDuplexCall => FromTo(Ack("FM duplex call", Freq())),
                        Cmd1J3eRt => FromTo(Ack("SSB call", Freq())),
                        Cmd1F1bFec => FromTo(Ack("FSK-FEC call", Freq())),
                        Cmd1F1bArq => FromTo(Ack("FSK-ARQ call", Freq())),
                        Cmd1Position => FromTo(Ack("Position request", Freq(true))),
                        Cmd1Test => FromTo(Ack("Test")),
                        _ => s
                    };
                }
                else if (cmd1.N == Cmd1UnableComply && cmd2.N >= Cmd2UnableFirst && cmd2.N <= Cmd2UnableLast && eos.N == Abq)
                    s = FromTo("Unable to comply ack" + Freq());
            }
            else if (eos.N == Eos && cmd1.N == Cmd1Test && cmd2.N == Cmd2Nop)
                s = FromTo("Test");
        }
        else if (cat.N == CatRoutine && fmt.N == FmtCommonInterest && cmd2.N == Cmd2Nop && eos.N == Eos)
        {
            s = "4.8";
            if (cmd1.N == Cmd1J3eRt) s = FromTo("SSB call" + Freq());
            else if (cmd1.N == Cmd1F1bFec) s = FromTo("FSK-FEC call" + Freq());
        }
        else if (cat.N == CatRoutine && fmt.N == FmtIndivSta && (eos.N == Arq || eos.N == Abq))
        {
            s = "4.9";
            if (cmd2.N == Cmd2Nop)
            {
                s = cmd1.N switch
                {
                    Cmd1FmCall => FromTo(Ack("FM call", Freq())),
                    Cmd1FmDuplexCall => FromTo(Ack("FM duplex call", Freq())),
                    Cmd1J3eRt => FromTo(Ack("SSB call", Freq())),
                    Cmd1F1bFec => FromTo(Ack("FSK-FEC call", Freq())),
                    Cmd1F1bArq => FromTo(Ack("FSK-ARQ call", Freq())),
                    Cmd1F1bData => FromTo(Ack("FSK-DATA call", Freq())),
                    Cmd1Polling => FromTo(Ack("Polling")),
                    _ => s
                };
            }
            else if (cmd1.N == Cmd1UnableComply && cmd2.N >= Cmd2UnableFirst && cmd2.N <= Cmd2UnableLast && eos.N == Abq)
                s = FromTo("Unable to comply ack" + Freq());
        }
        else if (cat.N == CatRoutine && fmt.N == FmtIsSemiAuto && (eos.N == Arq || eos.N == Abq))
            s = "4.10";
        else
            s = "4.x";

        string msgS = "", errS = "";
        if (fec)
        {
            errS = $"{_fecErrors} FEC";
            eccS = "";
        }
        else if (s.StartsWith("4.", StringComparison.Ordinal))
        {
            msgS = $"DECODE {s} fmt={fmt.N} cat={cat.N} cmd1={cmd1.N} cmd2={cmd2.N} from={from.Z} to={to.Z} freq1={f1.S} freq2={f2.S} eos={eos.N} ecc={ecc.N}";
        }
        else
            msgS = Type() + " " + s;

        if (showErrs)
        {
            string outS = errS != "" ? errS : msgS;
            return OutputMsg(outS + peS + eccS);
        }
        return errS != "" ? "" : OutputMsg(msgS + eccS);
    }
}
