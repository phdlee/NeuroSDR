namespace NeuroSDR.Plugins;

internal sealed class AfPluginUiHost(
    string instanceId,
    string pluginId,
    Func<bool> isRunning,
    Func<long> tunedFrequencyHz,
    Func<IReadOnlyDictionary<string, string>> load,
    Action<IReadOnlyDictionary<string, string>> save,
    Action<IReadOnlyDictionary<string, string>> configure,
    Func<int, string, int, string>? bindHostWaveOut = null,
    Func<bool, string>? setHostMainMuted = null) : IAfPluginUiHost
{
    public string InstanceId { get; } = instanceId;
    public string PluginId { get; } = pluginId;
    public bool IsRunning => isRunning();
    public long TunedFrequencyHz => tunedFrequencyHz();
    public IReadOnlyDictionary<string, string> LoadOptions() => load();
    public void SaveOptions(IReadOnlyDictionary<string, string> options) => save(options);
    public void Configure(IReadOnlyDictionary<string, string> options) => configure(options);
    public string BindHostWaveOut(int outputChannel1Based, string deviceNameContains, int volumePercent) =>
        bindHostWaveOut?.Invoke(outputChannel1Based, deviceNameContains, volumePercent)
        ?? "host bind unavailable";
    public string SetHostMainMuted(bool muted) =>
        setHostMainMuted?.Invoke(muted) ?? "host mute unavailable";
}
