namespace NeuroSDR.Plugins.Dump1090;

/// <summary>Airborne CPR from dump1090 (1090-WP-9-14 NL table).</summary>
internal static class ModeSCpr
{
    public static bool TryDecodeAirborne(
        int evenLat, int evenLon, long evenTime,
        int oddLat, int oddLon, long oddTime,
        out double lat, out double lon)
    {
        const double airDlat0 = 360.0 / 60;
        const double airDlat1 = 360.0 / 59;
        var j = Math.Floor((59.0 * evenLat - 60.0 * oddLat) / 131072.0 + 0.5);
        var rlat0 = airDlat0 * (Mod((int)j, 60) + evenLat / 131072.0);
        var rlat1 = airDlat1 * (Mod((int)j, 59) + oddLat / 131072.0);
        if (rlat0 >= 270) rlat0 -= 360;
        if (rlat1 >= 270) rlat1 -= 360;
        lat = 0;
        lon = 0;
        if (Nl(rlat0) != Nl(rlat1)) return false;
        if (evenTime > oddTime)
        {
            var ni = N(rlat0, false);
            var m = Math.Floor(((evenLon * (Nl(rlat0) - 1) - oddLon * Nl(rlat0)) / 131072.0) + 0.5);
            lon = Dlon(rlat0, false) * (Mod((int)m, ni) + evenLon / 131072.0);
            lat = rlat0;
        }
        else
        {
            var ni = N(rlat1, true);
            var m = Math.Floor(((evenLon * (Nl(rlat1) - 1) - oddLon * Nl(rlat1)) / 131072.0) + 0.5);
            lon = Dlon(rlat1, true) * (Mod((int)m, ni) + oddLon / 131072.0);
            lat = rlat1;
        }
        if (lon > 180) lon -= 360;
        return true;
    }

    private static int Mod(int a, int b)
    {
        if (b <= 0) return 0;
        var res = a % b;
        return res < 0 ? res + b : res;
    }

    private static int N(double lat, bool odd)
    {
        var nl = Nl(lat) - (odd ? 1 : 0);
        return nl < 1 ? 1 : nl;
    }

    private static double Dlon(double lat, bool odd) => 360.0 / N(lat, odd);

    private static int Nl(double lat)
    {
        if (lat < 0) lat = -lat;
        if (lat < 10.47047130) return 59;
        if (lat < 14.82817437) return 58;
        if (lat < 18.18626357) return 57;
        if (lat < 21.02939493) return 56;
        if (lat < 23.54504487) return 55;
        if (lat < 25.82924707) return 54;
        if (lat < 27.93898710) return 53;
        if (lat < 29.91135686) return 52;
        if (lat < 31.77209708) return 51;
        if (lat < 33.53993436) return 50;
        if (lat < 35.22899598) return 49;
        if (lat < 36.85025108) return 48;
        if (lat < 38.41241892) return 47;
        if (lat < 39.92256684) return 46;
        if (lat < 41.38651832) return 45;
        if (lat < 42.80914012) return 44;
        if (lat < 44.19454951) return 43;
        if (lat < 45.54626723) return 42;
        if (lat < 46.86733252) return 41;
        if (lat < 48.16039128) return 40;
        if (lat < 49.42776439) return 39;
        if (lat < 50.67150166) return 38;
        if (lat < 51.89342469) return 37;
        if (lat < 53.09516153) return 36;
        if (lat < 54.27817472) return 35;
        if (lat < 55.44378444) return 34;
        if (lat < 56.59318756) return 33;
        if (lat < 57.72747354) return 32;
        if (lat < 58.84763776) return 31;
        if (lat < 59.95459277) return 30;
        if (lat < 61.04917774) return 29;
        if (lat < 62.13216659) return 28;
        if (lat < 63.20427479) return 27;
        if (lat < 64.26616523) return 26;
        if (lat < 65.31845310) return 25;
        if (lat < 66.36171008) return 24;
        if (lat < 67.39646774) return 23;
        if (lat < 68.42322022) return 22;
        if (lat < 69.44242631) return 21;
        if (lat < 70.45451075) return 20;
        if (lat < 71.45986473) return 19;
        if (lat < 72.45884545) return 18;
        if (lat < 73.45177442) return 17;
        if (lat < 74.43893416) return 16;
        if (lat < 75.42056257) return 15;
        if (lat < 76.39684391) return 14;
        if (lat < 77.36789461) return 13;
        if (lat < 78.33374083) return 12;
        if (lat < 79.29428225) return 11;
        if (lat < 80.24923213) return 10;
        if (lat < 81.19801349) return 9;
        if (lat < 82.13956981) return 8;
        if (lat < 83.07199445) return 7;
        if (lat < 83.99173563) return 6;
        if (lat < 84.89166191) return 5;
        if (lat < 85.75541621) return 4;
        if (lat < 86.53536998) return 3;
        if (lat < 87.00000000) return 2;
        return 1;
    }
}

