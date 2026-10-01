using System.Globalization;
using NeuroSDR.Controls;
using NeuroSDR.Plugins;

namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// Feed discriminator PCM into DSD-FME (slicer+decode owned by dsdfme.dll).
/// </summary>
internal sealed class DsdFmePcmAfPlugin : IAfPlugin, IAfVisualPlugin, IAfFrequencyPresetPlugin
{
    public const string PluginId = "builtin.af.dsdfmepcm";

    private readonly object _sync = new();
    private readonly DsdFmeSession _dsd = new();
    private readonly short[] _pcm = new short[4_800];
    private bool _enabled;
    private int _outputChannel = 1;
    private float _peak = 1e-3f;
    private Control? _view;
    private DateTime _lastStatusUtc = DateTime.MinValue;
    private long _pushed, _pulled;
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "neurosdr-dsdfme-pcm.log");

    public AfPluginInfo Info { get; } = new(
        PluginId,
        "DSD-FME PCM",
        "AF → int16 @ 48 kHz → dsdfme.dll (full slicer+decode). Disable Digital Voice / DSD+ while testing.",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.AudioOutput | AfPluginCapabilities.Display |
        AfPluginCapabilities.HostResults);

    public string PresetGroup => "DMR";
    public event Action<AfPluginResult>? ResultAvailable;

    public Control CreateView(IAfPluginUiHost ui)
    {
        if (_view is { IsDisposed: false }) return _view;
        var view = new DsdProbePluginView(
            "DSD-FME PCM",
            "Host only levels AF and plays decoded voice. All DMR sync/slicer/AMBE runs inside dsdfme.dll.");
        view.LoadOptions(ui.LoadOptions());
        void Push(string? command = null)
        {
            var options = new Dictionary<string, string>(view.Snapshot(), StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = ui.IsRunning.ToString()
            };
            if (!string.IsNullOrEmpty(command)) options["command"] = command;
            ui.SaveOptions(options);
            ui.Configure(options);
        }
        view.OptionsChanged += () => Push();
        view.CommandRequested += command => Push(command);
        Push();
        return _view = view;
    }

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _outputChannel = Int(options, "output", _outputChannel, 1, 2);
            DigitalVoicePlayback.OwnedOutputIndex = _enabled ? _outputChannel - 1 : -1;
            if (_enabled)
            {
                _peak = 1e-3f;
                _pushed = _pulled = 0;
                DigitalVoicePlayback.Clear();
                DigitalVoicePlayback.OwnedOutputIndex = _outputChannel - 1;
                try { File.WriteAllText(_logPath, $"# DSD-FME PCM log started {DateTime.Now:O} OUT{_outputChannel}\n"); } catch { }
                // Polarity is DSD-FME's problem; live RSP1 tests lock cleanly with inverted_dmr=0.
                if (!_dsd.EnsureStarted(pcmMode: true, invertedDmr: false))
                    PublishStatus(_dsd.Status);
                else
                    PublishStatus(_dsd.Status + $" · OUT{_outputChannel} · log={_logPath}");
            }
            else
            {
                _dsd.Dispose();
                DigitalVoicePlayback.Clear();
                PublishStatus("idle");
            }
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled) return null;
            if (!_dsd.EnsureStarted(pcmMode: true, invertedDmr: false)) return null;
            var pcm = block.Input.Span;
            if (block.SampleRate != 48_000)
                pcm = Resample(pcm, block.SampleRate, 48_000);

            foreach (var sample in pcm)
            {
                var mag = Math.Abs(sample);
                if (mag > _peak) _peak = mag;
                else _peak = 0.995f * _peak + 0.005f * mag;
            }
            // Gentle level match only — not DMR polarity / slicer work.
            var scale = _peak > 1e-4f ? Math.Clamp(0.45f / _peak, 0.35f, 2.5f) : 1f;

            var offset = 0;
            while (offset < pcm.Length)
            {
                var take = Math.Min(_pcm.Length, pcm.Length - offset);
                for (var i = 0; i < take; i++)
                {
                    var sample = Math.Clamp(pcm[offset + i] * scale, -1f, 1f);
                    _pcm[i] = (short)Math.Clamp((int)Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
                }
                _dsd.PushPcm16(_pcm.AsSpan(0, take));
                _pushed += take;
                offset += take;
            }
            var before = DigitalVoicePlayback.BufferedSamples;
            _dsd.PumpDecodedAudio();
            _pulled += Math.Max(0, DigitalVoicePlayback.BufferedSamples - before);

            if ((DateTime.UtcNow - _lastStatusUtc).TotalSeconds >= 1)
            {
                _lastStatusUtc = DateTime.UtcNow;
                var st = _dsd.TryGetStatus();
                var m = DigitalVoicePlayback.SnapshotMetrics();
                if (st is { } s)
                {
                    var line =
                        $"sync={s.SyncType} tg={s.LastTg}/{s.LastTgR} src={s.LastSrc}/{s.LastSrcR} cc={s.ColorCode} " +
                        $"in={s.InQueued} nativeOut={s.OutQueued} voice48k={m.buffered} minBuf={m.minBuffered} " +
                        $"underrun={m.underruns} overflow={m.overflows} peak={m.peak:0.00} playing={(m.playing ? 1 : 0)} " +
                        $"gain={scale:0.00}x push8k={m.pushedFrames}";
                    PublishStatus(line);
                    try
                    {
                        File.AppendAllText(_logPath,
                            $"{DateTime.Now:HH:mm:ss.fff} {line} afIn={_pushed}{Environment.NewLine}");
                    }
                    catch { }
                    DigitalVoicePlayback.ResetMinBuffered();
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            DigitalVoicePlayback.OwnedOutputIndex = -1;
            _dsd.Dispose();
        }
    }

    private void PublishStatus(string text) =>
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", text, DateTime.UtcNow));

    private static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0 || input.IsEmpty) return [];
        var count = Math.Max(1, (int)Math.Round(input.Length * (double)toRate / fromRate));
        var output = new float[count];
        for (var i = 0; i < count; i++)
        {
            var source = i * (fromRate / (double)toRate);
            var index = (int)source;
            var frac = (float)(source - index);
            var a = input[Math.Clamp(index, 0, input.Length - 1)];
            var b = input[Math.Clamp(index + 1, 0, input.Length - 1)];
            output[i] = a + (b - a) * frac;
        }
        return output;
    }

    private static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max)
    {
        if (!options.TryGetValue(key, out var text) ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;
        return Math.Clamp(value, min, max);
    }

    private static bool Bool(IReadOnlyDictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;
}
