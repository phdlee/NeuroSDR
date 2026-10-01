namespace NeuroSDR.Dsp;

/// <summary>10-band graphic EQ (peaking biquads) for WFM music listening.</summary>
internal sealed class GraphicEqualizer
{
    public static readonly int[] BandHz = [32, 64, 125, 250, 500, 1_000, 2_000, 4_000, 8_000, 16_000];
    private readonly PeakingBand[] _left;
    private readonly PeakingBand[] _right;
    private readonly float[] _gainsDb;
    private readonly object _sync = new();

    public GraphicEqualizer(int sampleRate = AudioDemodulator.AudioSampleRate)
    {
        _gainsDb = new float[BandHz.Length];
        _left = new PeakingBand[BandHz.Length];
        _right = new PeakingBand[BandHz.Length];
        for (var i = 0; i < BandHz.Length; i++)
        {
            _left[i] = new PeakingBand();
            _right[i] = new PeakingBand();
            _left[i].Configure(BandHz[i], 0, sampleRate);
            _right[i].Configure(BandHz[i], 0, sampleRate);
        }
    }

    public int BandCount => BandHz.Length;

    public float[] GetGainsDb()
    {
        lock (_sync) return (float[])_gainsDb.Clone();
    }

    public void SetGainsDb(IReadOnlyList<float> gains)
    {
        lock (_sync)
        {
            for (var i = 0; i < _gainsDb.Length; i++)
            {
                var gain = i < gains.Count ? Math.Clamp(gains[i], -12f, 12f) : 0f;
                if (Math.Abs(gain - _gainsDb[i]) < 0.01f) continue;
                _gainsDb[i] = gain;
                _left[i].Configure(BandHz[i], gain, AudioDemodulator.AudioSampleRate);
                _right[i].Configure(BandHz[i], gain, AudioDemodulator.AudioSampleRate);
            }
        }
    }

    public void ProcessMono(Span<float> samples)
    {
        lock (_sync)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var v = samples[i];
                for (var b = 0; b < _left.Length; b++)
                    v = _left[b].Process(v);
                samples[i] = Math.Clamp(v, -1f, 1f);
            }
        }
    }

    public void ProcessStereoInterleaved(Span<float> interleaved)
    {
        lock (_sync)
        {
            for (var i = 0; i + 1 < interleaved.Length; i += 2)
            {
                var l = interleaved[i];
                var r = interleaved[i + 1];
                for (var b = 0; b < _left.Length; b++)
                {
                    l = _left[b].Process(l);
                    r = _right[b].Process(r);
                }
                interleaved[i] = Math.Clamp(l, -1f, 1f);
                interleaved[i + 1] = Math.Clamp(r, -1f, 1f);
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            foreach (var band in _left) band.Reset();
            foreach (var band in _right) band.Reset();
        }
    }

    private sealed class PeakingBand
    {
        private float _b0 = 1, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public void Configure(int frequencyHz, float gainDb, int sampleRate)
        {
            if (Math.Abs(gainDb) < 0.05f)
            {
                _b0 = 1; _b1 = _b2 = _a1 = _a2 = 0;
                return;
            }
            var a = MathF.Pow(10f, gainDb / 40f);
            var w0 = 2 * MathF.PI * frequencyHz / sampleRate;
            var cos = MathF.Cos(w0);
            var sin = MathF.Sin(w0);
            var q = 1.4f;
            var alpha = sin / (2 * q);
            var b0 = 1 + alpha * a;
            var b1 = -2 * cos;
            var b2 = 1 - alpha * a;
            var a0 = 1 + alpha / a;
            var a1 = -2 * cos;
            var a2 = 1 - alpha / a;
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public float Process(float x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;
    }
}
