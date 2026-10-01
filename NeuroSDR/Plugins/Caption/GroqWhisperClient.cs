using System.Net.Http.Headers;
using System.Text.Json;

namespace NeuroSDR.Plugins.Caption;

internal static class GroqWhisperClient
{
    public const string DefaultModel = "whisper-large-v3-turbo";
    public const string Endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";

    private static readonly HttpClient Http = CreateHttp();

    public static async Task<SttClip> TranscribeAsync(
        string apiKey,
        byte[] wavBytes,
        string fileName,
        string model,
        string language,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Groq API key is empty. Enter it in the plugin settings.");
        if (wavBytes.Length < 64)
            throw new InvalidOperationException("Audio clip is too short.");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "clip.wav" : fileName);
        content.Add(new StringContent(string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim()), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        var lang = NormalizeLang(language);
        if (lang.Length > 0 && lang != "auto")
            content.Add(new StringContent(lang), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = content;
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Groq HTTP {(int)response.StatusCode}: {TrimError(body)}");
        return ExtractSpeech(body);
    }

    public static async Task<string> TranslateChatAsync(
        string apiKey, string text, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(text)) return "";
        var target = CaptionLanguages.EnglishName(targetLanguage);
        var payload = JsonSerializer.Serialize(new
        {
            model = "llama-3.1-8b-instant",
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = $"Translate into {target}. Reply with only the translated text." },
                new { role = "user", content = text }
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Groq translate HTTP {(int)response.StatusCode}: {TrimError(body)}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString();
            return content?.Trim() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static async Task<string> TranslateMyMemoryAsync(
        string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var src = CaptionLanguages.Canonical(sourceLanguage);
        var tgt = CaptionLanguages.Canonical(targetLanguage);
        if (tgt is "auto" or "") tgt = "en";
        if (src.Length > 0 && src != "auto" && src == tgt) return "";
        if (src is "auto" or "") src = "Autodetect";
        var url =
            $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(src)}|{Uri.EscapeDataString(tgt)}";
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MyMemory HTTP {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("responseData", out var data) &&
            data.TryGetProperty("translatedText", out var translated))
        {
            var result = System.Net.WebUtility.HtmlDecode(translated.GetString() ?? "").Trim();
            if (result.Contains("PLEASE SELECT TWO DISTINCT", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("SAME LANGUAGE", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("INVALID LANGUAGE PAIR", StringComparison.OrdinalIgnoreCase))
                return "";
            return result;
        }
        return "";
    }

    internal static string LanguageName(string code) => CaptionLanguages.EnglishName(code);

    internal static string NormalizeLang(string? code)
    {
        var value = (code ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0 || value is "auto" or "auto-detect") return "auto";
        if (value.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        if (value.StartsWith("fil", StringComparison.Ordinal) || value.StartsWith("tl", StringComparison.Ordinal))
            return "tl";
        var dash = value.IndexOf('-');
        if (dash > 0) value = value[..dash];
        if (value.Length > 3) value = value[..3];
        return value;
    }

    internal static string ExtractText(string body) => ExtractSpeech(body).Text;

    internal static SttClip ExtractSpeech(string body)
    {
        body = body.Trim();
        if (body.Length == 0) return new("", "");
        if (body[0] != '{') return new(body, CaptionLanguages.GuessFromText(body));
        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = "";
            if (doc.RootElement.TryGetProperty("text", out var textEl))
                text = textEl.GetString()?.Trim() ?? "";
            var language = "";
            if (doc.RootElement.TryGetProperty("language", out var langEl))
                language = CaptionLanguages.Canonical(langEl.GetString());
            if (language is "auto" or "")
                language = CaptionLanguages.GuessFromText(text);
            return new(text, language is "auto" ? "" : language);
        }
        catch (JsonException)
        {
            return new(body, CaptionLanguages.GuessFromText(body));
        }
    }

    private static string TrimError(string body)
    {
        body = body.Trim();
        if (body.Length > 400) body = body[..400];
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                    return message.GetString() ?? body;
                return error.ToString();
            }
        }
        catch (JsonException) { }
        return body;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NeuroSDR-GroqWhisper/1.0");
        return http;
    }
}
