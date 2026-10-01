using System.Globalization;
using System.Reflection;
using NeuroSDR.Controls;
using NeuroSDR.Recording;
using SherpaOnnx;

namespace NeuroSDR.Plugins.Caption;

internal sealed class NeuroCaptionAfPlugin : IAfPlugin, IAfVisualPlugin
{
    public const string PluginId = NeuroCaption.PluginId;

    private readonly object _sync = new();
    private readonly List<float> _clip = [];
    private readonly SpeechActivityGate _gate = new() { Sensitivity = NeuroCaption.DefaultGate };
    private string _engine = NeuroCaption.Groq;
    private string _groqKey = "";
    private string _groqModel = GroqWhisperClient.DefaultModel;
    private string _whisperHost = LocalWhisperClient.DefaultHost;
    private int _whisperPort = LocalWhisperClient.DefaultPort;
    private bool _whisperHttps;
    private string _whisperKey = "";
    private string _whisperModel = LocalWhisperClient.DefaultModel;
    private string _onnxModelId = SherpaCaptionModels.DefaultId;
    private OfflineRecognizer? _recognizer;
    private string _loadedKey = "";
    private string _language = "auto";
    private int _chunkSeconds = GroqCaptionQuota.DefaultChunkSeconds;
    private bool _analyze;
    private string _translateEngine = "off";
    private string _translateTo = "ko";
    private string _displayMode = "original";
    private string _lastCaption = "";
    private int _sampleRate = 12_000;
    private int _silenceSamples;
    private int _speechSamples;
    private int _busy;
    private int _epoch;
    private NeuroCaptionPluginView? _view;
    private DateTime _lastHearbeatUtc = DateTime.MinValue;
    private bool _warnedSetup;
    private volatile bool _languageProbe;
    private string _lastHeardLanguage = "";
    private DateTime _lastHeardUtc = DateTime.MinValue;
    private PendingClip? _pendingProbeClip;

    public AfPluginInfo Info { get; } = new(
        PluginId,
        "NeuroCaption",
        "Speech-to-text captions: Groq, personal Whisper server, or local ONNX.",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults,
        IsBuiltIn: true);

    public event Action<AfPluginResult>? ResultAvailable;

    public Control CreateView(IAfPluginUiHost ui)
    {
        _view = new NeuroCaptionPluginView();
        _view.OptionsChanged += () =>
        {
            var opts = _view.BuildOptions();
            ui.SaveOptions(opts);
            ui.Configure(opts);
        };
        var loaded = ui.LoadOptions();
        _view.LoadOptions(loaded);
        Configure(loaded);
        return _view;
    }

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            if (options.TryGetValue("command", out var command) &&
                command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _clip.Clear();
                _silenceSamples = 0;
                _speechSamples = 0;
                _lastCaption = "";
                _gate.Reset();
                Interlocked.Increment(ref _epoch);
            }

            if (options.TryGetValue("analyze", out var analyzeText) && bool.TryParse(analyzeText, out var analyze))
                _analyze = analyze;

            // Host Apply / frequency reset send stub bags ({enabled,mode} or {command}).
            // Those must not rewrite language, translate, gate, or chunk back to defaults.
            if (!IsCaptionSettingsBag(options))
                return;

