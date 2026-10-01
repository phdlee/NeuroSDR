using System.Globalization;
using NeuroSDR.Settings;

namespace NeuroSDR.Plugins.Caption;

internal static class NeuroCaption
{
    public const string PluginId = "builtin.af.neurocaption";
    public const string Groq = "groq";
    public const string Whisper = "whisper";
    public const string Onnx = "onnx";
    public const int DefaultGate = 2;
    public const int GroqChunkSeconds = 20;
    public const int FastChunkSeconds = 4;

    public static readonly string[] Engines = [Groq, Whisper, Onnx];

    public static readonly string[] OldPluginIds =
    [
        "builtin.af.encaption",
        "builtin.af.groqwhisper",
        "builtin.af.whisper",
        "builtin.af.sherpacaption"
    ];

    public static string NormalizeEngine(string? value)
    {
        var engine = (value ?? Groq).Trim().ToLowerInvariant();
        return engine switch
        {
            "whisper" or "server" or "local" => Whisper,
            "onnx" or "sherpa" or "int8" => Onnx,
            _ => Groq
        };
    }

    public static bool IsLegacyCaption(string? pluginId) =>
        pluginId is not null && OldPluginIds.Any(id => id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));

    public static bool IsCaptionPlugin(string? pluginId) =>
        pluginId is not null &&
        (pluginId.Equals(PluginId, StringComparison.OrdinalIgnoreCase) || IsLegacyCaption(pluginId));

    public static int GateLevel(string? text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gate))
            return Math.Clamp(gate, 1, 10);
        return DefaultGate;
    }

    public static void MigrateSettings(AppSettings settings)
    {
        MergeUiState(settings.AfPluginUiState);
        ConvertInstances(settings.AfPluginInstances);
        foreach (var scene in settings.RxScenes ?? [])
        {
            MergeUiState(scene.AfPluginUiState);
            ConvertInstances(scene.AfPluginInstances);
        }
    }

    private static void ConvertInstances(List<AfPluginInstanceSettings>? instances)
    {
        if (instances is null) return;
        var keep = true;
        instances.RemoveAll(instance =>
        {
            if (!IsLegacyCaption(instance.PluginId) &&
                !instance.PluginId.Equals(PluginId, StringComparison.OrdinalIgnoreCase))
                return false;
            instance.PluginId = PluginId;
            if (keep)
            {
                keep = false;
                return false;
            }
            return true;
        });
    }

    private static void MergeUiState(Dictionary<string, Dictionary<string, string>>? state)
    {
        if (state is null) return;
        var merged = state.TryGetValue(PluginId, out var existing)
            ? new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hadGroq = CopyBag(state, "builtin.af.groqwhisper", merged);
        var hadWhisper = CopyBag(state, "builtin.af.whisper", merged);
        var hadOnnx = CopyBag(state, "builtin.af.sherpacaption", merged);
        var chosen = merged.GetValueOrDefault("engine", "");
        if (string.IsNullOrWhiteSpace(chosen))
        {
            if (!string.IsNullOrWhiteSpace(merged.GetValueOrDefault("apiKey"))) chosen = Groq;
            else if (!string.IsNullOrWhiteSpace(merged.GetValueOrDefault("host"))) chosen = Whisper;
            else if (hadGroq) chosen = Groq;
            else if (hadWhisper) chosen = Whisper;
            else if (hadOnnx) chosen = Onnx;
            else chosen = Groq;
        }
        merged["engine"] = NormalizeEngine(chosen);
        if (!merged.ContainsKey("speechGateEnabled")) merged["speechGateEnabled"] = "False";
        if (!merged.ContainsKey("speechGate"))
            merged["speechGate"] = DefaultGate.ToString(CultureInfo.InvariantCulture);
        if (merged.ContainsKey("model") && !merged.ContainsKey("groqModel") && merged["engine"] == Groq)
            merged["groqModel"] = merged["model"];
        if (merged.ContainsKey("model") && !merged.ContainsKey("whisperModel") && merged["engine"] == Whisper)
            merged["whisperModel"] = merged["model"];
        if (merged.ContainsKey("modelId") && !merged.ContainsKey("onnxModel"))
            merged["onnxModel"] = merged["modelId"];
        EnsureEngineOptions(merged);
        state[PluginId] = merged;
    }

    private static bool CopyBag(
        Dictionary<string, Dictionary<string, string>> state, string oldId,
        Dictionary<string, string> merged)
    {
        if (!state.Remove(oldId, out var bag) || bag is null) return false;
        foreach (var pair in bag)
            merged[pair.Key] = pair.Value;
        return true;
    }

    public static string Prefixed(string engine, string name) =>
        $"{NormalizeEngine(engine)}.{name}";

    public static void EnsureEngineOptions(Dictionary<string, string> options)
    {
        var active = NormalizeEngine(options.GetValueOrDefault("engine"));
        options["engine"] = active;
        foreach (var engine in Engines)
        {
            var isActive = engine == active;
            Seed(options, engine, "chunkSeconds",
                isActive && options.TryGetValue("chunkSeconds", out var sharedChunk) && sharedChunk.Length > 0
                    ? sharedChunk
                    : DefaultChunk(engine).ToString(CultureInfo.InvariantCulture));
            Seed(options, engine, "language", isActive ? options.GetValueOrDefault("language", "auto") : "auto");
            Seed(options, engine, "speechGate",
                isActive
                    ? options.GetValueOrDefault("speechGate", DefaultGate.ToString(CultureInfo.InvariantCulture))
                    : DefaultGate.ToString(CultureInfo.InvariantCulture));
            Seed(options, engine, "speechGateEnabled",
                isActive ? options.GetValueOrDefault("speechGateEnabled", "False") : "False");
            Seed(options, engine, "showTime",
                isActive ? options.GetValueOrDefault("showTime", "False") : "False");
            Seed(options, engine, "autoWrap",
                isActive ? options.GetValueOrDefault("autoWrap", "True") : "True");
            Seed(options, engine, "afTicker",
                isActive ? options.GetValueOrDefault("afTicker", "False") : "False");
            var translate = isActive ? options.GetValueOrDefault("translateEngine", "off") : "off";
            if (string.IsNullOrWhiteSpace(translate)) translate = "off";
            Seed(options, engine, "translateEngine", translate);
            Seed(options, engine, "translateTo", isActive ? options.GetValueOrDefault("translateTo", "ko") : "ko");
            var show = isActive ? options.GetValueOrDefault("displayMode", "") : "";
            Seed(options, engine, "displayMode", DefaultDisplayMode(Get(options, engine, "translateEngine", translate), show));
        }
        WriteActiveAliases(options, active);
    }

    public static void WriteActiveAliases(Dictionary<string, string> options, string engine)
    {
        engine = NormalizeEngine(engine);
        options["engine"] = engine;
        options["chunkSeconds"] = ChunkSeconds(options, engine).ToString(CultureInfo.InvariantCulture);
        options["language"] = Get(options, engine, "language", "auto");
        options["speechGate"] = Get(options, engine, "speechGate", DefaultGate.ToString(CultureInfo.InvariantCulture));
        options["speechGateEnabled"] = Get(options, engine, "speechGateEnabled", "False");
        options["showTime"] = Get(options, engine, "showTime", "False");
        options["autoWrap"] = Get(options, engine, "autoWrap", "True");
        options["afTicker"] = Get(options, engine, "afTicker", "False");
        var translate = Get(options, engine, "translateEngine", "off");
        options["translateEngine"] = translate;
        options["translateTo"] = Get(options, engine, "translateTo", "ko");
        options["displayMode"] = DefaultDisplayMode(translate, Get(options, engine, "displayMode", ""));
    }

    public static string Get(IReadOnlyDictionary<string, string> options, string engine, string name, string fallback)
    {
        if (options.TryGetValue(Prefixed(engine, name), out var prefixed) &&
            !string.IsNullOrWhiteSpace(prefixed))
            return prefixed;
        var active = NormalizeEngine(options.GetValueOrDefault("engine"));
        if (NormalizeEngine(engine) == active &&
            options.TryGetValue(name, out var shared) &&
            !string.IsNullOrWhiteSpace(shared))
            return shared;
        return fallback;
    }

    public static void Put(Dictionary<string, string> options, string engine, string name, string value)
    {
        engine = NormalizeEngine(engine);
        options[Prefixed(engine, name)] = value;
        if (NormalizeEngine(options.GetValueOrDefault("engine")) == engine)
            options[name] = value;
    }

    public static int DefaultChunk(string engine) =>
        NormalizeEngine(engine) == Groq ? GroqChunkSeconds : FastChunkSeconds;

    public static int ClampChunk(string engine, int seconds)
    {
        engine = NormalizeEngine(engine);
        if (engine == Groq)
            return seconds <= 4 ? GroqChunkSeconds : Math.Clamp(seconds, 6, 20);
        return Math.Clamp(seconds, 4, 20);
    }

    public static int ChunkSeconds(IReadOnlyDictionary<string, string> options, string engine)
    {
        engine = NormalizeEngine(engine);
        var raw = Get(options, engine, "chunkSeconds", "");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return DefaultChunk(engine);
        return ClampChunk(engine, seconds);
    }

    public static string DefaultDisplayMode(string? translateEngine, string? displayMode)
    {
        var translate = (translateEngine ?? "off").Trim().ToLowerInvariant();
        var show = (displayMode ?? "").Trim().ToLowerInvariant();
        if (show is "translation" or "translated") show = "translation";
        if (translate is "" or "off")
            return show is "translation" or "both" ? show : "original";
        if (show is "translation" or "both") return show;
        return "both";
    }

    public static bool HasGroqKey(IReadOnlyDictionary<string, string> options) =>
        !string.IsNullOrWhiteSpace(options.GetValueOrDefault("apiKey"));

    public static bool CaptionOn(IReadOnlyDictionary<string, string>? options) =>
        options is not null &&
        options.TryGetValue("analyze", out var text) &&
        bool.TryParse(text, out var on) && on;

    public static bool EngineReady(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null) return false;
        var engine = NormalizeEngine(options.GetValueOrDefault("engine"));
        if (engine == Groq) return HasGroqKey(options);
        if (engine == Whisper) return !string.IsNullOrWhiteSpace(options.GetValueOrDefault("host"));
        var model = options.GetValueOrDefault("onnxModel");
        if (string.IsNullOrWhiteSpace(model)) model = options.GetValueOrDefault("modelId", SherpaCaptionModels.DefaultId);
        return SherpaCaptionModels.IsReady(model);
    }

    private static void Seed(Dictionary<string, string> options, string engine, string name, string value)
    {
        var key = Prefixed(engine, name);
        if (!options.ContainsKey(key) || string.IsNullOrWhiteSpace(options[key]))
            options[key] = value;
    }
}
