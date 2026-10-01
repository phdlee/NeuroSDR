using System.Globalization;
using System.Text;

namespace NeuroSDR.Plugins.Fldigi;

internal sealed class FlRttyAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly FlRttyDecoder _decoder = new();
    private readonly StringBuilder[] _lines = Enumerable.Range(0, FlRttyDecoder.ChannelCount)
        .Select(_ => new StringBuilder()).ToArray();
    private FlSincResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private double _baud = 45.45, _shift = 170;
    private bool _inverse, _uos = true;

    public FlRttyAfPlugin()
    {
        _decoder.ChannelCleared += ch =>
        {
            string snapshot;
            lock (_sync)
            {
                if (ch >= 0 && ch < _lines.Length) _lines[ch].Clear();
                snapshot = Dump();
            }
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "FLRTTY_CHAR", "", DateTime.UtcNow,
                Fields: new Dictionary<string, string> { ["text"] = snapshot, ["channel"] = ch.ToString(CultureInfo.InvariantCulture) }));
        };
        _decoder.ChannelCharacter += (ch, freq, c) =>
        {
            string snapshot;
            lock (_sync)
            {
                if (!_enabled) return;
                var line = _lines[Math.Clamp(ch, 0, _lines.Length - 1)];
                if (line.Length == 0) line.Append($"{freq,4} ");
                line.Append(c);
                if (line.Length > 400) line.Remove(5, line.Length - 300);
                snapshot = Dump();
            }
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "FLRTTY_CHAR", c.ToString(), DateTime.UtcNow,
                Fields: new Dictionary<string, string>
                {
                    ["character"] = c.ToString(),
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
        "builtin.af.flrtty", "flrtty",
        "fldigi view-RTTY: AF-wide multi-channel Baudot (45/50/75…)",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _baud = Dbl(options, "baud", _baud, 40, 300);
            _shift = Dbl(options, "shift", _shift, 20, 900);
            if (options.ContainsKey("deviation"))
                _shift = Dbl(options, "deviation", _shift / 2, 10, 450) * 2;
            _inverse = Bool(options, "inverse", _inverse);
            _uos = Bool(options, "uos", _uos);
            var sql = Dbl(options, "squelch", 0, 0, 24);
            _decoder.Configure(_baud, _shift, _inverse, _uos, sql);
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
            if (line.Length == 0) continue;
            sb.AppendLine(line.ToString());
        }
        return sb.ToString();
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
        o.TryGetValue(k, out var t) && !string.IsNullOrWhiteSpace(t) ? t : f;
}
