using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NeuroSDR.Plugins.Caption;

internal static class LocalWhisperClient
{
    public const string DefaultHost = "192.168.227.185";
    public const int DefaultPort = 8100;
    public const string DefaultModel = "Systran/faster-whisper-small";

    private static readonly HttpClient Http = CreateHttp();

    public static Uri BaseUri(string host, int port, bool https)
    {
        host = (host ?? "").Trim();
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(host, UriKind.Absolute, out var parsed))
                throw new InvalidOperationException("Whisper server URL is invalid.");
            return new Uri($"{parsed.Scheme}://{parsed.Authority}/");
        }

        if (host.Contains(':') && !host.StartsWith('['))
        {
            var colon = host.LastIndexOf(':');
            if (colon > 0 &&
                int.TryParse(host[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var embedded) &&
                embedded is > 0 and <= 65_535)
            {
                port = embedded;
                host = host[..colon];
            }
        }

        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Whisper server host is empty.");
        port = Math.Clamp(port, 1, 65_535);
        return new Uri($"{(https ? "https" : "http")}://{host}:{port}/");
    }

    public static Uri TranscriptionUri(string host, int port, bool https) =>
        new(BaseUri(host, port, https), "v1/audio/transcriptions");

    public static Uri TranslationUri(string host, int port, bool https) =>
        new(BaseUri(host, port, https), "v1/audio/translations");

    public static Uri ModelsUri(string host, int port, bool https) =>
        new(BaseUri(host, port, https), "v1/models");

    public static async Task<SttClip> TranscribeAsync(
        string host, int port, bool https, string? apiKey, byte[] wavBytes, string fileName,
        string model, string language, bool translateToEnglish, CancellationToken cancellationToken)
    {
        if (wavBytes.Length < 64)
            throw new InvalidOperationException("Audio clip is too short.");
        var endpoint = translateToEnglish
            ? TranslationUri(host, port, https)
            : TranscriptionUri(host, port, https);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "clip.wav" : fileName);
        if (!string.IsNullOrWhiteSpace(model))
            content.Add(new StringContent(model.Trim()), "model");
        content.Add(new StringContent("json"), "response_format");
        if (!translateToEnglish)
        {
            var lang = GroqWhisperClient.NormalizeLang(language);
            if (lang.Length > 0 && lang != "auto")
                content.Add(new StringContent(lang), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = content;
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Whisper HTTP {(int)response.StatusCode}: {TrimError(body)}");
        return GroqWhisperClient.ExtractSpeech(body);
    }

    public static async Task<string[]> ListModelsAsync(
        string host, int port, bool https, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUri(host, port, https));
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Whisper models HTTP {(int)response.StatusCode}: {TrimError(body)}");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];
        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToArray();
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
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.ToString();
        }
        catch (JsonException) { }
        return body;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NeuroSDR-LocalWhisper/1.0");
        return http;
    }
}
