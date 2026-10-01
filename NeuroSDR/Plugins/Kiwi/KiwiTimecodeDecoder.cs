using System.Globalization;
using NeuroSDR.Core;

namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// Core of KiwiSDR <c>extensions/timecode/timecode.cpp</c>: PLL carrier recovery + moving-average amplitude.
/// Adds a simple amplitude-dip second-tick detector suitable for WWV/WWVH-style carriers.
/// Full station protocol decode (WWVB/DCF77/…) remains in the browser JS on Kiwi; this sample
/// exposes PLL metrics, scope bytes, and tick events for host UI / further plugins.
/// </summary>
internal sealed class KiwiTimecodeDecoder
{
    private readonly KiwiPll _pll = new();
    private float _fs = 12_000f;
    private int _exponent = 1;
    private float _pllBandwidth = 10f;
    private float _pllOffset;
    private float _gain; // 0 = auto scale from AMA
    private int _displayMode; // 0 = mix recovered carrier, 1 = carrier only
    private bool _pllEnabled = true;
    private float _ama;
    private int _maN;
    private int _nsamp;
    private int _maNsend;
    private float _envEma;
    private float _envBaseline = 1f;
    private bool _inDip;
    private int _dipSamples;
    private int _sinceTickSamples = int.MaxValue / 4;
    private readonly List<byte> _scopeScratch = new(4096);

    public float DfHz => _pllEnabled && _exponent != 0 ? _pll.Df / (2f * MathF.PI * _exponent) : 0f;
    public float Phase => _pll.Phase;
    public float Ama => _ama;
    public string Status { get; private set; } = "idle";

    public event Action<string>? StatusChanged;
    public event Action<string, IReadOnlyDictionary<string, string>>? TickDetected;
    public event Action<byte[]>? ScopeReady;

    public void Configure(float sampleRateHz, bool pllEnabled, int exponent, float bandwidthHz, float offsetHz,
        float gainLinear, int displayMode)
    {
        _fs = Math.Clamp(sampleRateHz, 1_000f, 2_500_000f);
        _pllEnabled = pllEnabled;
        _exponent = Math.Clamp(exponent is 1 or 2 or 4 or 8 ? exponent : 1, 1, 8);
        _pllBandwidth = Math.Clamp(bandwidthHz, 0.1f, 200f);
        _pllOffset = offsetHz;
        _gain = Math.Max(0f, gainLinear);
        _displayMode = displayMode == 0 ? 0 : 1;
        _maNsend = Math.Max(1, (int)(_fs / 4));
        _pll.Init(_pllBandwidth, _pllOffset, _fs);
        Status = $"PLL {(_pllEnabled ? "on" : "off")} · exp {_exponent} · BW {_pllBandwidth:0.#} Hz";
        StatusChanged?.Invoke(Status);
    }

    public void Reset()
    {
        _pll.Reset();
        _ama = 0;
        _maN = 0;
        _nsamp = 0;
        _envEma = 0;
        _envBaseline = 1f;
        _inDip = false;
        _dipSamples = 0;
        _sinceTickSamples = int.MaxValue / 4;
        _scopeScratch.Clear();
        Status = "reset";
        StatusChanged?.Invoke(Status);
    }

    public Complex32 ProcessSample(Complex32 sample)
    {
        float i = sample.I, q = sample.Q;
        if (_pllEnabled && _exponent != 0)
        {
            // pow(sample, exponent) for N-PSK / carrier recovery
            var (pi, pq) = PowComplex(i, q, _exponent);
            var phase = _pll.Update(pi, pq);
            var carrierPhase = -phase / _exponent;
            var c = MathF.Cos(carrierPhase);
            var s = MathF.Sin(carrierPhase);
            if (_displayMode == 0)
            {
                // mix recovered carrier onto sample
                var ni = i * c - q * s;
                var nq = i * s + q * c;
                i = ni;
                q = nq;
            }
            else
            {
                i = c;
                q = s;
            }
        }

        var mag = MathF.Sqrt(i * i + q * q);
        _ama = (_maN * _ama + mag) / (_maN + 1);
        if (_maN < (int)(0.5f * _fs)) _maN++;

        DetectTick(mag);
        return new Complex32(i, q);
    }

    public void ProcessBlock(ReadOnlySpan<Complex32> input, Span<Complex32> output, bool emitScope)
    {
        _scopeScratch.Clear();
        if (_scopeScratch.Capacity < input.Length) _scopeScratch.Capacity = input.Length;
        for (var index = 0; index < input.Length; index++)
        {
            var processed = ProcessSample(input[index]);
            output[index] = processed;
            if (emitScope) _scopeScratch.Add(ToU1(processed));
        }

        _nsamp += input.Length;
        if (_nsamp > _maNsend)
        {
            Status = string.Create(CultureInfo.InvariantCulture,
                $"df={DfHz:E3} Hz  phase={Phase:F3}  ama={_ama:F4}");
            StatusChanged?.Invoke(Status);
            _nsamp -= _maNsend;
        }

        if (emitScope && _scopeScratch.Count > 0)
            ScopeReady?.Invoke(_scopeScratch.ToArray());
    }

    private void DetectTick(float mag)
    {
        // Slow envelope baseline vs fast EMA — dips below ~55% of baseline look like WWV second markers.
        _envEma = 0.02f * mag + 0.98f * _envEma;
        if (!_inDip)
            _envBaseline = 0.001f * mag + 0.999f * _envBaseline;
        _sinceTickSamples++;

        var threshold = _envBaseline * 0.55f;
        if (!_inDip && _envEma < threshold && _envBaseline > 1e-6f)
        {
            _inDip = true;
            _dipSamples = 1;
        }
        else if (_inDip)
        {
            _dipSamples++;
            if (_envEma >= threshold)
            {
                var dipMs = 1000f * _dipSamples / _fs;
                // Accept ~80–900 ms dips, debounce ~0.7 s between ticks
                if (dipMs is >= 80f and <= 900f && _sinceTickSamples > (int)(0.7f * _fs))
                {
                    _sinceTickSamples = 0;
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dipMs"] = dipMs.ToString("F0", CultureInfo.InvariantCulture),
                        ["dfHz"] = DfHz.ToString("E3", CultureInfo.InvariantCulture),
                        ["ama"] = _ama.ToString("F4", CultureInfo.InvariantCulture)
                    };
                    TickDetected?.Invoke($"tick {dipMs:F0} ms", fields);
                }
                _inDip = false;
                _dipSamples = 0;
            }
            else if (_dipSamples > (int)(1.2f * _fs))
            {
                _inDip = false;
                _dipSamples = 0;
            }
        }
    }

    private byte ToU1(Complex32 sample)
    {
        const float cuteMax = 32767f;
        var scale = 255f * (_gain > 0 ? _gain / cuteMax : 1f / (2f * Math.Max(_ama, 1e-6f)));
        var v = 127f + sample.I * scale;
        return (byte)Math.Clamp((int)v, 0, 255);
    }

    private static (float I, float Q) PowComplex(float i, float q, int exp)
    {
        float ri = i, rq = q;
        for (var n = 1; n < exp; n++)
        {
            var ni = ri * i - rq * q;
            var nq = ri * q + rq * i;
            ri = ni;
            rq = nq;
        }
        return (ri, rq);
    }
}
