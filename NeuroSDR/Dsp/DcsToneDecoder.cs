namespace NeuroSDR.Dsp;

/// <summary>
/// CDCSS/DCS detector — light enough for the RF/audio callback.
/// Decimate AF, majority-vote a 23-bit word, Hamming-match templates (≤2).
/// Expensive Golay 2-bit repair is offline-only.
/// </summary>
internal sealed class DcsToneDecoder
{
    private const double Baud = 134.4;
    private const int TargetDecimatedRate = 2_688;
    private const int MaxBitScratch = 512;
    private const int DecodeIntervalMs = 200; // ≤5 Hz on the audio thread

    private static readonly int[] StandardCodes =
    [
        023, 025, 026, 031, 032, 036, 043, 047, 051, 053, 054, 065, 071, 072, 073, 074,
        114, 115, 116, 122, 125, 131, 132, 134, 143, 145, 152, 155, 156, 162, 165, 172, 174,
        205, 212, 223, 225, 226, 243, 244, 245, 246, 251, 252, 255, 261, 263, 265, 266, 271, 274,
        306, 311, 315, 325, 331, 332, 343, 346, 351, 356, 364, 365, 371,
        411, 412, 413, 423, 431, 432, 445, 446, 452, 454, 455, 462, 464, 465, 466, 503,
        506, 516, 523, 526, 532, 546, 565,
        606, 612, 624, 627, 631, 632, 654, 662, 664,
        703, 712, 723, 731, 732, 734, 743, 754
    ];

    private static readonly Dictionary<int, int> InversePair = BuildInversePairs();
    private static readonly Template[] Templates = BuildTemplates();

    private readonly object _sync = new();
    private readonly float[] _decimated = new float[TargetDecimatedRate * 2];
    private readonly byte[] _bitScratch = new byte[MaxBitScratch];
    private readonly int[] _majOnes = new int[23];
    private readonly int[] _majTotal = new int[23];
    private readonly List<(uint Air, int Conf, int Words)> _candidates = new(32);
    private int _sampleRate = AudioDemodulator.AudioSampleRate;
    private float _lpY, _hpX, _hpY, _hpA, _lpA;
    private float _decPhase;
    private int _decCount;
    private int _filterRate = -1;
    private int _decimation = 1;
    private float _samplesPerBit = 20f;
    private int _wordSamples = 460;
    private int _detectedCode = -1;
    private int _stableHits;
    private int _missHits;
    private int _candidate = -1;
    private bool _inverted;
    private float _debugCorr;
    private int _debugBestDist = 23;
    private int _debugVotes;
    private uint _debugTopPattern;
    private int _debugTopPatternCount;
    private int _activityHold;
    private long _nextDecodeMs;
    private bool _deepSearch;

    /// <summary>Enable Golay ≤2-bit repair (offline IQ analysis only — too heavy for UI).</summary>
    public bool DeepSearch
    {
        get { lock (_sync) return _deepSearch; }
        set { lock (_sync) _deepSearch = value; }
    }

    public int DetectedCode { get { lock (_sync) return _detectedCode; } }
    public bool Inverted { get { lock (_sync) return _inverted; } }
    public float DebugCorr { get { lock (_sync) return _debugCorr; } }
    public int DebugBestDist { get { lock (_sync) return _debugBestDist; } }
    public int DebugVotes { get { lock (_sync) return _debugVotes; } }
    public uint DebugTopPattern { get { lock (_sync) return _debugTopPattern; } }
    public int DebugTopPatternCount { get { lock (_sync) return _debugTopPatternCount; } }
    public bool HasActivity { get { lock (_sync) return _detectedCode >= 0 || _activityHold > 0; } }

    public string DetectedLabel
    {
        get
        {
            var code = DetectedCode;
            if (code < 0) return "";
            return (Inverted ? "D" : "") + code.ToString("D3");
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _decCount = 0;
            _decPhase = 0;
            _detectedCode = _candidate = -1;
            _stableHits = _missHits = 0;
            _inverted = false;
            _debugCorr = 0;
            _debugBestDist = 23;
            _debugVotes = 0;
            _activityHold = 0;
            _nextDecodeMs = 0;
            _lpY = _hpX = _hpY = 0;
            _filterRate = -1;
        }
    }

