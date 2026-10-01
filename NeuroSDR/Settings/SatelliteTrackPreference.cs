namespace NeuroSDR.Settings;

internal sealed class SatelliteTrackPreference
{
    public int NoradCatalogId { get; set; }
    public bool PriorityTracking { get; set; }
    public int Priority { get; set; } = 5;

    public SatelliteTrackPreference Clone() => new()
    {
        NoradCatalogId = NoradCatalogId,
        PriorityTracking = PriorityTracking,
        Priority = Priority
    };
}
