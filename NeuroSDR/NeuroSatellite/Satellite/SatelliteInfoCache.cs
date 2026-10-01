using System.Text.Json;

namespace NeuroSatellite.Satellite;

public enum DataFetchPolicy
{
    CacheOnly,
    PreferCache,
    Refresh
}

/// <summary>Stores remote catalog responses in one JSON file per source.</summary>
public sealed class SatelliteInfoCache
{
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SatTest",
        "satellite-info-cache.json");

    public async Task<IReadOnlyList<SatelliteInfo>?> TryLoadAsync(
        string key, CancellationToken cancellationToken = default)
    {
        var entry = await TryLoadEntryAsync(key, cancellationToken).ConfigureAwait(false);
        return entry?.Satellites;
    }

    public async Task<CachedSatelliteList?> TryLoadEntryAsync(
        string key, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_cachePath))
            return null;

        await using var stream = File.OpenRead(_cachePath);
        var document = await JsonSerializer.DeserializeAsync<CacheDocument>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return document?.Entries.TryGetValue(key, out var entry) == true ? entry : null;
    }

    public async Task SaveAsync(
        string key, IReadOnlyList<SatelliteInfo> satellites, CancellationToken cancellationToken = default)
    {
        var existing = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
        existing.Entries[key] = new CachedSatelliteList
        {
            SavedAt = DateTimeOffset.UtcNow,
            Satellites = satellites.ToList()
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await using var stream = File.Create(_cachePath);
        await JsonSerializer.SerializeAsync(stream, existing, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<CacheDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
            return new CacheDocument();

        await using var stream = File.OpenRead(_cachePath);
        return await JsonSerializer.DeserializeAsync<CacheDocument>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? new CacheDocument();
    }

    private sealed class CacheDocument
    {
        public Dictionary<string, CachedSatelliteList> Entries { get; init; } = [];
    }

    public sealed class CachedSatelliteList
    {
        public DateTimeOffset SavedAt { get; init; }
        public List<SatelliteInfo> Satellites { get; init; } = [];
    }
}
