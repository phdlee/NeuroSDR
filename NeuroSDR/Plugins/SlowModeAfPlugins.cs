using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using NeuroSDR.Dsp;
using EnsSstv.Host;
using EnsSstv.Host.WeatherFax;
using EnsSstv.SlowRx;

namespace NeuroSDR.Plugins;

internal sealed class SstvDecoderAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private ISlowRx? _decoder;
    private StreamingPcmResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private SlowRxBackend _backend = SlowRxBackend.ManagedCs;
    private bool _autoVis = true, _adaptive = true, _weakSignal, _slant = true, _median = true, _fskId = true;
    private int _manualMode, _frequencyShift;
    private long _lastPreviewMilliseconds;

    public AfPluginInfo Info { get; } = new(
        "builtin.af.sstv", "SSTV Decoder", "VIS/manual SSTV image decoder from EnsSstv.SlowRx",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            var nextEnabled = SlowModeOptions.Bool(options, "enabled", _enabled);
            var backendText = SlowModeOptions.Text(options, "backend", _backend == SlowRxBackend.ManagedHq ? "hq" : "managed");
            var nextBackend = backendText.Equals("hq", StringComparison.OrdinalIgnoreCase)
                ? SlowRxBackend.ManagedHq : SlowRxBackend.ManagedCs;
            _autoVis = SlowModeOptions.Bool(options, "autoVis", _autoVis);
            _adaptive = SlowModeOptions.Bool(options, "adaptive", _adaptive);
            _weakSignal = SlowModeOptions.Bool(options, "weakSignal", _weakSignal);
            _slant = SlowModeOptions.Bool(options, "slant", _slant);
            _median = SlowModeOptions.Bool(options, "median", _median);
            _fskId = SlowModeOptions.Bool(options, "fskId", _fskId);
            _manualMode = SlowModeOptions.Int(options, "manualMode", _manualMode, 0, 25);
            _frequencyShift = SlowModeOptions.Int(options, "frequencyShift", _frequencyShift, -500, 500);

            if (!nextEnabled)
            {
                _enabled = false;
                DisposeDecoder();
                return;
            }

            _enabled = true;
            if (_decoder is null || _backend != nextBackend)
            {
                DisposeDecoder();
                _backend = nextBackend;
                CreateDecoder();
            }
            ApplyOptions();

            var command = SlowModeOptions.Text(options, "command", "");
            if (command.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                if (_manualMode <= 0) _decoder!.SetAutoReceive(true);
                else _decoder!.ManualStart(_manualMode, _frequencyShift);
            }
            else if (command.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                     command.Equals("restart", StringComparison.OrdinalIgnoreCase))
            {
                DisposeDecoder();
                CreateDecoder();
                ApplyOptions();
            }
            else if (command.Equals("abort", StringComparison.OrdinalIgnoreCase))
            {
                _decoder!.Abort();
            }
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled || _decoder is null) return null;
            if (_resampler is null || _sourceRate != block.SampleRate)
            {
                _sourceRate = block.SampleRate;
                _resampler = new StreamingPcmResampler(block.SampleRate, 44_100);
            }
            var pcm = _resampler.Process(block.Input.Span);
            if (pcm.Length > 0) _decoder.PushPcm(pcm, pcm.Length);
        }
        return null;
    }

    private void CreateDecoder()
    {
        var decoder = SlowRxFactory.Create(_backend);
        decoder.ImageUpdated += OnImageUpdated;
        decoder.PictureDone += OnPictureDone;
        decoder.StatusChanged += status => Publish("STATUS", status);
        decoder.VisDetected += (mode, shift, name, manual) => Publish("SSTV_VIS", name,
            new Dictionary<string, string>
            {
                ["mode"] = mode.ToString(CultureInfo.InvariantCulture),
                ["shift"] = shift.ToString(CultureInfo.InvariantCulture),
                ["name"] = name,
                ["manual"] = manual.ToString()
            });
        decoder.FskIdReceived += id => Publish("SSTV_FSK_ID", id);
        decoder.ReceptionEnded += (finished, mode) => Publish("SSTV_END", finished != 0 ? "complete" : "stopped",
            new Dictionary<string, string> { ["mode"] = mode.ToString(CultureInfo.InvariantCulture) });
        if (decoder.Init() != 0 || decoder.Start() != 0)
        {
            decoder.Dispose();
            throw new InvalidOperationException("SSTV decoder initialization failed.");
        }
        decoder.SetRingDropOnFull(true);
        decoder.SetPreviewEnabled(true);
        _decoder = decoder;
        _resampler?.Reset();
        Publish("STATUS", $"Ready · {decoder.BackendName}");
    }

    private void ApplyOptions()
    {
        if (_decoder is null) return;
        _decoder.SetAdaptive(_adaptive);
        _decoder.SetAutoReceive(_autoVis || _manualMode <= 0);
        _decoder.SetEnableFsk(_fskId);
        _decoder.SetEnableSlant(_slant);
        _decoder.SetWeakSignalMode(_weakSignal);
        _decoder.SetMedianFilter(_median);
        _decoder.SetFastDemod(false);
        _decoder.SetDownsample(false);
    }

    private void OnImageUpdated(byte[] rgb, int width, int height, int stride)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastPreviewMilliseconds) < 250) return;
        Interlocked.Exchange(ref _lastPreviewMilliseconds, now);
        PublishImage(rgb, width, height, stride, false, _decoder?.LastMode ?? 0, "", "");
    }

    private void OnPictureDone(byte[] rgb, int width, int height, int stride, int mode, string name, string fsk) =>
        PublishImage(rgb, width, height, stride, true, mode, name, fsk);

    private void PublishImage(byte[] rgb, int width, int height, int stride, bool complete, int mode, string name, string fsk)
    {
        if (!_enabled || rgb.Length == 0) return;
        var fields = new Dictionary<string, string>
        {
            ["width"] = width.ToString(CultureInfo.InvariantCulture),
            ["height"] = height.ToString(CultureInfo.InvariantCulture),
            ["stride"] = stride.ToString(CultureInfo.InvariantCulture),
            ["complete"] = complete.ToString(),
            ["mode"] = mode.ToString(CultureInfo.InvariantCulture),
            ["name"] = name,
            ["fsk"] = fsk
        };
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "SSTV_IMAGE", name, DateTime.UtcNow,
            Fields: fields, BinaryData: (byte[])rgb.Clone()));
    }

    private void Publish(string kind, string text, IReadOnlyDictionary<string, string>? fields = null)
    {
        if (_enabled) ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, text, DateTime.UtcNow, Fields: fields));
    }

    private void DisposeDecoder()
    {
        var decoder = _decoder;
        _decoder = null;
        if (decoder is null) return;
        try { decoder.Dispose(); } catch { }
        _resampler?.Reset();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            DisposeDecoder();
        }
    }
}

