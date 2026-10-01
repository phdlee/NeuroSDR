using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NeuroSDR.Hardware;

internal sealed class RemoteSdrEntry
{
    public required string Name { get; init; }
    public required string Protocol { get; init; }
    public required string Url { get; init; }
    public string Location { get; init; } = "";
    public string Country { get; init; } = "";
    public string City { get; init; } = "";
    public string Grid { get; init; } = "";
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AltitudeMeters { get; init; }
    public IReadOnlyList<RemoteSdrBandSpan> Bands { get; init; } = [];
    public string BandSummary { get; init; } = "";
    public RemoteSdrSpectrum Spectrum { get; init; }
    /// <summary>0 = feed order (matches public website). Higher ranks sort earlier only when intentionally used.</summary>
    internal int SortScore { get; init; }
    public int DirectoryOrder { get; init; }

    public bool HasCoordinates => Latitude is not null && Longitude is not null;

    public string LocationSummary
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Country) && !string.IsNullOrWhiteSpace(City))
                return $"{Country} / {City}";
            if (!string.IsNullOrWhiteSpace(Country)) return Country;
            if (!string.IsNullOrWhiteSpace(City)) return City;
            return Location;
        }
    }

    public override string ToString()
    {
            var where = LocationSummary;
            var band = BandSummary;
            if (!string.IsNullOrWhiteSpace(band) && !string.IsNullOrWhiteSpace(where))
                return $"{Name}  ·  {band}  ·  {where}";
            if (!string.IsNullOrWhiteSpace(band)) return $"{Name}  ·  {band}";
            if (string.IsNullOrWhiteSpace(where)) return Name;
            return $"{Name}  ·  {where}";
    }
}

internal static class RemoteSdrCatalog
{
    public const string DirectoryFileName = "sdr_servers_active.csv";
    public const string FavoritesFileName = "sdr_favorites.json";
    public const string DatabaseFileName = "remote_sdr_directory.json";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly object Gate = new();
    private static IReadOnlyList<RemoteSdrEntry>? _cachedMerged;
    private static DateTimeOffset _lastOfficialRefreshUtc = DateTimeOffset.MinValue;

    // Official public directories — order must match what users see on the websites.
    // WebSDR: websdr.org / utwente org page uses fmt=2 (popularity / active-user order).
    // Kiwi: linkfanel mirror of kiwisdr.com/public/ — keep file order (do not re-sort).
    private static readonly string[] KiwiDirectoryUrls =
    [
        "http://rx.linkfanel.net/kiwisdr_com.js",
        "https://rx.skywavelinux.com/kiwisdr_com.js"
    ];

