using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NeuroSatellite.Satellite;

/// <summary>Read-only client that joins the SatNOGS DB catalog with the latest TLE.</summary>
public sealed class SatNogsClient(HttpClient httpClient)
{
    private const string BaseUrl = "https://db.satnogs.org/api/";

    public async Task<IReadOnlyList<SatelliteInfo>> GetSatellitesAsync(
        bool activeOnly = false,
        bool includeRadio = false,
        bool amateurOnly = true,
        CancellationToken cancellationToken = default)
    {
        var catalog = await httpClient.GetFromJsonAsync<List<SatNogsSatellite>>(
            $"{BaseUrl}satellites/", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SatNOGS catalog did not return JSON.");

        var tles = await httpClient.GetFromJsonAsync<List<SatNogsTle>>(
            $"{BaseUrl}tle/", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SatNOGS TLE catalog did not return JSON.");

        var tleByNoradId = tles
            .Where(tle => tle.NoradCatalogId.HasValue)
            .GroupBy(tle => tle.NoradCatalogId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(tle => tle.Updated).First());
        IReadOnlyDictionary<int, SatelliteRadioInfo> radioByNoradId = includeRadio
            ? await GetRadioCatalogAsync(cancellationToken).ConfigureAwait(false)
            : new Dictionary<int, SatelliteRadioInfo>();

        var satellites = catalog
            .Where(satellite => !amateurOnly ||
                                string.Equals(satellite.Operator, "Amateur", StringComparison.OrdinalIgnoreCase))
            .Select(satellite =>
        {
            tleByNoradId.TryGetValue(satellite.NoradCatalogId, out var tle);
            radioByNoradId.TryGetValue(satellite.NoradCatalogId, out var radio);
            var isActive = string.Equals(satellite.Status, "alive", StringComparison.OrdinalIgnoreCase)
                           && string.IsNullOrWhiteSpace(satellite.Decayed);
            return new SatelliteInfo(
                SatelliteSource.SatNogs,
                satellite.Name ?? "Unknown",
                satellite.NoradCatalogId,
                satellite.Status,
                isActive,
                null,
                tle?.Tle1,
                tle?.Tle2,
                tle?.Updated ?? satellite.Updated,
                satellite.Operator,
                Radio: radio);
        });

        return (activeOnly ? satellites.Where(satellite => satellite.IsActive) : satellites)
            .Where(satellite => satellite.HasTle)
            .OrderBy(satellite => satellite.Name)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<int, SatelliteRadioInfo>> GetRadioCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var transmitters = await httpClient.GetFromJsonAsync<List<SatNogsTransmitter>>(
            $"{BaseUrl}transmitters/", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SatNOGS transmitter catalog did not return JSON.");

        return transmitters
            .Where(transmitter => transmitter.NoradCatalogId.HasValue
                                  && string.Equals(transmitter.Status, "active", StringComparison.OrdinalIgnoreCase)
                                  && transmitter.Alive)
            .GroupBy(transmitter => transmitter.NoradCatalogId!.Value)
            .ToDictionary(group => group.Key, ToRadioInfo);
    }

    private sealed class SatNogsSatellite
    {
        [JsonPropertyName("norad_cat_id")]
        public int NoradCatalogId { get; init; }
        [JsonPropertyName("name")]
        public string? Name { get; init; }
        [JsonPropertyName("status")]
        public string? Status { get; init; }
        [JsonPropertyName("decayed")]
        public string? Decayed { get; init; }
        [JsonPropertyName("operator")]
        public string? Operator { get; init; }
        [JsonPropertyName("updated")]
        public DateTimeOffset? Updated { get; init; }
    }

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

    private sealed class SatNogsTransmitter
    {
        [JsonPropertyName("norad_cat_id")]
        public int? NoradCatalogId { get; init; }
        [JsonPropertyName("downlink_low")]
        public long? DownlinkLow { get; init; }
        [JsonPropertyName("uplink_low")]
        public long? UplinkLow { get; init; }
        [JsonPropertyName("downlink_mode")]
        public string? DownlinkMode { get; init; }
        [JsonPropertyName("uplink_mode")]
        public string? UplinkMode { get; init; }
        [JsonPropertyName("type")]
        public string? Type { get; init; }
        [JsonPropertyName("status")]
        public string? Status { get; init; }
        [JsonPropertyName("alive")]
        public bool Alive { get; init; }
        [JsonPropertyName("updated")]
        public DateTimeOffset? Updated { get; init; }
    }

    private static SatelliteRadioInfo ToRadioInfo(IGrouping<int, SatNogsTransmitter> transmitters)
    {
        var preferred = transmitters
            .OrderByDescending(transmitter => !string.Equals(transmitter.Type, "Transmitter", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(transmitter => transmitter.Updated)
            .First();
        var telemetryOnly = transmitters.All(transmitter =>
            string.Equals(transmitter.Type, "Transmitter", StringComparison.OrdinalIgnoreCase));
        return new SatelliteRadioInfo(
            preferred.DownlinkLow,
            telemetryOnly ? null : preferred.UplinkLow,
            preferred.DownlinkMode,
            preferred.UplinkMode,
            telemetryOnly,
            Provider: "SatNOGS DB");
    }
}