internal sealed class RttyDecoderAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly RttyDecoder _decoder = new();
    private bool _enabled;
    private RttyConfig _configuration = new();
    private bool _configured;

    public RttyDecoderAfPlugin()
    {
        _decoder.CharacterReceived += character => Publish("RTTY_CHAR", character.ToString(),
            new Dictionary<string, string> { ["character"] = character.ToString() });
        _decoder.StateChanged += state => Publish("RTTY_STATE", state.ToString());
        _decoder.InverseChanged += inverse => Publish("RTTY_INVERSE", inverse.ToString());
        _decoder.Bell += () => Publish("RTTY_BELL", "Bell");
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.rtty", "RTTY (FSK) Decoder", "ITA-2/Baudot FSK decoder from EnsSstv",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);
    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            var nextEnabled = SlowModeOptions.Bool(options, "enabled", _enabled);
            var command = SlowModeOptions.Text(options, "command", "");
            var next = new RttyConfig
            {
                SampleRate = 48_000,
                BaudRate = SlowModeOptions.Double(options, "baud", _configuration.BaudRate, 10, 300),
                CenterFrequencyHz = SlowModeOptions.Double(options, "center", _configuration.CenterFrequencyHz, 200, 3_000),
                DeviationHz = SlowModeOptions.Double(options, "deviation", _configuration.DeviationHz, 10, 700),
                LowpassFilterHz = _configuration.LowpassFilterHz,
                Inverse = SlowModeOptions.Bool(options, "inverse", _configuration.Inverse),
                AudioMinimum = _configuration.AudioMinimum,
                StopBitUnits = SlowModeOptions.Double(options, "stopBits", _configuration.StopBitUnits, 1, 2),
                UnshiftOnSpace = SlowModeOptions.Bool(options, "usos", _configuration.UnshiftOnSpace),
                UnshiftOnError = SlowModeOptions.Bool(options, "unshiftOnError", _configuration.UnshiftOnError),
                AutoPolarity = SlowModeOptions.Bool(options, "autoPolarity", _configuration.AutoPolarity)
            };
            var changed = !_configured || !SlowModeOptions.RttyEquals(_configuration, next);
            _configuration = next;
            _enabled = nextEnabled;
            if (changed || command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _decoder.Configure(next);
                _configured = true;
            }
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase)) _decoder.ClearText();
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled) return null;
            if (_configuration.SampleRate != block.SampleRate)
            {
                _configuration.SampleRate = block.SampleRate;
                _decoder.Configure(_configuration);
                _configured = true;
            }
            _decoder.ProcessSamples(FtxDecoderAfPlugin.ToPcm16(block.Input.Span));
        }
        return null;
    }

    private void Publish(string kind, string text, IReadOnlyDictionary<string, string>? fields = null)
    {
        if (_enabled) ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, text, DateTime.UtcNow, Fields: fields));
    }

    internal RttyConfig ConfigurationForVerification => _configuration;
    public void Dispose() { }
}

