// Port of KiwiSDR FSK_async.js — Copyright (C) 2011 Paul Lutus (GPL-2.0+)

using System.Globalization;

namespace NeuroSDR.Plugins.Kiwi.Jnx;

internal sealed class FskAsyncEncoding : IJnxEncoding
{
    readonly bool _ita2;
    readonly int _dataBits, _parityBits, _startBit;
    readonly double _stopBits;
    readonly bool _raw, _efr, _efr2, _chu;
    readonly int _nbits, _msb, _dataMsb;
    readonly int _letters = 0x1f, _figures = 0x1b;
    readonly Dictionary<int, string> _codeLtrs = new();
    readonly Dictionary<int, string> _codeFigs = new();

    bool _shift;
    int _lastCode;
    bool _efrHdrFound;
    readonly List<int> _efrCh = new();
    readonly List<int> _chuCh = new();
    readonly List<int> _chuCn = new();

    public FskAsyncEncoding(string framing, string encoding)
    {
        framing ??= "5N1";
        _startBit = 1;
        _dataBits = framing.Length > 0 && char.IsDigit(framing[0]) ? framing[0] - '0' : 5;
        _raw = false;

        if (framing.EndsWith("0V", StringComparison.Ordinal))
            _stopBits = 0;
        else if (framing.EndsWith("1V", StringComparison.Ordinal))
            _stopBits = 1;
        else if (framing.EndsWith("1.5", StringComparison.Ordinal))
            _stopBits = 1.5;
        else if (framing.EndsWith("2", StringComparison.Ordinal))
            _stopBits = 2;
        else
            _stopBits = 1;

        if (framing.Contains('E') && !framing.Contains("EFR", StringComparison.Ordinal))
            _parityBits = 1;
        else if (framing.Contains('O'))
            _parityBits = 1;
        else if (framing.Contains('P'))
            _parityBits = 1;
        else
            _parityBits = 0;

        if (framing.Contains("EFR", StringComparison.Ordinal))
        {
            _efr = true;
            _efr2 = framing == "EFR2";
            _dataBits = 8;
            _parityBits = 1;
            _stopBits = 1;
        }

        if (framing == "CHU")
        {
            _chu = true;
            _dataBits = 8;
            _parityBits = 0;
            _stopBits = 2;
            _raw = true;
        }

        _nbits = (int)(_startBit + _dataBits + _parityBits + _stopBits);
        if (_stopBits == 1.5) _nbits *= 2;
        _msb = 1 << (_nbits - 1);
        _dataMsb = 1 << (_dataBits - 1);

        switch (encoding)
        {
            case "ASCII":
                break;
            default:
                _ita2 = true;
                break;
        }

        // US-TTY Baudot
        string[] ltrs =
        {
            "\0", "E", "\n", "A", " ", "S", "I", "U", "\r", "D", "R", "J", "N", "F", "C", "K",
            "T", "Z", "L", "W", "H", "Y", "P", "Q", "O", "B", "G", "_", "M", "X", "V", "_"
        };
        string[] figs =
        {
            "\0", "3", "\n", "-", " ", "\u0007", "8", "7", "\r", "$", "4", "'", ",", "!", ":", "(",
            "5", "\"", ")", "2", "#", "6", "0", "1", "9", "?", "&", "_", ".", "/", ";", "_"
        };
        for (int code = 0; code < 32; code++)
        {
            if (ltrs[code] != "_") _codeLtrs[code] = ltrs[code];
            if (figs[code] != "_") _codeFigs[code] = figs[code];
        }
    }

    public int GetNbits() => _nbits;
    public int GetMsb() => _msb;
    public bool SearchSync(int bit) => false;

    public void Reset()
    {
        _shift = false;
        _efrHdrFound = false;
        _efrCh.Clear();
        _chuCh.Clear();
        _chuCn.Clear();
    }

    string? CodeToChar(int code, bool shift, int fixedStart)
    {
        if (_ita2)
        {
            var map = shift ? _codeFigs : _codeLtrs;
            return map.TryGetValue(code, out var s) ? s : null;
        }

        if (_efr)
            return DecodeEfr(code);

        if (_chu)
            return DecodeChu(code, fixedStart);

        // ASCII
        if ((code >= 0x00 && code <= 0x09) ||
            (code >= 0x0b && code <= 0x0c) ||
            (code >= 0x0e && code <= 0x1f) ||
            code >= 0x7f)
            return null;
        return ((char)code).ToString();
    }

