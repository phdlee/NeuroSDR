using System.Globalization;
using System.Text;
using ENSdr.Plugins;
using KiwiSDRPlugin.Views;

namespace KiwiSDRPlugin;

/// <summary>
/// AF plug-in wrapping <see cref="KiwiCwDecoder"/> (UHSDR / KiwiSDR CW decoder).
/// Resamples host AF (typically 48 kHz) to 12 kHz to match original Goertzel block timing.
/// </summary>
public sealed class KiwiCwAfPlugin : IAfPlugin, IAfVisualPlugin
{
    private readonly object _sync = new();
    private readonly KiwiCwDecoder _decoder = new();
    private readonly StringBuilder _buffer = new();
    private PcmResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private float _pitchHz = KiwiCwDecoder.DefaultPitchHz;
    private int _fixedWpm; // 0 = auto
    private int _trainingInterval = 100;
    private bool _wordSpaceCorrection = true;
    private bool _autoThreshold;
    private float _thresholdDb = KiwiCwDecoder.DefaultThresholdDb;

    public KiwiCwAfPlugin()
    {
        _decoder.CharacterDecoded += text =>
        {
            lock (_sync)
            {
                if (!_enabled) return;
                _buffer.Append(text);
                if (_buffer.Length > 8_000) _buffer.Remove(0, _buffer.Length - 6_000);
                ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "KIWICW_CHAR", text, DateTime.UtcNow,
                    Fields: new Dictionary<string, string>
                    {
                        ["character"] = text,
                        ["text"] = _buffer.ToString()
                    }));
            }
        };
        _decoder.StatusChanged += status =>
        {
            if (_enabled)
                ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", status, DateTime.UtcNow));
        };
        _decoder.WpmChanged += wpm =>
        {
            if (_enabled)
                ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "WPM", wpm.ToString(CultureInfo.InvariantCulture), DateTime.UtcNow,
                    Fields: new Dictionary<string, string> { ["wpm"] = wpm.ToString(CultureInfo.InvariantCulture) }));
        };
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.kiwicw", "KiwiCW",
        "CW Morse decoder (UHSDR / KiwiSDR CW_decoder)",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;
    private Control? _view;

    public Control CreateView(IAfPluginUiHost ui)
    {
        return PluginViewHost.KeepOrCreate(ref _view, () =>
        {
            var view = new KiwiCwPluginView();
            PluginViewHost.Bind(view, ui, view.Snapshot, view.LoadOptions);
            return view;
        });
    }

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _pitchHz = (float)Double(options, "pitch", _pitchHz, 200, 3_000);
            if (!options.ContainsKey("pitch") && options.ContainsKey("center"))
                _pitchHz = (float)Double(options, "center", _pitchHz, 200, 3_000);

            var autoWpm = Bool(options, "autoWpm", _fixedWpm == 0);
            if (options.ContainsKey("wpm"))
            {
                var wpm = (int)Double(options, "wpm", _fixedWpm, 0, 60);
                _fixedWpm = autoWpm ? 0 : wpm;
            }
            else if (options.ContainsKey("autoWpm"))
            {
                _fixedWpm = autoWpm ? 0 : Math.Max(5, _fixedWpm == 0 ? 10 : _fixedWpm);
            }

            _trainingInterval = (int)Double(options, "trainingInterval", _trainingInterval, 20, 500);
            _wordSpaceCorrection = Bool(options, "wsc", _wordSpaceCorrection);
            _autoThreshold = Bool(options, "autoThreshold", _autoThreshold);
            _thresholdDb = (float)Double(options, "thresholdDb", _thresholdDb, 10, 80);

            _decoder.Configure(_pitchHz, _fixedWpm, _trainingInterval, _wordSpaceCorrection, _autoThreshold, _thresholdDb);

            var command = Text(options, "command", "");
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase))
                _buffer.Clear();
            if (command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _buffer.Clear();
                _decoder.Reset();
            }
            if (command.Equals("train", StringComparison.OrdinalIgnoreCase))
            {
                _fixedWpm = 0;
                _decoder.Configure(_pitchHz, 0, _trainingInterval, _wordSpaceCorrection, _autoThreshold, _thresholdDb);
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
                _resampler = new PcmResampler(block.SampleRate, KiwiCwDecoder.NominalSampleRate);
            }
            var pcm = _resampler.Process(block.Input.Span);
            if (pcm.Length > 0) _decoder.ProcessPcm16(pcm);
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
