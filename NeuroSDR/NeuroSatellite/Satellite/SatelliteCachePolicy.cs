namespace NeuroSatellite.Satellite;

/// <summary>Recommended refresh intervals for satellite catalog data.</summary>
public static class SatelliteCachePolicy
{
    /// <summary>TLE catalog is re-downloaded when older than this (well under the 1–2 week tracking floor).</summary>
    public static readonly TimeSpan DefaultTleMaxAge = TimeSpan.FromHours(12);
    public static readonly TimeSpan DefaultFrequencyMaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultAmsatStatusMaxAge = TimeSpan.FromMinutes(15);
    /// <summary>Tracking TLEs must not stay cached longer than two weeks.</summary>
    public static readonly TimeSpan MaximumTleMaxAge = TimeSpan.FromDays(14);

    public static bool IsFresh(DateTimeOffset? savedAt, TimeSpan maxAge)
        => savedAt is not null && DateTimeOffset.UtcNow - savedAt.Value < maxAge;

    public static bool IsFileFresh(string path, TimeSpan maxAge)
        => File.Exists(path) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge;
}

public sealed record SatelliteDatasetMetadata(
    DateTimeOffset? TleRetrievedAt,
    DateTimeOffset? FrequencyRetrievedAt,
    DateTimeOffset? AmsatStatusRetrievedAt,
    int RecentlyHeardCount,
    bool TleFromNetwork,
    bool FrequencyFromNetwork,
    bool AmsatStatusFromNetwork)
{
    public string FormatStatusLine()
    {
        static string Age(DateTimeOffset? at) => at is null ? "—" : FormatAge(DateTimeOffset.UtcNow - at.Value);
        return $"TLE {Age(TleRetrievedAt)} · Freq {Age(FrequencyRetrievedAt)} · Heard {RecentlyHeardCount} ({Age(AmsatStatusRetrievedAt)})";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{age.TotalHours:F0}h ago";
        return $"{age.TotalDays:F0}d ago";
    }
}
