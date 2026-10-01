using WebSdr;

namespace NeuroSDR.Plugins;

internal sealed class FtxDecoderAfPlugin : IAfPlugin
{
    private readonly Ft8SlotReceiver _receiver = new() { Enabled = true, LatencyOffsetSec = 0 };
    private readonly object _adjustSync = new();
    private readonly List<double> _dtSamples = [];
    private bool _autoTimeAdjust;
    private double _timeAdjustSeconds;

    public FtxDecoderAfPlugin()
    {
        _receiver.StatusChanged += status => Publish("STATUS", status);
        _receiver.ErrorOccurred += error => Publish("ERROR", error.GetBaseException().Message);
        _receiver.SlotDecoded += result =>
        {
            if (result.Messages.Count == 0)
            {
                Publish("STATUS", $"{result.Mode.ToString().ToUpperInvariant()} {result.SlotStartUtc:HH:mm:ss} UTC · no decode");
                return;
            }
            foreach (var line in result.Messages)
                PublishDecode(result, line);
            Publish("STATUS", $"{result.Mode.ToString().ToUpperInvariant()} {result.SlotStartUtc:HH:mm:ss} UTC · {result.Messages.Count} decoded");
            AdjustTimeFromDt(result);
        };
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.ftx", "FT8 / FT4 Decoder", "UTC slot based FT8 and FT4 decoder (ft8_lib)",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);
    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        var enabled = !options.TryGetValue("enabled", out var enabledText) ||
                      !bool.TryParse(enabledText, out var parsedEnabled) || parsedEnabled;
        var nextMode = options.TryGetValue("mode", out var mode) && mode.Equals("FT4", StringComparison.OrdinalIgnoreCase)
            ? FtxMode.Ft4 : FtxMode.Ft8;
        var nextOffset = options.TryGetValue("timeAdjustSeconds", out var offsetText) &&
                         double.TryParse(offsetText, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var parsedOffset)
            ? Math.Clamp(parsedOffset, -5, 30) : 0;
        var nextAuto = options.TryGetValue("autoTimeAdjust", out var autoText) && bool.TryParse(autoText, out var parsedAuto) && parsedAuto;
        lock (_adjustSync)
        {
            if (_receiver.Mode != nextMode || _autoTimeAdjust != nextAuto || Math.Abs(_timeAdjustSeconds - nextOffset) > .0001)
                _dtSamples.Clear();
            _receiver.Mode = nextMode;
            _receiver.LatencyOffsetSec = nextOffset;
            _receiver.Enabled = enabled;
            _timeAdjustSeconds = nextOffset;
            _autoTimeAdjust = nextAuto;
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        _receiver.PushPcm(ToPcm16(block.Input.Span), block.SampleRate);
        return null;
    }

    private void Publish(string kind, string text) =>
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, text, DateTime.UtcNow));

    private void PublishDecode(Ft8SlotResult result, Ft8DecodedLine line)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["slot"] = result.SlotStartUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["utc"] = result.SlotStartUtc.ToString("HH:mm:ss"),
            ["db"] = line.Score.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["dt"] = line.TimeSec.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            ["freq"] = line.FreqHz.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            ["message"] = line.Text
        };
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "FTX_DECODE", line.Text, DateTime.UtcNow, Fields: fields));
    }

    private void AdjustTimeFromDt(Ft8SlotResult result)
    {
        lock (_adjustSync)
        {
            if (!_autoTimeAdjust) return;
            _dtSamples.AddRange(result.Messages.Select(message => (double)message.TimeSec)
                .Where(dt => double.IsFinite(dt) && Math.Abs(dt) <= 3.0));
            if (_dtSamples.Count < 5)
            {
                Publish("AUTO DT", $"collecting {_dtSamples.Count}/5 valid DT samples");
                return;
            }

            var firstMean = _dtSamples.Average();
            var variance = _dtSamples.Average(dt => Math.Pow(dt - firstMean, 2));
            var tolerance = Math.Max(.20, 2 * Math.Sqrt(variance));
            var coherent = _dtSamples.Where(dt => Math.Abs(dt - firstMean) <= tolerance).ToList();
            if (coherent.Count < 5)
            {
                Publish("AUTO DT", "waiting for 5 coherent DT samples");
                return;
            }

            var meanDt = coherent.Average();
            if (Math.Abs(meanDt) <= .1)
            {
                _autoTimeAdjust = false;
                _dtSamples.Clear();
                Publish("AUTO_DT_STATE", "OFF");
                Publish("AUTO DT", $"complete · mean DT {meanDt:+0.00;-0.00;0.00}s");
                return;
            }

            _timeAdjustSeconds = Math.Clamp(_timeAdjustSeconds + meanDt, -5, 30);
            _receiver.LatencyOffsetSec = _timeAdjustSeconds;
            _dtSamples.Clear();
            Publish("TIME_ADJUST", _timeAdjustSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            Publish("AUTO DT", $"mean DT {meanDt:+0.00;-0.00;0.00}s · offset → {_timeAdjustSeconds:0.00}s");
        }
    }

    internal void ApplyDtSamplesForVerification(params float[] samples)
    {
        var messages = samples.Select((dt, index) => new Ft8DecodedLine($"TEST {index}", 20, dt, 1_000 + index, 0)).ToArray();
        AdjustTimeFromDt(new Ft8SlotResult(DateTime.UtcNow, messages, 13.5));
    }

    internal double TimeAdjustForVerification
    {
        get { lock (_adjustSync) return _timeAdjustSeconds; }
    }

    internal static short[] ToPcm16(ReadOnlySpan<float> input)
    {
        var pcm = new short[input.Length];
        for (var index = 0; index < input.Length; index++)
            pcm[index] = (short)Math.Round(Math.Clamp(input[index], -1f, 1f) * 32767f);
        return pcm;
    }

    public void Dispose() => _receiver.Dispose();
}

internal sealed class CwDecoderAfPlugin : IAfPlugin
{
    private int _resetCount;
    private readonly MorseViewer _viewer = new()
    {
        Enabled = true,
        StartHz = 300,
        SpacingHz = 100,
        ChannelCount = 30,
        Profile = CwProcessingProfile.Balanced
    };

    public CwDecoderAfPlugin()
    {
        _viewer.CharacterDecoded += character =>
        {
            if (character.CommitState == CwCommitState.Suppressed) return;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["channel"] = character.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["slotHz"] = character.SlotHz.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                ["trackedHz"] = character.TrackedHz.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                ["wpm"] = character.Wpm.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["text"] = character.Text,
                ["snr"] = character.SnrDb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            };
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "CW_DECODE", character.Text, DateTime.UtcNow, Fields: fields));
        };
        _viewer.StatusChanged += status => Publish("STATUS", status);
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.cw", "CEC CW Decoder", "Decodes CW channels across the AF passband",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);
    public event Action<AfPluginResult>? ResultAvailable;
    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("enabled", out var enabledText) && bool.TryParse(enabledText, out var enabled))
            _viewer.Enabled = enabled;
        if (options.TryGetValue("command", out var command) && command.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _viewer.Reset();
            _resetCount++;
        }
    }

    internal int ResetCountForVerification => _resetCount;

    public AfPluginResult? Process(AfAudioBlock block)
    {
        _viewer.PushPcm(FtxDecoderAfPlugin.ToPcm16(block.Input.Span), block.SampleRate);
        return null;
    }

    private void Publish(string kind, string text) =>
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, text, DateTime.UtcNow));
    public void Dispose() => _viewer.Dispose();
}
