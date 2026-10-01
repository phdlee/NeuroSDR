using NeuroSDR.Core;
using System.Reflection;
using System.Runtime.Loader;

namespace NeuroSDR.Hardware;

internal static class HardwareSourceCatalog
{
    public static IReadOnlyList<SampleSourceDiscoveryResult> Discover()
    {
        var results = new List<SampleSourceDiscoveryResult>();
        var providers = new List<ISampleSourceProvider>
        {
            new SdrplaySourceProvider(),
            new RtlSdrSourceProvider(),
            new HackRfSourceProvider(),
            new AirspySourceProvider(),
            new AirspyHfSourceProvider(),
            new SoapySdrSourceProvider(),
            new RtlTcpSourceProvider(),
            new WebSdrSourceProvider(WebRadioKind.WebSdr),
            new WebSdrSourceProvider(WebRadioKind.KiwiSdr),
            new WebSdrSourceProvider(WebRadioKind.OpenWebRx),
            new SyntheticSourceProvider()
        };
        providers.AddRange(LoadExternalProviders(results));

        foreach (var provider in providers)
        {
            try
            {
                var discovered = provider.Discover().ToArray();
                if (discovered.Length == 0)
                    results.Add(new SampleSourceDiscoveryResult(null, $"{provider.Name}: No source available"));
                else
                    results.AddRange(discovered);
            }
            catch (Exception exception)
            {
                results.Add(new SampleSourceDiscoveryResult(null, $"{provider.Name} plugin error: {exception.GetBaseException().Message}"));
            }
        }
        return results;
    }

    private static IEnumerable<ISampleSourceProvider> LoadExternalProviders(List<SampleSourceDiscoveryResult> results)
    {
        var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins", "hardware");
        if (!Directory.Exists(pluginDirectory)) yield break;

        foreach (var path in Directory.EnumerateFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            Type[] types;
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
                types = assembly.GetTypes();
            }
            catch (Exception exception)
            {
                results.Add(new SampleSourceDiscoveryResult(null, $"{Path.GetFileName(path)} load failed: {exception.GetBaseException().Message}"));
                continue;
            }

            foreach (var type in types.Where(type => !type.IsAbstract && !type.IsInterface && typeof(ISampleSourceProvider).IsAssignableFrom(type)))
            {
                ISampleSourceProvider? provider = null;
                try { provider = Activator.CreateInstance(type) as ISampleSourceProvider; }
                catch (Exception exception)
                {
                    results.Add(new SampleSourceDiscoveryResult(null, $"{type.FullName} creation failed: {exception.GetBaseException().Message}"));
                }
                if (provider is not null) yield return provider;
            }
        }
    }

}