internal sealed class WeatherFaxDecoderAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly WeatherFaxDecoder _decoder = new();
    private StreamingPcmResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private WeatherFaxConfig _configuration = new();

    public WeatherFaxDecoderAfPlugin()
    {
        _decoder.StatusMessage += status => Publish("STATUS", status);
        _decoder.StateChanged += state => Publish("WEFAX_STATE", state.ToString());
        _decoder.LineAdded += lines =>
        {
            if (lines == 1 || lines % 4 == 0) PublishImage("WEFAX_IMAGE");
        };
        _decoder.ChartFinished += () => PublishImage("WEFAX_COMPLETE");
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.weatherfax", "Weather Fax Decoder", "IOC-576 HF weather fax decoder from EnsSstv",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);
    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            var nextEnabled = SlowModeOptions.Bool(options, "enabled", _enabled);
            var next = new WeatherFaxConfig
            {
                SampleRate = 24_000,
                ImageWidth = SlowModeOptions.Int(options, "width", _configuration.ImageWidth, 320, 2_400),
                LinesPerSecond = SlowModeOptions.Int(options, "lps", _configuration.LinesPerSecond, 1, 2),
                SyncSeconds = 12,
                QuickSyncSeconds = 5,
                Calibration = SlowModeOptions.Double(options, "calibration", _configuration.Calibration, -.05, .05),
                Grayscale = SlowModeOptions.Bool(options, "grayscale", _configuration.Grayscale),
                VideoFilter = SlowModeOptions.Bool(options, "videoFilter", _configuration.VideoFilter),
                MaxImageLines = 4_000,
                GainThreshold = 40,
                PllCenterHz = 1_900,
                PllDeviationHz = 400,
                StartToneHz = 300,
                StopToneHz = 450,
                GoertzelAccept = .35,
                SkipStartTone = SlowModeOptions.Bool(options, "skipStartTone", _configuration.SkipStartTone),
                NoToneTimeoutSeconds = 2,
                AutoSlantCorrect = false
            };
            var changed = !SlowModeOptions.WeatherEquals(_configuration, next);
            _configuration = next;
            if (changed || nextEnabled != _enabled)
            {
                _enabled = nextEnabled;
                _decoder.Configure(next);
                if (_enabled) _decoder.Start();
            }
            var command = SlowModeOptions.Text(options, "command", "");
            if (!_enabled) return;
            if (command.Equals("lock", StringComparison.OrdinalIgnoreCase)) _decoder.ForceLock();
            else if (command.Equals("immediate", StringComparison.OrdinalIgnoreCase)) _decoder.ForceLockImmediate();
            else if (command.Equals("unlock", StringComparison.OrdinalIgnoreCase)) _decoder.Unlock();
            else if (command.Equals("restart", StringComparison.OrdinalIgnoreCase)) _decoder.Start();
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
                _resampler = new StreamingPcmResampler(block.SampleRate, 24_000);
            }
            var pcm = _resampler.Process(block.Input.Span);
            if (pcm.Length > 0) _decoder.ProcessSamples(pcm);
        }
        return null;
    }

    private void PublishImage(string kind)
    {
        if (!_enabled) return;
        using var bitmap = _decoder.Image.ToBitmap();
        if (bitmap is null) return;
        var rgb = SlowModeImage.ToRgb(bitmap, out var stride);
        var fields = new Dictionary<string, string>
        {
            ["width"] = bitmap.Width.ToString(CultureInfo.InvariantCulture),
            ["height"] = bitmap.Height.ToString(CultureInfo.InvariantCulture),
            ["stride"] = stride.ToString(CultureInfo.InvariantCulture),
            ["lines"] = _decoder.BufferHeight.ToString(CultureInfo.InvariantCulture),
            ["lpm"] = (_decoder.LinesPerSecond * 60).ToString(CultureInfo.InvariantCulture)
        };
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, $"{_decoder.BufferHeight} lines",
            DateTime.UtcNow, Fields: fields, BinaryData: rgb));
    }

    private void Publish(string kind, string text)
    {
        if (_enabled) ResultAvailable?.Invoke(new AfPluginResult(Info.Id, kind, text, DateTime.UtcNow));
    }

    internal WeatherFaxConfig ConfigurationForVerification => _configuration;

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            _decoder.Stop();
        }
    }
}

