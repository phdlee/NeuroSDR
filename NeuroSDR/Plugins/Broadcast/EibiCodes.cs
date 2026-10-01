namespace NeuroSDR.Plugins.Broadcast;

internal static class EibiCodes
{
    public static string Language(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var parts = code.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(", ", parts.Select(ExpandLanguage));
    }

    public static string Country(string itu)
    {
        var key = itu.Trim();
        if (key.Length == 0) return "";
        return Itu.TryGetValue(key, out var name) ? name : key;
    }

    public static string Site(string remarks)
    {
        var key = remarks.Trim();
        if (key.Length == 0) return "";
        if (key.StartsWith('/'))
        {
            var body = key.TrimStart('/');
            var dash = body.LastIndexOf('-');
            var siteKey = dash >= 0 ? body[(dash + 1)..] : body;
            var via = dash >= 0 ? Country(body[..dash]) : "";
            var site = ExpandSite(siteKey);
            return via.Length > 0 ? $"via {via} · {site}" : site;
        }
        return ExpandSite(key);
    }

    public static string Days(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "1234567") return "Daily";
        return raw.Trim();
    }

    public static bool DayMatches(string days, DayOfWeek dow)
    {
        if (string.IsNullOrWhiteSpace(days) || days == "1234567") return true;
        var text = days.Trim();
        var want = dow == DayOfWeek.Sunday ? 7 : (int)dow;
        if (text.Any(char.IsDigit))
        {
            var code = (char)('0' + want);
            if (text.Contains(code)) return true;
        }
        var token = dow switch
        {
            DayOfWeek.Monday => "Mo",
            DayOfWeek.Tuesday => "Tu",
            DayOfWeek.Wednesday => "We",
            DayOfWeek.Thursday => "Th",
            DayOfWeek.Friday => "Fr",
            DayOfWeek.Saturday => "Sa",
            _ => "Su"
        };
        return text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpandLanguage(string raw)
    {
        var key = raw.Trim();
        if (key.StartsWith('-')) key = key[1..];
        if (Languages.TryGetValue(raw.Trim(), out var named)) return named;
        if (Languages.TryGetValue(key, out named)) return named;
        return raw.Trim().TrimStart('-');
    }

    private static string ExpandSite(string key)
    {
        var id = key.Trim();
        return Sites.TryGetValue(id, out var name) ? $"{name} ({id})" : id;
    }

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CW"] = "CW", ["-CW"] = "CW", ["TS"] = "Time signal", ["-TS"] = "Time signal",
        ["MX"] = "Mixed", ["-MX"] = "Mixed",
        ["E"] = "English", ["D"] = "German", ["F"] = "French", ["S"] = "Spanish", ["P"] = "Portuguese",
        ["I"] = "Italian", ["R"] = "Russian", ["A"] = "Arabic", ["C"] = "Chinese", ["M"] = "Mandarin",
        ["J"] = "Japanese", ["K"] = "Korean", ["NL"] = "Dutch", ["DN"] = "Danish", ["DA"] = "Danish",
        ["SW"] = "Swedish", ["NO"] = "Norwegian", ["NW"] = "Norwegian", ["FI"] = "Finnish",
        ["PL"] = "Polish", ["CZ"] = "Czech", ["H"] = "Hungarian", ["RO"] = "Romanian", ["BG"] = "Bulgarian",
        ["GR"] = "Greek", ["T"] = "Turkish", ["IN"] = "Indonesian", ["TAG"] = "Tagalog", ["VT"] = "Vietnamese",
        ["THA"] = "Thai", ["TH"] = "Thai", ["BUR"] = "Burmese", ["BR"] = "Burmese", ["HIN"] = "Hindi",
        ["HI"] = "Hindi", ["UR"] = "Urdu", ["BEN"] = "Bengali", ["BN"] = "Bengali", ["FA"] = "Persian",
        ["HA"] = "Hausa", ["SWA"] = "Swahili", ["AM"] = "Amharic", ["PS"] = "Pashto", ["DR"] = "Dari",
        ["HR"] = "Croatian", ["SGA"] = "Shangaan", ["O"] = "Other", ["AL"] = "Albanian", ["SL"] = "Slovak",
        ["UK"] = "Ukrainian", ["BE"] = "Belarusian", ["CA"] = "Catalan", ["EO"] = "Esperanto"
    };

    private static readonly Dictionary<string, string> Itu = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALG"] = "Algeria", ["ARG"] = "Argentina", ["ARS"] = "Saudi Arabia", ["AUS"] = "Australia",
        ["AUT"] = "Austria", ["BEL"] = "Belgium", ["BLR"] = "Belarus", ["BRA"] = "Brazil",
        ["BUL"] = "Bulgaria", ["CAN"] = "Canada", ["CHN"] = "China", ["CLN"] = "Sri Lanka",
        ["CTR"] = "Costa Rica", ["CUB"] = "Cuba", ["CVA"] = "Vatican", ["CZE"] = "Czechia",
        ["D"] = "Germany", ["DNK"] = "Denmark", ["E"] = "Egypt", ["EQA"] = "Ecuador",
        ["F"] = "France", ["FIN"] = "Finland", ["G"] = "United Kingdom", ["GRC"] = "Greece",
        ["HNG"] = "Hungary", ["HOL"] = "Netherlands", ["HRV"] = "Croatia", ["I"] = "Italy",
        ["IND"] = "India", ["INS"] = "Indonesia", ["IRL"] = "Ireland", ["IRN"] = "Iran",
        ["IRQ"] = "Iraq", ["ISL"] = "Iceland", ["ISR"] = "Israel", ["J"] = "Japan",
        ["JOR"] = "Jordan", ["KAZ"] = "Kazakhstan", ["KGZ"] = "Kyrgyzstan", ["KOR"] = "Korea",
        ["KWT"] = "Kuwait", ["LBN"] = "Lebanon", ["LTU"] = "Lithuania", ["LVA"] = "Latvia",
        ["MCO"] = "Monaco", ["MDA"] = "Moldova", ["MEX"] = "Mexico", ["MLA"] = "Malaysia",
        ["MLD"] = "Maldives", ["MNG"] = "Mongolia", ["MRC"] = "Morocco", ["NIG"] = "Nigeria",
        ["NOR"] = "Norway", ["NZL"] = "New Zealand", ["OMA"] = "Oman", ["PAK"] = "Pakistan",
        ["PHL"] = "Philippines", ["POL"] = "Poland", ["POR"] = "Portugal", ["ROU"] = "Romania",
        ["RUS"] = "Russia", ["S"] = "Sweden", ["SDN"] = "Sudan", ["SEN"] = "Senegal",
        ["SEY"] = "Seychelles", ["SNG"] = "Singapore", ["STP"] = "Sao Tome", ["SUI"] = "Switzerland",
        ["SVK"] = "Slovakia", ["SWZ"] = "Eswatini", ["SYR"] = "Syria", ["THA"] = "Thailand",
        ["TJK"] = "Tajikistan", ["TKM"] = "Turkmenistan", ["TUN"] = "Tunisia", ["TUR"] = "Turkey",
        ["TWN"] = "Taiwan", ["UAE"] = "UAE", ["UKR"] = "Ukraine", ["USA"] = "USA",
        ["UZB"] = "Uzbekistan", ["VTN"] = "Vietnam", ["ZMB"] = "Zambia", ["ASC"] = "Ascension",
        ["GUM"] = "Guam", ["MRA"] = "Northern Mariana", ["AFS"] = "South Africa"
    };

    private static readonly Dictionary<string, string> Sites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["w"] = "Woofferton", ["r"] = "Rohrbach", ["n"] = "Nauen", ["an"] = "Anthorn",
        ["no"] = "Noviken", ["v"] = "Vizag", ["mo"] = "Molodechno", ["bk"] = "Bishkek",
        ["kd"] = "Krasnodar", ["ak"] = "Arkhangelsk", ["nn"] = "Nizhni Novgorod",
        ["g"] = "Grindavik", ["o"] = "Ohtakadoya", ["h"] = "Hagane", ["ff"] = "Fort Collins",
        ["t"] = "Taldom", ["sq"] = "Shangqiu", ["mf"] = "Mainflingen", ["p"] = "Pucheng",
        ["sh"] = "Saudanes", ["va"] = "Vardo", ["ka"] = "Kashi", ["a"] = "Abu Zaabal",
        ["y"] = "Yamata", ["B"] = "Beijing", ["xx"] = "Unknown", ["wof"] = "Woofferton",
        ["skn"] = "Skelton", ["ram"] = "Rampisham", ["iss"] = "Issoudun", ["mos"] = "Moscow",
        ["sm"] = "Samara", ["kl"] = "Kaliningrad", ["se"] = "Sevastopol", ["m"] = "Moscow",
        ["as"] = "Astrakhan", ["vl"] = "Vladivostok", ["pk"] = "Petropavlovsk", ["mg"] = "Magadan",
        ["pt"] = "Saint Petersburg", ["is"] = "Istanbul", ["dl"] = "Dlouhy"
    };
}