internal sealed class AdsbAircraft
{
    public required string Icao { get; init; }
    public string Flight { get; set; } = "";
    public int? AltitudeFt { get; set; }
    public int? SpeedKt { get; set; }
    public int? Heading { get; set; }
    public int? Squawk { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int Messages { get; set; }
    public DateTime LastUtc { get; set; }
    public int EvenCprLat, EvenCprLon, OddCprLat, OddCprLon;
    public long EvenCprTime, OddCprTime;
}

internal sealed class AdsbAircraftTracker
{
    private readonly Dictionary<string, AdsbAircraft> _aircraft = new(StringComparer.OrdinalIgnoreCase);
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(60);

    public IReadOnlyCollection<AdsbAircraft> Snapshot()
    {
        Expire();
        return _aircraft.Values.OrderBy(item => item.Icao).ToArray();
    }

    public void Clear() => _aircraft.Clear();

    public void Apply(ModeSMessage mm, DateTime utc)
    {
        if (!mm.CrcOk || mm.Address == 0) return;
        Expire();
        if (!_aircraft.TryGetValue(mm.Icao, out var ac))
            _aircraft[mm.Icao] = ac = new AdsbAircraft { Icao = mm.Icao };
        ac.LastUtc = utc;
        ac.Messages++;
        if (mm.Flight.Length > 0) ac.Flight = mm.Flight;
        if (mm.Altitude != 0) ac.AltitudeFt = mm.Unit == ModeSDecoder.UnitMeters ? (int)(mm.Altitude * 3.28084) : mm.Altitude;
        if (mm.Velocity > 0) ac.SpeedKt = mm.Velocity;
        if (mm.Heading != 0 || mm.HeadingIsValid) ac.Heading = mm.Heading;
        if (mm.MsgType is 5 or 21) ac.Squawk = mm.Identity;
        if (mm.MsgType == 17 && mm.MeType is >= 9 and <= 18)
        {
            var ticks = utc.Ticks;
            if (mm.FFlag == 0)
            {
                ac.EvenCprLat = mm.RawLatitude;
                ac.EvenCprLon = mm.RawLongitude;
                ac.EvenCprTime = ticks;
            }
            else
            {
                ac.OddCprLat = mm.RawLatitude;
                ac.OddCprLon = mm.RawLongitude;
                ac.OddCprTime = ticks;
            }
            if (ac.EvenCprTime != 0 && ac.OddCprTime != 0 &&
                Math.Abs(ac.EvenCprTime - ac.OddCprTime) < TimeSpan.FromSeconds(10).Ticks &&
                ModeSCpr.TryDecodeAirborne(ac.EvenCprLat, ac.EvenCprLon, ac.EvenCprTime,
                    ac.OddCprLat, ac.OddCprLon, ac.OddCprTime, out var lat, out var lon))
            {
                ac.Latitude = lat;
                ac.Longitude = lon;
            }
        }
    }

    private void Expire()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var dead in _aircraft.Where(pair => pair.Value.LastUtc < cutoff).Select(pair => pair.Key).ToArray())
            _aircraft.Remove(dead);
    }
}
