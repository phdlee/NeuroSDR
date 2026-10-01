using System.Text.Json;

namespace NeuroSatellite.Satellite;

/// <summary>
/// Converts palewire/amateur-satellite-database AMSAT active frequencies
/// into the shared SatelliteRadioInfo shape.
/// </summary>
public sealed class PalewireAmateurDatabaseClient(HttpClient httpClient)
{
    private const string ActiveFrequenciesUrl =
        "https://raw.githubusercontent.com/palewire/amateur-satellite-database/main/data/amsat-active-frequencies.json";
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SatTest",
        "palewire-amsat-active-frequencies.json");

    public async Task<IReadOnlyDictionary<int, SatelliteRadioInfo>> GetRadioCatalogAsync(
        DataFetchPolicy policy = DataFetchPolicy.PreferCache,
        TimeSpan? maxCacheAge = null,
        CancellationToken cancellationToken = default)
    {
        var json = await GetJsonAsync(policy, maxCacheAge, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var radios = new Dictionary<int, SatelliteRadioInfo>();

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("norad_id", out var noradValue)
                || !int.TryParse(noradValue.GetString(), out var noradId))
                continue;

            var uplink = GetString(item, "uplink");
            var downlink = GetString(item, "downlink");
            var beacon = GetString(item, "beacon");
            var mode = GetString(item, "mode");
            var callsign = GetString(item, "callsign");
            var telemetryOnly = string.IsNullOrWhiteSpace(uplink);
            var radio = new SatelliteRadioInfo(
                null,
                null,
                mode,
                mode,
                telemetryOnly,
                FormatMhz(downlink),
                telemetryOnly ? "TELEMETRY" : FormatMhz(uplink),
                FormatMhz(beacon),
                callsign,
                "Palewire AMSAT database");

            // When one satellite has several modes, prefer an uplink/downlink pair.
            if (!radios.TryGetValue(noradId, out var existing) || (existing.TelemetryOnly && !telemetryOnly))
                radios[noradId] = radio;
        }

        return radios;
    }

    public bool IsCacheFresh(TimeSpan maxAge) => SatelliteCachePolicy.IsFileFresh(_cachePath, maxAge);

    public DateTimeOffset? GetCacheTimestamp()
        => File.Exists(_cachePath) ? File.GetLastWriteTimeUtc(_cachePath) : null;

    private async Task<string> GetJsonAsync(
        DataFetchPolicy policy,
        TimeSpan? maxCacheAge,
        CancellationToken cancellationToken)
    {
        if (policy == DataFetchPolicy.CacheOnly)
        {
            if (!File.Exists(_cachePath))
                throw new InvalidOperationException("No Palewire frequency cache. Choose Download.");
            return await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
        }

        if (policy != DataFetchPolicy.Refresh && File.Exists(_cachePath))
        {
            var fresh = maxCacheAge is null || IsCacheFresh(maxCacheAge.Value);
            if (fresh)
                return await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false);
        }

        var json = await httpClient.GetStringAsync(ActiveFrequenciesUrl, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await File.WriteAllTextAsync(_cachePath, json, cancellationToken).ConfigureAwait(false);
        return json;
    }

    private static string? GetString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FormatMhz(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : $"{value.Trim()} MHz";
}
