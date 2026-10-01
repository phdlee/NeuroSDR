namespace NeuroSDR.Plugins;

[Flags]
public enum AfPluginCapabilities
{
    None = 0,
    AudioInput = 1,
    AudioOutput = 2,
    Display = 4,
    HostResults = 8
}

public sealed record AfPluginInfo(
    string Id,
    string Name,
    string Description,
    AfPluginCapabilities Capabilities,
    bool IsBuiltIn = false);

public sealed record AfAudioBlock(ReadOnlyMemory<float> Input, int SampleRate, DateTime TimestampUtc);

public sealed record AfPluginResult(
    string PluginId,
    string Kind,
    string Text,
    DateTime TimestampUtc,
    float[]? OutputAudio = null,
    IReadOnlyDictionary<string, string>? Fields = null,
    byte[]? BinaryData = null);

/// <summary>
/// AF plug-in contract. AudioOutput plug-ins execute in the audio path and must return quickly;
/// input-only analyzers are isolated by the host on bounded background queues.
/// </summary>
public interface IAfPlugin : IDisposable
{
    AfPluginInfo Info { get; }
    event Action<AfPluginResult>? ResultAvailable;
    void Configure(IReadOnlyDictionary<string, string> options);
    AfPluginResult? Process(AfAudioBlock block);
}

/// <summary>
/// Non-display AF plug-in hosted in the Extends Area (left of spectrum/waterfall).
/// </summary>
public interface IAfSidebarPlugin
{
    Control SidebarView { get; }
}

/// <summary>
/// AF decoder that supplies its own WinForms view (AF.Visual). Host docks the control in a tab.
/// </summary>
public interface IAfVisualPlugin
{
    Control CreateView(IAfPluginUiHost ui);
}

/// <summary>Host services for an AF.Visual plug-in view (settings bag + Configure).</summary>
public interface IAfPluginUiHost
{
    string InstanceId { get; }
    string PluginId { get; }
    bool IsRunning { get; }
    long TunedFrequencyHz { get; }
    IReadOnlyDictionary<string, string> LoadOptions();
    void SaveOptions(IReadOnlyDictionary<string, string> options);
    void Configure(IReadOnlyDictionary<string, string> options);
    /// <summary>
    /// Route a host OUT channel through the normal WaveOut path (same as Plugin Setup).
    /// Used by DSD+ Bridge so AF reaches VB-Cable exactly like manual OUT2→CABLE.
    /// </summary>
    string BindHostWaveOut(int outputChannel1Based, string deviceNameContains, int volumePercent);
    /// <summary>Mute or restore MAIN (OUT1) while DSD+ owns the speakers.</summary>
    string SetHostMainMuted(bool muted);
}

/// <summary>Optional: receive decoder results on the UI thread.</summary>
public interface IAfResultView
{
    void ApplyResult(AfPluginResult result);
}

/// <summary>Optional: AF spectrum FSK mark/space markers.</summary>
public interface IAfFskTuningView
{
    bool TryGetFskMarkers(out int centerHz, out int deviationHz);
    void SetFskTuning(int centerHz, int deviationHz);
}

/// <summary>Optional: receive RF power spectrum snapshots (dBFS bins) for adaptive gating.</summary>
public interface IAfRfSpectrumAware
{
    void OnRfSpectrum(float[] spectrumDb, long centerHz, int sampleRateHz, long tunedHz, int channelBandwidthHz);
}

/// <summary>Optional: show frequency presets from neurosdrpreset.json for this group name.</summary>
public interface IAfFrequencyPresetPlugin
{
    string PresetGroup { get; }
}
