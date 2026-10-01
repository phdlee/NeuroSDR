using System.Globalization;
using System.Text.RegularExpressions;

namespace NeuroSDR.Hardware;

/// <summary>
/// Location helpers for remote SDR directory rows (Maidenhead, GPS parse, city/country fallbacks).
/// </summary>
internal static class RemoteSdrGeo
{
    private static readonly Dictionary<string, (double Lat, double Lon)> CountryCentroids =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Afghanistan"] = (33.94, 67.71),
            ["Albania"] = (41.15, 20.17),
            ["Algeria"] = (28.03, 1.66),
            ["Andorra"] = (42.55, 1.60),
            ["Argentina"] = (-38.42, -63.62),
            ["Armenia"] = (40.07, 45.04),
            ["Australia"] = (-25.27, 133.78),
            ["Austria"] = (47.52, 14.55),
            ["Azerbaijan"] = (40.14, 47.58),
            ["Belarus"] = (53.71, 27.95),
            ["Belgium"] = (50.50, 4.47),
            ["Bosnia"] = (43.92, 17.68),
            ["Bosnia and Herzegovina"] = (43.92, 17.68),
            ["Brazil"] = (-14.24, -51.93),
            ["Brasil"] = (-14.24, -51.93),
            ["Bulgaria"] = (42.73, 25.49),
            ["Canada"] = (56.13, -106.35),
            ["Chile"] = (-35.68, -71.54),
            ["China"] = (35.86, 104.20),
            ["Colombia"] = (4.57, -74.30),
            ["Croatia"] = (45.10, 15.20),
            ["Czech"] = (49.82, 15.47),
            ["Czech Republic"] = (49.82, 15.47),
            ["Czechia"] = (49.82, 15.47),
            ["Denmark"] = (56.26, 9.50),
            ["Egypt"] = (26.82, 30.80),
            ["England"] = (52.36, -1.17),
            ["Estonia"] = (58.60, 25.01),
            ["Finland"] = (61.92, 25.75),
            ["France"] = (46.23, 2.21),
            ["Georgia"] = (42.32, 43.36),
            ["Germany"] = (51.17, 10.45),
            ["Greece"] = (39.07, 21.82),
            ["Holland"] = (52.13, 5.29),
            ["Hungary"] = (47.16, 19.50),
            ["Iceland"] = (64.96, -19.02),
            ["India"] = (20.59, 78.96),
            ["Indonesia"] = (-0.79, 113.92),
            ["Iran"] = (32.43, 53.69),
            ["Ireland"] = (53.14, -7.69),
            ["Israel"] = (31.05, 34.85),
            ["Italy"] = (41.87, 12.57),
            ["Japan"] = (36.20, 138.25),
            ["Kazakhstan"] = (48.02, 66.92),
            ["Korea"] = (35.91, 127.77),
            ["Latvia"] = (56.88, 24.60),
            ["Lithuania"] = (55.17, 23.88),
            ["Luxembourg"] = (49.82, 6.13),
            ["Mexico"] = (23.63, -102.55),
            ["Morocco"] = (31.79, -7.09),
            ["Netherlands"] = (52.13, 5.29),
            ["New Zealand"] = (-40.90, 174.89),
            ["NL"] = (52.13, 5.29),
            ["Norway"] = (60.47, 8.47),
            ["Poland"] = (51.92, 19.15),
            ["Portugal"] = (39.40, -8.22),
            ["Romania"] = (45.94, 24.97),
            ["Russia"] = (61.52, 105.32),
            ["Scotland"] = (56.49, -4.20),
            ["Serbia"] = (44.02, 21.01),
            ["Slovakia"] = (48.67, 19.70),
            ["Slovenia"] = (46.15, 14.99),
            ["South Africa"] = (-30.56, 22.94),
            ["South Korea"] = (35.91, 127.77),
            ["Spain"] = (40.46, -3.75),
            ["Sweden"] = (60.13, 18.64),
            ["Switzerland"] = (46.82, 8.23),
            ["Taiwan"] = (23.70, 120.96),
            ["Thailand"] = (15.87, 100.99),
            ["Turkey"] = (38.96, 35.24),
            ["Türkiye"] = (38.96, 35.24),
            ["U.S.A"] = (37.09, -95.71),
            ["U.S.A."] = (37.09, -95.71),
            ["UK"] = (55.38, -3.44),
            ["Ukraine"] = (48.38, 31.17),
            ["United Kingdom"] = (55.38, -3.44),
            ["United States"] = (37.09, -95.71),
            ["Uruguay"] = (-32.52, -55.77),
            ["US"] = (37.09, -95.71),
            ["USA"] = (37.09, -95.71),
            ["Venezuela"] = (6.42, -66.59),
            ["Wales"] = (52.13, -3.78),
        };

    private static readonly Dictionary<string, (double Lat, double Lon)> CityCoords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Amsterdam"] = (52.37, 4.90),
            ["Arvika"] = (59.66, 12.59),
            ["Assen"] = (52.99, 6.56),
            ["Athens"] = (37.98, 23.73),
            ["Berlin"] = (52.52, 13.41),
            ["Bordeaux"] = (44.84, -0.58),
            ["Brussels"] = (50.85, 4.35),
            ["Bucharest"] = (44.43, 26.10),
            ["Budapest"] = (47.50, 19.04),
            ["Buenos Aires"] = (-34.60, -58.38),
            ["Chelyabinsk"] = (55.16, 61.44),
            ["Chichester"] = (50.84, -0.78),
            ["Copenhagen"] = (55.68, 12.57),
            ["Dublin"] = (53.35, -6.26),
            ["Enschede"] = (52.22, 6.90),
            ["Helsinki"] = (60.17, 24.94),
            ["London"] = (51.51, -0.13),
            ["Madrid"] = (40.42, -3.70),
            ["Maasbree"] = (51.36, 6.07),
            ["Milford"] = (41.32, -74.80),
            ["Mora"] = (61.00, 14.54),
            ["Moscow"] = (55.76, 37.62),
            ["Munich"] = (48.14, 11.58),
            ["Nantwich"] = (53.07, -2.52),
            ["Oslo"] = (59.91, 10.75),
            ["Paris"] = (48.86, 2.35),
            ["Prague"] = (50.08, 14.44),
            ["Rome"] = (41.90, 12.50),
            ["Seoul"] = (37.57, 126.98),
            ["Stockholm"] = (59.33, 18.07),
            ["Tokyo"] = (35.68, 139.69),
            ["Twente"] = (52.24, 6.85),
            ["Utah"] = (41.11, -112.35),
            ["Corinne"] = (41.55, -112.11),
            ["Ventspils"] = (57.39, 21.56),
            ["Vienna"] = (48.21, 16.37),
            ["Warsaw"] = (52.23, 21.01),
            ["Washington"] = (38.91, -77.04),
            ["Wildflecken"] = (50.37, 9.91),
            ["Yaroslavl"] = (57.63, 39.87),
            ["Zurich"] = (47.38, 8.54),
        };

    public static bool TryParseGps(string? text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Regex.Match(text, @"\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)");
        if (!m.Success)
            m = Regex.Match(text, @"(-?\d+(?:\.\d+)?)\s*[,;]\s*(-?\d+(?:\.\d+)?)");
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)) return false;
        if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out longitude)) return false;
        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
    }

    public static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text) &&
               double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Decode Maidenhead locator (4, 6, or 8 characters) to approximate lat/lon.</summary>
    public static bool TryMaidenhead(string? grid, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(grid)) return false;
        var m = Regex.Match(grid.Trim().ToUpperInvariant(), @"^([A-R]{2})(\d{2})([A-X]{2})?(\d{2})?$");
        if (!m.Success) return false;
        var g = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value;
        var lon = (g[0] - 'A') * 20.0 + (g[2] - '0') * 2.0 - 180.0;
        var lat = (g[1] - 'A') * 10.0 + (g[3] - '0') * 1.0 - 90.0;
        if (g.Length >= 6)
        {
            lon += (g[4] - 'A') * (2.0 / 24.0);
            lat += (g[5] - 'A') * (1.0 / 24.0);
            if (g.Length >= 8)
            {
                lon += (g[6] - '0') * (2.0 / 240.0) + 1.0 / 240.0;
                lat += (g[7] - '0') * (1.0 / 240.0) + 0.5 / 240.0;
            }
            else
            {
                lon += 1.0 / 24.0;
                lat += 0.5 / 24.0;
            }
        }
        else
        {
            lon += 1.0;
            lat += 0.5;
        }
        latitude = lat;
        longitude = lon;
        return true;
    }

    public static bool LooksLikeMaidenhead(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text.Trim(), @"^[A-Ra-r]{2}\d{2}([A-Xa-x]{2})?(\d{2})?$");
    }
    public static void SplitLocation(string? location, out string country, out string city)
    {
        country = "";
        city = "";
        if (string.IsNullOrWhiteSpace(location)) return;
        var text = Regex.Replace(location, @"\s+", " ").Trim();
        var parts = text.Split(['/', '|', '·'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            country = parts[0];
            city = string.Join(" / ", parts.Skip(1));
            return;
        }
        parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            city = string.Join(", ", parts.Take(parts.Length - 1));
            country = parts[^1];
            if (LooksLikeCountry(city) && !LooksLikeCountry(country))
                (country, city) = (city, country);
            return;
        }
        if (LooksLikeCountry(text)) country = text;
        else city = text;
    }

    public static bool TryGeocode(string? country, string? city, string? freeText, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (!string.IsNullOrWhiteSpace(city) && CityCoords.TryGetValue(city.Trim(), out var c))
        {
            latitude = c.Lat;
            longitude = c.Lon;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(freeText))
        {
            foreach (var (name, coords) in CityCoords)
            {
                if (freeText.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    latitude = coords.Lat;
                    longitude = coords.Lon;
                    return true;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(country) && CountryCentroids.TryGetValue(country.Trim(), out var nation))
        {
            latitude = nation.Lat;
            longitude = nation.Lon;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(freeText))
        {
            foreach (var (name, coords) in CountryCentroids)
            {
                if (freeText.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    latitude = coords.Lat;
                    longitude = coords.Lon;
                    return true;
                }
            }
        }
        return false;
    }

    public static string InferCountry(string? location, string? name, double? lat, double? lon)
    {
        SplitLocation(location, out var country, out _);
        if (!string.IsNullOrWhiteSpace(country) && LooksLikeCountry(country))
            return NormalizeCountry(country);
        var hay = $"{location} {name}";
        foreach (var key in CountryCentroids.Keys.OrderByDescending(k => k.Length))
        {
            if (hay.Contains(key, StringComparison.OrdinalIgnoreCase))
                return NormalizeCountry(key);
        }
        if (lat is double la && lon is double lo)
            return ApproximateCountryFromCoords(la, lo);
        return country;
    }

    private static bool LooksLikeCountry(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        if (CountryCentroids.ContainsKey(text.Trim())) return true;
        try
        {
            foreach (var region in CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                         .Select(c =>
                         {
                             try { return new RegionInfo(c.Name); }
                             catch { return null; }
                         })
                         .Where(r => r is not null)
                         .DistinctBy(r => r!.EnglishName))
            {
                if (region!.EnglishName.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                    region.TwoLetterISORegionName.Equals(text, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static string NormalizeCountry(string country)
    {
        country = country.Trim();
        if (country.Equals("UK", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("England", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("Scotland", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("Wales", StringComparison.OrdinalIgnoreCase))
            return "United Kingdom";
        if (country.Equals("USA", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("US", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("U.S.A", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("U.S.A.", StringComparison.OrdinalIgnoreCase))
            return "United States";
        if (country.Equals("Holland", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("NL", StringComparison.OrdinalIgnoreCase))
            return "Netherlands";
        if (country.Equals("Brasil", StringComparison.OrdinalIgnoreCase))
            return "Brazil";
        if (country.Equals("Czechia", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("Czech", StringComparison.OrdinalIgnoreCase))
            return "Czech Republic";
        return country;
    }

    private static string ApproximateCountryFromCoords(double lat, double lon)
    {
        // Prefer empty over coarse continent labels — InferCountry uses this as last resort.
        _ = lat;
        _ = lon;
        return "";
    }

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthKm = 6_371;
        var r1 = lat1 * Math.PI / 180;
        var r2 = lat2 * Math.PI / 180;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(r1) * Math.Cos(r2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
    }
}
