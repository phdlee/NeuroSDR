using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace NeuroSDR.Plugins;

internal sealed record AfPluginRegistration(AfPluginInfo Info, Func<IAfPlugin> Create);

internal sealed class AfPluginCatalog
{
    public IReadOnlyList<AfPluginRegistration> Plugins { get; }
    public IReadOnlyList<string> Errors { get; }

    public AfPluginCatalog()
    {
        var plugins = new List<AfPluginRegistration>
        {
            Register<FtxDecoderAfPlugin>(),
            Register<CwDecoderAfPlugin>(),
            Register<SstvDecoderAfPlugin>(),
            Register<RttyDecoderAfPlugin>(),
            Register<WeatherFaxDecoderAfPlugin>(),
            Register<NeuroSDR.Plugins.Kiwi.KiwiNavtexAfPlugin>(),
            Register<NeuroSDR.Plugins.Kiwi.KiwiWwvAfPlugin>(),
            Register<NeuroSDR.Plugins.Fldigi.FlRttyAfPlugin>(),
            Register<NeuroSDR.Plugins.Fldigi.FlCwAfPlugin>(),
            Register<NeuroSDR.Plugins.Fldigi.FlFaxAfPlugin>(),
            // DSD-FME PCM / Digital Voice are built-in RadioModes (DMR/DSTAR/C4FM).
            // Keep DSD+ Bridge as an optional external helper plugin.
            Register<NeuroSDR.Plugins.DigitalVoice.DsdPlusBridgeAfPlugin>(),
            Register<AfRecordAfPlugin>(),
            Register<NeuroSDR.Plugins.OokAsk.OokAskAfPlugin>(),
            Register<NeuroSDR.Plugins.Caption.NeuroCaptionAfPlugin>(),
            Register<NeuroSDR.Plugins.Broadcast.EibiBroadcastAfPlugin>()
        };
        var errors = new List<string>();
        var loadedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LoadJsonManifests(AppContext.BaseDirectory, plugins, errors, loadedAssemblies);
        var afDir = Path.Combine(AppContext.BaseDirectory, "plugins", "af");
        if (Directory.Exists(afDir))
        {
            LoadJsonManifests(afDir, plugins, errors, loadedAssemblies);
            LoadExternalDlls(afDir, plugins, errors, loadedAssemblies);
        }
        Plugins = plugins.GroupBy(item => item.Info.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        Errors = errors;
    }

    private static AfPluginRegistration Register<T>() where T : IAfPlugin, new()
    {
        using var prototype = new T();
        return new AfPluginRegistration(prototype.Info with { IsBuiltIn = true }, static () => new T());
    }

    private static void LoadJsonManifests(string directory, List<AfPluginRegistration> plugins, List<string> errors,
        HashSet<string> loadedAssemblies)
    {
        foreach (var jsonPath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;
                if (!root.TryGetProperty("neurosdrPlugin", out var flag) || flag.ValueKind != JsonValueKind.True)
                    continue;
                var format = root.TryGetProperty("format", out var formatEl) ? formatEl.GetString() ?? "" : "";
                if (!format.Equals("AF.Visual", StringComparison.OrdinalIgnoreCase) &&
                    !format.Equals("AF", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{Path.GetFileName(jsonPath)}: unsupported format '{format}'");
                    continue;
                }
                var assemblyName = root.TryGetProperty("assembly", out var asmEl) ? asmEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    errors.Add($"{Path.GetFileName(jsonPath)}: missing assembly");
                    continue;
                }
                var dllPath = Path.GetFullPath(Path.Combine(directory, assemblyName));
                if (!File.Exists(dllPath))
                {
                    errors.Add($"{Path.GetFileName(jsonPath)}: DLL not found ({assemblyName})");
                    continue;
                }
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
                loadedAssemblies.Add(dllPath);
                var typeNames = new List<string>();
                if (root.TryGetProperty("plugins", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeEl) && typeEl.GetString() is { Length: > 0 } typeName)
                            typeNames.Add(typeName);
                    }
                }
                IEnumerable<Type> types = typeNames.Count > 0
                    ? typeNames.Select(name => assembly.GetType(name, throwOnError: false))
                        .OfType<Type>()
                    : assembly.GetTypes().Where(type => !type.IsAbstract && !type.IsInterface &&
                        typeof(IAfPlugin).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null);
                foreach (var type in types)
                {
                    if (type is null || type.GetConstructor(Type.EmptyTypes) is null) continue;
                    using var prototype = (IAfPlugin)Activator.CreateInstance(type)!;
                    var info = prototype.Info with { IsBuiltIn = false };
                    var captured = type;
                    plugins.Add(new AfPluginRegistration(info, () => (IAfPlugin)Activator.CreateInstance(captured)!));
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(jsonPath)}: {exception.GetBaseException().Message}");
            }
        }
    }

    private static void LoadExternalDlls(string directory, List<AfPluginRegistration> plugins, List<string> errors,
        HashSet<string> loadedAssemblies)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var full = Path.GetFullPath(path);
            if (!loadedAssemblies.Add(full)) continue;
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
                foreach (var type in assembly.GetTypes().Where(type => !type.IsAbstract && !type.IsInterface &&
                             typeof(IAfPlugin).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null))
                {
                    using var prototype = (IAfPlugin)Activator.CreateInstance(type)!;
                    var info = prototype.Info with { IsBuiltIn = false };
                    plugins.Add(new AfPluginRegistration(info, () => (IAfPlugin)Activator.CreateInstance(type)!));
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(path)}: {exception.GetBaseException().Message}");
            }
        }
    }
}
