namespace NeuroSDR.Plugins.Broadcast;

internal static class EibiTransmitterLocations
{
    public static bool TryLocate(EibiEntry entry, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        var itu = entry.Itu.Trim();
        if (itu.Equals("CHN", StringComparison.OrdinalIgnoreCase))
        {
            ScatterChina(RowKey(entry), out latitude, out longitude);
            return true;
        }
        if (!Centroids.TryGetValue(itu, out var center))
            return false;
        Offset(RowKey(entry), center.Lat, center.Lon, out latitude, out longitude);
        return true;
    }

    public static string RowKey(EibiEntry entry) =>
        $"{entry.FrequencyHz}|{entry.StartUtc.Ticks}|{entry.Station}";

    private static void ScatterChina(string key, out double latitude, out double longitude)
    {
        unchecked
        {
            var hash = (uint)key.GetHashCode();
            var u = (hash & 0xFFFF) / 65535d;
            var v = (hash >> 16) / 65535d;
            // Spread across the PRC land mass instead of piling on the centroid.
            latitude = 21.5 + v * 31.0;
            longitude = 80.0 + u * 52.0;
        }
    }

    private static void Offset(string key, double lat, double lon, out double latitude, out double longitude)
    {
        unchecked
        {
            var hash = (uint)key.GetHashCode();
            var dLat = ((hash & 0xFF) / 255d - 0.5) * 2.2;
            var dLon = (((hash >> 8) & 0xFF) / 255d - 0.5) * 2.8;
            latitude = Math.Clamp(lat + dLat, -85, 85);
            longitude = lon + dLon;
            if (longitude > 180) longitude -= 360;
            if (longitude < -180) longitude += 360;
        }
    }

    // Approximate geographic centers of EiBi ITU transmitter countries.
    private static readonly Dictionary<string, (double Lat, double Lon)> Centroids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AFS"] = (-29.0, 24.0), ["ALG"] = (28.0, 3.0), ["ARG"] = (-34.0, -64.0),
        ["ARS"] = (24.0, 45.0), ["ASC"] = (-7.95, -14.35), ["AUS"] = (-25.0, 134.0),
        ["AUT"] = (47.5, 14.5), ["BEL"] = (50.5, 4.5), ["BLR"] = (53.5, 28.0),
        ["BRA"] = (-10.0, -55.0), ["BUL"] = (42.7, 25.3), ["CAN"] = (56.0, -106.0),
        ["CHN"] = (35.0, 105.0), ["CLN"] = (7.9, 80.7), ["CTR"] = (10.0, -84.0),
        ["CUB"] = (21.5, -80.0), ["CVA"] = (41.90, 12.45), ["CZE"] = (49.8, 15.5),
        ["D"] = (51.0, 10.0), ["DNK"] = (56.0, 10.0), ["E"] = (26.0, 30.0),
        ["EQA"] = (-1.8, -78.2), ["F"] = (46.0, 2.0), ["FIN"] = (64.0, 26.0),
        ["G"] = (54.0, -2.0), ["GRC"] = (39.0, 22.0), ["GUM"] = (13.44, 144.79),
        ["HNG"] = (47.0, 19.5), ["HOL"] = (52.3, 5.5), ["HRV"] = (45.1, 15.2),
        ["I"] = (42.8, 12.8), ["IND"] = (22.0, 79.0), ["INS"] = (-2.0, 118.0),
        ["IRL"] = (53.4, -8.0), ["IRN"] = (32.0, 53.0), ["IRQ"] = (33.0, 44.0),
        ["ISL"] = (65.0, -18.0), ["ISR"] = (31.5, 35.0), ["J"] = (36.0, 138.0),
        ["JOR"] = (31.0, 36.0), ["KAZ"] = (48.0, 67.0), ["KGZ"] = (41.2, 74.8),
        ["KOR"] = (36.5, 127.8), ["KWT"] = (29.3, 47.5), ["LBN"] = (33.9, 35.9),
        ["LTU"] = (55.2, 24.0), ["LVA"] = (56.9, 24.6), ["MCO"] = (43.73, 7.42),
        ["MDA"] = (47.0, 29.0), ["MEX"] = (23.0, -102.0), ["MLA"] = (4.0, 102.0),
        ["MLD"] = (3.2, 73.0), ["MNG"] = (46.0, 105.0), ["MRA"] = (15.2, 145.75),
        ["MRC"] = (32.0, -6.0), ["NIG"] = (10.0, 8.0), ["NOR"] = (62.0, 10.0),
        ["NZL"] = (-41.0, 174.0), ["OMA"] = (21.0, 57.0), ["PAK"] = (30.0, 69.0),
        ["PHL"] = (13.0, 122.0), ["POL"] = (52.0, 20.0), ["POR"] = (39.5, -8.0),
        ["ROU"] = (46.0, 25.0), ["RUS"] = (60.0, 100.0), ["S"] = (62.0, 15.0),
        ["SDN"] = (15.5, 32.0), ["SEN"] = (14.5, -14.5), ["SEY"] = (-4.6, 55.5),
        ["SNG"] = (1.35, 103.82), ["STP"] = (0.2, 6.6), ["SUI"] = (46.8, 8.2),
        ["SVK"] = (48.7, 19.7), ["SWZ"] = (-26.5, 31.5), ["SYR"] = (35.0, 38.0),
        ["THA"] = (15.5, 101.0), ["TJK"] = (38.5, 71.0), ["TKM"] = (39.0, 59.5),
        ["TUN"] = (34.0, 9.0), ["TUR"] = (39.0, 35.0), ["TWN"] = (23.7, 121.0),
        ["UAE"] = (24.0, 54.0), ["UKR"] = (49.0, 32.0), ["USA"] = (40.0, -98.0),
        ["UZB"] = (41.5, 64.0), ["VTN"] = (16.0, 108.0), ["ZMB"] = (-13.0, 28.0)
    };
}
