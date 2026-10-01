// Port of KiwiSDR CCIR476.js — Copyright (C) 2011 Paul Lutus (GPL-2.0+)

namespace NeuroSDR.Plugins.Kiwi.Jnx;

internal sealed class Ccir476Encoding : IJnxEncoding
{
    const int Letters = 0x5a;
    const int Figures = 0x36;
    const int CodeAlpha = 0x0f;
    const int CodeBeta = 0x33;
    const int CodeChar32 = 0x6a;
    const int CodeRep = 0x66;

    readonly Dictionary<int, string> _codeLtrs = new();
    readonly Dictionary<int, string> _codeFigs = new();
    readonly HashSet<int> _validCodes = new();

    bool _shift;
    bool _alphaPhase;
    int _c1, _c2, _c3;

    public Ccir476Encoding()
    {
        // Same tables as CCIR476.js (documentation placeholders '_' skipped)
        string[] ltrs = new string[128];
        string[] figs = new string[128];
        for (int i = 0; i < 128; i++) { ltrs[i] = "_"; figs[i] = "_"; }

        void Set(string[] t, int code, string ch) => t[code] = ch;

        Set(ltrs, 0x0f, "_"); // ALF
        Set(ltrs, 0x17, "J"); Set(ltrs, 0x1b, "F"); Set(ltrs, 0x1d, "C"); Set(ltrs, 0x1e, "K");
        Set(ltrs, 0x27, "W"); Set(ltrs, 0x2b, "Y"); Set(ltrs, 0x2d, "P"); Set(ltrs, 0x2e, "Q");
        Set(ltrs, 0x33, "_"); // BET
        Set(ltrs, 0x35, "G"); Set(ltrs, 0x36, "_"); // FGS
        Set(ltrs, 0x39, "M"); Set(ltrs, 0x3a, "X"); Set(ltrs, 0x3c, "V");
        Set(ltrs, 0x47, "A"); Set(ltrs, 0x4b, "S"); Set(ltrs, 0x4d, "I"); Set(ltrs, 0x4e, "U");
        Set(ltrs, 0x53, "D"); Set(ltrs, 0x55, "R"); Set(ltrs, 0x56, "E");
        Set(ltrs, 0x59, "N"); Set(ltrs, 0x5a, "_"); // LTR
        Set(ltrs, 0x5c, " ");
        Set(ltrs, 0x63, "Z"); Set(ltrs, 0x65, "L"); Set(ltrs, 0x66, "_"); // REP
        Set(ltrs, 0x69, "H"); Set(ltrs, 0x6a, "_"); // C32
        Set(ltrs, 0x6c, "\n");
        Set(ltrs, 0x71, "O"); Set(ltrs, 0x72, "B"); Set(ltrs, 0x74, "T"); Set(ltrs, 0x78, "\r");

        Set(figs, 0x0f, "_");
        Set(figs, 0x17, "'"); Set(figs, 0x1b, "!"); Set(figs, 0x1d, ":"); Set(figs, 0x1e, "(");
        Set(figs, 0x27, "2"); Set(figs, 0x2b, "6"); Set(figs, 0x2d, "0"); Set(figs, 0x2e, "1");
        Set(figs, 0x33, "_");
        Set(figs, 0x35, "&"); Set(figs, 0x36, "_");
        Set(figs, 0x39, "."); Set(figs, 0x3a, "/"); Set(figs, 0x3c, ";");
        Set(figs, 0x47, "-"); Set(figs, 0x4b, "\u0007"); Set(figs, 0x4d, "8"); Set(figs, 0x4e, "7");
        Set(figs, 0x53, "$"); Set(figs, 0x55, "4"); Set(figs, 0x56, "3");
        Set(figs, 0x59, ","); Set(figs, 0x5a, "_");
        Set(figs, 0x5c, " ");
        Set(figs, 0x63, "\""); Set(figs, 0x65, ")"); Set(figs, 0x66, "_");
        Set(figs, 0x69, "#"); Set(figs, 0x6a, "_");
        Set(figs, 0x6c, "\n");
        Set(figs, 0x71, "9"); Set(figs, 0x72, "?"); Set(figs, 0x74, "5"); Set(figs, 0x78, "\r");

        for (int code = 0; code < 128; code++)
        {
            if (!CheckBits(code)) continue;
            _validCodes.Add(code);
            if (ltrs[code] != "_") _codeLtrs[code] = ltrs[code];
            if (figs[code] != "_") _codeFigs[code] = figs[code];
        }
    }

    public int GetNbits() => 7;
    public int GetMsb() => 0x40;
    public bool SearchSync(int bit) => false;

    public void Reset()
    {
        _shift = false;
        _alphaPhase = false;
        _c1 = _c2 = _c3 = 0;
    }

    public bool CheckBits(int v)
    {
        int bc = 0;
        while (v != 0)
        {
            bc++;
            v &= v - 1;
        }
        return bc == 4;
    }

    string? CodeToChar(int code, bool shift)
    {
        var map = shift ? _codeFigs : _codeLtrs;
        return map.TryGetValue(code, out var ch) ? ch : null;
    }

    public ProcessCharResult ProcessChar(int code, int fixedStart, Action<string> output, bool showRaw, bool showErrs)
    {
        bool success = CheckBits(code);
        int tally = 0;
        int chr = -1;

        if (code == CodeRep) _alphaPhase = false;
        else if (code == CodeAlpha) _alphaPhase = true;

        if (!_alphaPhase)
        {
            _c1 = _c2;
            _c2 = _c3;
            _c3 = code;
        }
        else
        {
            if (success) chr = code;
            else if (CheckBits(_c1)) chr = _c1;

            if (chr == -1)
                tally = -1;
            else
            {
                tally = 1;
                switch (chr)
                {
                    case CodeRep:
                    case CodeAlpha:
                    case CodeBeta:
                    case CodeChar32:
                        break;
                    case Letters:
                        _shift = false;
                        break;
                    case Figures:
                        _shift = true;
                        break;
                    default:
                        var s = CodeToChar(chr, _shift);
                        if (s != null) output(s);
                        break;
                }
            }
        }

        _alphaPhase = !_alphaPhase;
        return new ProcessCharResult { Success = success, Tally = tally };
    }
}
