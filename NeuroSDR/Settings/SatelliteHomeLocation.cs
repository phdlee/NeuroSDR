namespace NeuroSDR.Settings;

internal sealed class SatelliteHomeLocation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Home";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeMeters { get; set; }
    /// <summary>When set, this home mirrors a remote WebSDR / Kiwi / OpenWebRX site QTH.</summary>
    public string? RemoteSdrUrl { get; set; }
    public string? RemoteSdrProtocol { get; set; }

    public bool IsRemoteSdr => !string.IsNullOrWhiteSpace(RemoteSdrUrl);

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "Home" : Name;

    public SatelliteHomeLocation Clone() => new()
    {
        Id = Id,
        Name = Name,
        Latitude = Latitude,
        Longitude = Longitude,
        AltitudeMeters = AltitudeMeters,
        RemoteSdrUrl = RemoteSdrUrl,
        RemoteSdrProtocol = RemoteSdrProtocol
    };
}