            if (options.TryGetValue("engine", out var engine))
                _engine = NeuroCaption.NormalizeEngine(engine);
            if (options.TryGetValue("apiKey", out var key))
            {
                _groqKey = key.Trim();
                _warnedSetup = false;
            }
            if (options.TryGetValue("groqModel", out var groqModel) && !string.IsNullOrWhiteSpace(groqModel))
                _groqModel = groqModel.Trim();
            else if (options.TryGetValue("model", out var legacyModel) &&
                     _engine == NeuroCaption.Groq && !string.IsNullOrWhiteSpace(legacyModel))
                _groqModel = legacyModel.Trim();
            if (options.TryGetValue("host", out var host) && !string.IsNullOrWhiteSpace(host))
            {
                _whisperHost = host.Trim();
                _warnedSetup = false;
            }
            if (options.TryGetValue("port", out var portText) &&
                int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                _whisperPort = Math.Clamp(port, 1, 65_535);
            if (options.TryGetValue("https", out var httpsText) && bool.TryParse(httpsText, out var https))
                _whisperHttps = https;
            if (options.TryGetValue("whisperKey", out var whisperKey))
                _whisperKey = whisperKey.Trim();
            if (options.TryGetValue("whisperModel", out var whisperModel) && !string.IsNullOrWhiteSpace(whisperModel))
                _whisperModel = whisperModel.Trim();
            else if (options.TryGetValue("model", out var serverModel) &&
                     _engine == NeuroCaption.Whisper && !string.IsNullOrWhiteSpace(serverModel))
                _whisperModel = serverModel.Trim();
            if (options.TryGetValue("onnxModel", out var onnx) && !string.IsNullOrWhiteSpace(onnx))
                _onnxModelId = onnx.Trim();
            else if (options.TryGetValue("modelId", out var modelId) && !string.IsNullOrWhiteSpace(modelId))
                _onnxModelId = modelId.Trim();
            _language = NeuroCaption.Get(options, _engine, "language", "auto");
            _chunkSeconds = NeuroCaption.ChunkSeconds(options, _engine);
            _gate.Sensitivity = NeuroCaption.GateLevel(NeuroCaption.Get(options, _engine, "speechGate",
                NeuroCaption.DefaultGate.ToString(CultureInfo.InvariantCulture)));
            _gate.Enabled = bool.TryParse(NeuroCaption.Get(options, _engine, "speechGateEnabled", "False"), out var gateOn) && gateOn;
            _translateEngine = NeuroCaption.Get(options, _engine, "translateEngine", "off").Trim().ToLowerInvariant();
            _translateTo = NeuroCaption.Get(options, _engine, "translateTo", "ko").Trim();
            _displayMode = NeuroCaption.DefaultDisplayMode(_translateEngine,
                NeuroCaption.Get(options, _engine, "displayMode", "original"));
            if (_engine == NeuroCaption.Onnx)
            {
                try { TryLoadRecognizer(); }
                catch (Exception exception)
                {
                    ResultAvailable?.Invoke(new AfPluginResult(PluginId, "ERROR",
                        exception.GetBaseException().Message, DateTime.UtcNow));
                }
            }
        }
        var ticker = options.TryGetValue("afTicker", out var tickerText) ? tickerText : "";
        if (ticker.Length > 0)
            ResultAvailable?.Invoke(new AfPluginResult(PluginId, "OPTIONS", ticker, DateTime.UtcNow,
                Fields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["afTicker"] = ticker }));
    }

    private static bool IsCaptionSettingsBag(IReadOnlyDictionary<string, string> options)
    {
        foreach (var key in options.Keys)
        {
            if (key.Equals("command", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("enabled", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("mode", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("timeAdjustSeconds", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("autoTimeAdjust", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("analyze", StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }
        return false;
    }

    public void SetLanguageProbe(bool on)
    {
        lock (_sync)
        {
            _languageProbe = on;
            _clip.Clear();
            _silenceSamples = 0;
            _speechSamples = 0;
            _lastHeardLanguage = "";
            _lastHeardUtc = DateTime.MinValue;
            _pendingProbeClip = null;
            _gate.Reset();
            Interlocked.Increment(ref _epoch);
        }
    }

    public string LastHeardLanguage
    {
        get
        {
            lock (_sync) return _lastHeardLanguage;
        }
    }

    public DateTime LastHeardUtc
    {
        get
        {
            lock (_sync) return _lastHeardUtc;
        }
    }

    public bool IsReady => ReadyForEngine(out _);

    public AfPluginResult? Process(AfAudioBlock block)
    {
        if (!_analyze || block.Input.Length == 0) return null;
        if (!ReadyForEngine(out var setupError))
        {
            if (!_warnedSetup)
            {
                _warnedSetup = true;
                ResultAvailable?.Invoke(new AfPluginResult(PluginId, "ERROR", setupError, DateTime.UtcNow));
            }
            return null;
        }

        float[]? flush = null;
        int rate;
        int buffered;
        bool speech;
        lock (_sync)
        {
            _sampleRate = Math.Max(1, block.SampleRate);
            var input = block.Input.Span;
            speech = _languageProbe || _gate.Observe(input, _sampleRate);
            var chunk = _languageProbe ? 5 : _chunkSeconds;
            var maxSamples = _sampleRate * chunk;
            var hangSamples = Math.Max(_sampleRate / 4, _sampleRate / 2);
            if (speech)
            {
                _silenceSamples = 0;
                _speechSamples += input.Length;
                Append(input, maxSamples);
            }
            else if (_clip.Count > 0)
            {
                _silenceSamples += input.Length;
                if (_silenceSamples >= hangSamples)
                    flush = _speechSamples >= _sampleRate / 2 ? TakeClip() : DiscardClip();
            }
            if (flush is null && _clip.Count >= maxSamples &&
                (_languageProbe || _speechSamples >= _sampleRate / 2))
                flush = TakeClip();
            rate = _sampleRate;
            buffered = _clip.Count;
        }

        var now = DateTime.UtcNow;
        if (now - _lastHearbeatUtc > TimeSpan.FromSeconds(2) && flush is null)
        {
            _lastHearbeatUtc = now;
            var secs = buffered / (double)Math.Max(1, rate);
            ResultAvailable?.Invoke(new AfPluginResult(PluginId, "STATUS",
                speech
                    ? (_gate.Enabled
                        ? $"{EngineLabel()} · speech · {secs:0.0}s / {_chunkSeconds}s"
                        : $"{EngineLabel()} · GATE off · {secs:0.0}s / {_chunkSeconds}s")
                    : $"{EngineLabel()} · listening (GATE on)",
                now));
        }

        if (flush is null || flush.Length < rate / 2) return null;
        var snapshot = Snapshot();
        var epoch = _epoch;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            if (_languageProbe)
            {
                lock (_sync)
                    _pendingProbeClip = new PendingClip(flush, rate, snapshot, epoch);
            }
            return null;
        }
        _ = Task.Run(() => SendClipAsync(flush, rate, snapshot, epoch));
        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    private bool ReadyForEngine(out string error)
    {
        error = "";
        if (_engine == NeuroCaption.Groq && string.IsNullOrWhiteSpace(_groqKey))
        {
            error = "SETUP: Groq API key is empty";
            return false;
        }
        if (_engine == NeuroCaption.Whisper && string.IsNullOrWhiteSpace(_whisperHost))
        {
            error = "SETUP: Whisper server host is empty";
            return false;
        }
        if (_engine == NeuroCaption.Onnx && !SherpaCaptionModels.IsReady(_onnxModelId))
        {
            error = "SETUP: download ONNX Whisper INT8 (GET)";
            return false;
        }
        return true;
    }

    private string EngineLabel() => _engine switch
    {
        NeuroCaption.Whisper => $"Whisper {_whisperHost}:{_whisperPort}",
        NeuroCaption.Onnx => $"ONNX {_onnxModelId}",
        _ => "Groq"
    };

    private EngineSnapshot Snapshot() => new(
        _engine, _groqKey, _groqModel, _whisperHost, _whisperPort, _whisperHttps, _whisperKey, _whisperModel,
        _onnxModelId, _languageProbe ? "auto" : _language, _translateEngine, _translateTo, _displayMode);

    private async Task SendClipAsync(float[] samples, int sampleRate, EngineSnapshot snap, int epoch)
    {
        try
        {
            if (!IsCurrentEpoch(epoch)) return;
            ResultAvailable?.Invoke(new AfPluginResult(PluginId, "STATUS",
                $"{snap.Engine} · {samples.Length / (double)sampleRate:0.0}s", DateTime.UtcNow));
            string original;
            var detected = "";
            if (snap.Engine == NeuroCaption.Onnx)
            {
                var clip = DecodeOnnx(samples, sampleRate);
                original = clip.Text;
                detected = clip.Language;
            }
            else
            {
                var wav = AfAudioFileWriter.ToWavBytes(sampleRate, ToPcm16(samples));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var whisperTranslate = snap.TranslateEngine.Equals("whisper", StringComparison.OrdinalIgnoreCase);
                SttClip clip;
                if (snap.Engine == NeuroCaption.Whisper)
                {
                    clip = await LocalWhisperClient.TranscribeAsync(
                        snap.WhisperHost, snap.WhisperPort, snap.WhisperHttps, snap.WhisperKey, wav,
                        "neurosdr-af.wav", snap.WhisperModel, snap.Language, whisperTranslate, cts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    clip = await GroqWhisperClient.TranscribeAsync(
                        snap.GroqKey, wav, "neurosdr-af.wav", snap.GroqModel, snap.Language, cts.Token)
                        .ConfigureAwait(false);
                }
                original = clip.Text;
                detected = clip.Language;
            }
            if (!IsCurrentEpoch(epoch)) return;
            original = CaptionText.SanitizeStt(original);
            if (original.Length == 0) return;
            if (string.IsNullOrWhiteSpace(detected) || detected == "auto")
                detected = CaptionLanguages.GuessFromText(original);
            lock (_sync)
            {
                if (detected.Length > 0 && detected != "auto")
                {
                    _lastHeardLanguage = CaptionLanguages.Canonical(detected);
                    _lastHeardUtc = DateTime.UtcNow;
                }
            }

            var translation = "";
            var sameLanguage = CaptionLanguages.Canonical(detected) is var heard &&
                               heard.Length > 0 && heard != "auto" &&
                               heard == CaptionLanguages.Canonical(snap.TranslateTo);
            if (!sameLanguage &&
                !snap.TranslateEngine.Equals("off", StringComparison.OrdinalIgnoreCase) &&
                !snap.TranslateEngine.Equals("whisper", StringComparison.OrdinalIgnoreCase))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                translation = await TranslateAsync(snap, original, detected, cts.Token).ConfigureAwait(false);
            }
            if (!IsCurrentEpoch(epoch)) return;
            translation = CaptionText.SanitizeStt(translation);
            if (sameLanguage || CaptionLanguages.Canonical(detected) == CaptionLanguages.Canonical(snap.TranslateTo))
                translation = "";
            var display = CaptionText.FormatDisplay(original, translation, snap.DisplayMode);
            if (display.Length == 0) return;
            lock (_sync)
            {
                if (CaptionText.SameCaption(display, _lastCaption)) return;
                _lastCaption = display;
            }
            ResultAvailable?.Invoke(new AfPluginResult(PluginId, "CAPTION", display, DateTime.UtcNow,
                Fields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["original"] = original,
                    ["translation"] = translation,
                    ["display"] = display,
                    ["engine"] = snap.Engine,
                    ["language"] = detected.Length > 0 ? detected : snap.Language
                }));
        }
        catch (Exception exception)
        {
            ResultAvailable?.Invoke(new AfPluginResult(PluginId, "ERROR",
                exception.GetBaseException().Message, DateTime.UtcNow));
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            TryStartPendingProbe();
        }
    }

    private void TryStartPendingProbe()
    {
        PendingClip? pending;
        lock (_sync)
        {
            if (!_languageProbe || _pendingProbeClip is null)
                return;
            pending = _pendingProbeClip;
            _pendingProbeClip = null;
        }
        if (!IsCurrentEpoch(pending.Value.Epoch))
            return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            lock (_sync)
            {
                if (_languageProbe)
                    _pendingProbeClip = pending;
            }
            return;
        }
        _ = Task.Run(() => SendClipAsync(
            pending.Value.Samples, pending.Value.SampleRate, pending.Value.Snapshot, pending.Value.Epoch));
    }

    private SttClip DecodeOnnx(float[] samples, int sampleRate)
    {
        lock (_sync)
        {
            TryLoadRecognizer();
            if (_recognizer is null)
                throw new InvalidOperationException("ONNX Whisper INT8 model is not downloaded.");
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            _recognizer.Decode(stream);
            var text = stream.Result.Text?.Trim() ?? "";
            var lang = CaptionLanguages.GuessFromText(text);
            try
            {
                var prop = stream.Result.GetType().GetProperty("Lang") ??
                           stream.Result.GetType().GetProperty("Language");
                var tagged = prop?.GetValue(stream.Result) as string;
                if (!string.IsNullOrWhiteSpace(tagged))
                    lang = CaptionLanguages.Canonical(tagged);
            }
            catch { }
            return new(text, lang is "auto" ? "" : lang);
        }
    }

    private void TryLoadRecognizer()
    {
        if (!SherpaCaptionModels.TryGetFiles(_onnxModelId, out var files))
        {
            _recognizer?.Dispose();
            _recognizer = null;
            _loadedKey = "";
            return;
        }
        var lang = GroqWhisperClient.NormalizeLang(_languageProbe ? "auto" : _language);
        if (lang == "auto") lang = "";
        var key = $"{files.Encoder}|{lang}";
        if (_recognizer is not null && _loadedKey == key) return;
        _recognizer?.Dispose();
        _recognizer = null;
        var config = new OfflineRecognizerConfig();
        var model = config.ModelConfig;
        var whisper = model.Whisper;
        whisper.Encoder = files.Encoder;
        whisper.Decoder = files.Decoder;
        whisper.Language = lang;
        whisper.Task = "transcribe";
        model.Whisper = whisper;
        model.Tokens = files.Tokens;
        model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        model.Provider = "cpu";
        config.ModelConfig = model;
        _recognizer = new OfflineRecognizer(config);
        _loadedKey = key;
        _warnedSetup = false;
    }

    private static async Task<string> TranslateAsync(EngineSnapshot snap, string original, string detected, CancellationToken ct)
    {
        var srcSetting = CaptionLanguages.Canonical(snap.Language);
        var src = srcSetting is "auto" or "" ? CaptionLanguages.Canonical(detected) : srcSetting;
        var tgt = CaptionLanguages.Canonical(snap.TranslateTo);
        if (tgt.Length == 0 || tgt == "auto") return "";
        if (src.Length > 0 && src != "auto" && src == tgt) return "";
        if (src is "auto" or "") src = "auto";
        if (snap.TranslateEngine.Equals("groq", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(snap.GroqKey))
        {
            var text = await GroqWhisperClient.TranslateChatAsync(snap.GroqKey, original, tgt, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return await GroqWhisperClient.TranslateMyMemoryAsync(original, src, tgt, ct).ConfigureAwait(false);
    }

    private void Append(ReadOnlySpan<float> input, int maxSamples)
    {
        for (var i = 0; i < input.Length; i++)
            _clip.Add(input[i]);
        if (_clip.Count <= maxSamples) return;
        var drop = _clip.Count - maxSamples;
        _clip.RemoveRange(0, drop);
        _speechSamples = Math.Max(0, _speechSamples - drop);
    }

    private float[] TakeClip()
    {
        var copy = _clip.ToArray();
        DiscardClip();
        return copy;
    }

    private float[]? DiscardClip()
    {
        _clip.Clear();
        _silenceSamples = 0;
        _speechSamples = 0;
        return null;
    }

    private bool IsCurrentEpoch(int epoch) => Volatile.Read(ref _epoch) == epoch;

    private static short[] ToPcm16(float[] samples)
    {
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            pcm[i] = (short)Math.Clamp(Math.Round(samples[i] * 32767), short.MinValue, short.MaxValue);
        return pcm;
    }

    private readonly record struct EngineSnapshot(
        string Engine, string GroqKey, string GroqModel, string WhisperHost, int WhisperPort, bool WhisperHttps,
        string WhisperKey, string WhisperModel, string OnnxModelId, string Language, string TranslateEngine,
        string TranslateTo, string DisplayMode);

    private readonly record struct PendingClip(
        float[] Samples, int SampleRate, EngineSnapshot Snapshot, int Epoch);
}
