using System.Reflection;
using System.Runtime.InteropServices;

namespace NeuroSDR;

/// <summary>
/// Single assembly-wide DllImport resolver. Only one resolver may be registered per assembly.
/// </summary>
internal static class HostNativeDllResolver
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(HostNativeDllResolver).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        foreach (var candidate in Candidates(libraryName))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates(string libraryName)
    {
        var root = AppContext.BaseDirectory;
        foreach (var name in Aliases(libraryName))
        {
            yield return Path.Combine(root, name);
            yield return Path.Combine(root, "native", name);
            yield return Path.Combine(root, "native", "win-x64", name);
            yield return Path.Combine(root, "native", "win-x86", name);
        }
    }

    private static IEnumerable<string> Aliases(string libraryName)
    {
        var baseName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? libraryName
            : libraryName + ".dll";
        yield return baseName;
        if (baseName.Equals("ens_lte.dll", StringComparison.OrdinalIgnoreCase))
            yield return "libens_lte.dll";
        else if (baseName.Equals("libens_lte.dll", StringComparison.OrdinalIgnoreCase))
            yield return "ens_lte.dll";
        else if (baseName.Equals("codec2.dll", StringComparison.OrdinalIgnoreCase))
            yield return "libcodec2.dll";
        else if (baseName.Equals("libcodec2.dll", StringComparison.OrdinalIgnoreCase))
            yield return "codec2.dll";
    }
}
