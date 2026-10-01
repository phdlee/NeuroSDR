namespace NeuroSDR.Plugins.Kiwi;

/// <summary>Christoph's carrier PLL from KiwiSDR <c>rx/kiwi/pll.h</c>.</summary>
internal sealed class KiwiPll
{
    private float _fs = 12_000f;
    private float _fc;
    private float _df;
    private float _phase;
    private float _b0;
    private float _b1;
    private float _ud;

    public float Phase => _phase;
    public float Df => _df;

    public void Init(float bandwidthHz, float offsetHz, float sampleRateHz, float damping = 0.70710678f)
    {
        var wn = MathF.PI * bandwidthHz / damping;
        var tau0 = 1f / (wn * wn);
        var tau1 = 2f * damping / wn;
        _fs = sampleRateHz;
        var ts2 = 0.5f / _fs;
        _b0 = ts2 / tau0 * (1f + 1f / MathF.Tan(ts2 / tau1));
        _b1 = ts2 / tau0 * (1f - 1f / MathF.Tan(ts2 / tau1));
        _phase = _ud = _df = 0;
        _fc = offsetHz;
    }

    public float Update(float i, float q)
    {
        _phase = MathF.IEEERemainder(_phase + (2f * MathF.PI * _fc + _df) / _fs, 16f * MathF.PI);
        var udOld = _ud;
        // arg(sample * exp(-j phase))
        var c = MathF.Cos(_phase);
        var s = MathF.Sin(_phase);
        var re = i * c + q * s;
        var im = q * c - i * s;
        _ud = MathF.Atan2(im, re);
        _df += _b0 * _ud + _b1 * udOld;
        return _phase;
    }

    public void Reset()
    {
        _phase = _ud = _df = 0;
    }
}
