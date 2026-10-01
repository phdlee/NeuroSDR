using System.Globalization;
using NeuroSDR.Controls;
using NeuroSDR.Plugins;

namespace NeuroSDR.Plugins.DigitalVoice;

internal sealed class DigitalVoiceAfPlugin : IAfPlugin, IAfVisualPlugin, IAfFrequencyPresetPlugin
{
    public const string PluginId = "builtin.af.digitalvoice";

    private readonly object _sync = new();
    private readonly DigitalVoiceDecoder _decoder = new();
    private readonly DsdFmeSession _dsd = new();
    private readonly byte[] _dibitScratch = new byte[4_800];
    private bool _enabled;
    private int _outputChannel = 1;
    private Control? _view;

    public DigitalVoiceAfPlugin()
    {
        _decoder.RadioIds.Reload();
        _decoder.QsoDecoded += qso =>
        {
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "QSO_RECORD", Format(qso), qso.Utc,
                Fields: Fields(qso)));
        };
        _decoder.StatusChanged += text =>
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", text, DateTime.UtcNow));
        _decoder.BurstReady += burst =>
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "DSD_SYMBOLS", burst.Kind, burst.Utc,
                Fields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["protocol"] = burst.Protocol,
                    ["kind"] = burst.Kind,
                    ["dibits"] = burst.Dibits.Length.ToString(CultureInfo.InvariantCulture),
                    ["format"] = "dsd-symbol-file"
                },
                BinaryData: ToSymbolBytes(burst.DsdSymbols)));
    }

    public AfPluginInfo Info { get; } = new(
        PluginId,
        "Digital Voice",
        "DMR / D-STAR metadata plus DSD-FME voice. Host sends 4800 baud dibits, not discriminator PCM.",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.AudioOutput | AfPluginCapabilities.Display |
        AfPluginCapabilities.HostResults);

    public string PresetGroup => "DMR";
    public event Action<AfPluginResult>? ResultAvailable;

    public Control CreateView(IAfPluginUiHost ui)
    {
        if (_view is { IsDisposed: false }) return _view;
        var view = new DigitalVoicePluginView();
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
            _decoder.Invert = Bool(options, "invert", _decoder.Invert);
            _outputChannel = Int(options, "output", _outputChannel, 1, 2);
            DigitalVoicePlayback.OwnedOutputIndex = _enabled ? _outputChannel - 1 : -1;
            if (_enabled) _dsd.EnsureStarted();
            else
            {
                _dsd.Dispose();
                DigitalVoicePlayback.Clear();
            }
            var command = Text(options, "command", "");
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _decoder.Reset();
                ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "QSO_CLEAR", "cleared", DateTime.UtcNow));
            }
            if (command.Equals("reload-radioid", StringComparison.OrdinalIgnoreCase))
                _decoder.RadioIds.Reload();
            PublishDirectoryStatus();
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled) return null;
            var pcm = block.Input.Span;
            if (block.SampleRate != DigitalVoiceDecoder.AudioRate)
                pcm = Resample(pcm, block.SampleRate, DigitalVoiceDecoder.AudioRate);
            var rssi = Rssi(pcm);
            _decoder.ProcessAudio(pcm, DigitalVoiceDecoder.AudioRate, block.TimestampUtc, rssi);
            if (_dsd.EnsureStarted())
            {
                var n = _decoder.DibitQueue.Pull(_dibitScratch);
                if (n > 0) _dsd.PushDibits(_dibitScratch.AsSpan(0, n));
                _dsd.PumpDecodedAudio();
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
            _decoder.Reset();
        }
    }

    internal DigitalVoiceDecoder DecoderForVerification => _decoder;

    private void PublishDirectoryStatus()
    {
        var path = string.IsNullOrEmpty(_decoder.RadioIds.LoadedPath) ? "no user.csv" : _decoder.RadioIds.LoadedPath;
        var voice = _dsd.IsRunning ? _dsd.Status : (_dsd.Status.Length > 0 ? _dsd.Status : "voice idle");
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS",
            $"RadioID {_decoder.RadioIds.Count} · OUT{_outputChannel} · {voice} ({path})", DateTime.UtcNow,
            Fields: new Dictionary<string, string>
            {
                ["radioIdCount"] = _decoder.RadioIds.Count.ToString(CultureInfo.InvariantCulture),
                ["output"] = _outputChannel.ToString(CultureInfo.InvariantCulture)
            }));
    }

    private static Dictionary<string, string> Fields(DigitalVoiceQso qso) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["timestamp"] = qso.Utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        ["protocol"] = qso.Protocol,
        ["sync"] = qso.Sync,
        ["slot"] = qso.Slot?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["cc"] = qso.ColorCode?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["source"] = qso.SourceId?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["target"] = qso.TargetId?.ToString(CultureInfo.InvariantCulture) ?? "",
        ["callsign"] = qso.Callsign,
        ["name"] = qso.Name,
        ["city"] = qso.City,
        ["country"] = qso.Country,
        ["callType"] = qso.CallType,
        ["rssi"] = qso.RssiDb.ToString("0.0", CultureInfo.InvariantCulture),
        ["ber"] = qso.BerPercent.ToString("0.0", CultureInfo.InvariantCulture)
    };

    private static string Format(DigitalVoiceQso qso) =>
        $"{qso.Protocol} {qso.Sync} src={qso.SourceId} dst={qso.TargetId} {qso.Callsign}".Trim();

    private static byte[] ToSymbolBytes(sbyte[] symbols)
    {
        var bytes = new byte[symbols.Length];
        Buffer.BlockCopy(symbols, 0, bytes, 0, symbols.Length);
        return bytes;
    }

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

    private static float Rssi(ReadOnlySpan<float> pcm)
    {
        if (pcm.IsEmpty) return -120;
        double sum = 0;
        foreach (var sample in pcm) sum += sample * sample;
        var rms = Math.Sqrt(sum / pcm.Length);
        return (float)(20 * Math.Log10(Math.Max(rms, 1e-9)));
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

    private static string Text(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var text) ? text : fallback;
}
