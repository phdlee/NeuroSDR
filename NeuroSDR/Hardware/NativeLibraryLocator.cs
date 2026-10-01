using System.Runtime.InteropServices;

namespace NeuroSDR.Hardware;

internal static class NativeLibraryLocator
{
    public static bool TryLoad(string[] names, out IntPtr library, out string location, string? explicitPath = null)
        => TryLoad(names, out library, out location, out _, explicitPath);

    public static bool TryLoad(string[] names, out IntPtr library, out string location, out string failure,
        string? explicitPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        var architectureDirectory = Environment.Is64BitProcess ? "win-x64" : "win-x86";
        foreach (var name in names)
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "native", architectureDirectory, name));
        foreach (var name in names)
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "native", name));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, name));
            candidates.Add(name);
        }

        var errors = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                library = NativeLibrary.Load(candidate);
                location = candidate;
                failure = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or FileLoadException)
            {
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                    errors.Add($"{candidate}: {exception.GetBaseException().Message}");
            }
        }
        library = IntPtr.Zero;
        location = string.Empty;
        failure = errors.Count == 0
            ? $"{architectureDirectory} library was not found"
            : string.Join(" | ", errors);
        return false;
    }
}