    public void Process(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (mono.Length == 0 || sampleRate < 8_000) return;
        lock (_sync)
        {
            if (sampleRate != _sampleRate)
            {
                _sampleRate = sampleRate;
                _filterRate = -1;
                _decCount = 0;
                _decPhase = 0;
            }
            EnsureFilters();

            foreach (var raw in mono)
            {
                var hp = _hpA * (_hpY + raw - _hpX);
                _hpX = raw;
                _hpY = hp;
                _lpY += _lpA * (hp - _lpY);
                _decPhase += 1f;
                if (_decPhase < _decimation) continue;
                _decPhase -= _decimation;
                if (_decCount < _decimated.Length)
                    _decimated[_decCount++] = _lpY;
            }

            if (_decCount < _wordSamples * 3) return;

            var now = Environment.TickCount64;
            // Offline: every fill. UI: throttle only while locked (search stays responsive, audio stays light).
            if (!_deepSearch && _detectedCode >= 0 && now < _nextDecodeMs)
            {
                if (_decCount > _wordSamples * 4)
                {
                    var keep = _wordSamples * 3;
                    Array.Copy(_decimated, _decCount - keep, _decimated, 0, keep);
                    _decCount = keep;
                }
                return;
            }
            if (!_deepSearch && _detectedCode < 0 && now < _nextDecodeMs)
            {
                // Searching: still cap ~10 Hz so RF callback cannot starve.
                if (_decCount > _wordSamples * 4)
                {
                    var keep = _wordSamples * 3;
                    Array.Copy(_decimated, _decCount - keep, _decimated, 0, keep);
                    _decCount = keep;
                }
                return;
            }
            _nextDecodeMs = now + (_detectedCode >= 0 ? DecodeIntervalMs : DecodeIntervalMs / 2);

            TryDecode(_decimated.AsSpan(0, Math.Min(_decCount, _wordSamples * 4)));
            var remain = Math.Min(_wordSamples * 2, _decCount);
            Array.Copy(_decimated, _decCount - remain, _decimated, 0, remain);
            _decCount = remain;
        }
    }

    private void EnsureFilters()
    {
        if (_filterRate == _sampleRate) return;
        _filterRate = _sampleRate;
        _hpA = MathF.Exp(-2 * MathF.PI * 15f / _sampleRate);
        _lpA = 1f - MathF.Exp(-2 * MathF.PI * 250f / _sampleRate);
        _lpY = _hpX = _hpY = 0;
        _decimation = Math.Max(1, _sampleRate / TargetDecimatedRate);
        var actualDecRate = (float)_sampleRate / _decimation;
        _samplesPerBit = (float)(actualDecRate / Baud);
        _wordSamples = Math.Max(400, (int)MathF.Ceiling(_samplesPerBit * 23f));
    }

    private void TryDecode(ReadOnlySpan<float> block)
    {
        double energy = 0;
        for (var i = 0; i < block.Length; i++) energy += block[i] * block[i];
        var rms = Math.Sqrt(energy / Math.Max(1, block.Length));
        if (rms < 1e-5)
        {
            if (_activityHold > 0) _activityHold--;
            Register(-1, false, 0, 23);
            return;
        }

        if (!TryMatchTemplates(block, rms, out var code, out var hits, out var bestDist, out var topPat))
        {
            _debugCorr = 0;
            _debugBestDist = bestDist;
            _debugVotes = hits;
            _debugTopPattern = topPat;
            _debugTopPatternCount = hits;
            if (bestDist <= 3) _activityHold = 2;
            else if (_activityHold > 0) _activityHold--;
            Register(-1, false, 0, bestDist);
            return;
        }

        _debugCorr = 1f - bestDist / 23f;
        _debugBestDist = bestDist;
        _debugVotes = hits;
        _debugTopPattern = topPat;
        _debugTopPatternCount = hits;
        _activityHold = 4;
        Register(code, false, _debugCorr, bestDist);
    }

