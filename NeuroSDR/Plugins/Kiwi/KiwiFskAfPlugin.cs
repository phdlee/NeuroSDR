using System.Globalization;
using System.Text;
using NeuroSDR.Dsp;
using NeuroSDR.Plugins.Kiwi.Jnx;
using EnsSstv.Host;
using EnsSstv.Jnx;

namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// KiwiSDR FSK extension with selectable encodings.
/// ITA2 uses the proven EnsSstv <see cref="RttyDecoder"/> (48 kHz).
/// ASCII / DSC / Selcall use the JNX port; CCIR476 uses <see cref="NavtexDecoder"/>.
/// </summary>
internal sealed class KiwiFskAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly RttyDecoder _rtty = new();
    private readonly NavtexDecoder _navtex = new();
    private readonly JnxDecoder _jnx = new();
    private readonly StringBuilder _buffer = new();
    private StreamingPcmResampler? _navtexResampler;
    private int _navtexSourceRate;
    private bool _enabled;
    private double _centerHz = 1_000;
    private double _deviationHz = 85; // half-shift (AF markers)
    private double _baud = 45.45;
    private string _framing = "5N1.5";
    private bool _inverted;
    private JnxEncoding _encoding = JnxEncoding.Ita2;
    private bool _showRaw;
    private bool _showErrs;
    private double _rfHz;
    private double _stopBits = 1.5;

    public KiwiFskAfPlugin()
    {
        void OnText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_sync)
            {
                if (!_enabled) return;
                _buffer.Append(text);
                if (_buffer.Length > 12_000) _buffer.Remove(0, _buffer.Length - 8_000);
            }
            ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "KIWIFSK_CHAR", text, DateTime.UtcNow,
                Fields: new Dictionary<string, string>
                {
                    ["character"] = text,
                    ["text"] = BufferSnapshot(),
                    ["encoding"] = _encoding.ToString()
                }));
        }

        _rtty.CharacterReceived += ch => OnText(ch.ToString());
        _rtty.StateChanged += state => EmitStatus($"RTTY {state}");
        _navtex.CharacterReceived += ch => OnText(ch.ToString());
        _navtex.StateChanged += state => EmitStatus($"CCIR476 {state}");
        _jnx.TextReceived += OnText;
        _jnx.StatusChanged += s => EmitStatus($"JNX {s}");
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.kiwifsk", "KiwiFSK",
        "KiwiSDR FSK (ITA2/ASCII/CCIR476/DSC/Selcall)",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            ApplyPreset(Text(options, "preset", ""));
            _centerHz = Double(options, "center", _centerHz, 200, 3_000);
            if (options.ContainsKey("shift"))
                _deviationHz = Double(options, "shift", _deviationHz * 2, 20, 2_000) / 2;
            else if (options.ContainsKey("deviation"))
                _deviationHz = Double(options, "deviation", _deviationHz, 10, 1_000);
            _baud = Double(options, "baud", _baud, 10, 300);
            _framing = Text(options, "framing", _framing);
            _inverted = Bool(options, "inverse", _inverted);
            _encoding = ParseEncoding(Text(options, "encoding", _encoding.ToString()));
            _showRaw = Bool(options, "showRaw", _showRaw);
            _showErrs = Bool(options, "showErrs", _showErrs);
            _rfHz = Double(options, "rfHz", _rfHz, 0, 3e9);
            _stopBits = ParseStopBits(_framing);

            ApplyDecoderConfig();

            var command = Text(options, "command", "");
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase)) _buffer.Clear();
            if (command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _buffer.Clear();
                ApplyDecoderConfig();
            }

            if (_enabled)
                EmitStatus($"ready {_encoding} {_framing} {_baud:0.##}bd CF={_centerHz:0} DEV={_deviationHz:0} INV={_inverted}");
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled || block.Input.Length == 0) return null;
            switch (_encoding)
            {
                case JnxEncoding.Ita2:
                    _rtty.ProcessSamples(FtxDecoderAfPlugin.ToPcm16(block.Input.Span));
                    break;
                case JnxEncoding.Ccir476:
                {
                    if (_navtexResampler is null || _navtexSourceRate != block.SampleRate)
                    {
                        _navtexSourceRate = block.SampleRate;
                        _navtexResampler = new StreamingPcmResampler(block.SampleRate, 12_000);
                    }
                    var pcm = _navtexResampler.Process(block.Input.Span);
                    if (pcm.Length > 0) _navtex.ProcessSamples(pcm);
                    break;
                }
                default:
                    // ASCII / DSC / Selcall — JNX at host AF rate (typically 48 kHz)
                    _jnx.ProcessPcm16(FtxDecoderAfPlugin.ToPcm16(block.Input.Span));
                    break;
            }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            _jnx.Dispose();
        }
    }

    private void ApplyDecoderConfig(int sampleRate = 0)
    {
        if (sampleRate <= 0) sampleRate = AudioDemodulator.AudioSampleRate;
        switch (_encoding)
        {
            case JnxEncoding.Ita2:
                _rtty.Configure(new RttyConfig
                {
                    SampleRate = sampleRate,
                    BaudRate = _baud,
                    CenterFrequencyHz = _centerHz,
                    DeviationHz = _deviationHz,
                    Inverse = _inverted,
                    StopBitUnits = _stopBits,
                    AutoPolarity = true,
                    UnshiftOnError = true
                });
                break;
            case JnxEncoding.Ccir476:
                _navtex.Configure(new NavtexConfig
                {
                    SampleRate = 12_000,
                    BaudRate = _baud <= 0 ? 100 : _baud,
                    CenterFrequencyHz = _centerHz,
                    DeviationHz = _deviationHz,
                    Inverse = _inverted,
                    NavtexMessageFiltering = false
                });
                break;
            default:
                _jnx.Configure(new JnxConfig
                {
                    SampleRate = sampleRate,
                    CenterFrequencyHz = _centerHz,
                    ShiftHz = Math.Max(20, _deviationHz * 2),
                    BaudRate = _baud,
                    Framing = string.IsNullOrWhiteSpace(_framing) ? DefaultFraming(_encoding) : _framing,
                    Inverted = _inverted,
                    Encoding = _encoding,
                    ShowRaw = _showRaw,
                    ShowErrs = _showErrs,
                    GetFrequencyHz = () => _rfHz
                });
                break;
        }
    }

    private string BufferSnapshot()
    {
        lock (_sync) return _buffer.ToString();
    }

    private void EmitStatus(string text)
    {
        try { ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", text, DateTime.UtcNow)); } catch { }
    }

    private void ApplyPreset(string preset)
    {
        switch (preset.ToLowerInvariant())
        {
            case "ham":
                _baud = 45.45; _deviationHz = 85; _framing = "5N1.5"; _inverted = false; _encoding = JnxEncoding.Ita2;
                break;
            case "wx":
                _baud = 50; _deviationHz = 225; _framing = "5N1.5"; _inverted = true; _encoding = JnxEncoding.Ita2;
                break;
            case "sitor-b":
                _baud = 100; _deviationHz = 85; _framing = "4/7"; _inverted = false; _encoding = JnxEncoding.Ccir476;
                break;
            case "dsc":
                _baud = 100; _deviationHz = 85; _framing = "7/3"; _inverted = true; _encoding = JnxEncoding.Dsc;
                break;
            case "selcall":
                _baud = 100; _deviationHz = 85; _framing = "7/3"; _inverted = false; _encoding = JnxEncoding.Selcall;
                break;
        }
    }

    private static double ParseStopBits(string framing)
    {
        if (framing.EndsWith("1.5", StringComparison.Ordinal)) return 1.5;
        if (framing.EndsWith('2')) return 2;
        return 1;
    }

    private static string DefaultFraming(JnxEncoding encoding) => encoding switch
    {
        JnxEncoding.Ascii => "8N1",
        JnxEncoding.Ccir476 => "4/7",
        JnxEncoding.Dsc or JnxEncoding.Selcall => "7/3",
        _ => "5N1.5"
    };

    private static JnxEncoding ParseEncoding(string text) => text.Trim().ToUpperInvariant() switch
    {
        "ASCII" => JnxEncoding.Ascii,
        "CCIR476" or "CCIR-476" => JnxEncoding.Ccir476,
        "DSC" => JnxEncoding.Dsc,
        "SELCALL" => JnxEncoding.Selcall,
        _ => JnxEncoding.Ita2
    };

    private static bool Bool(IReadOnlyDictionary<string, string> o, string k, bool f) =>
        o.TryGetValue(k, out var t) && bool.TryParse(t, out var v) ? v : f;
    private static double Double(IReadOnlyDictionary<string, string> o, string k, double f, double min, double max)
    {
        if (!o.TryGetValue(k, out var t) || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return f;
        return Math.Clamp(v, min, max);
    }
    private static string Text(IReadOnlyDictionary<string, string> o, string k, string f) =>
        o.TryGetValue(k, out var t) && !string.IsNullOrWhiteSpace(t) ? t : f;
}
