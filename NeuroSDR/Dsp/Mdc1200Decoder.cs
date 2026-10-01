namespace NeuroSDR.Dsp;

/// <summary>
/// Lightweight MDC-1200 (Motorola PTT-ID / ANI) detector — 1200 baud FFSK 1200/1800 Hz.
/// Decodes unit ID when a sync + repeated data word is found. Experimental.
/// </summary>
internal sealed class Mdc1200Decoder
{
    private const int Baud = 1_200;
    private const int DecimatedRate = 4_800; // 4 samples/bit

    private readonly object _sync = new();
    private readonly float[] _dec = new float[DecimatedRate];
    private float _lpY, _lpA;
    private float _decPhase;
    private int _decCount;
    private int _sampleRate = AudioDemodulator.AudioSampleRate;
    private int _filterRate = -1;
    private int _decimation = 1;
    private string _lastId = "";
    private long _holdUntil;
    private float _debugCorr;

    public string LastId
    {
        get
        {
            lock (_sync)
                return Environment.TickCount64 < _holdUntil ? _lastId : "";
        }
    }

    public string DetectedLabel
    {
        get
        {
            var id = LastId;
            return id.Length > 0 ? "MDC " + id : "";
        }
    }

    public float DebugCorr { get { lock (_sync) return _debugCorr; } }

    public void Reset()
    {
        lock (_sync)
        {
            _decCount = 0;
            _decPhase = 0;
            _lastId = "";
            _holdUntil = 0;
            _lpY = 0;
            _filterRate = -1;
        }
    }

    public void Process(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (mono.Length == 0) return;
        lock (_sync)
        {
            if (sampleRate != _sampleRate)
            {
                _sampleRate = sampleRate;
                _filterRate = -1;
                _decCount = 0;
            }
            EnsureFilter();
            foreach (var raw in mono)
            {
                _lpY += _lpA * (raw - _lpY);
                _decPhase += 1f;
                if (_decPhase < _decimation) continue;
                _decPhase -= _decimation;
                if (_decCount < _dec.Length) _dec[_decCount++] = _lpY;
            }
            if (_decCount < Baud) return; // need ≥1s of bits at 4 samp/bit ≈ 0.25s min; use half buffer
            if (_decCount < DecimatedRate / 2) return;
            TryDecode(_dec.AsSpan(0, _decCount));
            var keep = Math.Min(DecimatedRate / 4, _decCount);
            Array.Copy(_dec, _decCount - keep, _dec, 0, keep);
            _decCount = keep;
        }
    }

    private void EnsureFilter()
    {
        if (_filterRate == _sampleRate) return;
        _filterRate = _sampleRate;
        _lpA = 1f - MathF.Exp(-2 * MathF.PI * 2_400f / _sampleRate);
        _decimation = Math.Max(1, _sampleRate / DecimatedRate);
        _lpY = 0;
    }

    private void TryDecode(ReadOnlySpan<float> block)
    {
        // Quadrature discriminators for 1200 / 1800 Hz at decimated rate → soft bit.
        var spb = DecimatedRate / Baud; // 4
        if (block.Length < spb * 64) return;

        Span<byte> bits = stackalloc byte[256];
        var bitCount = Math.Min(bits.Length, (block.Length - spb) / spb);
        for (var b = 0; b < bitCount; b++)
        {
            double e1200 = 0, e1800 = 0;
            var i0 = b * spb;
            for (var i = 0; i < spb; i++)
            {
                var t = (i0 + i) / (double)DecimatedRate;
                var s = block[i0 + i];
                e1200 += s * Math.Cos(2 * Math.PI * 1200 * t);
                e1800 += s * Math.Cos(2 * Math.PI * 1800 * t);
            }
            bits[b] = Math.Abs(e1200) >= Math.Abs(e1800) ? (byte)0 : (byte)1; // mark=1200 → 0 convention
        }

        // Search for typical MDC preamble-ish runs then extract 32-bit words.
        for (var start = 0; start + 64 <= bitCount; start++)
        {
            // Look for alternating or long sync-ish pattern (many transitions).
            var transitions = 0;
            for (var i = start + 1; i < start + 24 && i < bitCount; i++)
                if (bits[i] != bits[i - 1]) transitions++;
            if (transitions < 6) continue;

            if (!TryWords(bits.Slice(start, Math.Min(96, bitCount - start)), out var id, out var corr))
                continue;
            if (corr < 0.6f) continue;
            _lastId = id;
            _holdUntil = Environment.TickCount64 + 6_000;
            _debugCorr = corr;
            return;
        }
    }

    private static bool TryWords(ReadOnlySpan<byte> bits, out string id, out float corr)
    {
        id = "";
        corr = 0;
        if (bits.Length < 64) return false;
        // Pack first two 32-bit words; MDC often repeats the data word.
        uint w0 = 0, w1 = 0;
        for (var i = 0; i < 32; i++)
        {
            if (bits[i] != 0) w0 |= 1u << (31 - i);
            if (bits[i + 32] != 0) w1 |= 1u << (31 - i);
        }
        // Prefer matching repeated words (Hamming ≤ 4).
        var xor = w0 ^ w1;
        var ham = 0;
        for (var t = xor; t != 0; t >>= 1) ham += (int)(t & 1);
        if (ham > 6) return false;
        var word = w0; // use first
        // Unit ID is typically low 16 bits of opcode/data layout — show as 4 hex digits.
        var unit = (word >> 8) & 0xFFFF;
        if (unit == 0 || unit == 0xFFFF) unit = word & 0xFFFF;
        if (unit == 0 || unit == 0xFFFF) return false;
        id = unit.ToString("X4");
        corr = 1f - ham / 32f;
        return true;
    }
}
