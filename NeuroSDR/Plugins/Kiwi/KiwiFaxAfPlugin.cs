using System.Globalization;
using NeuroSDR.Dsp;

namespace NeuroSDR.Plugins.Kiwi;

internal sealed class KiwiFaxAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly KiwiFaxDecoder _decoder = new();
    private StreamingPcmResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private int _lpm = 120;
    private int _imageWidth = KiwiFaxDecoder.DefaultImageWidth;
    private bool _phasing = true;
    private bool _autoStop = true;
    private bool _includeHeaders = true;
    private readonly List<byte> _rgb = [];
    private int _lines;

    public KiwiFaxAfPlugin()
    {
        _decoder.StatusChanged += text => Publish("STATUS", text);
        _decoder.LineReady += OnLine;
        _decoder.ChartFinished += () => PublishImage("KIWIFAX_COMPLETE");
        _decoder.Cleared += () =>
        {
            lock (_sync)
            {
                _rgb.Clear();
                _lines = 0;
            }
            Publish("STATUS", "Cleared");
        };
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.kiwifax",
        "KiwiFAX",
        "HF weather fax decoder ported from KiwiSDR FAX extension",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            var nextEnabled = Bool(options, "enabled", _enabled);
            _lpm = Int(options, "lpm", _lpm, 60, 120);
            if (_lpm is not 60 and not 120) _lpm = 120;
            _imageWidth = Int(options, "width", _imageWidth, 320, 2_400);
            _phasing = Bool(options, "phasing", _phasing);
            _autoStop = Bool(options, "autostop", _autoStop);
            _includeHeaders = Bool(options, "headers", _includeHeaders);
            var reset = nextEnabled != _enabled ||
                        options.ContainsKey("command");
            _enabled = nextEnabled;
            _decoder.Configure(_lpm, _imageWidth, KiwiFaxDecoder.DefaultCarrierHz, KiwiFaxDecoder.DefaultDeviationHz,
                KiwiFaxDecoder.FirBandwidth.Middle, _includeHeaders, _phasing, _autoStop, reset || !_enabled);

            var command = Text(options, "command", "");
            if (!_enabled) return;
            if (command.Equals("restart", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("clear", StringComparison.OrdinalIgnoreCase))
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
            if (!_enabled) return null;
            if (_resampler is null || _sourceRate != block.SampleRate)
            {
                _sourceRate = block.SampleRate;
                _resampler = new StreamingPcmResampler(block.SampleRate, KiwiFaxDecoder.NominalSampleRate);
            }
            var pcm = _resampler.Process(block.Input.Span);
            if (pcm.Length > 0) _decoder.ProcessPcm16(pcm, KiwiFaxDecoder.NominalSampleRate);
        }
        return null;
    }

    private void OnLine(byte[] gray)
    {
        lock (_sync)
        {
            if (!_enabled) return;
            for (var x = 0; x < gray.Length; x++)
            {
                var g = gray[x];
                _rgb.Add(g);
                _rgb.Add(g);
                _rgb.Add(g);
            }
            _lines++;
            if (_lines == 1 || _lines % 2 == 0) PublishImage("KIWIFAX_IMAGE");
        }
    }

    private void PublishImage(string kind)
    {
        if (!_enabled || _lines <= 0 || _rgb.Count == 0) return;
        var width = _imageWidth;
        var height = _lines;
        var stride = width * 3;
        if (_rgb.Count < stride * height) return;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["width"] = width.ToString(CultureInfo.InvariantCulture),
            ["height"] = height.ToString(CultureInfo.InvariantCulture),
            ["stride"] = stride.ToString(CultureInfo.InvariantCulture),
            ["lines"] = height.ToString(CultureInfo.InvariantCulture),
            ["lpm"] = _lpm.ToString(CultureInfo.InvariantCulture)
        };
        var data = _rgb.ToArray();
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, $"{height} lines", DateTime.UtcNow,
            Fields: fields, BinaryData: data));
    }

    private void Publish(string kind, string text)
    {
        if (_enabled) ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, text, DateTime.UtcNow));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            _decoder.Dispose();
        }
    }

    private static bool Bool(IReadOnlyDictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max)
    {
        if (!options.TryGetValue(key, out var text) ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;
        return Math.Clamp(value, min, max);
    }

    private static string Text(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var text) ? text : fallback;
}
