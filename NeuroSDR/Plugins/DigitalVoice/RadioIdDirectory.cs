using System.Globalization;

namespace NeuroSDR.Plugins.DigitalVoice;

internal sealed record RadioIdUser(int RadioId, string Callsign, string Name, string City, string Country);

/// <summary>
/// RadioID.net <c>user.csv</c> lookup. Load from disk only — never from the audio thread via HTTP.
/// </summary>
internal sealed class RadioIdDirectory
{
    private readonly Dictionary<int, RadioIdUser> _users = [];
    public string LoadedPath { get; private set; } = "";
    public int Count => _users.Count;

    public static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "radioid-user.csv");
        yield return Path.Combine(AppContext.BaseDirectory, "user.csv");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeuroSDR");
        yield return Path.Combine(local, "radioid-user.csv");
        yield return Path.Combine(local, "user.csv");
    }

    public void Reload()
    {
        _users.Clear();
        LoadedPath = "";
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path)) continue;
            LoadFile(path);
            if (_users.Count > 0)
            {
                LoadedPath = path;
                return;
            }
        }
    }

    public RadioIdUser? Lookup(int radioId) =>
        radioId > 0 && _users.TryGetValue(radioId, out var user) ? user : null;

    private void LoadFile(string path)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header is null) return;
        var map = MapHeader(header);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var cols = SplitCsv(line);
            if (cols.Count == 0) continue;
            var idText = Cell(cols, map, "RADIO_ID", 0);
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                continue;
            var call = Cell(cols, map, "CALLSIGN", 1);
            var first = Cell(cols, map, "FIRST_NAME", 2);
            var last = Cell(cols, map, "LAST_NAME", 3);
            var city = Cell(cols, map, "CITY", 4);
            var country = Cell(cols, map, "COUNTRY", 6);
            var name = string.Join(' ', new[] { first, last }.Where(part => part.Length > 0));
            _users[id] = new RadioIdUser(id, call, name, city, country);
        }
    }

    private static Dictionary<string, int> MapHeader(string header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cols = SplitCsv(header);
        for (var i = 0; i < cols.Count; i++)
            if (cols[i].Length > 0) map[cols[i]] = i;
        return map;
    }

    private static string Cell(List<string> cols, Dictionary<string, int> map, string name, int fallback)
    {
        if (map.TryGetValue(name, out var index) && index >= 0 && index < cols.Count) return cols[index].Trim();
        return fallback < cols.Count ? cols[fallback].Trim() : "";
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        result.Add(current.ToString());
        return result;
    }
}
