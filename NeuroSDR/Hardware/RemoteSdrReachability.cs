using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeuroSDR.Hardware;

internal sealed class RemoteSdrSiteCheck
{
    public string Url { get; set; } = "";
    public bool Reachable { get; set; }
    public string Detail { get; set; } = "";
    public DateTimeOffset CheckedUtc { get; set; }
    /// <summary>0 = no audio. Higher means audio arrived sooner and with fewer gaps.</summary>
    public int ReceiveScore { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<RemoteSdrSiteCheck>))]
internal partial class RemoteSdrReachabilityJsonContext : JsonSerializerContext;

/// <summary>
/// Sites that were checked and could not be received. Unknown sites stay visible
/// until a check marks them. The file next to the executable is what a published build ships.
/// </summary>
internal static class RemoteSdrReachability
{
    private const string FileName = "site-reachability.json";
    private static readonly object Gate = new();
    private static Dictionary<string, RemoteSdrSiteCheck>? _byKey;

    /// <summary>True only after a check received audio from this site.</summary>
    public static bool IsReachable(string url) => Find(url) is { Reachable: true };

    public static RemoteSdrSiteCheck? Find(string url)
    {
        EnsureLoaded();
        lock (Gate)
            return _byKey!.TryGetValue(RemoteSdrCatalog.SiteKey(url), out var check) ? check : null;
    }

    public static void Save(string url, bool reachable, string detail, DateTimeOffset checkedAt, int receiveScore = 0)
    {
        EnsureLoaded();
        var check = new RemoteSdrSiteCheck
        {
            Url = url,
            Reachable = reachable,
            Detail = detail,
            CheckedUtc = checkedAt,
            ReceiveScore = receiveScore
        };
        List<RemoteSdrSiteCheck> snapshot;
        lock (Gate)
        {
            _byKey![RemoteSdrCatalog.SiteKey(url)] = check;
            snapshot = _byKey.Values.ToList();
        }
        var json = JsonSerializer.Serialize(snapshot, RemoteSdrReachabilityJsonContext.Default.ListRemoteSdrSiteCheck);
        foreach (var path in WritePaths())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, json);
            }
            catch
            {
                // One path can fail when the folder is not writable.
            }
        }
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_byKey is not null) return;
            _byKey = new Dictionary<string, RemoteSdrSiteCheck>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in ReadPaths())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var rows = JsonSerializer.Deserialize(
                        File.ReadAllText(path),
                        RemoteSdrReachabilityJsonContext.Default.ListRemoteSdrSiteCheck);
                    if (rows is null) continue;
                    foreach (var row in rows)
                    {
                        if (string.IsNullOrWhiteSpace(row.Url)) continue;
                        _byKey[RemoteSdrCatalog.SiteKey(row.Url)] = row;
                    }
                }
                catch
                {
                    // Ignore a damaged file and keep the other copies.
                }
            }
        }
    }

    private static IEnumerable<string> ReadPaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
        yield return AppDataPath();
        var project = ProjectDirectoryFile();
        if (project is not null) yield return project;
    }

    private static IEnumerable<string> WritePaths()
    {
        yield return AppDataPath();
        yield return Path.Combine(AppContext.BaseDirectory, FileName);
        var project = ProjectDirectoryFile();
        if (project is not null) yield return project;
    }

    private static string AppDataPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NeuroSDR", "directory", FileName);

    private static string? ProjectDirectoryFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "NeuroSDR.csproj"))) continue;
            return Path.Combine(dir.FullName, "directory", FileName);
        }
        return null;
    }
}
