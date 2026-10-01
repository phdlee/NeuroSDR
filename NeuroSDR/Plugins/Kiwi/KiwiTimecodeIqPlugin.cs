using System.Globalization;
using NeuroSDR.Core;

namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// Sample IQ plug-in: KiwiSDR Timecode PLL / envelope path (<c>builtin.iq.kiwitimecode</c>).
/// Demonstrates IQ IN (always) and optional IQ OUT when <c>replaceIq=true</c>.
/// </summary>
internal sealed class KiwiTimecodeIqPlugin : IIqPlugin, IConditionalIqOutputPlugin
{
    private readonly object _sync = new();
    private readonly KiwiTimecodeDecoder _decoder = new();
    private bool _enabled;
    private bool _pllEnabled = true;
    private int _exponent = 1;
    private float _bandwidthHz = 10f;
    private float _offsetHz;
    private float _gainLinear; // 0 = auto
    private int _displayMode;
    // Read on the RF dispatch thread while Process() runs under _sync on a worker.
    // Locking this property would stall real-time IQ delivery for the full decoder pass.
    private volatile bool _replaceIq;
    private bool _emitScope = true;
    private int _configuredRate = -1;
    private long _nextScopeTick;

    public KiwiTimecodeIqPlugin()
    {
        _decoder.StatusChanged += status =>
        {
            if (_enabled)
                ResultAvailable?.Invoke(new IqPluginResult(Info.Id, "STATUS", status, DateTime.UtcNow,
                    Fields: new Dictionary<string, string>
                    {
                        ["dfHz"] = _decoder.DfHz.ToString("E3", CultureInfo.InvariantCulture),
                        ["phase"] = _decoder.Phase.ToString("F3", CultureInfo.InvariantCulture),
                        ["ama"] = _decoder.Ama.ToString("F4", CultureInfo.InvariantCulture)
                    }));
        };
        _decoder.TickDetected += (text, fields) =>
        {
            if (_enabled)
                ResultAvailable?.Invoke(new IqPluginResult(Info.Id, "TICK", text, DateTime.UtcNow, Fields: fields));
        };
        _decoder.ScopeReady += bytes =>
        {
            if (_enabled)
                ResultAvailable?.Invoke(new IqPluginResult(Info.Id, "SCOPE", $"{bytes.Length} samples", DateTime.UtcNow,
                    BinaryData: bytes));
        };
    }

    public IqPluginInfo Info { get; } = new(
        "builtin.iq.kiwitimecode",
        "KiwiTIMECODE",
        "Timecode carrier PLL + amplitude ticks (KiwiSDR timecode IQ path)",
        IqPluginCapabilities.IqInput | IqPluginCapabilities.IqOutput | IqPluginCapabilities.Display | IqPluginCapabilities.HostResults);

    public event Action<IqPluginResult>? ResultAvailable;
    public bool ProducesIqOutput => _replaceIq;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _pllEnabled = Bool(options, "pll", _pllEnabled);
            _exponent = (int)Double(options, "exponent", _exponent, 1, 8);
            if (_exponent is not (1 or 2 or 4 or 8)) _exponent = 1;
            _bandwidthHz = (float)Double(options, "bandwidth", _bandwidthHz, 0.1, 200);
            _offsetHz = (float)Double(options, "offset", _offsetHz, -50_000, 50_000);
            _displayMode = (int)Double(options, "displayMode", _displayMode, 0, 1);
            _replaceIq = Bool(options, "replaceIq", _replaceIq);
            _emitScope = Bool(options, "emitScope", _emitScope);

            // gain: UI 0..100 maps to linear like Kiwi (0 = auto)
            if (options.TryGetValue("gain", out var gainText) &&
                double.TryParse(gainText, NumberStyles.Float, CultureInfo.InvariantCulture, out var gainUi))
            {
                gainUi = Math.Clamp(gainUi, 0, 100);
                _gainLinear = gainUi <= 0
                    ? 0
                    : (float)Math.Pow(10.0, (gainUi - 50.0) / 10.0);
            }

            if (_configuredRate > 0)
                _decoder.Configure(_configuredRate, _pllEnabled, _exponent, _bandwidthHz, _offsetHz, _gainLinear, _displayMode);

            var command = Text(options, "command", "");
            if (command.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("clear", StringComparison.OrdinalIgnoreCase))
                _decoder.Reset();
        }
    }

    public IqPluginResult? Process(IqSampleBlock block)
    {
        lock (_sync)
        {
            if (!_enabled || block.Samples.Length == 0) return null;
            if (_configuredRate != block.SampleRate)
            {
                _configuredRate = block.SampleRate;
                _decoder.Configure(_configuredRate, _pllEnabled, _exponent, _bandwidthHz, _offsetHz, _gainLinear, _displayMode);
            }

            var input = block.Samples.Span;
            var now = Environment.TickCount64;
            var emitScope = _emitScope && now >= _nextScopeTick;
            if (emitScope) _nextScopeTick = now + 100;
            if (_replaceIq)
            {
                var output = new Complex32[input.Length];
                _decoder.ProcessBlock(input, output, emitScope);
                return new IqPluginResult(Info.Id, "IQ_OUT", $"{output.Length}", DateTime.UtcNow, OutputSamples: output);
            }

            // Analyze without replacing host IQ (chunked scratch to avoid large stackalloc).
            const int chunk = 4096;
            Span<Complex32> scratch = stackalloc Complex32[Math.Min(chunk, input.Length)];
            var offset = 0;
            var scopeOnce = emitScope;
            while (offset < input.Length)
            {
                var take = Math.Min(scratch.Length, input.Length - offset);
                _decoder.ProcessBlock(input.Slice(offset, take), scratch[..take], scopeOnce && offset == 0);
                offset += take;
            }
            return null;
        }
    }

    public void Dispose() { lock (_sync) _enabled = false; }

    private static bool Bool(IReadOnlyDictionary<string, string> o, string k, bool f) =>
        o.TryGetValue(k, out var t) && bool.TryParse(t, out var v) ? v : f;
    private static double Double(IReadOnlyDictionary<string, string> o, string k, double f, double min, double max)
    {
        if (!o.TryGetValue(k, out var t) || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return f;
        return Math.Clamp(v, min, max);
    }
    private static string Text(IReadOnlyDictionary<string, string> o, string k, string f) =>
        o.TryGetValue(k, out var t) ? t : f;
}