    private bool TryMatchTemplates(ReadOnlySpan<float> block, double rms,
        out int code, out int hits, out int bestDist, out uint topRawPattern)
    {
        if (_deepSearch)
            return TryMatchTemplatesDeep(block, rms, out code, out hits, out bestDist, out topRawPattern);

        code = -1;
        hits = 0;
        bestDist = 23;
        topRawPattern = 0;
        var gain = (float)(1.0 / rms);
        _candidates.Clear();

        // UI realtime path — light + permissive (show something; accuracy secondary).
        for (var pol = 0; pol < 2; pol++)
        {
            var spb = _samplesPerBit;
            var phaseSteps = Math.Max(8, (int)MathF.Round(spb));
            for (var phase = 0; phase < phaseSteps; phase += 2)
            {
                var bitCount = SliceBits(block, phase, spb, gain, invertBits: pol != 0);
                if (bitCount < 46) continue;
                CollectMajorityCandidates(bitCount, confMin: 10);
            }
        }

        return ScoreTopCandidates(hamOnly: true, take: 6, loose: true, out code, out hits, out bestDist, out topRawPattern);
    }

    private bool TryMatchTemplatesDeep(ReadOnlySpan<float> block, double rms,
        out int code, out int hits, out int bestDist, out uint topRawPattern)
    {
        code = -1;
        hits = 0;
        bestDist = 23;
        topRawPattern = 0;
        var gain = (float)(1.0 / rms);
        var globalBestDist = 99;
        var globalBestCode = -1;
        var globalBestVotes = 0;
        uint globalPat = 0;

        // Offline path — denser grid, Hamming ≤2 unique (no per-slot Golay).
        for (var pol = 0; pol < 2; pol++)
        {
            for (var baudStep = -1; baudStep <= 1; baudStep++)
            {
                var spb = _samplesPerBit * (1f + baudStep * 0.005f);
                var phaseSteps = Math.Max(8, (int)MathF.Round(spb));
                for (var phase = 0; phase < phaseSteps; phase += 2)
                {
                    var bitCount = SliceBits(block, phase, spb, gain, invertBits: pol != 0);
                    if (bitCount < 46) continue;

                    for (var align = 0; align < 23; align++)
                    {
                        Array.Clear(_majOnes, 0, 23);
                        Array.Clear(_majTotal, 0, 23);
                        var words = 0;
                        for (var start = align; start + 23 <= bitCount; start += 23)
                        {
                            words++;
                            for (var i = 0; i < 23; i++)
                            {
                                _majTotal[i]++;
                                if (_bitScratch[start + i] != 0) _majOnes[i]++;
                            }
                        }
                        if (words < 2) continue;

                        uint air = 0;
                        var confident = 0;
                        for (var i = 0; i < 23; i++)
                        {
                            if (_majOnes[i] * 2 >= _majTotal[i]) air |= 1u << i;
                            var agree = Math.Max(_majOnes[i], _majTotal[i] - _majOnes[i]);
                            if (agree * 4 >= _majTotal[i] * 3) confident++;
                        }
                        if (confident < 12) continue;
                        if (!BestUniqueTemplate(air, out var c, out var d) || d > 2) continue;
                        NormalizeInverse(ref c);

                        if (d < globalBestDist
                            || (d == globalBestDist && words > globalBestVotes)
                            || (d == globalBestDist && words == globalBestVotes && c < globalBestCode))
                        {
                            globalBestDist = d;
                            globalBestCode = c;
                            globalBestVotes = words;
                            globalPat = air;
                        }
                    }
                }
            }
        }

        topRawPattern = globalPat;
        bestDist = globalBestDist >= 99 ? 23 : globalBestDist;
        hits = globalBestVotes;
        if (globalBestCode < 0 || globalBestDist > 2 || globalBestVotes < 2) return false;
        code = globalBestCode;
        return true;
    }

    private void CollectMajorityCandidates(int bitCount, int confMin)
    {
        for (var align = 0; align < 23; align++)
        {
            Array.Clear(_majOnes, 0, 23);
            Array.Clear(_majTotal, 0, 23);
            var words = 0;
            for (var start = align; start + 23 <= bitCount; start += 23)
            {
                words++;
                for (var i = 0; i < 23; i++)
                {
                    _majTotal[i]++;
                    if (_bitScratch[start + i] != 0) _majOnes[i]++;
                }
            }
            if (words < 2) continue;

            uint air = 0;
            var confident = 0;
            for (var i = 0; i < 23; i++)
            {
                if (_majOnes[i] * 2 >= _majTotal[i]) air |= 1u << i;
                var agree = Math.Max(_majOnes[i], _majTotal[i] - _majOnes[i]);
                if (agree * 4 >= _majTotal[i] * 3) confident++;
            }
            if (confident < confMin) continue;

            var dup = -1;
            for (var i = 0; i < _candidates.Count; i++)
                if (_candidates[i].Air == air) { dup = i; break; }
            if (dup >= 0)
            {
                if (words > _candidates[dup].Words)
                    _candidates[dup] = (air, confident, words);
            }
            else if (_candidates.Count < 24)
                _candidates.Add((air, confident, words));
        }
    }

