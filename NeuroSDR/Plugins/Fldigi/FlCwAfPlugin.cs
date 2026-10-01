using System.Globalization;
using System.Text;

namespace NeuroSDR.Plugins.Fldigi;

internal sealed class FlCwAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly FlCwDecoder _decoder = new();
    private readonly StringBuilder[] _lines = Enumerable.Range(0, FlCwDecoder.ChannelCount)
        .Select(_ => new StringBuilder()).ToArray();
    private FlSincResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private int _wpm = 18;

    public FlCwAfPlugin()
    {
        _decoder.ChannelCleared += ch =>
        {
            string snapshot;
            lock (_sync)
            {
                if (ch >= 0 && ch < _lines.Length) _lines[ch].Clear();
                snapshot = Dump();
            }
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "FLCW_CHAR", "", DateTime.UtcNow,
                Fields: new Dictionary<string, string> { ["text"] = snapshot }));
        };
        _decoder.ChannelText += (ch, freq, text) =>
        {
            string snapshot;
            lock (_sync)
            {
                if (!_enabled) return;
                if (string.IsNullOrWhiteSpace(text))
                {
                    var existing = _lines[Math.Clamp(ch, 0, _lines.Length - 1)];
                    if (!HasDecodedText(existing)) return;
                }
                var line = _lines[Math.Clamp(ch, 0, _lines.Length - 1)];
                if (line.Length == 0) line.Append($"{freq,4} ");
                line.Append(text);
                if (line.Length > 400) line.Remove(5, line.Length - 300);
                snapshot = Dump();
            }
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "FLCW_CHAR", text, DateTime.UtcNow,
                Fields: new Dictionary<string, string>
                {
                    ["character"] = text,
                    ["channel"] = ch.ToString(CultureInfo.InvariantCulture),
                    ["freq"] = freq.ToString(CultureInfo.InvariantCulture),
                    ["text"] = snapshot
                }));
        };
        _decoder.StatusChanged += text =>
        {
            if (_enabled) ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", text, DateTime.UtcNow));
        };
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.flcw", "flcw",
        "fldigi view-CW: AF-wide 100 Hz Morse channels",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _wpm = (int)Dbl(options, "wpm", _wpm, 5, 60);
            var sql = Dbl(options, "squelch", 0, 0, 24);
            _decoder.Configure(_wpm, sql);
            var command = Text(options, "command", "");
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase))
                foreach (var line in _lines) line.Clear();
            if (command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in _lines) line.Clear();
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

    private string Dump()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            if (!HasDecodedText(line)) continue;
            sb.AppendLine(line.ToString());
        }
        return sb.ToString();
    }

    private static bool HasDecodedText(StringBuilder line)
    {
        if (line.Length <= 5) return false;
        for (var i = 5; i < line.Length; i++)
            if (!char.IsWhiteSpace(line[i])) return true;
        return false;
    }

    private static bool Bool(IReadOnlyDictionary<string, string> o, string k, bool f) =>
        o.TryGetValue(k, out var t) && bool.TryParse(t, out var v) ? v : f;
    private static double Dbl(IReadOnlyDictionary<string, string> o, string k, double f, double min, double max)
    {
        if (!o.TryGetValue(k, out var t) || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return f;
        return Math.Clamp(v, min, max);
    }
    private static string Text(IReadOnlyDictionary<string, string> o, string k, string f) =>
        o.TryGetValue(k, out var t) ? t : f;
}