internal static class SlowModeImage
{
    public static byte[] ToRgb(Bitmap bitmap, out int rgbStride)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            rgbStride = bitmap.Width * 3;
            var rgb = new byte[rgbStride * bitmap.Height];
            var source = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, source, 0, source.Length);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var sourceIndex = x * 3;
                    var targetIndex = y * rgbStride + sourceIndex;
                    rgb[targetIndex] = source[sourceIndex + 2];
                    rgb[targetIndex + 1] = source[sourceIndex + 1];
                    rgb[targetIndex + 2] = source[sourceIndex];
                }
            }
            return rgb;
        }
        finally { bitmap.UnlockBits(data); }
    }
}

internal static class SlowModeOptions
{
    public static string Text(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var value) ? value : fallback;

    public static bool Bool(IReadOnlyDictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    public static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback, int minimum, int maximum) =>
        options.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum) : fallback;

    public static double Double(IReadOnlyDictionary<string, string> options, string key, double fallback, double minimum, double maximum) =>
        options.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum) : fallback;

    public static bool WeatherEquals(WeatherFaxConfig left, WeatherFaxConfig right) =>
        left.SampleRate == right.SampleRate && left.ImageWidth == right.ImageWidth &&
        left.LinesPerSecond == right.LinesPerSecond && Math.Abs(left.Calibration - right.Calibration) < 1e-9 &&
        left.Grayscale == right.Grayscale && left.VideoFilter == right.VideoFilter && left.SkipStartTone == right.SkipStartTone;

    public static bool RttyEquals(RttyConfig left, RttyConfig right) =>
        left.SampleRate == right.SampleRate && Math.Abs(left.BaudRate - right.BaudRate) < 1e-9 &&
        Math.Abs(left.CenterFrequencyHz - right.CenterFrequencyHz) < 1e-9 &&
        Math.Abs(left.DeviationHz - right.DeviationHz) < 1e-9 && left.Inverse == right.Inverse &&
        Math.Abs(left.StopBitUnits - right.StopBitUnits) < 1e-9 && left.UnshiftOnSpace == right.UnshiftOnSpace &&
        left.UnshiftOnError == right.UnshiftOnError && left.AutoPolarity == right.AutoPolarity;
}
