using System.Net.Http.Json;
using System.Text.Json;

namespace NeuroSatellite.Satellite;

/// <summary>Read-only client for the AMSAT Satellite Status API.</summary>
public sealed class AmsatStatusClient(HttpClient httpClient)
{
    private const string BaseUrl = "https://www.amsat.org/status/api/v1/";

    public async Task<IReadOnlyList<SatelliteInfo>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var document = await httpClient.GetFromJsonAsync<JsonDocument>(
            $"{BaseUrl}catalog.php", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AMSAT catalog did not return JSON.");

        return document.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(item => new SatelliteInfo(
                SatelliteSource.Amsat,
                item.GetProperty("name").GetString() ?? "Unknown",
                null,
                null,
                false,
                null,
                null,
                null,
                null,
                item.TryGetProperty("display_name", out var displayName) ? displayName.GetString() : null))
            .OrderBy(satellite => satellite.Name)
            .ToList();
    }

    /// <summary>
    /// Returns satellites whose latest report includes Heard, Telemetry Only, or Crew Active.
    /// That is a community status flag, not a guarantee the bird is on the air.
    /// </summary>
    public async Task<IReadOnlyList<SatelliteInfo>> GetRecentlyActiveAsync(
        int hours = 24, CancellationToken cancellationToken = default)
    {
        if (hours is < 1 or > 720)
            throw new ArgumentOutOfRangeException(nameof(hours), "Hours must be between 1 and 720.");

        using var document = await httpClient.GetFromJsonAsync<JsonDocument>(
            $"{BaseUrl}summary.php?hours={hours}", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("AMSAT summary did not return JSON.");

        var results = new List<SatelliteInfo>();
        CollectActiveEntries(document.RootElement, results);
        return results
            .GroupBy(satellite => satellite.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(satellite => satellite.Name)
            .ToList();
    }

    public async Task<bool> IsSatelliteRecentlyActiveAsync(
        string name, int hours = 24, CancellationToken cancellationToken = default)
    {
        var reportsUrl = $"{BaseUrl}reports.php?name={Uri.EscapeDataString(name)}&hours={hours}&limit=100";
        using var document = await httpClient.GetFromJsonAsync<JsonDocument>(reportsUrl, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("AMSAT reports did not return JSON.");

        return document.RootElement.GetProperty("data").EnumerateArray().Any(report =>
        {
            var status = report.TryGetProperty("report", out var value) ? value.GetString() : null;
            return IsPositiveStatus(status);
        });
    }

    private static void CollectActiveEntries(JsonElement element, ICollection<SatelliteInfo> results)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectActiveEntries(item, results);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        var name = GetString(element, "name") ?? GetString(element, "satellite") ?? GetString(element, "satellite_name");
        var status = GetString(element, "report") ?? GetString(element, "status");
        if (!string.IsNullOrWhiteSpace(name) && IsPositiveStatus(status))
            results.Add(new SatelliteInfo(SatelliteSource.Amsat, name, null, status, true, null, null, null, null));

        foreach (var property in element.EnumerateObject())
            CollectActiveEntries(property.Value, results);
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsPositiveStatus(string? status)
        => status is "Heard" or "Telemetry Only" or "Crew Active";
}
