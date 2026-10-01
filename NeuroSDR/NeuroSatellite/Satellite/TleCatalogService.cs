using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeuroSatellite.Satellite;

public enum TleSource
{
    CelesTrakAmateur,
    SatNogs,
    Amsat
}

public sealed record TleEntry(
    int NoradCatalogId,
    string Name,
    string Line1,
    string Line2,
    TleSource Source,
    DateTimeOffset RetrievedAt);

/// <summary>
/// Downloads amateur-satellite TLEs and stores them in a JSON cache.
/// The cache is %LocalAppData%\SatTest\tle-cache.json.
/// </summary>
public sealed class TleCatalogService(HttpClient httpClient)
{
    private const string CelesTrakUrl = "https://celestrak.org/NORAD/elements/gp.php?GROUP=amateur&FORMAT=tle";
    private const string SatNogsUrl = "https://db.satnogs.org/api/tle/";
    private const string AmsatUrl = "https://www.amsat.org/tle/current/nasabare.txt";
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SatTest",
        "tle-cache.json");

    public string CachePath => _cachePath;

    /// <summary>
    /// Called from periodic work. Downloads only when the cache is missing or older than the retention window.
    /// </summary>
    public async Task<(IReadOnlyList<TleEntry> Entries, bool Refreshed)> RefreshIfStaleAsync(
        TleSource source, TimeSpan maxCacheAge, CancellationToken cancellationToken = default)
    {
        if (maxCacheAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxCacheAge));

        var cachedEntries = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        var mostRecentForSource = cachedEntries
            .Where(entry => entry.Source == source)
            .Select(entry => entry.RetrievedAt)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        if (DateTimeOffset.UtcNow - mostRecentForSource < maxCacheAge)
        {
            return (cachedEntries, false);
        }

        return (await RefreshAsync(source, cancellationToken).ConfigureAwait(false), true);
    }

    public async Task<IReadOnlyList<TleEntry>> RefreshAsync(
        TleSource source, CancellationToken cancellationToken = default)
    {
        var retrievedAt = DateTimeOffset.UtcNow;
        var entries = source == TleSource.SatNogs
            ? await DownloadSatNogsAsync(retrievedAt, cancellationToken).ConfigureAwait(false)
            : await DownloadTextFeedAsync(GetUrl(source), source, retrievedAt, cancellationToken).ConfigureAwait(false);

        if (entries.Count == 0)
            throw new InvalidOperationException($"{source} did not provide valid TLE records.");

        await SaveCacheAsync(entries, cancellationToken).ConfigureAwait(false);
        return entries;
    }

    /// <summary>
    /// Turns CelesTrak's public amateur group into a standalone satellite list.
    /// Space-Track needs credentials, so it is not the default automatic source.
    /// </summary>
    public async Task<DateTimeOffset?> GetMostRecentRetrievedAtAsync(
        TleSource source, CancellationToken cancellationToken = default)
    {
        var cachedEntries = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        return cachedEntries
            .Where(entry => entry.Source == source)
            .Select(entry => entry.RetrievedAt)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max() is var max && max > DateTimeOffset.MinValue ? max : null;
    }

    public async Task<IReadOnlyList<SatelliteInfo>> GetCelesTrakAmateurSatellitesAsync(
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        TimeSpan? maxCacheAge = null,
        CancellationToken cancellationToken = default)
    {
        var cachedEntries = (await LoadCacheAsync(cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.Source == TleSource.CelesTrakAmateur)
            .ToList();
        var newestRetrievedAt = cachedEntries.Count > 0
            ? cachedEntries.Max(entry => entry.RetrievedAt)
            : (DateTimeOffset?)null;
        var cacheFresh = maxCacheAge is null ||
                         SatelliteCachePolicy.IsFresh(newestRetrievedAt, maxCacheAge.Value);
        IReadOnlyList<TleEntry> entries = policy switch
        {
            DataFetchPolicy.CacheOnly when cachedEntries.Count == 0
                => throw new InvalidOperationException("No CelesTrak TLE cache. Choose Download."),
            DataFetchPolicy.CacheOnly or DataFetchPolicy.PreferCache when cachedEntries.Count > 0 && (policy == DataFetchPolicy.CacheOnly || cacheFresh)
                => cachedEntries,
            _ => await RefreshAsync(TleSource.CelesTrakAmateur, cancellationToken).ConfigureAwait(false)
        };

        return entries.Select(entry => new SatelliteInfo(
            SatelliteSource.CelesTrak,
            entry.Name,
            entry.NoradCatalogId,
            "TLE available",
            true,
            null,
            entry.Line1,
            entry.Line2,
            entry.RetrievedAt,
            "CelesTrak Amateur public TLE feed"))
            .OrderBy(satellite => satellite.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<TleEntry>> LoadCacheAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_cachePath))
            return [];

        await using var stream = File.OpenRead(_cachePath);
        var cache = await JsonSerializer.DeserializeAsync(stream, SatelliteJsonContext.Default.TleCache, cancellationToken)
            .ConfigureAwait(false);
        return cache?.Entries ?? [];
    }

    public async Task<IReadOnlyList<SatelliteInfo>> ApplyCachedTlesAsync(
        IEnumerable<SatelliteInfo> satellites, CancellationToken cancellationToken = default)
        => ApplyTles(satellites, await LoadCacheAsync(cancellationToken).ConfigureAwait(false));

    public static IReadOnlyList<SatelliteInfo> ApplyTles(
        IEnumerable<SatelliteInfo> satellites, IEnumerable<TleEntry> tles)
    {
        var byNorad = tles
            .GroupBy(tle => tle.NoradCatalogId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(tle => tle.RetrievedAt).First());
        var byNormalizedName = tles
            .SelectMany(tle => GetNameKeys(tle.Name).Select(key => (Key: key, Tle: tle)))
            .GroupBy(pair => pair.Key)
            .Select(group => group.OrderByDescending(pair => pair.Tle.RetrievedAt).First())
            .GroupBy(pair => pair.Key)
            .Select(group => group.First())
            .ToDictionary(pair => pair.Key, pair => pair.Tle);

        return satellites.Select(satellite =>
        {
            TleEntry? tle = null;
            if (satellite.NoradCatalogId is int noradId)
                byNorad.TryGetValue(noradId, out tle);

            // The AMSAT status API has no NORAD id. A trailing _[FM] or _[U/v] is a mode tag.
            // Apply a match only when the name without that tag hits exactly one TLE.
            tle ??= FindByName(satellite.Name, byNormalizedName);
            if (tle is null)
                return satellite;

            var details = string.IsNullOrWhiteSpace(satellite.Details)
                ? $"TLE: {GetSourceLabel(tle.Source)}"
                : $"{satellite.Details} | TLE: {GetSourceLabel(tle.Source)}";
            return satellite with
            {
                NoradCatalogId = tle.NoradCatalogId,
                TleLine1 = tle.Line1,
                TleLine2 = tle.Line2,
                UpdatedAt = tle.RetrievedAt,
                Details = details
            };
        }).ToList();
    }

    private async Task<IReadOnlyList<TleEntry>> DownloadTextFeedAsync(
        string url, TleSource source, DateTimeOffset retrievedAt, CancellationToken cancellationToken)
    {
        var text = await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseTextTles(text, source, retrievedAt);
    }

    private async Task<IReadOnlyList<TleEntry>> DownloadSatNogsAsync(
        DateTimeOffset retrievedAt, CancellationToken cancellationToken)
    {
        var entries = await httpClient.GetFromJsonAsync<List<SatNogsTle>>(SatNogsUrl, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("SatNOGS TLE catalog did not return JSON.");

        return entries
            .Where(entry => entry.NoradCatalogId.HasValue && IsTlePair(entry.Tle1, entry.Tle2))
            .GroupBy(entry => entry.NoradCatalogId!.Value)
            .Select(group => group.OrderByDescending(entry => entry.Updated).First())
            .Select(entry => new TleEntry(
                entry.NoradCatalogId!.Value,
                string.IsNullOrWhiteSpace(entry.Tle0) ? entry.NoradCatalogId.Value.ToString() : entry.Tle0!,
                entry.Tle1!.Trim(),
                entry.Tle2!.Trim(),
                TleSource.SatNogs,
                entry.Updated ?? retrievedAt))
            .ToList();
    }

    private async Task SaveCacheAsync(IReadOnlyList<TleEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var existingEntries = await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        var mergedEntries = existingEntries
            .Where(existing => !entries.Any(updated => updated.NoradCatalogId == existing.NoradCatalogId))
            .Concat(entries)
            .OrderBy(entry => entry.NoradCatalogId)
            .ToList();
        var cache = new TleCache(DateTimeOffset.UtcNow, mergedEntries);
        await using var stream = File.Create(_cachePath);
        await JsonSerializer.SerializeAsync(stream, cache, SatelliteJsonContext.Default.TleCache, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<TleEntry> ParseTextTles(string text, TleSource source, DateTimeOffset retrievedAt)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var result = new List<TleEntry>();

        for (var index = 0; index < lines.Length - 2; index++)
        {
            if (!lines[index + 1].StartsWith("1 ") || !lines[index + 2].StartsWith("2 "))
                continue;
            if (!TryGetNoradId(lines[index + 1], out var noradId) || !IsTlePair(lines[index + 1], lines[index + 2]))
                continue;

            result.Add(new TleEntry(noradId, lines[index], lines[index + 1], lines[index + 2], source, retrievedAt));
            index += 2;
        }

        return result;
    }

    private static string GetUrl(TleSource source) => source switch
    {
        TleSource.CelesTrakAmateur => CelesTrakUrl,
        TleSource.Amsat => AmsatUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static bool IsTlePair(string? line1, string? line2)
        => line1?.StartsWith("1 ") == true && line2?.StartsWith("2 ") == true;

    private static bool TryGetNoradId(string line1, out int noradId)
    {
        noradId = 0;
        return line1.Length >= 7 && int.TryParse(line1.AsSpan(2, 5), out noradId);
    }

    private static TleEntry? FindByName(string satelliteName, IReadOnlyDictionary<string, TleEntry> byNormalizedName)
    {
        var normalizedSatelliteName = NormalizeName(satelliteName);
        if (byNormalizedName.TryGetValue(normalizedSatelliteName, out var exactMatch))
            return exactMatch;

        var candidates = byNormalizedName
            .Where(pair => pair.Key.StartsWith(normalizedSatelliteName, StringComparison.Ordinal)
                           || normalizedSatelliteName.StartsWith(pair.Key, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .GroupBy(tle => tle.NoradCatalogId)
            .Select(group => group.First())
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string NormalizeName(string name)
    {
        var baseName = name.Split('_', 2)[0];
        return new string(baseName.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static IEnumerable<string> GetNameKeys(string name)
    {
        yield return NormalizeName(name);
        var parts = name.Split(['(', ')'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 1; index < parts.Length; index += 2)
        {
            var alias = NormalizeName(parts[index]);
            if (!string.IsNullOrEmpty(alias))
                yield return alias;
        }
    }

    private static string GetSourceLabel(TleSource source) => source switch
    {
        TleSource.CelesTrakAmateur => "CelesTrak Amateur",
        TleSource.SatNogs => "SatNOGS",
        TleSource.Amsat => "AMSAT",
        _ => source.ToString()
    };

    private sealed class SatNogsTle
    {
        [JsonPropertyName("norad_cat_id")]
        public int? NoradCatalogId { get; init; }
        [JsonPropertyName("tle0")]
        public string? Tle0 { get; init; }
        [JsonPropertyName("tle1")]
        public string? Tle1 { get; init; }
        [JsonPropertyName("tle2")]
        public string? Tle2 { get; init; }
        [JsonPropertyName("updated")]
        public DateTimeOffset? Updated { get; init; }
    }
}

internal sealed record TleCache(DateTimeOffset SavedAt, List<TleEntry> Entries);

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(TleCache))]
internal partial class SatelliteJsonContext : JsonSerializerContext;
