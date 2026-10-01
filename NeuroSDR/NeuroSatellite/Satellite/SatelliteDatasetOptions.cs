namespace NeuroSatellite.Satellite;

public sealed record SatelliteDatasetOptions(
    TimeSpan? TleMaxAge = null,
    TimeSpan? FrequencyMaxAge = null,
    TimeSpan? AmsatStatusMaxAge = null,
    int AmsatStatusHours = 24,
    bool ForceRefresh = false)
{
    public TimeSpan ResolvedTleMaxAge => TleMaxAge ?? SatelliteCachePolicy.DefaultTleMaxAge;
    public TimeSpan ResolvedFrequencyMaxAge => FrequencyMaxAge ?? SatelliteCachePolicy.DefaultFrequencyMaxAge;
    public TimeSpan ResolvedAmsatStatusMaxAge => AmsatStatusMaxAge ?? SatelliteCachePolicy.DefaultAmsatStatusMaxAge;
}