    private bool ScoreTopCandidates(bool hamOnly, int take, bool loose,
        out int code, out int hits, out int bestDist, out uint topRawPattern)
    {
        code = -1;
        hits = 0;
        bestDist = 23;
        topRawPattern = 0;
        if (_candidates.Count == 0) return false;

        _candidates.Sort((a, b) =>
        {
            var c = b.Conf.CompareTo(a.Conf);
            return c != 0 ? c : b.Words.CompareTo(a.Words);
        });

        var globalBestDist = 99;
        var globalBestCode = -1;
        var globalBestVotes = 0;
        uint globalPat = 0;
        take = Math.Min(take, _candidates.Count);
        var maxHam = loose ? 3 : 2;

        for (var i = 0; i < take; i++)
        {
            var (air, _, words) = _candidates[i];
            int c, d;
            if (loose)
            {
                if (!BestAnyTemplate(air, maxHam, out c, out d)) continue;
            }
            else if (BestUniqueTemplate(air, out c, out d) && d <= maxHam)
            { }
            else if (!hamOnly && TryGolayRepair(air, out c, out d) && d <= maxHam)
            { }
            else continue;

            NormalizeInverse(ref c);

            if (d < globalBestDist
                || (d == globalBestDist && words > globalBestVotes)
                || (d == globalBestDist && words == globalBestVotes && c < globalBestCode))
            {
                globalBestDist = d;
                globalBestCode = c;
                globalBestVotes = words;
                globalPat = air;
            }
        }

        topRawPattern = globalPat;
        bestDist = globalBestDist >= 99 ? 23 : globalBestDist;
        hits = globalBestVotes;
        if (globalBestCode < 0 || globalBestDist > maxHam) return false;
        if (!loose && globalBestVotes < 2) return false;
        code = globalBestCode;
        return true;
    }

    /// <summary>Closest template within maxHam — no uniqueness check (UI permissive).</summary>
    private static bool BestAnyTemplate(uint air, int maxHam, out int code, out int dist)
    {
        code = -1;
        dist = 99;
        foreach (var t in Templates)
        {
            var d = Hamming23(air, t.Word);
            if (d > maxHam || d > dist) continue;
            if (d < dist || (d == dist && t.Code < code))
            {
                dist = d;
                code = t.Code;
            }
        }
        return code >= 0;
    }

    private static void NormalizeInverse(ref int code)
    {
        if (InversePair.TryGetValue(code, out var pair) && pair < code) code = pair;
    }

    private static bool BestUniqueTemplate(uint air, out int code, out int dist)
    {
        code = -1;
        dist = 99;
        var second = 99;
        foreach (var t in Templates)
        {
            var d = Hamming23(air, t.Word);
            if (d > 2) continue;
            if (d < dist)
            {
                second = dist;
                dist = d;
                code = t.Code;
            }
            else if (t.Code != code && d < second)
                second = d;
            else if (t.Code != code && d == dist)
                second = d;
        }
        return code >= 0 && dist <= 2 && second > dist;
    }

    /// <summary>Offline-only Golay ≤2-bit repair.</summary>
    private static bool TryGolayRepair(uint air, out int code, out int dist)
    {
        code = -1;
        dist = 99;
        var w = air & 0x7FFFFF;
        if (DecodeBySignature(w, out code)) { dist = 0; return true; }

        for (var b1 = 0; b1 < 23; b1++)
        {
            var f1 = w ^ (1u << b1);
            if (DecodeBySignature(f1, out code)) { dist = 1; return true; }
            for (var b2 = b1 + 1; b2 < 23; b2++)
            {
                if (DecodeBySignature(f1 ^ (1u << b2), out code)) { dist = 2; return true; }
            }
        }
        return false;
    }

    private static bool DecodeBySignature(uint word23, out int code)
    {
        code = -1;
        if (DecodeBySignatureOne(word23, out code)) return true;
        if (DecodeBySignatureOne(BitReverse23(word23), out code)) return true;
        return false;
    }

