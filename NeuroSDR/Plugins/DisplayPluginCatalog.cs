using System.Reflection;
using System.Runtime.Loader;

namespace NeuroSDR.Plugins;

internal sealed record SpectrumPluginRegistration(DisplayPluginInfo Info, Func<ISpectrumRendererPlugin> Create);
internal sealed record WaterfallPluginRegistration(DisplayPluginInfo Info, Func<IWaterfallRendererPlugin> Create);

internal sealed class DisplayPluginCatalog
{
    public IReadOnlyList<SpectrumPluginRegistration> SpectrumPlugins { get; }
    public IReadOnlyList<WaterfallPluginRegistration> WaterfallPlugins { get; }
    public IReadOnlyList<string> Errors { get; }

    public DisplayPluginCatalog()
    {
        var spectra = new List<SpectrumPluginRegistration>
        {
            RegisterSpectrum<NeonLineSpectrumPlugin>(true),
            RegisterSpectrum<FilledSpectrumPlugin>(true)
        };
        var waterfalls = new List<WaterfallPluginRegistration>
        {
            RegisterWaterfall<NightWaterfallPlugin>(true),
            RegisterWaterfall<MonochromeWaterfallPlugin>(true)
        };
        var errors = new List<string>();
        LoadExternal(spectra, waterfalls, errors);
        SpectrumPlugins = spectra.GroupBy(item => item.Info.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        WaterfallPlugins = waterfalls.GroupBy(item => item.Info.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        Errors = errors;
    }

    private static SpectrumPluginRegistration RegisterSpectrum<T>(bool builtIn) where T : ISpectrumRendererPlugin, new()
    {
        var prototype = new T();
        return new SpectrumPluginRegistration(new DisplayPluginInfo(prototype.Id, prototype.Name, prototype.Description, builtIn), static () => new T());
    }

    private static WaterfallPluginRegistration RegisterWaterfall<T>(bool builtIn) where T : IWaterfallRendererPlugin, new()
    {
        using var prototype = new T();
        return new WaterfallPluginRegistration(new DisplayPluginInfo(prototype.Id, prototype.Name, prototype.Description, builtIn), static () => new T());
    }

    private static void LoadExternal(List<SpectrumPluginRegistration> spectra, List<WaterfallPluginRegistration> waterfalls, List<string> errors)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugins", "display");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
                foreach (var type in assembly.GetTypes().Where(type => !type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) is not null))
                {
                    if (typeof(ISpectrumRendererPlugin).IsAssignableFrom(type))
                    {
                        var prototype = (ISpectrumRendererPlugin)Activator.CreateInstance(type)!;
                        spectra.Add(new SpectrumPluginRegistration(
                            new DisplayPluginInfo(prototype.Id, prototype.Name, prototype.Description, false),
                            () => (ISpectrumRendererPlugin)Activator.CreateInstance(type)!));
                    }
                    if (typeof(IWaterfallRendererPlugin).IsAssignableFrom(type))
                    {
                        using var prototype = (IWaterfallRendererPlugin)Activator.CreateInstance(type)!;
                        waterfalls.Add(new WaterfallPluginRegistration(
                            new DisplayPluginInfo(prototype.Id, prototype.Name, prototype.Description, false),
                            () => (IWaterfallRendererPlugin)Activator.CreateInstance(type)!));
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{Path.GetFileName(path)}: {exception.GetBaseException().Message}");
            }
        }
    }
}
