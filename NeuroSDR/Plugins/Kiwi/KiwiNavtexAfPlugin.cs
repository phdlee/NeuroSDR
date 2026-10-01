using System.Globalization;
using System.Text;
using NeuroSDR.Dsp;
using EnsSstv.Jnx;

namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// NAVTEX / SITOR-B decoder using EnsSstv.Jnx (same Paul Lutus JNX lineage as KiwiSDR's browser FSK/NAVTEX).
/// KiwiSDR server extension only relays audio; decode runs client-side via JNX.js — this is the C# equivalent.
/// </summary>
internal sealed class KiwiNavtexAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly NavtexDecoder _decoder = new();
    private readonly StringBuilder _buffer = new();
    private StreamingPcmResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private NavtexConfig _config = new() { SampleRate = 12_000, BaudRate = 100, CenterFrequencyHz = 1_000, DeviationHz = 85, Inverse = true };

    public KiwiNavtexAfPlugin()
    {
        _decoder.CharacterReceived += ch =>
        {
            lock (_sync)
            {
                if (!_enabled) return;
                _buffer.Append(ch);
                if (_buffer.Length > 8_000) _buffer.Remove(0, _buffer.Length - 6_000);
                ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "KIWINAVTEX_CHAR", ch.ToString(), DateTime.UtcNow,
                    Fields: new Dictionary<string, string> { ["character"] = ch.ToString(), ["text"] = _buffer.ToString() }));
            }
        };
        _decoder.StateChanged += state =>
        {
            if (_enabled)
                ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", state.ToString(), DateTime.UtcNow));
        };
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.kiwinavtex", "KiwiNAVTEX",
        "NAVTEX/SITOR-B decoder (JNX lineage used by KiwiSDR NAVTEX)",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _config = new NavtexConfig
            {
                SampleRate = 12_000,
                BaudRate = Double(options, "baud", _config.BaudRate, 50, 200),
                CenterFrequencyHz = Double(options, "center", _config.CenterFrequencyHz, 200, 3_000),
                DeviationHz = Double(options, "deviation", _config.DeviationHz, 40, 200),
                Inverse = Bool(options, "inverse", _config.Inverse),
                UsePll = Bool(options, "pll", _config.UsePll),
                NavtexMessageFiltering = Bool(options, "filter", _config.NavtexMessageFiltering)
            };
            _decoder.Configure(_config);
            if (Text(options, "command", "").Equals("clear", StringComparison.OrdinalIgnoreCase))
                _buffer.Clear();
            if (Text(options, "command", "").Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _buffer.Clear();
                _decoder.ResetSoft();
            }
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled) return null;
            if (_resampler is null || _sourceRate != block.SampleRate)
            {
                _sourceRate = block.SampleRate;
                _resampler = new StreamingPcmResampler(block.SampleRate, 12_000);
            }
            var pcm = _resampler.Process(block.Input.Span);
            if (pcm.Length > 0) _decoder.ProcessSamples(pcm);
        }
        return null;
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