    private static readonly string[] WebSdrDirectoryUrls =
    [
        "http://websdr.ewi.utwente.nl/~~websdrlistk?v=1&fmt=2&chseq=0",
        "http://websdr.org/websdrlistk.tmp"
    ];

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, DirectoryFileName);

    private static string CacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeuroSDR", "directory");

    private static string KiwiCachePath => Path.Combine(CacheDirectory, "kiwisdr_com.js");
    private static string WebSdrCachePath => Path.Combine(CacheDirectory, "websdrlist_fmt2.json");
    private static string DatabasePath => Path.Combine(CacheDirectory, DatabaseFileName);

    public static DateTimeOffset LastOfficialRefreshUtc
    {
        get { lock (Gate) return _lastOfficialRefreshUtc; }
    }

    public static IReadOnlyList<RemoteSdrEntry> LoadDirectory()
    {
        lock (Gate)
        {
            if (_cachedMerged is { Count: > 0 })
                return _cachedMerged;

            var fromDb = TryLoadDatabase();
            if (fromDb is { Count: > 0 } &&
                fromDb.Any(item => (item.Protocol is "WebSDR" or "KiwiSDR") && item.Bands.Count == 0))
                fromDb = null;
            if (fromDb is { Count: > 0 })
            {
                _cachedMerged = fromDb;
                return _cachedMerged;
            }

            _cachedMerged = BuildMergedDirectory();
            TrySaveDatabase(_cachedMerged);
            return _cachedMerged;
        }
    }

    public static RemoteSdrEntry? FindByUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var key = UrlKey(NormalizeUrl(url));
        return LoadDirectory().FirstOrDefault(e => UrlKey(e.Url) == key);
    }

    public static bool SameSite(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return UrlKey(NormalizeUrl(a)) == UrlKey(NormalizeUrl(b));
    }

    public static IReadOnlyList<RemoteSdrEntry> Upsert(RemoteSdrEntry entry, string? replaceUrl = null)
    {
        lock (Gate)
        {
            var list = LoadDirectory().ToList();
            var key = UrlKey(NormalizeUrl(string.IsNullOrWhiteSpace(replaceUrl) ? entry.Url : replaceUrl));
            var index = list.FindIndex(item => UrlKey(item.Url) == key);
            var order = index >= 0 ? list[index].DirectoryOrder : list.Count;
            var saved = Enrich(new RemoteSdrEntry
            {
                Name = entry.Name,
                Protocol = NormalizeProtocol(entry.Protocol) ?? entry.Protocol,
                Url = NormalizeUrl(entry.Url),
                Location = entry.Location,
                Country = entry.Country,
                City = entry.City,
                Grid = entry.Grid,
                Latitude = entry.Latitude,
                Longitude = entry.Longitude,
                AltitudeMeters = entry.AltitudeMeters,
                Bands = entry.Bands,
                DirectoryOrder = order,
                SortScore = entry.SortScore
            });
            if (index >= 0) list[index] = saved;
            else list.Add(saved);
            _cachedMerged = list;
            TrySaveDatabase(list);
            return list;
        }
    }

    public static IReadOnlyList<RemoteSdrEntry> RemoveByUrl(string url)
    {
        lock (Gate)
        {
            var key = UrlKey(NormalizeUrl(url));
            var list = LoadDirectory().Where(item => UrlKey(item.Url) != key).ToList();
            _cachedMerged = list;
            TrySaveDatabase(list);
            return list;
        }
    }

    /// <summary>
    /// Downloads official KiwiSDR / WebSDR directories, caches them, rebuilds the merged Site list + local DB.
    /// </summary>
    public static async Task<bool> RefreshOfficialDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CacheDirectory);
        var refreshed = false;

        foreach (var url in KiwiDirectoryUrls)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                if (bytes.Length < 1_000) continue;
                await File.WriteAllBytesAsync(KiwiCachePath, bytes, cancellationToken).ConfigureAwait(false);
                refreshed = true;
                break;
            }
            catch
            {
                // try next mirror
            }
        }

        foreach (var url in WebSdrDirectoryUrls)
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                if (bytes.Length < 200) continue;
                await File.WriteAllBytesAsync(WebSdrCachePath, bytes, cancellationToken).ConfigureAwait(false);
                refreshed = true;
                break;
            }
            catch
            {
                // try next source
            }
        }

        if (!refreshed && !File.Exists(KiwiCachePath) && !File.Exists(WebSdrCachePath) && !File.Exists(DatabasePath))
            return false;

        lock (Gate)
        {
            _cachedMerged = BuildMergedDirectory();
            TrySaveDatabase(_cachedMerged);
            _lastOfficialRefreshUtc = DateTimeOffset.UtcNow;
        }

        return true;
    }

    public static string? ProtocolForSourceName(string sourceName) => sourceName switch
    {
        "Virtual WebSDR" => "WebSDR",
        "Virtual KiwiSDR" => "KiwiSDR",
        "Virtual OpenWebRX" => "OpenWebRX",
        _ => null
    };

    public static string DisplayName(string sourceName) => sourceName switch
    {
        "Virtual WebSDR" => "WebSDR",
        "Virtual KiwiSDR" => "KiwiSDR",
        "Virtual OpenWebRX" => "OpenWeb",
        _ => sourceName
    };

    public static string? NormalizeProtocol(string protocol)
    {
        if (protocol.Equals("WebSDR", StringComparison.OrdinalIgnoreCase)) return "WebSDR";
        if (protocol.Equals("KiwiSDR", StringComparison.OrdinalIgnoreCase)) return "KiwiSDR";
        if (protocol.Equals("OpenWebRX", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("OpenWebRx", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("OpenRX", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("OpenWeb", StringComparison.OrdinalIgnoreCase))
            return "OpenWebRX";
        return null;
    }

    private static IReadOnlyList<RemoteSdrEntry> BuildMergedDirectory()
    {
        EnsureDirectoryFile();
        var csv = LoadCsvEntries(DirectoryPath);
        var kiwiOfficial = LoadKiwiEntries(ReadBestOfficial("kiwisdr_com.js", KiwiCachePath));
        var websdrOfficial = LoadWebSdrEntries(ReadBestOfficial("websdrlist_fmt2.json", WebSdrCachePath)
                                               ?? ReadBestOfficial("websdrlistk.tmp",
                                                   Path.Combine(CacheDirectory, "websdrlistk.tmp")));

        var merged = new List<RemoteSdrEntry>(capacity: csv.Count + kiwiOfficial.Count + websdrOfficial.Count);
        var order = 0;

        // WebSDR: website order (fmt=2) first, then CSV leftovers.
        var websdrKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in websdrOfficial)
        {
            if (!websdrKeys.Add(UrlKey(entry.Url))) continue;
            merged.Add(WithOrder(entry, order++));
        }

        // KiwiSDR: official public-list order only (CSV leftovers are often dead).
        if (kiwiOfficial.Count > 0)
        {
            var kiwiKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in kiwiOfficial)
            {
                if (!kiwiKeys.Add(UrlKey(entry.Url))) continue;
                merged.Add(WithOrder(entry, order++));
            }
        }
        else
        {
            foreach (var entry in csv.Where(e => e.Protocol == "KiwiSDR"))
                merged.Add(WithOrder(Enrich(entry), order++));
        }

        // OpenWebRX: CSV order, enriched with geocoded coordinates when possible.
        foreach (var entry in csv.Where(e => e.Protocol == "OpenWebRX"))
            merged.Add(WithOrder(Enrich(entry), order++));

        return merged;
    }

    private static RemoteSdrEntry WithOrder(RemoteSdrEntry entry, int order) =>
        new()
        {
            Name = entry.Name,
            Protocol = entry.Protocol,
            Url = entry.Url,
            Location = entry.Location,
            Country = entry.Country,
            City = entry.City,
            Grid = entry.Grid,
            Latitude = entry.Latitude,
            Longitude = entry.Longitude,
            AltitudeMeters = entry.AltitudeMeters,
            Bands = entry.Bands,
            BandSummary = entry.BandSummary,
            Spectrum = entry.Spectrum,
            SortScore = entry.SortScore,
            DirectoryOrder = order
        };

    private static List<RemoteSdrEntry> LoadCsvEntries(string path)
    {
        var rows = new List<RemoteSdrEntry>();
        if (!File.Exists(path)) return rows;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = SplitCsv(line);
            if (cols.Count < 5) continue;
            if (cols[0].Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
            var protocol = NormalizeProtocol(cols[1]);
            if (protocol is null) continue;
            var url = NormalizeUrl(cols[4].Trim());
            if (url.Length == 0) continue;
            var name = cols[0].Trim().Trim('"');
            var location = cols.Count > 5 ? cols[5].Trim() : "";
            double? lat = null, lon = null, alt = null;
            var country = "";
            var city = "";
            var grid = "";
            if (cols.Count > 6 && !string.IsNullOrWhiteSpace(cols[6])) country = cols[6].Trim();
            if (cols.Count > 7 && !string.IsNullOrWhiteSpace(cols[7])) city = cols[7].Trim();
            if (cols.Count > 8 && RemoteSdrGeo.TryParseDouble(cols[8], out var la)) lat = la;
            if (cols.Count > 9 && RemoteSdrGeo.TryParseDouble(cols[9], out var lo)) lon = lo;
            if (cols.Count > 10 && RemoteSdrGeo.TryParseDouble(cols[10], out var al)) alt = al;
            if (cols.Count > 11 && !string.IsNullOrWhiteSpace(cols[11])) grid = cols[11].Trim();

            rows.Add(Enrich(new RemoteSdrEntry
            {
                Name = name,
                Protocol = protocol,
                Url = url,
                Location = location,
                Country = country,
                City = city,
                Grid = grid,
                Latitude = lat,
                Longitude = lon,
                AltitudeMeters = alt
            }));
        }
        return rows;
    }

    private static List<RemoteSdrEntry> LoadKiwiEntries(string? js)
    {
        var rows = new List<RemoteSdrEntry>();
        if (string.IsNullOrWhiteSpace(js)) return rows;

        // Preserve website / mirror file order — do not re-sort by free slots or proxy.
        foreach (Match block in Regex.Matches(js, @"\{[^{}]+\}", RegexOptions.Singleline))
        {
            var body = block.Value;
            var offline = Field(body, "offline");
            if (offline.Equals("yes", StringComparison.OrdinalIgnoreCase)) continue;
            var url = NormalizeUrl(Field(body, "url"));
            if (url.Length == 0) continue;
            var name = Field(body, "name");
            if (name.Length == 0) name = url;
            var loc = Field(body, "loc");
            var grid = Field(body, "grid");
            double? lat = null, lon = null, alt = null;
            if (RemoteSdrGeo.TryParseGps(Field(body, "gps"), out var gLat, out var gLon))
            {
                lat = gLat;
                lon = gLon;
            }
            else if (RemoteSdrGeo.TryMaidenhead(grid, out gLat, out gLon))
            {
                lat = gLat;
                lon = gLon;
            }
            if (RemoteSdrGeo.TryParseDouble(Field(body, "asl"), out var asl))
                alt = asl;
            var bands = RemoteSdrBands.ParseKiwi(Field(body, "bands"), name);

            rows.Add(Enrich(new RemoteSdrEntry
            {
                Name = SanitizeDisplay(name),
                Protocol = "KiwiSDR",
                Url = url,
                Location = SanitizeDisplay(loc),
                Grid = grid,
                Latitude = lat,
                Longitude = lon,
                AltitudeMeters = alt,
                Bands = bands
            }));
        }
        return rows;
    }

    private static List<RemoteSdrEntry> LoadWebSdrEntries(string? js)
    {
        var rows = new List<RemoteSdrEntry>();
        if (string.IsNullOrWhiteSpace(js)) return rows;

        // websdr.org fmt=2: [ { "url": "...", "bands": [{...}] }, ... ] — preserve array order.
        // Legacy .tmp uses single-quoted keys.
        foreach (var body in EnumerateTopLevelObjects(js))
        {
            var url = NormalizeUrl(AnyQuotedField(body, "url"));
            if (url.Length == 0) continue;
            var desc = HtmlDecode(AnyQuotedField(body, "desc"));
            var qth = AnyQuotedField(body, "qth");
            var name = string.IsNullOrWhiteSpace(desc) ? url : SanitizeDisplay(desc);
            double? lat = null, lon = null;
            if (RemoteSdrGeo.TryParseDouble(AnyQuotedField(body, "lat"), out var la) &&
                RemoteSdrGeo.TryParseDouble(AnyQuotedField(body, "lon"), out var lo))
            {
                lat = la;
                lon = lo;
            }
            else if (RemoteSdrGeo.TryMaidenhead(qth, out la, out lo))
            {
                lat = la;
                lon = lo;
            }
            var bands = RemoteSdrBands.ParseWebSdrObject(body);
            if (bands.Count == 0) bands = RemoteSdrBands.ParseFromTitle(desc);

            rows.Add(Enrich(new RemoteSdrEntry
            {
                Name = name,
                Protocol = "WebSDR",
                Url = url,
                Location = string.IsNullOrWhiteSpace(qth) ? "" : qth.Trim(),
                Grid = string.IsNullOrWhiteSpace(qth) ? "" : qth.Trim(),
                Latitude = lat,
                Longitude = lon,
                Bands = bands
            }));
        }
        return rows;
    }

    private static RemoteSdrEntry Enrich(RemoteSdrEntry entry)
    {
        var location = entry.Location;
        var grid = entry.Grid;
        if (string.IsNullOrWhiteSpace(grid) && RemoteSdrGeo.LooksLikeMaidenhead(location))
            grid = location.Trim();

        RemoteSdrGeo.SplitLocation(location, out var country, out var city);
        if (!string.IsNullOrWhiteSpace(entry.Country)) country = entry.Country;
        if (!string.IsNullOrWhiteSpace(entry.City)) city = entry.City;
        if (RemoteSdrGeo.LooksLikeMaidenhead(city)) city = "";
        if (RemoteSdrGeo.LooksLikeMaidenhead(location) && string.IsNullOrWhiteSpace(entry.City))
            city = "";

        var lat = entry.Latitude;
        var lon = entry.Longitude;
        if (lat is null || lon is null)
        {
            if (RemoteSdrGeo.TryMaidenhead(grid, out var gLat, out var gLon))
            {
                lat = gLat;
                lon = gLon;
            }
            else if (RemoteSdrGeo.TryGeocode(country, city, $"{location} {entry.Name}", out gLat, out gLon))
            {
                lat = gLat;
                lon = gLon;
            }
        }

        if (string.IsNullOrWhiteSpace(country) || country.Equals("Europe", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("Asia", StringComparison.OrdinalIgnoreCase) ||
            country.Equals("South America", StringComparison.OrdinalIgnoreCase))
        {
            var inferred = RemoteSdrGeo.InferCountry(location, entry.Name, lat, lon);
            if (!string.IsNullOrWhiteSpace(inferred) &&
                !inferred.Equals("Europe", StringComparison.OrdinalIgnoreCase) &&
                !inferred.Equals("Asia", StringComparison.OrdinalIgnoreCase) &&
                !inferred.Equals("South America", StringComparison.OrdinalIgnoreCase))
                country = inferred;
            else if (string.IsNullOrWhiteSpace(country))
                country = inferred;
        }

        if (string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(location) &&
            !location.Equals(country, StringComparison.OrdinalIgnoreCase) &&
            !RemoteSdrGeo.LooksLikeMaidenhead(location))
        {
            city = location;
        }

        var bands = entry.Bands.Count > 0 ? entry.Bands : RemoteSdrBands.ParseFromTitle(entry.Name);
        return new RemoteSdrEntry
        {
            Name = entry.Name,
            Protocol = entry.Protocol,
            Url = entry.Url,
            Location = location,
            Country = country,
            City = city,
            Grid = grid,
            Latitude = lat,
            Longitude = lon,
            AltitudeMeters = entry.AltitudeMeters,
            Bands = bands,
            BandSummary = RemoteSdrBands.Summarize(bands),
            Spectrum = RemoteSdrBands.Coverage(bands),
            SortScore = entry.SortScore,
            DirectoryOrder = entry.DirectoryOrder
        };
    }

    private static IEnumerable<string> EnumerateTopLevelObjects(string text)
    {
        var startArray = text.IndexOf('[');
        if (startArray < 0) yield break;
        var depth = 0;
        var start = -1;
        var inString = false;
        var quote = '\0';
        var escape = false;
        for (var i = startArray; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == quote) inString = false;
                continue;
            }
            if (c is '\'' or '"')
            {
                inString = true;
                quote = c;
                continue;
            }
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                if (depth == 0) continue;
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return text[start..(i + 1)];
                    start = -1;
                }
            }
        }
    }

    private static string Field(string body, string key)
    {
        var m = Regex.Match(body, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
        return m.Success ? UnescapeJson(m.Groups[1].Value) : "";
    }

    private static string AnyQuotedField(string body, string key)
    {
        var doubleQuoted = Field(body, key);
        if (doubleQuoted.Length > 0) return doubleQuoted;
        // bare number: "lat": 52.2292
        var num = Regex.Match(body, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
        if (num.Success) return num.Groups[1].Value;
        var m = Regex.Match(body, $"'{Regex.Escape(key)}'\\s*:\\s*'((?:\\\\.|[^'\\\\])*)'");
        if (m.Success) return m.Groups[1].Value.Replace("\\'", "'", StringComparison.Ordinal);
        num = Regex.Match(body, $"'{Regex.Escape(key)}'\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");
        return num.Success ? num.Groups[1].Value : "";
    }

    private static string UnescapeJson(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal)
             .Replace("\\\\", "\\", StringComparison.Ordinal)
             .Replace("\\r", "", StringComparison.Ordinal)
             .Replace("\\n", " ", StringComparison.Ordinal);

    private static string HtmlDecode(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return WebUtility.HtmlDecode(value);
    }

    private static string SanitizeDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var cleaned = Regex.Replace(value, @"\s+", " ").Trim();
        return cleaned.Length <= 120 ? cleaned : cleaned[..117] + "…";
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "";
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.EndsWith('/') ? url : url + "/";
        var builder = new UriBuilder(uri) { Fragment = "", Query = "" };
        var text = builder.Uri.GetLeftPart(UriPartial.Path);
        if (!text.EndsWith('/')) text += "/";
        return text;
    }

    internal static string SiteKey(string url) => UrlKey(NormalizeUrl(url));

    private static string UrlKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.TrimEnd('/').ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return host + port + uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
    }

    private static string? ReadBestOfficial(string fileName, string cachePath)
    {
        foreach (var path in new[]
                 {
                     cachePath,
                     Path.Combine(AppContext.BaseDirectory, "directory", fileName),
                     Path.Combine(AppContext.BaseDirectory, fileName)
                 })
        {
            var text = TryRead(path);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static string? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch { return null; }
    }

    private static IReadOnlyList<RemoteSdrEntry>? TryLoadDatabase()
    {
        try
        {
            if (!File.Exists(DatabasePath)) return null;
            var json = File.ReadAllText(DatabasePath, Encoding.UTF8);
            var rows = JsonSerializer.Deserialize(json, RemoteSdrDirectoryJsonContext.Default.ListRemoteSdrEntryDto);
            if (rows is null || rows.Count == 0) return null;
            return rows.Select(dto => Enrich(new RemoteSdrEntry
            {
                Name = dto.Name,
                Protocol = dto.Protocol,
                Url = dto.Url,
                Location = dto.Location ?? "",
                Country = dto.Country ?? "",
                City = dto.City ?? "",
                Grid = dto.Grid ?? "",
                Latitude = dto.Latitude,
                Longitude = dto.Longitude,
                AltitudeMeters = dto.AltitudeMeters,
                DirectoryOrder = dto.DirectoryOrder,
                Bands = RestoreBands(dto)
            })).ToList();
        }
        catch
        {
            return null;
        }
    }

    private static void TrySaveDatabase(IReadOnlyList<RemoteSdrEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var dto = entries.Select(e => new RemoteSdrEntryDto
            {
                Name = e.Name,
                Protocol = e.Protocol,
                Url = e.Url,
                Location = e.Location,
                Country = e.Country,
                City = e.City,
                Grid = e.Grid,
                Latitude = e.Latitude,
                Longitude = e.Longitude,
                AltitudeMeters = e.AltitudeMeters,
                DirectoryOrder = e.DirectoryOrder,
                BandSummary = e.BandSummary,
                Spectrum = (int)e.Spectrum,
                BandLows = e.Bands.Select(b => b.LowMhz).ToList(),
                BandHighs = e.Bands.Select(b => b.HighMhz).ToList()
            }).ToList();
            var json = JsonSerializer.Serialize(dto, RemoteSdrDirectoryJsonContext.Default.ListRemoteSdrEntryDto);
            File.WriteAllText(DatabasePath, json, Encoding.UTF8);

            // Also refresh a CSV snapshot next to the DB for inspection / backup.
            var csvPath = Path.Combine(CacheDirectory, DirectoryFileName);
            WriteCsvSnapshot(csvPath, entries);
        }
        catch
        {
            // non-fatal
        }
    }

    private static void WriteCsvSnapshot(string path, IReadOnlyList<RemoteSdrEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Protocol,Host,Port,URL,Location,Country,City,Latitude,Longitude,AltitudeMeters,Grid");
        foreach (var e in entries)
        {
            Uri.TryCreate(e.Url, UriKind.Absolute, out var uri);
            var host = uri?.Host ?? "";
            var port = uri is null ? "" : uri.Port.ToString(CultureInfo.InvariantCulture);
            sb.Append(Csv(e.Name)).Append(',')
              .Append(Csv(e.Protocol)).Append(',')
              .Append(Csv(host)).Append(',')
              .Append(Csv(port)).Append(',')
              .Append(Csv(e.Url)).Append(',')
              .Append(Csv(e.Location)).Append(',')
              .Append(Csv(e.Country)).Append(',')
              .Append(Csv(e.City)).Append(',')
              .Append(Csv(e.Latitude?.ToString(CultureInfo.InvariantCulture) ?? "")).Append(',')
              .Append(Csv(e.Longitude?.ToString(CultureInfo.InvariantCulture) ?? "")).Append(',')
              .Append(Csv(e.AltitudeMeters?.ToString(CultureInfo.InvariantCulture) ?? "")).Append(',')
              .Append(Csv(e.Grid))
              .AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return value;
    }

    private static void EnsureDirectoryFile()
    {
        var dest = DirectoryPath;
        if (File.Exists(dest)) return;
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, DirectoryFileName),
                     Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", DirectoryFileName))
                 })
        {
            if (!File.Exists(candidate) || string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(candidate, dest, overwrite: false);
            return;
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NeuroSDR/1.0 (+local catalog refresh)");
        return http;
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else quoted = false;
                }
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static IReadOnlyList<RemoteSdrBandSpan> RestoreBands(RemoteSdrEntryDto dto)
    {
        if (dto.BandLows is { Count: > 0 } && dto.BandHighs is { Count: > 0 })
        {
            var n = Math.Min(dto.BandLows.Count, dto.BandHighs.Count);
            var rows = new List<RemoteSdrBandSpan>(n);
            for (var i = 0; i < n; i++)
                rows.Add(new RemoteSdrBandSpan(dto.BandLows[i], dto.BandHighs[i]));
            return rows;
        }
        return [];
    }
}

internal sealed class RemoteSdrEntryDto
{
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Location { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? Grid { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? AltitudeMeters { get; set; }
    public int DirectoryOrder { get; set; }
    public string? BandSummary { get; set; }
    public int Spectrum { get; set; }
    public List<double>? BandLows { get; set; }
    public List<double>? BandHighs { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<RemoteSdrEntryDto>))]
internal partial class RemoteSdrDirectoryJsonContext : JsonSerializerContext;
