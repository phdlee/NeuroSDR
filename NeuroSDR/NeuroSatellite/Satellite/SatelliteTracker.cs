using SGPdotNET.CoordinateSystem;
using SGPdotNET.Observation;
using SGPdotNET.Util;
using OrbitSatellite = SGPdotNET.Observation.Satellite;

namespace NeuroSatellite.Satellite;

/// <summary>Computes the current antenna look angle from a TLE and an observer.</summary>
public sealed class SatelliteTracker
{
    public DateTimeOffset? FindNextVisibility(
        SatelliteInfo satellite,
        ObserverLocation observer,
        TimeSpan searchWindow,
        DateTimeOffset? startAt = null)
    {
        if (!satellite.HasTle)
            return null;

        var location = new GeodeticCoordinate(
            Angle.FromDegrees(observer.LatitudeDegrees),
            Angle.FromDegrees(observer.LongitudeDegrees),
            observer.AltitudeMeters / 1000d);
        var station = new GroundStation(location);
        var tleSatellite = new OrbitSatellite(satellite.Name, satellite.TleLine1!, satellite.TleLine2!);
        var start = (startAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        // Observe's built-in pass list is empty for some TLEs, so scan elevation directly.
        // Two-minute steps are enough for a LEO pass and stay cheap enough to finish.
        for (var time = start; time <= start + searchWindow; time = time.AddMinutes(2))
        {
            try
            {
                if (station.Observe(tleSatellite, time).Elevation.Degrees >= 0)
                    return new DateTimeOffset(time, TimeSpan.Zero);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    public SatelliteGeographicPosition GetGeographicPosition(
        SatelliteInfo satellite,
        DateTimeOffset? observedAt = null)
    {
        if (!satellite.HasTle)
            throw new InvalidOperationException($"{satellite.Name} has no TLE data.");

        var tleSatellite = new OrbitSatellite(satellite.Name, satellite.TleLine1!, satellite.TleLine2!);
        var timestamp = (observedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var position = tleSatellite.Predict(timestamp).ToGeodetic();
        return new SatelliteGeographicPosition(
            position.Latitude.Degrees,
            position.Longitude.Degrees,
            position.Altitude);
    }

    public SatelliteLookAngle GetLookAngle(
        SatelliteInfo satellite,
        ObserverLocation observer,
        DateTimeOffset? observedAt = null)
    {
        if (!satellite.HasTle)
            throw new InvalidOperationException($"{satellite.Name} has no TLE data.");

        var location = new GeodeticCoordinate(
            Angle.FromDegrees(observer.LatitudeDegrees),
            Angle.FromDegrees(observer.LongitudeDegrees),
            observer.AltitudeMeters / 1000d);
        var station = new GroundStation(location);
        var tleSatellite = new OrbitSatellite(satellite.Name, satellite.TleLine1!, satellite.TleLine2!);
        var timestamp = (observedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var observation = station.Observe(tleSatellite, timestamp);

        return new SatelliteLookAngle(
            observation.Azimuth.Degrees,
            observation.Elevation.Degrees,
            observation.Range,
            observation.RangeRate);
    }
}
