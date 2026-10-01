namespace NeuroSDR.Plugins.Fldigi;

/// <summary>
/// Windowed-sinc resampler in the same role as fldigi's libsamplerate path
/// (modem rate 8000 Hz). Integer 6:1 (48 kHz → 8 kHz) uses anti-alias FIR then decimate.
/// </summary>
internal sealed class FlSincResampler
{
    public const int ModemRate = 8_000;
    private readonly int _inRate;
    private readonly int _ratio;
    private readonly double[] _taps;
    private readonly double[] _hist;
    private int _histFill;
    private int _skip;
    private double _phase;
    private readonly bool _integer;

    public FlSincResampler(int inputRate)
    {
        _inRate = Math.Max(1, inputRate);
        _integer = _inRate % ModemRate == 0;
        _ratio = _integer ? _inRate / ModemRate : 0;
        var cutoff = Math.Min(0.45 * ModemRate, 0.45 * _inRate);
        var taps = _integer ? 97 : 65;
        _taps = DesignLowpass(taps, cutoff / _inRate);
        _hist = new double[_taps.Length];
    }

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty) return [];
        if (_inRate == ModemRate) return input.ToArray();
        var list = new List<float>(input.Length / Math.Max(1, _ratio) + 8);
        foreach (var sample in input)
        {
            Array.Copy(_hist, 1, _hist, 0, _hist.Length - 1);
            _hist[^1] = sample;
            if (_histFill < _hist.Length) { _histFill++; continue; }

            if (_integer)
            {
                if (++_skip < _ratio) continue;
                _skip = 0;
                list.Add((float)Dot(_hist, _taps));
            }
            else
            {
                _phase += ModemRate;
                while (_phase >= _inRate)
                {
                    _phase -= _inRate;
                    list.Add((float)Dot(_hist, _taps));
                }
            }
        }
        return list.ToArray();
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static double[] DesignLowpass(int length, double fc)
    {
        var h = new double[length];
        var m = (length - 1) / 2.0;
        var sum = 0.0;
        for (var i = 0; i < length; i++)
        {
            var x = i - m;
            var sinc = Math.Abs(x) < 1e-9 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            var w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (length - 1));
            h[i] = sinc * w;
            sum += h[i];
        }
        if (Math.Abs(sum) > 1e-12)
            for (var i = 0; i < length; i++) h[i] /= sum;
        return h;
    }
}
