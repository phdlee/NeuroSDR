using System.Globalization;
using System.Text;
using NeuroSDR.Dsp;

namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// AF plug-in: KiwiSDR CW_skimmer (FFT channelizer + Csdr CwDecoder per 100 Hz bin).
/// Id: <c>builtin.af.kiwicwskimmer</c>.
/// </summary>
internal sealed class KiwiCwSkimmerAfPlugin : IAfPlugin
{
    private readonly object _sync = new();
    private readonly Dictionary<int, StringBuilder> _byFreq = new();
    private KiwiCwSkimmerEngine? _engine;
    private StreamingPcmResampler? _resampler;
    private int _sourceRate;
    private bool _enabled;
    private int _pwrCalc = KiwiCwSkimmerEngine.PowerCalcAvgRatio;
    private bool _filterNeighbors;

    public KiwiCwSkimmerAfPlugin() { }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.kiwicwskimmer",
        "KiwiCWSkimmer",
        "Multi-channel CW skimmer (KiwiSDR CW_skimmer + Csdr CwDecoder)",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public event Action<AfPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _pwrCalc = (int)Double(options, "pwrCalc", _pwrCalc, 0, 2);
            _filterNeighbors = Bool(options, "filterNeighbors", _filterNeighbors);
            EnsureEngine().SetParams(_pwrCalc, _filterNeighbors);

            var command = Text(options, "command", "");
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase))
                _byFreq.Clear();
            if (command.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("flush", StringComparison.OrdinalIgnoreCase))
            {
                _byFreq.Clear();
                _engine?.Reset();
            }
        }
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_enabled) return null;
            var engine = EnsureEngine();
            if (_resampler is null || _sourceRate != block.SampleRate)
            {
                _sourceRate = block.SampleRate;
                _resampler = new StreamingPcmResampler(block.SampleRate, KiwiCwSkimmerEngine.NominalSampleRate);
            }
            var pcm = _resampler.Process(block.Input.Span);
            if (pcm.Length > 0) engine.ProcessPcm16(pcm);
        }
        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            if (_engine is not null)
                _engine.CharacterDecoded -= OnChar;
            _engine = null;
        }
    }

    private KiwiCwSkimmerEngine EnsureEngine()
    {
        if (_engine is not null) return _engine;
        _engine = new KiwiCwSkimmerEngine(KiwiCwSkimmerEngine.NominalSampleRate);
        _engine.SetParams(_pwrCalc, _filterNeighbors);
        _engine.CharacterDecoded += OnChar;
        return _engine;
    }

    private void OnChar(int freqHz, char ch, int wpm)
    {
        string line;
        lock (_sync)
        {
            if (!_enabled) return;
            if (!_byFreq.TryGetValue(freqHz, out var sb))
            {
                sb = new StringBuilder();
                _byFreq[freqHz] = sb;
            }
            sb.Append(ch);
            if (sb.Length > 400) sb.Remove(0, sb.Length - 300);
            line = sb.ToString();
        }

        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "SKIMMER_CHAR", ch.ToString(), DateTime.UtcNow,
            Fields: new Dictionary<string, string>
            {
                ["frequency"] = freqHz.ToString(CultureInfo.InvariantCulture),
                ["character"] = ch.ToString(),
                ["wpm"] = wpm.ToString(CultureInfo.InvariantCulture),
                ["line"] = line
            }));
    }

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