    private static bool DecodeBySignatureOne(uint word23, out int code)
    {
        code = -1;
        var w = word23 & 0x7FFFFF;
        for (var rot = 0; rot < 23; rot++)
        {
            var data = w & 0xFFF;
            var parity = (w >> 12) & 0x7FF;
            if ((data & 0xE00) == 0x800 && GolayParity(data) == parity)
            {
                code = OctalFromData9(data & 0x1FF);
                if (IsStandardCode(code)) return true;
            }

            data = (w >> 11) & 0xFFF;
            parity = w & 0x7FF;
            if ((data & 0xE00) == 0x800 && GolayParity(data) == parity)
            {
                code = OctalFromData9(data & 0x1FF);
                if (IsStandardCode(code)) return true;
            }

            w = ((w << 1) | (w >> 22)) & 0x7FFFFF;
        }
        return false;
    }

    private static int OctalFromData9(uint code9)
    {
        var d0 = (int)((code9 >> 6) & 7);
        var d1 = (int)((code9 >> 3) & 7);
        var d2 = (int)(code9 & 7);
        return d0 * 100 + d1 * 10 + d2;
    }

    private static bool IsStandardCode(int code)
    {
        foreach (var c in StandardCodes) if (c == code) return true;
        return false;
    }

    private int SliceBits(ReadOnlySpan<float> block, int phase, float spb, float gain, bool invertBits)
    {
        var bitCount = 0;
        for (float cursor = phase; cursor + spb <= block.Length && bitCount < MaxBitScratch; cursor += spb)
        {
            var i0 = (int)cursor;
            var i1 = Math.Min(block.Length, (int)(cursor + spb));
            var mid0 = i0 + (i1 - i0) / 4;
            var mid1 = i1 - (i1 - i0) / 4;
            if (mid1 <= mid0) { mid0 = i0; mid1 = i1; }
            double sum = 0;
            for (var i = mid0; i < mid1; i++) sum += block[i] * gain;
            var one = sum >= 0;
            if (invertBits) one = !one;
            _bitScratch[bitCount++] = one ? (byte)1 : (byte)0;
        }
        return bitCount;
    }

