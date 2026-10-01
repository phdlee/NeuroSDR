using System.Globalization;
using System.Text;

namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// USB-audio WWV/WWVH time decoder. Reads the 100 Hz IRIG-H subcarrier already
/// present in demodulated AF (Virtual Kiwi USB, sound card, etc.).
/// Id: <c>builtin.af.kiwiwwv</c>.
/// </summary>
internal sealed class KiwiWwvAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly WwvTimeDecoder _decoder = new();
    private readonly StringBuilder _buffer = new();
    private bool _enabled;
    private double _toneHz = 100;
    private bool _invert;

    public KiwiWwvAfPlugin()
    {
        _decoder.StatusChanged += text =>
        {
            if (_enabled)
                ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", text, DateTime.UtcNow));
        };
        _decoder.LineReceived += line =>
        {
            lock (_sync)
            {
                if (!_enabled) return;
                _buffer.AppendLine(line);
                if (_buffer.Length > 12_000) _buffer.Remove(0, _buffer.Length - 8_000);
            }
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "KIWIWWV_LINE", line, DateTime.UtcNow,
                Fields: new Dictionary<string, string>
                {
                    ["line"] = line,
                    ["text"] = BufferSnapshot()
                }));
        };
        _decoder.TimeDecoded += decode =>
        {
            if (!_enabled) return;
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "KIWIWWV_TIME", decode.Text, DateTime.UtcNow,
                Fields: new Dictionary<string, string>
                {
                    ["utc"] = decode.UtcMinute.ToString("o"),
                    ["doy"] = decode.DayOfYear.ToString(CultureInfo.InvariantCulture),
                    ["text"] = decode.Text
                }));
        };
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.kiwiwwv", "KiwiWWV",
        "WWV/WWVH USB AF time (100 Hz IRIG-H)",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _toneHz = Double(options, "tone", _toneHz, 50, 2_000);
            if (!options.ContainsKey("tone") && options.ContainsKey("center"))
                _toneHz = Double(options, "center", _toneHz, 50, 2_000);
            _invert = Bool(options, "inverse", _invert);
            _decoder.Configure(_toneHz, _invert);

            var command = Text(options, "command", "");
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase)) _buffer.Clear();
            if (command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _buffer.Clear();
                _decoder.Reset();
            }
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled || block.Input.Length == 0) return null;
            _decoder.Process(block.Input.Span, block.SampleRate);
        }
        return null;
    }

    public void Dispose() { lock (_sync) _enabled = false; }

    private string BufferSnapshot()
    {
        lock (_sync) return _buffer.ToString();
    }

    private static bool Bool(IReadOnlyDictionary<string, string> o, string k, bool f) =>
        o.TryGetValue(k, out var t) && bool.TryParse(t, out var v) ? v : f;
    private static double Double(IReadOnlyDictionary<string, string> o, string k, double f, double min, double max)
    {
        if (!o.TryGetValue(k, out var t) || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return f;
        return Math.Clamp(v, min, max);
    }
    private static string Text(IReadOnlyDictionary<string, string> o, string k, string f) =>
        o.TryGetValue(k, out var t) && !string.IsNullOrWhiteSpace(t) ? t : f;
}
