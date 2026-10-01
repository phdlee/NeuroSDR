using System.Net.Http.Headers;

namespace NeuroSDR.Plugins.Caption;

internal sealed record SherpaModelFiles(string Encoder, string Decoder, string Tokens);

internal static class SherpaCaptionModels
{
    public const string DefaultId = "tiny";

    private static readonly HttpClient Http = CreateHttp();

    private static readonly ModelSpec[] Catalog =
    [
        new("tiny", "https://huggingface.co/csukuangfj/sherpa-onnx-whisper-tiny/resolve/main/",
            8_000_000, 40_000_000, "Whisper tiny INT8 (~104 MB, fastest CPU)"),
        new("base", "https://huggingface.co/csukuangfj/sherpa-onnx-whisper-base/resolve/main/",
            20_000_000, 80_000_000, "Whisper base INT8 (~161 MB, better)"),
        new("small", "https://huggingface.co/csukuangfj/sherpa-onnx-whisper-small/resolve/main/",
            40_000_000, 140_000_000, "Whisper small INT8 (~500 MB, CPU OK, slower)"),
        new("medium", "https://huggingface.co/csukuangfj/sherpa-onnx-whisper-medium/resolve/main/",
            80_000_000, 400_000_000, "Whisper medium INT8 (~1.5 GB, CPU, slow)")
    ];

    public static IReadOnlyList<string> Ids { get; } = Catalog.Select(item => item.Id).ToArray();

    public static string Root(string modelId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NeuroSDR", "models", $"sherpa-onnx-whisper-{Normalize(modelId)}");

    public static string Label(string modelId)
    {
        var id = Normalize(modelId);
        return Catalog.First(item => item.Id == id).Label;
    }

    public static bool IsReady(string modelId) => TryGetFiles(modelId, out _);

    public static bool TryGetFiles(string modelId, out SherpaModelFiles files)
    {
        var id = Normalize(modelId);
        var dir = Root(id);
        files = new SherpaModelFiles(
            Path.Combine(dir, $"{id}-encoder.int8.onnx"),
            Path.Combine(dir, $"{id}-decoder.int8.onnx"),
            Path.Combine(dir, $"{id}-tokens.txt"));
        return File.Exists(files.Encoder) && File.Exists(files.Decoder) && File.Exists(files.Tokens) &&
               new FileInfo(files.Encoder).Length > 1_000_000 &&
               new FileInfo(files.Decoder).Length > 1_000_000 &&
               new FileInfo(files.Tokens).Length > 10_000;
    }

    public static async Task DownloadAsync(string modelId, IProgress<string>? progress, CancellationToken ct)
    {
        var spec = Spec(Normalize(modelId));
        var dir = Root(spec.Id);
        Directory.CreateDirectory(dir);
        var items = new (string Name, long MinBytes)[]
        {
            ($"{spec.Id}-encoder.int8.onnx", spec.EncoderMinBytes),
            ($"{spec.Id}-decoder.int8.onnx", spec.DecoderMinBytes),
            ($"{spec.Id}-tokens.txt", 10_000)
        };
        for (var i = 0; i < items.Length; i++)
        {
            var (name, minBytes) = items[i];
            var dest = Path.Combine(dir, name);
            progress?.Report($"Downloading {name} ({i + 1}/{items.Length})");
            await DownloadFileAsync($"{spec.BaseUrl}{name}?download=true", dest, minBytes, percent =>
            {
                progress?.Report($"{name}  {percent:0}%");
            }, ct).ConfigureAwait(false);
        }
        if (!TryGetFiles(spec.Id, out _))
            throw new InvalidOperationException("Download finished but model files are incomplete.");
        progress?.Report("Model ready");
    }

    private static async Task DownloadFileAsync(
        string url, string dest, long minBytes, Action<double> progress, CancellationToken ct)
    {
        var temp = dest + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[256 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                if (total > 0) progress(100.0 * copied / total);
            }
        }
        var size = new FileInfo(temp).Length;
        if (size < minBytes)
        {
            File.Delete(temp);
            throw new InvalidOperationException($"Downloaded file is too small ({size} bytes): {Path.GetFileName(dest)}");
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(temp, dest);
    }

    private static string Normalize(string? modelId)
    {
        var id = (modelId ?? DefaultId).Trim().ToLowerInvariant();
        return Catalog.Any(item => item.Id == id) ? id : DefaultId;
    }

    private static ModelSpec Spec(string id) => Catalog.First(item => item.Id == id);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(40) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NeuroSDR", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return http;
    }

    private readonly record struct ModelSpec(
        string Id, string BaseUrl, long EncoderMinBytes, long DecoderMinBytes, string Label);
}