    private static int Hamming23(uint a, uint b)
    {
        var x = (a ^ b) & 0x7FFFFFu;
        x = x - ((x >> 1) & 0x55555555u);
        x = (x & 0x33333333u) + ((x >> 2) & 0x33333333u);
        return (int)((((x + (x >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
    }

    private void Register(int code, bool inverted, float corr, int dist)
    {
        if (code < 0)
        {
            _missHits++;
            // Hold last shown code ~8–10 s so a brief 023/466 does not vanish instantly.
            if (_missHits < 40) return;
            _detectedCode = -1;
            _candidate = -1;
            _stableHits = 0;
            _missHits = 0;
            return;
        }

        _missHits = 0;

        if (_detectedCode >= 0)
        {
            // Sticky: keep first lock; only refresh confidence if same/inverse.
            if (code == _detectedCode
                || (InversePair.TryGetValue(_detectedCode, out var detPair) && code == detPair))
            {
                _stableHits++;
                _debugCorr = corr;
                _debugBestDist = dist;
            }
            return;
        }

        // UI permissive: Ham ≤3 locks immediately. Offline: Ham ≤2, need 2 hits.
        if (dist > (_deepSearch ? 2 : 3)) return;

        var same = code == _candidate;
        if (!same && _candidate >= 0 && InversePair.TryGetValue(_candidate, out var pair) && code == pair)
            same = true;

        if (same) _stableHits++;
        else
        {
            _candidate = code;
            _inverted = false;
            _stableHits = 1;
        }

        var need = _deepSearch ? 2 : 1;
        if (_stableHits >= need)
        {
            _detectedCode = _candidate;
            _debugCorr = corr;
            _debugBestDist = dist;
        }
    }

    internal static IEnumerable<uint> EnumerateCodewords(int octalCode)
    {
        var d0 = (octalCode / 100) % 10;
        var d1 = (octalCode / 10) % 10;
        var d2 = octalCode % 10;
        var code9 = (uint)((d0 << 6) | (d1 << 3) | d2);
        var data12 = 0x800u | (code9 & 0x1FFu);
        var parity = GolayParity(data12);
        var dataParity = (data12 << 11) | (parity & 0x7FFu);
        var parityData = (parity << 12) | data12;
        yield return BitReverse23(dataParity);
        yield return dataParity & 0x7FFFFF;
        yield return BitReverse23(parityData);
        yield return parityData & 0x7FFFFF;
    }

    internal static uint BuildCodeword(int octalCode) => EnumerateCodewords(octalCode).First();

    private static uint GolayParity(uint data12)
    {
        const uint poly = 0xC75;
        uint code = (data12 & 0xFFF) << 11;
        for (var i = 22; i >= 11; i--)
        {
            if (((code >> i) & 1) != 0)
                code ^= poly << (i - 11);
        }
        return code & 0x7FF;
    }

    private static uint BitReverse23(uint value)
    {
        uint rev = 0;
        for (var i = 0; i < 23; i++)
        {
            rev <<= 1;
            rev |= (value >> i) & 1;
        }
        return rev;
    }

    private readonly struct Template
    {
        public readonly uint Word;
        public readonly int Code;
        public Template(uint word, int code) { Word = word; Code = code; }
    }

    private static Template[] BuildTemplates()
    {
        var list = new List<Template>(StandardCodes.Length * 4);
        foreach (var code in StandardCodes)
        {
            foreach (var w in EnumerateCodewords(code))
                list.Add(new Template(w, code));
        }
        return list.ToArray();
    }

    private static Dictionary<int, int> BuildInversePairs()
    {
        int[][] pairs =
        [
            [023, 047], [025, 244], [026, 464], [031, 627], [032, 051], [036, 172],
            [043, 445], [047, 023], [051, 032], [053, 452], [054, 413], [065, 271],
            [071, 306], [072, 245], [073, 506], [074, 174], [114, 712], [115, 152],
            [116, 754], [122, 225], [125, 365], [131, 364], [132, 546], [134, 223],
            [143, 412], [145, 274], [152, 115], [155, 731], [156, 265], [162, 503],
            [165, 251], [172, 036], [174, 074], [205, 263], [212, 356], [223, 134],
            [225, 122], [226, 411], [243, 351], [244, 025], [245, 072], [246, 523],
            [251, 165], [252, 462], [255, 446], [261, 732], [263, 205], [265, 156],
            [266, 454], [271, 065], [274, 145], [306, 071], [311, 664], [315, 423],
            [325, 526], [331, 465], [332, 455], [343, 532], [346, 612], [351, 243],
            [356, 212], [364, 131], [365, 125], [371, 734], [411, 226], [412, 143],
            [413, 054], [423, 315], [431, 723], [432, 516], [445, 043], [446, 255],
            [452, 053], [454, 266], [455, 332], [462, 252], [464, 026], [465, 331],
            [466, 662], [503, 162], [506, 073], [516, 432], [523, 246], [526, 325],
            [532, 343], [546, 132], [565, 703], [606, 631], [612, 346], [624, 632],
            [627, 031], [631, 606], [632, 624], [654, 743], [662, 466], [664, 311],
            [703, 565], [712, 114], [723, 431], [731, 155], [732, 261], [734, 371],
            [743, 654], [754, 116]
        ];
        var map = new Dictionary<int, int>();
        foreach (var p in pairs) map[p[0]] = p[1];
        return map;
    }

    internal static bool SelfCheck023()
    {
        const uint expectedMsb = 0b10000001001111101100011u;
        if (BuildCodeword(023) != BitReverse23(expectedMsb) || GolayParity(0x813) != 0x763)
            return false;
        if (!Templates.Any(t => t.Code == 023)) return false;

        var rate = AudioDemodulator.AudioSampleRate;
        var word = BuildCodeword(023);
        var spb = rate / Baud;
        var samples = new float[(int)(spb * 23 * 20)];
        for (var i = 0; i < samples.Length; i++)
        {
            var bit = (int)(i / spb) % 23;
            samples[i] = ((word >> bit) & 1) != 0 ? 0.08f : -0.08f;
        }
        var dec = new DcsToneDecoder { DeepSearch = true };
        var mid = samples.Length / 2;
        dec.Process(samples.AsSpan(0, mid), rate);
        dec.Process(samples.AsSpan(mid), rate);
        if (dec.DetectedCode == 023) return true;

        dec.Reset();
        for (var i = 0; i < samples.Length; i++) samples[i] = -samples[i];
        dec.Process(samples.AsSpan(0, mid), rate);
        dec.Process(samples.AsSpan(mid), rate);
        return dec.DetectedCode == 023;
    }
}