    string DecodeEfr(int code)
    {
        if (!_efrHdrFound)
        {
            _efrCh.Add(code);
            if (_efrCh.Count > 4) _efrCh.RemoveAt(0);
            var c = _efrCh;
            if (c.Count == 4 && c[0] == 0x68 && c[1] == c[2] && c[3] == 0x68)
                _efrHdrFound = true;
            return "";
        }

        _efrCh.Add(code);
        var ch = _efrCh;
        int ulen = ch[1];
        int len = ulen + (4 + 2);
        if (ch.Count != len) return "";

        string s = "";
        if (ch[len - 1] == 0x16)
        {
            bool timeTelegram = ulen == 0xa && (ch[4] & 0xf) == 0x7 && ch[5] == 0 && ch[6] == 0 && (ch[13] & 0x7f) != 0x7f;
            if (timeTelegram)
            {
                if (!_efr2)
                {
                    string ss = BitUtil.LeadingZeros(ch[8] >> 2, 2);
                    string mm = BitUtil.LeadingZeros(ch[9] & 0x3f, 2);
                    string hh = BitUtil.LeadingZeros(ch[10] & 0x1f, 2);
                    bool dst = (ch[10] & 0x80) != 0;
                    int dd = ch[11] & 0x1f;
                    int dy = ch[11] >> 5;
                    int mo = ch[12] & 0x0f;
                    int yy = ch[13] & 0x7f;
                    string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
                    string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
                    string day = dy >= 1 && dy <= 7 ? days[dy - 1] : "?";
                    string month = mo >= 1 && mo <= 12 ? months[mo - 1] : "?";
                    s = $"{hh}:{mm}:{ss}{(dst ? " DST " : " ST ")}{day} {dd} {month} 20{yy:D2}\n";
                }
            }
            else
            {
                bool commonTelegram = ulen == 0x13 && (ch[4] & 0xf) == 0 && ch[5] == 0x20;
                if (!_efr2 || !commonTelegram)
                {
                    if (_efr2)
                        s = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " ";
                    s += $"len={BitUtil.ToHex(ulen, 2)} ";
                    s += $"C={BitUtil.ToHex(ch[4], 2)} ";
                    s += $"A={BitUtil.ToHex(ch[5], 2)} ";
                    s += $"CI={BitUtil.ToHex(ch[6], 2)} [";
                    for (int i = 7; i < ch.Count - 2; i++)
                        s += BitUtil.ToHex(ch[i], 2) + (i == ch.Count - 3 ? "] " : " ");
                    s += $"ck={BitUtil.ToHex(ch[^2], 2)} |";
                    for (int i = 7; i < ch.Count - 2; i++)
                        s += ch[i] >= 0x20 && ch[i] < 0x7f ? ((char)ch[i]).ToString() : ".";
                    s += "|\n";
                }
            }
        }
        _efrCh.Clear();
        _efrHdrFound = false;
        return s;
    }

    string DecodeChu(int code, int fixedStart)
    {
        if (fixedStart != 0)
        {
            _chuCh.Clear();
            _chuCn.Clear();
        }

        code = (((code & 0xf) << 4) & 0xf0) | (((code & 0xf0) >> 4) & 0xf);
        _chuCh.Add(code);
        _chuCn.Add(code ^ 0xff);

        if (_chuCh.Count != 10) return "";

        var c = _chuCh;
        int f1 = c[0] & 0xf0, f2 = c[5] & 0xf0, s1 = c[4] & 0xf0, s2 = c[9] & 0xf0;
        string s = "";

        if (f1 == 0x60 && s1 == 0x30 && f2 == 0x60 && s2 == 0x30 &&
            c[0] == c[5] && c[1] == c[6] && c[2] == c[7] && c[3] == c[8] && c[4] == c[9])
        {
            var d = DateTime.UtcNow;
            s = $"CHU {BitUtil.ToHex(c[2], 2)}:{BitUtil.ToHex(c[3], 2)}:{BitUtil.ToHex(c[4], 2)} UTC (host {d:HH:mm:ss})\n";
        }
        else
        {
            var cn = _chuCn;
            if (c[0] == cn[5] && c[1] == cn[6] && c[2] == cn[7] && c[3] == cn[8] && c[4] == cn[9])
            {
                string yyyy = BitUtil.ToHex(c[1], 2) + BitUtil.ToHex(c[2], 2);
                string taiUtc = BitUtil.ToHex(c[3], 1);
                string dst = BitUtil.ToHex(c[4], 1);
                s = $"CHU year={yyyy} DST={dst} TAI-UTC={taiUtc}\n";
            }
        }
        return s;
    }

    public bool CheckBits(int v)
    {
        if (_stopBits == 1.5)
        {
            if ((v & 3) != 0) return false;
            v >>= 2;
            _lastCode = 0;
            for (int i = 0; i < _dataBits; i++)
            {
                int d = v & 3;
                if (d != 0 && d != 3) return false;
                _lastCode = (_lastCode >> 1) | (d != 0 ? _dataMsb : 0);
                v >>= 2;
            }
            if ((v & 7) != 7) return false;
            v >>= 3;
            if (v != 0) return false;
            return true;
        }

        if (!_raw && (v & 1) != 0) return false;
        v >>= 1;
        _lastCode = 0;
        for (int i = 0; i < _dataBits; i++)
        {
            _lastCode = (_lastCode >> 1) | ((v & 1) != 0 ? _dataMsb : 0);
            v >>= 1;
        }
        if (_parityBits == 1) v >>= 1;
        if (_stopBits == 2)
        {
            if (!_raw && (v & 3) != 3) return false;
            v >>= 2;
        }
        else if (_stopBits == 1)
        {
            if (!_raw && (v & 1) != 1) return false;
            v >>= 1;
        }
        if (!_raw && v != 0) return false;
        return true;
    }

    public ProcessCharResult ProcessChar(int code, int fixedStart, Action<string> output, bool showRaw, bool showErrs)
    {
        bool success = CheckBits(code);
        int tally = 0;
        if (!success)
            tally = -1;
        else
        {
            tally = 1;
            if (_ita2)
            {
                if (_lastCode == _letters)
                    _shift = false;
                else if (_lastCode == _figures)
                    _shift = true;
                else
                {
                    var chr = CodeToChar(_lastCode, _shift, fixedStart);
                    if (chr != null)
                        output(chr);
                }
            }
            else
            {
                var chr = CodeToChar(_lastCode, false, fixedStart);
                if (!string.IsNullOrEmpty(chr))
                    output(chr);
            }
        }
        return new ProcessCharResult { Success = success, Tally = tally };
    }
}
