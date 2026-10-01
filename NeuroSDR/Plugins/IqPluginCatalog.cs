using System.Reflection;
using System.Runtime.Loader;

namespace NeuroSDR.Plugins;

internal sealed record IqPluginRegistration(IqPluginInfo Info, Func<IIqPlugin> Create);

internal sealed class IqPluginCatalog
{
    public IReadOnlyList<IqPluginRegistration> Plugins { get; }
    public IReadOnlyList<string> Errors { get; }

    public IqPluginCatalog()
    {
        var plugins = new List<IqPluginRegistration>
        {
            Register<NeuroSDR.Plugins.Kiwi.KiwiTimecodeIqPlugin>(),
            Register<NeuroSDR.Plugins.Dump1090.AdsbIqPlugin>(),
            Register<NeuroSDR.Plugins.Lte.LteIqPlugin>()
        };
        var errors = new List<string>();
        LoadExternal(plugins, errors);
        Plugins = plugins.GroupBy(item => item.Info.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        Errors = errors;
    }

    private static IqPluginRegistration Register<T>() where T : IIqPlugin, new()
    {
        using var prototype = new T();
        return new IqPluginRegistration(prototype.Info with { IsBuiltIn = true }, static () => new T());
    }

    private static void LoadExternal(List<IqPluginRegistration> plugins, List<string> errors)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugins", "iq");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
                foreach (var type in assembly.GetTypes().Where(type => !type.IsAbstract && !type.IsInterface &&
                             typeof(IIqPlugin).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null))
                {
                    using var prototype = (IIqPlugin)Activator.CreateInstance(type)!;
                    var info = prototype.Info with { IsBuiltIn = false };
                    plugins.Add(new IqPluginRegistration(info, () => (IIqPlugin)Activator.CreateInstance(type)!));
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(path)}: {exception.GetBaseException().Message}");
            }
        }
    }
}
