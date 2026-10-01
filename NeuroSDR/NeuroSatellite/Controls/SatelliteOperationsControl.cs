using System.ComponentModel;
using NeuroSatellite.Satellite;

namespace NeuroSatellite.Controls;

/// <summary>
/// Non-visual satellite control for reuse in another WinForms application.
/// Catalog lookup, TLE cache, TLE apply, and position math stay off the UI.
/// </summary>
public sealed class SatelliteOperationsControl : Component
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly AmsatStatusClient _amsat;
    private readonly SatNogsClient _satNogs;
    private readonly PalewireAmateurDatabaseClient _palewire;
    private readonly TleCatalogService _tleCatalog;
    private readonly SatelliteInfoCache _infoCache = new();
    private readonly SatelliteTracker _tracker = new();

    public SatelliteOperationsControl()
    {
        _amsat = new AmsatStatusClient(_httpClient);
        _satNogs = new SatNogsClient(_httpClient);
        _palewire = new PalewireAmateurDatabaseClient(_httpClient);
        _tleCatalog = new TleCatalogService(_httpClient);
    }

    public string TleCachePath => _tleCatalog.CachePath;

    public Task<IReadOnlyList<SatelliteInfo>> GetAmsatCatalogAsync(
        DataFetchPolicy policy = DataFetchPolicy.PreferCache, CancellationToken cancellationToken = default)
        => GetOrLoadAsync("amsat-catalog", policy, SatelliteCachePolicy.DefaultFrequencyMaxAge, async () =>
            await ApplyCachedTlesAsync(await _amsat.GetCatalogAsync(cancellationToken), cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SatelliteInfo>> GetAmsatActiveAsync(
        int hours = 24,
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        TimeSpan? maxCacheAge = null,
        CancellationToken cancellationToken = default)
        => GetOrLoadAsync($"amsat-active-{hours}", policy, maxCacheAge ?? SatelliteCachePolicy.DefaultAmsatStatusMaxAge,
            async () => await ApplyCachedTlesAsync(await _amsat.GetRecentlyActiveAsync(hours, cancellationToken), cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<SatelliteInfo>> GetSatNogsActiveAsync(
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        TimeSpan? maxCacheAge = null,
        CancellationToken cancellationToken = default)
        => GetOrLoadAsync("satnogs-active", policy, maxCacheAge ?? SatelliteCachePolicy.DefaultFrequencyMaxAge,
            async () => await _satNogs.GetSatellitesAsync(activeOnly: true, includeRadio: true, cancellationToken: cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<SatelliteInfo>> GetCelesTrakAmateurTlesAsync(
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        TimeSpan? maxCacheAge = null,
        CancellationToken cancellationToken = default)
        => _tleCatalog.GetCelesTrakAmateurSatellitesAsync(policy, maxCacheAge, cancellationToken);

    public Task<DateTimeOffset?> GetCelesTrakTleRetrievedAtAsync(CancellationToken cancellationToken = default)
        => _tleCatalog.GetMostRecentRetrievedAtAsync(TleSource.CelesTrakAmateur, cancellationToken);

    public DateTimeOffset? GetPalewireCacheTimestamp() => _palewire.GetCacheTimestamp();

    public bool IsPalewireCacheFresh(TimeSpan maxAge) => _palewire.IsCacheFresh(maxAge);

    public Task<SatelliteInfoCache.CachedSatelliteList?> TryLoadAmsatActiveCacheAsync(
        int hours, CancellationToken cancellationToken = default)
        => _infoCache.TryLoadEntryAsync($"amsat-active-{hours}", cancellationToken);

    public Task<bool> IsAmsatSatelliteActiveAsync(string name, int hours = 24, CancellationToken cancellationToken = default)
        => _amsat.IsSatelliteRecentlyActiveAsync(name, hours, cancellationToken);

    public Task<IReadOnlyList<TleEntry>> RefreshTlesAsync(TleSource source, CancellationToken cancellationToken = default)
        => _tleCatalog.RefreshAsync(source, cancellationToken);

    public Task<IReadOnlyList<TleEntry>> LoadCachedTlesAsync(CancellationToken cancellationToken = default)
        => _tleCatalog.LoadCacheAsync(cancellationToken);

    public Task<(IReadOnlyList<TleEntry> Entries, bool Refreshed)> RefreshTlesIfStaleAsync(
        TleSource source, TimeSpan maxCacheAge, CancellationToken cancellationToken = default)
        => _tleCatalog.RefreshIfStaleAsync(source, maxCacheAge, cancellationToken);

    public Task<IReadOnlyList<SatelliteInfo>> ApplyCachedTlesAsync(
        IEnumerable<SatelliteInfo> satellites, CancellationToken cancellationToken = default)
        => _tleCatalog.ApplyCachedTlesAsync(satellites, cancellationToken);

    public IReadOnlyList<SatelliteInfo> ApplyTles(IEnumerable<SatelliteInfo> satellites, IEnumerable<TleEntry> tles)
        => TleCatalogService.ApplyTles(satellites, tles);

    public async Task<IReadOnlyList<SatelliteInfo>> ApplyRadioInfoAsync(
        IEnumerable<SatelliteInfo> satellites, CancellationToken cancellationToken = default)
    {
        var radios = await _satNogs.GetRadioCatalogAsync(cancellationToken);
        return satellites.Select(satellite =>
        {
            radios.TryGetValue(satellite.NoradCatalogId ?? -1, out var radio);
            var telemetryOnly = satellite.Status is "Telemetry Only" or "Not Heard";
            return satellite with
            {
                Radio = radio is null
                    ? telemetryOnly ? new SatelliteRadioInfo(null, null, null, null, true) : null
                    : radio with { TelemetryOnly = telemetryOnly || radio.TelemetryOnly }
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<SatelliteInfo>> ApplyPalewireRadioInfoAsync(
        IEnumerable<SatelliteInfo> satellites,
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        TimeSpan? maxCacheAge = null,
        CancellationToken cancellationToken = default)
    {
        var radios = await _palewire.GetRadioCatalogAsync(policy, maxCacheAge, cancellationToken);
        return satellites.Select(satellite =>
        {
            radios.TryGetValue(satellite.NoradCatalogId ?? -1, out var radio);
            return satellite with { Radio = radio ?? satellite.Radio };
        }).ToList();
    }

    public IReadOnlyList<SatelliteInfo> ApplyRecentlyHeardFlags(
        IEnumerable<SatelliteInfo> satellites,
        IReadOnlyList<SatelliteInfo> recentlyHeard)
    {
        var heardNames = recentlyHeard
            .Select(item => NormalizeSatelliteName(item.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return satellites.Select(satellite =>
        {
            var heard = heardNames.Contains(NormalizeSatelliteName(satellite.Name));
            return satellite.RecentlyHeard == heard ? satellite : satellite with { RecentlyHeard = heard };
        }).ToList();
    }

    public SatelliteLookAngle GetLookAngle(SatelliteInfo satellite, ObserverLocation observer)
        => _tracker.GetLookAngle(satellite, observer);

    public SatelliteGeographicPosition GetGeographicPosition(SatelliteInfo satellite)
        => _tracker.GetGeographicPosition(satellite);

    public DateTimeOffset? FindNextVisibility(SatelliteInfo satellite, ObserverLocation observer, TimeSpan searchWindow)
        => _tracker.FindNextVisibility(satellite, observer, searchWindow);

    private async Task<IReadOnlyList<SatelliteInfo>> GetOrLoadAsync(
        string cacheKey,
        DataFetchPolicy policy,
        TimeSpan maxCacheAge,
        Func<Task<IReadOnlyList<SatelliteInfo>>> download,
        CancellationToken cancellationToken)
    {
        var entry = await _infoCache.TryLoadEntryAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (policy == DataFetchPolicy.CacheOnly)
            return entry?.Satellites ?? throw new InvalidOperationException("No local cache. Choose Download.");
        if (policy == DataFetchPolicy.PreferCache && entry is not null &&
            SatelliteCachePolicy.IsFresh(entry.SavedAt, maxCacheAge))
            return entry.Satellites;

        var downloaded = await download().ConfigureAwait(false);
        await _infoCache.SaveAsync(cacheKey, downloaded, cancellationToken).ConfigureAwait(false);
        return downloaded;
    }

    private static string NormalizeSatelliteName(string name)
        => name.Split('_', 2)[0].Trim();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _httpClient.Dispose();
        base.Dispose(disposing);
    }
}
