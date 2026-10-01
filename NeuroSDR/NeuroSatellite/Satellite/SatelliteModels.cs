namespace NeuroSatellite.Satellite;

public enum SatelliteSource
{
    Amsat,
    SatNogs,
    CelesTrak
}

public sealed record SatelliteInfo(
    SatelliteSource Source,
    string Name,
    int? NoradCatalogId,
    string? Status,
    bool IsActive,
    string? Frequency,
    string? TleLine1,
    string? TleLine2,
    DateTimeOffset? UpdatedAt,
    string? Details = null,
    SatelliteLookAngle? CurrentLookAngle = null,
    SatelliteGeographicPosition? CurrentGeographicPosition = null,
    SatelliteRadioInfo? Radio = null,
    DateTimeOffset? NextVisibilityAt = null,
    long? DopplerCorrectedReceiveHz = null,
    bool RecentlyHeard = false)
{
    public bool HasTle => !string.IsNullOrWhiteSpace(TleLine1) && !string.IsNullOrWhiteSpace(TleLine2);
    public double? AzimuthDegrees => CurrentLookAngle?.AzimuthDegrees;
    public double? ElevationDegrees => CurrentLookAngle?.ElevationDegrees;
    public double? RangeKilometers => CurrentLookAngle?.RangeKilometers;
    public bool IsAboveHorizon => CurrentLookAngle?.IsAboveHorizon ?? false;
    public string ReceiveFrequency => Radio?.ReceiveFrequencyDisplay ?? "-";
    public string TransmitFrequency => Radio?.TransmitFrequencyDisplay ?? "-";
    public string NextVisibilityDisplay => NextVisibilityAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
    public string RadioSettingFrequency => !IsAboveHorizon
        ? "SPACE"
        : DopplerCorrectedReceiveHz is long frequency ? $"{frequency / 1_000_000d:F6} MHz" : ReceiveFrequency;

    /// <summary>Has a published downlink or uplink frequency (including telemetry/beacon).</summary>
    public bool HasFrequencyInfo
    {
        get
        {
            if (Radio?.GetReceiveFrequencyHz() is > 0) return true;
            if (Radio?.UplinkHz is > 0) return true;
            if (!string.IsNullOrWhiteSpace(Frequency) && Frequency is not "-") return true;
            var rx = Radio?.ReceiveFrequencyDisplay;
            return !string.IsNullOrWhiteSpace(rx) && rx is not "-";
        }
    }

    /// <summary>Has a usable uplink and downlink (not telemetry/beacon-only).</summary>
    public bool CanCommunicate
    {
        get
        {
            if (Radio is not { TelemetryOnly: false } radio) return false;
            if (radio.GetReceiveFrequencyHz() is not > 0) return false;
            if (radio.UplinkHz is > 0) return true;
            var tx = radio.TransmitFrequencyDisplay;
            return !string.IsNullOrWhiteSpace(tx) && tx is not ("-" or "TELEMETRY");
        }
    }
}

public sealed record ObserverLocation(double LatitudeDegrees, double LongitudeDegrees, double AltitudeMeters = 0);

public sealed record SatelliteLookAngle(
    double AzimuthDegrees,
    double ElevationDegrees,
    double RangeKilometers,
    double RangeRateKilometersPerSecond)
{
    public bool IsAboveHorizon => ElevationDegrees >= 0;

    public double CalculateDopplerShiftHz(double frequencyHz)
        => -(RangeRateKilometersPerSecond / 299_792.458d) * frequencyHz;
}

public sealed record SatelliteGeographicPosition(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double AltitudeKilometers);

public sealed record SatelliteRadioInfo(
    long? DownlinkHz,
    long? UplinkHz,
    string? DownlinkMode,
    string? UplinkMode,
    bool TelemetryOnly,
    string? ReceiveFrequencyOverride = null,
    string? TransmitFrequencyOverride = null,
    string? BeaconFrequency = null,
    string? Callsign = null,
    string? Provider = null)
{
    public string ReceiveFrequencyDisplay => ReceiveFrequencyOverride ?? FormatFrequency(DownlinkHz, DownlinkMode);
    public string TransmitFrequencyDisplay => TransmitFrequencyOverride ?? (TelemetryOnly ? "TELEMETRY" : FormatFrequency(UplinkHz, UplinkMode));

    private static string FormatFrequency(long? frequencyHz, string? mode)
        => frequencyHz is null ? "-" : $"{frequencyHz.Value / 1_000_000d:F6} MHz{(string.IsNullOrWhiteSpace(mode) ? string.Empty : $" {mode}")}";

    public long? GetReceiveFrequencyHz()
    {
        if (DownlinkHz.HasValue)
            return DownlinkHz;
        if (string.IsNullOrWhiteSpace(ReceiveFrequencyOverride))
            return null;

        var numeric = new string(ReceiveFrequencyOverride
            .TakeWhile(character => char.IsDigit(character) || character is '.' or ',')
            .ToArray())
            .Replace(',', '.');
        return double.TryParse(numeric, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var mhz)
            ? (long)Math.Round(mhz * 1_000_000d)
            : null;
    }
}
