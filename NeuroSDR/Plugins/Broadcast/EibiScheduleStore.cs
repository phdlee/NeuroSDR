using System.Net.Http;

namespace NeuroSDR.Plugins.Broadcast;

internal static class EibiScheduleStore
{
    private static readonly object Sync = new();
    private static readonly HttpClient Http = CreateHttp();
    private static IReadOnlyList<EibiEntry> _entries = [];
    private static string _status = "No schedule loaded";
    private static DateTime _loadedUtc;

    public static IReadOnlyList<EibiEntry> Entries
    {
        get { lock (Sync) return _entries; }
    }

    public static string Status
    {
        get { lock (Sync) return _status; }
    }

    public static DateTime LoadedUtc
    {
        get { lock (Sync) return _loadedUtc; }
    }

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NeuroSDR", "eibi", "sked.csv");

    public static void LoadCacheIfPresent()
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path)) return;
            Apply(File.ReadAllText(path), $"Cached · {new FileInfo(path).LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        catch (Exception exception)
        {
            lock (Sync) _status = exception.GetBaseException().Message;
        }
    }

    public static async Task<bool> DownloadAsync(CancellationToken cancellation = default)
    {
        var errors = new List<string>();
        foreach (var url in CandidateUrls(DateTime.UtcNow))
        {
            try
            {
                using var response = await Http.GetAsync(url, cancellation).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{url} · HTTP {(int)response.StatusCode}");
                    continue;
                }
                var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                if (Apply(text, $"Downloaded {Path.GetFileName(url)}"))
                {
                    var path = CachePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, text, cancellation).ConfigureAwait(false);
                    return true;
                }
                errors.Add($"{url} · empty or unrecognized");
            }
            catch (Exception exception)
            {
                errors.Add($"{url} · {exception.GetBaseException().Message}");
            }
        }
        lock (Sync) _status = errors.Count == 0 ? "Download failed" : errors[0];
        return false;
    }

    public static IReadOnlyList<EibiEntry> Filter(
        string language,
        string country,
        string search,
        bool onAirOnly,
        DateTime utc,
        long minHz = 0,
        long maxHz = 0)
    {
        IReadOnlyList<EibiEntry> source;
        lock (Sync) source = _entries;
        var lang = language.Trim();
        var itu = country.Trim();
        var q = search.Trim();
        var allLang = lang.Length == 0 || lang.Equals("All", StringComparison.OrdinalIgnoreCase);
        var allItu = itu.Length == 0 || itu.Equals("All", StringComparison.OrdinalIgnoreCase);
        List<EibiEntry> rows = [];
        foreach (var entry in source)
        {
            if (minHz > 0 && entry.FrequencyHz < minHz) continue;
            if (maxHz > 0 && entry.FrequencyHz > maxHz) continue;
            if (onAirOnly && !EibiScheduleParser.IsOnAir(entry, utc)) continue;
            if (!allLang &&
                !entry.Language.Equals(lang, StringComparison.OrdinalIgnoreCase) &&
                !entry.LanguageName.Equals(lang, StringComparison.OrdinalIgnoreCase) &&
                entry.LanguageName.IndexOf(lang, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var countryName = EibiCodes.Country(entry.Itu);
            if (!allItu &&
                !entry.Itu.Equals(itu, StringComparison.OrdinalIgnoreCase) &&
                !countryName.Equals(itu, StringComparison.OrdinalIgnoreCase))
                continue;
            if (q.Length > 0 &&
                entry.Station.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                entry.Site.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                countryName.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                entry.Target.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                entry.FrequencyHz.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            rows.Add(entry);
        }
        return rows;
    }

    private static bool Apply(string text, string status)
    {
        var parsed = EibiScheduleParser.Parse(text);
        if (parsed.Count == 0) return false;
        lock (Sync)
        {
            _entries = parsed;
            _loadedUtc = DateTime.UtcNow;
            _status = $"{status} · {parsed.Count:N0} rows";
        }
        return true;
    }

    internal static IReadOnlyList<string> CandidateUrls(DateTime utc)
    {
        var urls = new List<string>();
        foreach (var season in SeasonIds(utc))
        {
            urls.Add($"http://www.eibispace.de/dx/sked-{season}.csv");
            urls.Add($"https://www.eibispace.de/dx/sked-{season}.csv");
        }
        return urls;
    }

    internal static IReadOnlyList<string> SeasonIds(DateTime utc)
    {
        var year = utc.Year;
        var aStart = LastSunday(year, 3);
        var aEnd = LastSunday(year, 10);
        string current;
        if (utc.Date >= aStart && utc.Date < aEnd) current = $"a{year % 100:00}";
        else if (utc.Date >= aEnd) current = $"b{year % 100:00}";
        else current = $"b{(year - 1) % 100:00}";

        var list = new List<string> { current };
        void Add(string id)
        {
            if (!list.Contains(id, StringComparer.OrdinalIgnoreCase)) list.Add(id);
        }
        var yy = year % 100;
        Add($"a{yy:00}");
        Add($"b{yy:00}");
        Add($"a{(yy - 1 + 100) % 100:00}");
        Add($"b{(yy - 1 + 100) % 100:00}");
        return list;
    }

    private static DateTime LastSunday(int year, int month)
    {
        var day = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (day.DayOfWeek != DayOfWeek.Sunday) day = day.AddDays(-1);
        return day.Date;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NeuroSDR/1.0 (EiBi schedule)");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/csv,text/plain,*/*");
        return http;
    }
}
