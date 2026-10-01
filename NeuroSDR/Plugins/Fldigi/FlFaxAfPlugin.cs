using System.Globalization;

namespace NeuroSDR.Plugins.Fldigi;

internal sealed class FlFaxAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly FlFaxDecoder _decoder = new();
    private FlSincResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private int _lpm = 120;
    private readonly List<byte> _rgb = [];
    private int _lines;

    public FlFaxAfPlugin()
    {
        _decoder.StatusChanged += text => Publish("STATUS", text);
        _decoder.LineReady += OnLine;
        _decoder.ChartFinished += () => PublishImage("FLFAX_COMPLETE");
        _decoder.Cleared += () =>
        {
            lock (_sync) { _rgb.Clear(); _lines = 0; }
            Publish("STATUS", "Cleared");
        };
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.flfax", "flfax",
        "fldigi WEFAX (IOC-576 FM fax)",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            var next = Bool(options, "enabled", _enabled);
            _lpm = Int(options, "lpm", _lpm, 60, 120);
            if (_lpm is not 60 and not 120) _lpm = 120;
            var center = Dbl(options, "center", 1_900, 1_000, 2_800);
            var shift = Dbl(options, "shift", 800, 400, 1_000);
            var manual = Bool(options, "manual", true);
            var phasing = Bool(options, "phasing", false);
            var command = Text(options, "command", "");
            var reset = next != _enabled || command.Length > 0;
            _enabled = next;
            _decoder.Configure(_lpm, center, shift, manual, phasing, reset || !_enabled);
            if (!_enabled) return;
            if (command.Equals("restart", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _rgb.Clear();
                _lines = 0;
                _decoder.Reset();
            }
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled || block.Input.Length == 0) return null;
            if (_resampler is null || _sourceRate != block.SampleRate)
            {
                _sourceRate = block.SampleRate;
                _resampler = new FlSincResampler(block.SampleRate);
            }
            var audio = _resampler.Process(block.Input.Span);
            if (audio.Length > 0) _decoder.Process(audio);
        }
        return null;
    }

    public void Dispose() { lock (_sync) _enabled = false; }

    private void OnLine(byte[] gray)
    {
        lock (_sync)
        {
            if (!_enabled) return;
            foreach (var g in gray) { _rgb.Add(g); _rgb.Add(g); _rgb.Add(g); }
            _lines++;
            if (_lines == 1 || _lines % 2 == 0) PublishImage("FLFAX_IMAGE");
        }
    }

    private void PublishImage(string kind)
    {
        if (!_enabled || _lines <= 0 || _rgb.Count == 0) return;
        var width = FlFaxDecoder.DefaultWidth;
        var height = _lines;
        var stride = width * 3;
        if (_rgb.Count < stride * height) return;
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, $"{height} lines", DateTime.UtcNow,
            Fields: new Dictionary<string, string>
            {
                ["width"] = width.ToString(CultureInfo.InvariantCulture),
                ["height"] = height.ToString(CultureInfo.InvariantCulture),
                ["stride"] = stride.ToString(CultureInfo.InvariantCulture),
                ["lines"] = height.ToString(CultureInfo.InvariantCulture),
                ["lpm"] = _lpm.ToString(CultureInfo.InvariantCulture)
            },
            BinaryData: _rgb.ToArray()));
    }

    private void Publish(string kind, string text)
    {
        if (_enabled) ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, text, DateTime.UtcNow));
    }

    private static bool Bool(IReadOnlyDictionary<string, string> o, string k, bool f) =>
        o.TryGetValue(k, out var t) && bool.TryParse(t, out var v) ? v : f;
    private static int Int(IReadOnlyDictionary<string, string> o, string k, int f, int min, int max)
    {
        if (!o.TryGetValue(k, out var t) || !int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return f;
        return Math.Clamp(v, min, max);
    }
    private static double Dbl(IReadOnlyDictionary<string, string> o, string k, double f, double min, double max)
    {
        if (!o.TryGetValue(k, out var t) || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return f;
        return Math.Clamp(v, min, max);
    }
    private static string Text(IReadOnlyDictionary<string, string> o, string k, string f) =>
        o.TryGetValue(k, out var t) ? t : f;
}
