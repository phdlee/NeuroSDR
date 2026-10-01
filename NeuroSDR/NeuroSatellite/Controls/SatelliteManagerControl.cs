using System.ComponentModel;
using NeuroSatellite.Satellite;

namespace NeuroSatellite.Controls;

[Flags]
public enum SatelliteDataSource
{
    None = 0,
    Amsat = 1,
    CelesTrak = 2,
    SatNogs = 4,
    All = Amsat | CelesTrak | SatNogs
}

public enum FrequencyDataSource
{
    None,
    Palewire,
    SatNogs
}

public sealed record SatelliteRefreshOptions(
    SatelliteDataSource Sources = SatelliteDataSource.CelesTrak,
    DataFetchPolicy CatalogPolicy = DataFetchPolicy.PreferCache,
    FrequencyDataSource FrequencySource = FrequencyDataSource.Palewire,
    DataFetchPolicy FrequencyPolicy = DataFetchPolicy.PreferCache);

public sealed record SatelliteDisplayFilter(
    bool ActiveOnly = false,
    bool AmateurOnly = false,
    bool HasTleOnly = false,
    bool VisibleOnly = false,
    bool UpcomingOnly = false,
    string? NameContains = null);

/// <summary>
/// Reusable WinForms control for satellite download, cache, TLE, frequencies, and positions.
/// </summary>
public sealed class SatelliteManagerControl : UserControl
{
    private readonly SatelliteOperationsControl _operations = new();
    private readonly ProgressBar _progressBar = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };
    private readonly Label _progressLabel = new() { Dock = DockStyle.Left, Width = 180, TextAlign = ContentAlignment.MiddleLeft };
    private readonly List<SatelliteInfo> _satellites = [];
    private readonly SemaphoreSlim _positionGate = new(1, 1);
    private SatelliteDisplayFilter _displayFilter = new();

    public SatelliteManagerControl()
    {
        Height = 24;
        MinimumSize = new Size(250, 24);
        Controls.Add(_progressBar);
        Controls.Add(_progressLabel);
        SetProgress(0, "Ready");
    }

    public IReadOnlyList<SatelliteInfo> Satellites => _satellites;
    public IReadOnlyList<SatelliteInfo> DisplaySatellites => ApplyDisplayFilter(_satellites, _displayFilter);
    public IReadOnlyList<SatelliteInfo> VisibleSatellites => _satellites.Where(satellite => satellite.IsAboveHorizon).ToList();
    public IReadOnlyList<SatelliteInfo> UpcomingSatellites => _satellites
        .Where(satellite => !satellite.IsAboveHorizon && satellite.NextVisibilityAt.HasValue)
        .OrderBy(satellite => satellite.NextVisibilityAt)
        .ToList();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SatelliteDatasetMetadata LastDatasetMetadata { get; private set; } = new(null, null, null, 0, false, false, false);

    public event EventHandler? SatellitesChanged;
    public event EventHandler? DisplayFilterChanged;
    public event EventHandler<string>? ProgressMessageChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SatelliteDisplayFilter DisplayFilter
    {
        get => _displayFilter;
        set
        {
            _displayFilter = value ?? new SatelliteDisplayFilter();
            DisplayFilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Convenience proxies so another WinForms project can call the same steps.
    public string TleCachePath => _operations.TleCachePath;
    public Task<IReadOnlyList<SatelliteInfo>> GetAmsatCatalogAsync(DataFetchPolicy policy = DataFetchPolicy.PreferCache, CancellationToken cancellationToken = default)
        => _operations.GetAmsatCatalogAsync(policy, cancellationToken);
    public Task<IReadOnlyList<SatelliteInfo>> GetAmsatActiveAsync(int hours = 24, DataFetchPolicy policy = DataFetchPolicy.PreferCache, CancellationToken cancellationToken = default)
        => _operations.GetAmsatActiveAsync(hours, policy, cancellationToken: cancellationToken);
    public Task<IReadOnlyList<SatelliteInfo>> GetCelesTrakAmateurTlesAsync(DataFetchPolicy policy = DataFetchPolicy.PreferCache, CancellationToken cancellationToken = default)
        => _operations.GetCelesTrakAmateurTlesAsync(policy, cancellationToken: cancellationToken);
    public Task<bool> IsAmsatSatelliteActiveAsync(string name, int hours = 24, CancellationToken cancellationToken = default)
        => _operations.IsAmsatSatelliteActiveAsync(name, hours, cancellationToken);
    public Task<IReadOnlyList<TleEntry>> RefreshTlesAsync(TleSource source, CancellationToken cancellationToken = default)
        => _operations.RefreshTlesAsync(source, cancellationToken);
    public Task<IReadOnlyList<TleEntry>> LoadCachedTlesAsync(CancellationToken cancellationToken = default)
        => _operations.LoadCachedTlesAsync(cancellationToken);
    public Task<(IReadOnlyList<TleEntry> Entries, bool Refreshed)> RefreshTlesIfStaleAsync(TleSource source, TimeSpan maxCacheAge, CancellationToken cancellationToken = default)
        => _operations.RefreshTlesIfStaleAsync(source, maxCacheAge, cancellationToken);
    public IReadOnlyList<SatelliteInfo> ApplyTles(IEnumerable<SatelliteInfo> satellites, IEnumerable<TleEntry> tles)
        => _operations.ApplyTles(satellites, tles);
    public Task<IReadOnlyList<SatelliteInfo>> ApplyRadioInfoAsync(IEnumerable<SatelliteInfo> satellites, CancellationToken cancellationToken = default)
        => _operations.ApplyRadioInfoAsync(satellites, cancellationToken);
    public Task<IReadOnlyList<SatelliteInfo>> ApplyPalewireRadioInfoAsync(
        IEnumerable<SatelliteInfo> satellites,
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        TimeSpan? maxCacheAge = null,
        CancellationToken cancellationToken = default)
        => _operations.ApplyPalewireRadioInfoAsync(satellites, policy, maxCacheAge, cancellationToken);
    public SatelliteLookAngle GetLookAngle(SatelliteInfo satellite, ObserverLocation observer) => _operations.GetLookAngle(satellite, observer);
    public SatelliteGeographicPosition GetGeographicPosition(SatelliteInfo satellite) => _operations.GetGeographicPosition(satellite);
    public DateTimeOffset? FindNextVisibility(SatelliteInfo satellite, ObserverLocation observer, TimeSpan searchWindow)
        => _operations.FindNextVisibility(satellite, observer, searchWindow);

    public void Clear()
    {
        _satellites.Clear();
        SatellitesChanged?.Invoke(this, EventArgs.Empty);
        SetProgress(0, "List cleared");
    }

    /// <summary>
    /// CelesTrak TLE + Palewire frequencies + AMSAT recently-heard status.
    /// Skips network when local cache is still fresh unless <paramref name="options"/> forces refresh.
    /// </summary>
    public async Task<SatelliteDatasetMetadata> EnsureSatelliteDatasetAsync(
        SatelliteDatasetOptions options,
        CancellationToken cancellationToken = default)
    {
        var tleMaxAge = options.ResolvedTleMaxAge;
        var frequencyMaxAge = options.ResolvedFrequencyMaxAge;
        var amsatMaxAge = options.ResolvedAmsatStatusMaxAge;
        var tleFromNetwork = false;
        var frequencyFromNetwork = false;
        var amsatFromNetwork = false;

        SetProgress(0, "Checking satellite data...");

        if (_satellites.Count == 0)
        {
            try
            {
                var cached = await _operations.GetCelesTrakAmateurTlesAsync(
                    DataFetchPolicy.CacheOnly, cancellationToken: cancellationToken);
                ReplaceSatellites(cached);
            }
            catch
            {
                // First run may have no TLE cache yet.
            }
        }

        var tleRetrievedAt = await _operations.GetCelesTrakTleRetrievedAtAsync(cancellationToken);
        var tleFresh = !options.ForceRefresh &&
                       _satellites.Count > 0 &&
                       SatelliteCachePolicy.IsFresh(tleRetrievedAt, tleMaxAge) &&
                       SatelliteCachePolicy.IsFresh(tleRetrievedAt, SatelliteCachePolicy.MaximumTleMaxAge);
        if (!tleFresh)
        {
            SetProgress(25, "Refreshing TLE...");
            var catalog = await _operations.GetCelesTrakAmateurTlesAsync(
                options.ForceRefresh ? DataFetchPolicy.Refresh : DataFetchPolicy.PreferCache,
                tleMaxAge,
                cancellationToken);
            ReplaceSatellites(catalog);
            tleRetrievedAt = await _operations.GetCelesTrakTleRetrievedAtAsync(cancellationToken);
            tleFromNetwork = true;
        }

        DateTimeOffset? frequencyRetrievedAt = _operations.GetPalewireCacheTimestamp();
        var frequencyFresh = !options.ForceRefresh &&
                             _operations.IsPalewireCacheFresh(frequencyMaxAge);
        var missingFrequencies = _satellites.Any(item => item.Radio is null);
        if (_satellites.Count > 0 && (options.ForceRefresh || !frequencyFresh || missingFrequencies))
        {
            SetProgress(55, "Refreshing frequencies...");
            var before = frequencyRetrievedAt;
            var updated = await _operations.ApplyPalewireRadioInfoAsync(
                _satellites,
                options.ForceRefresh ? DataFetchPolicy.Refresh : DataFetchPolicy.PreferCache,
                frequencyMaxAge,
                cancellationToken);
            ReplaceSatellites(updated);
            frequencyRetrievedAt = _operations.GetPalewireCacheTimestamp();
            frequencyFromNetwork = options.ForceRefresh || before != frequencyRetrievedAt || !frequencyFresh;
        }

        IReadOnlyList<SatelliteInfo> recentlyHeard = [];
        DateTimeOffset? amsatRetrievedAt = null;
        try
        {
            SetProgress(75, "Checking AMSAT status...");
            var cacheEntry = await _operations.TryLoadAmsatActiveCacheAsync(options.AmsatStatusHours, cancellationToken);
            var heardFresh = !options.ForceRefresh &&
                             cacheEntry is not null &&
                             SatelliteCachePolicy.IsFresh(cacheEntry.SavedAt, amsatMaxAge);
            if (heardFresh)
            {
                recentlyHeard = cacheEntry!.Satellites;
                amsatRetrievedAt = cacheEntry.SavedAt;
            }
            else
            {
                recentlyHeard = await _operations.GetAmsatActiveAsync(
                    options.AmsatStatusHours,
                    options.ForceRefresh ? DataFetchPolicy.Refresh : DataFetchPolicy.PreferCache,
                    amsatMaxAge,
                    cancellationToken);
                amsatFromNetwork = true;
                amsatRetrievedAt = DateTimeOffset.UtcNow;
            }
        }
        catch
        {
            // AMSAT status is optional enrichment.
        }

        if (recentlyHeard.Count > 0)
            ReplaceSatellites(_operations.ApplyRecentlyHeardFlags(_satellites, recentlyHeard));

        LastDatasetMetadata = new SatelliteDatasetMetadata(
            tleRetrievedAt,
            frequencyRetrievedAt,
            amsatRetrievedAt,
            _satellites.Count(item => item.RecentlyHeard),
            tleFromNetwork,
            frequencyFromNetwork,
            amsatFromNetwork);
        SetProgress(100, $"{_satellites.Count:N0} satellites · {LastDatasetMetadata.FormatStatusLine()}");
        return LastDatasetMetadata;
    }

    public async Task RefreshSatelliteInfoAsync(SatelliteRefreshOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SatelliteRefreshOptions();
        var sources = ExpandSources(options.Sources);
        var downloaded = new List<SatelliteInfo>();
        var errors = new List<string>();
        SetProgress(0, "Refreshing satellite catalog...");

        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            try
            {
                SetProgress(index * 100 / Math.Max(1, sources.Count), $"Fetching {source}...");
                var data = source switch
                {
                    SatelliteDataSource.Amsat => await _operations.GetAmsatCatalogAsync(options.CatalogPolicy, cancellationToken: cancellationToken),
                    SatelliteDataSource.CelesTrak => await _operations.GetCelesTrakAmateurTlesAsync(options.CatalogPolicy, cancellationToken: cancellationToken),
                    SatelliteDataSource.SatNogs => await _operations.GetSatNogsActiveAsync(options.CatalogPolicy, cancellationToken: cancellationToken),
                    _ => []
                };
                downloaded.AddRange(data);
            }
            catch (Exception exception)
            {
                errors.Add($"{source}: {exception.Message}");
            }
        }

        ReplaceSatellites(MergeSatellites(downloaded));
        if (options.FrequencySource != FrequencyDataSource.None && _satellites.Count > 0)
            await RefreshFrequencyInfoAsync(options.FrequencySource, options.FrequencyPolicy, cancellationToken: cancellationToken);

        SetProgress(100, errors.Count == 0
            ? $"{_satellites.Count:N0} satellites ready"
            : $"{_satellites.Count:N0} ready / {string.Join(" | ", errors)}");
    }

    public Task LoadSatelliteInfoFromCacheAsync(
        SatelliteDataSource sources = SatelliteDataSource.All,
        FrequencyDataSource frequencySource = FrequencyDataSource.None,
        CancellationToken cancellationToken = default)
        => RefreshSatelliteInfoAsync(new SatelliteRefreshOptions(
            sources,
            DataFetchPolicy.CacheOnly,
            frequencySource,
            DataFetchPolicy.CacheOnly), cancellationToken);

    public async Task ReceiveTlesAsync(
        TleSource source = TleSource.CelesTrakAmateur,
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        CancellationToken cancellationToken = default)
    {
        SetProgress(0, "Receiving TLE...");
        var tles = policy == DataFetchPolicy.Refresh
            ? await _operations.RefreshTlesAsync(source, cancellationToken)
            : await _operations.LoadCachedTlesAsync(cancellationToken);
        if (tles.Count == 0 && policy != DataFetchPolicy.CacheOnly)
            tles = await _operations.RefreshTlesAsync(source, cancellationToken);

        ReplaceSatellites(_operations.ApplyTles(_satellites, tles));
        SetProgress(100, $"{tles.Count:N0} TLE entries applied");
    }

    public async Task RefreshFrequencyInfoAsync(
        FrequencyDataSource source = FrequencyDataSource.Palewire,
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        TimeSpan? maxCacheAge = null,
        CancellationToken cancellationToken = default)
    {
        SetProgress(0, "Receiving frequency data...");
        var updated = source switch
        {
            FrequencyDataSource.Palewire => await _operations.ApplyPalewireRadioInfoAsync(
                _satellites, policy, maxCacheAge, cancellationToken),
            FrequencyDataSource.SatNogs => await _operations.ApplyRadioInfoAsync(_satellites, cancellationToken),
            _ => _satellites
        };
        ReplaceSatellites(updated);
        SetProgress(100, "Frequency data applied");
    }

    public async Task UpdatePositionsAsync(
        ObserverLocation observer,
        TimeSpan? visibilitySearchWindow = null,
        bool predictPasses = true,
        CancellationToken cancellationToken = default)
    {
        await _positionGate.WaitAsync(cancellationToken);
        try
        {
        var snapshot = _satellites.ToList();
        var window = visibilitySearchWindow ?? TimeSpan.FromHours(24);
        SetProgress(0, predictPasses ? "Computing positions and passes..." : "Computing positions...");

        var updated = await Task.Run(() => snapshot.Select((satellite, index) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!satellite.HasTle)
                return satellite;

            SatelliteLookAngle? lookAngle = null;
            SatelliteGeographicPosition? geographicPosition = null;
            try
            {
                lookAngle = _operations.GetLookAngle(satellite, observer);
                geographicPosition = _operations.GetGeographicPosition(satellite);
                var baseFrequency = satellite.Radio?.GetReceiveFrequencyHz();
                long? correctedFrequency = baseFrequency is null ? null
                    : (long)Math.Round(baseFrequency.Value + lookAngle.CalculateDopplerShiftHz(baseFrequency.Value));
                var nextVisibility = satellite.NextVisibilityAt;
                if (predictPasses)
                {
                    try
                    {
                        nextVisibility = lookAngle.IsAboveHorizon
                            ? null
                            : _operations.FindNextVisibility(satellite, observer, window);
                    }
                    catch
                    {
                        nextVisibility = satellite.NextVisibilityAt;
                    }
                }
                else if (lookAngle.IsAboveHorizon)
                    nextVisibility = null;

                return satellite with
                {
                    CurrentLookAngle = lookAngle,
                    CurrentGeographicPosition = geographicPosition,
                    DopplerCorrectedReceiveHz = correctedFrequency,
                    NextVisibilityAt = nextVisibility
                };
            }
            catch
            {
                return lookAngle is null
                    ? satellite
                    : satellite with
                    {
                        CurrentLookAngle = lookAngle,
                        CurrentGeographicPosition = geographicPosition
                    };
            }
        }).ToList(), cancellationToken);

        ReplaceSatellites(updated);
        SetProgress(100, $"Positions updated · {VisibleSatellites.Count:N0} visible · {UpcomingSatellites.Count:N0} upcoming");
        }
        finally
        {
            _positionGate.Release();
        }
    }

    public void ReplaceSatellites(IEnumerable<SatelliteInfo> satellites)
    {
        var snapshot = satellites.ToList();
        _satellites.Clear();
        _satellites.AddRange(snapshot);
        SatellitesChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operations.Dispose();
            _positionGate.Dispose();
        }
        base.Dispose(disposing);
    }

    private static List<SatelliteDataSource> ExpandSources(SatelliteDataSource sources)
    {
        var result = new List<SatelliteDataSource>();
        foreach (var source in new[] { SatelliteDataSource.Amsat, SatelliteDataSource.CelesTrak, SatelliteDataSource.SatNogs })
            if (sources.HasFlag(source))
                result.Add(source);
        return result;
    }

    private static IReadOnlyList<SatelliteInfo> MergeSatellites(IEnumerable<SatelliteInfo> satellites)
        => satellites
            .GroupBy(satellite => satellite.NoradCatalogId is int norad
                ? $"N:{norad}"
                : $"S:{satellite.Source}:{satellite.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(satellite => satellite.Name)
            .ToList();

    private static IReadOnlyList<SatelliteInfo> ApplyDisplayFilter(
        IEnumerable<SatelliteInfo> satellites,
        SatelliteDisplayFilter filter)
        => satellites.Where(satellite =>
                (!filter.ActiveOnly || satellite.IsActive)
                && (!filter.AmateurOnly || satellite.Source is SatelliteSource.Amsat or SatelliteSource.CelesTrak)
                && (!filter.HasTleOnly || satellite.HasTle)
                && (!filter.VisibleOnly || satellite.IsAboveHorizon)
                && (!filter.UpcomingOnly || (!satellite.IsAboveHorizon && satellite.NextVisibilityAt.HasValue))
                && (string.IsNullOrWhiteSpace(filter.NameContains)
                    || satellite.Name.Contains(filter.NameContains, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private void SetProgress(int value, string message)
    {
        value = Math.Clamp(value, 0, 100);
        if (InvokeRequired)
        {
            BeginInvoke(() => SetProgress(value, message));
            return;
        }

        _progressBar.Value = value;
        _progressLabel.Text = message;
        ProgressMessageChanged?.Invoke(this, message);
    }
}
