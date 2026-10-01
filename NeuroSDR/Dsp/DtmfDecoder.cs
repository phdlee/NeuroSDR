namespace NeuroSDR.Dsp;

/// <summary>
/// DTMF decoder (Goertzel). Common for PTT-ID / ANI on consumer and commercial radios.
/// </summary>
internal sealed class DtmfDecoder
{
    private static readonly float[] LowHz = [697, 770, 852, 941];
    private static readonly float[] HighHz = [1209, 1336, 1477, 1633];
    private static readonly char[,] Keys =
    {
        { '1', '2', '3', 'A' },
        { '4', '5', '6', 'B' },
        { '7', '8', '9', 'C' },
        { '*', '0', '#', 'D' }
    };

    private readonly object _sync = new();
    private readonly float[] _buf = new float[AudioDemodulator.AudioSampleRate / 20]; // 50 ms
    private int _count;
    private int _sampleRate = AudioDemodulator.AudioSampleRate;
    private char _candidate;
    private int _hits;
    private string _digits = "";
    private long _lastDigitTick;
    private string _lastBurst = "";
    private long _burstHoldUntil;

    public string LastBurst
    {
        get
        {
            lock (_sync)
                return Environment.TickCount64 < _burstHoldUntil ? _lastBurst : "";
        }
    }

    public string DetectedLabel
    {
        get
        {
            var b = LastBurst;
            return b.Length > 0 ? "DTMF " + b : "";
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _count = 0;
            _candidate = '\0';
            _hits = 0;
            _digits = "";
            _lastBurst = "";
            _burstHoldUntil = 0;
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
                _count = 0;
            }

            foreach (var s in mono)
            {
                if (_count < _buf.Length) _buf[_count++] = s;
                if (_count < _buf.Length) continue;
                AnalyzeBlock();
                _count = 0;
            }
        }
    }

    private void AnalyzeBlock()
    {
        var n = _buf.Length;
        var lowMags = new double[4];
        var highMags = new double[4];
        for (var i = 0; i < 4; i++)
        {
            lowMags[i] = Goertzel(_buf, n, LowHz[i], _sampleRate);
            highMags[i] = Goertzel(_buf, n, HighHz[i], _sampleRate);
        }

        var li = ArgMax(lowMags);
        var hi = ArgMax(highMags);
        var low = lowMags[li];
        var high = highMags[hi];
        var secondLow = SecondMax(lowMags, li);
        var secondHigh = SecondMax(highMags, hi);
        var energy = 0.0;
        for (var i = 0; i < n; i++) energy += _buf[i] * _buf[i];
        var rms = Math.Sqrt(energy / n);
        if (rms < 1e-4 || low < 0.02 || high < 0.02) { TwistMiss(); return; }
        if (low < secondLow * 1.5 || high < secondHigh * 1.5) { TwistMiss(); return; }
        // Twist: low/high within ~8 dB
        var ratio = low / Math.Max(1e-12, high);
        if (ratio < 0.4 || ratio > 2.5) { TwistMiss(); return; }

        var key = Keys[li, hi];
        if (key == _candidate) _hits++;
        else { _candidate = key; _hits = 1; }
        if (_hits < 2) return;

        var now = Environment.TickCount64;
        if (now - _lastDigitTick < 80 && _digits.Length > 0 && _digits[^1] == key) return;
        if (now - _lastDigitTick > 700) _digits = "";
        _digits += key;
        _lastDigitTick = now;
        if (_digits.Length > 16) _digits = _digits[^16..];
        _lastBurst = _digits;
        _burstHoldUntil = now + 4_000;
    }

    private void TwistMiss()
    {
        _candidate = '\0';
        _hits = 0;
        if (_digits.Length > 0 && Environment.TickCount64 - _lastDigitTick > 900)
        {
            _lastBurst = _digits;
            _burstHoldUntil = Environment.TickCount64 + 4_000;
            _digits = "";
        }
    }

    private static int ArgMax(double[] v)
    {
        var i = 0;
        for (var n = 1; n < v.Length; n++) if (v[n] > v[i]) i = n;
        return i;
    }

    private static double SecondMax(double[] v, int skip)
    {
        var best = 0.0;
        for (var i = 0; i < v.Length; i++)
            if (i != skip && v[i] > best) best = v[i];
        return best;
    }

    private static double Goertzel(float[] x, int n, float freq, int rate)
    {
        var w = 2 * Math.PI * freq / rate;
        var coeff = 2 * Math.Cos(w);
        double q0 = 0, q1 = 0, q2 = 0;
        for (var i = 0; i < n; i++)
        {
            q0 = coeff * q1 - q2 + x[i];
            q2 = q1;
            q1 = q0;
        }
        return Math.Sqrt(q1 * q1 + q2 * q2 - q1 * q2 * coeff) / n;
    }
}
