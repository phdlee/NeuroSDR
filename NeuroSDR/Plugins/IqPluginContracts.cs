using NeuroSDR.Core;

namespace NeuroSDR.Plugins;

[Flags]
public enum IqPluginCapabilities
{
    None = 0,
    /// <summary>Receives complex IQ sample blocks from the capture path (before demod).</summary>
    IqInput = 1,
    /// <summary>May return <see cref="IqPluginResult.OutputSamples"/> to replace the IQ stream for later plugins and demod.</summary>
    IqOutput = 2,
    Display = 4,
    HostResults = 8
}

public sealed record IqPluginInfo(
    string Id,
    string Name,
    string Description,
    IqPluginCapabilities Capabilities,
    bool IsBuiltIn = false);

/// <summary>
/// One contiguous IQ block from the active sample source.
/// <see cref="Samples"/> must not be retained after <see cref="IIqPlugin.Process"/> returns.
/// </summary>
public sealed record IqSampleBlock(
    ReadOnlyMemory<Complex32> Samples,
    int SampleRate,
    long CenterFrequencyHz,
    DateTime TimestampUtc);

public sealed record IqPluginResult(
    string PluginId,
    string Kind,
    string Text,
    DateTime TimestampUtc,
    Complex32[]? OutputSamples = null,
    IReadOnlyDictionary<string, string>? Fields = null,
    byte[]? BinaryData = null);

/// <summary>
/// IQ plug-in contract. Plugins with <see cref="IqPluginCapabilities.IqOutput"/> run on the capture
/// thread and must return quickly. Input-only analyzers are isolated on bounded background queues.
/// </summary>
public interface IIqPlugin : IDisposable
{
    IqPluginInfo Info { get; }
    event Action<IqPluginResult>? ResultAvailable;
    void Configure(IReadOnlyDictionary<string, string> options);
    IqPluginResult? Process(IqSampleBlock block);
}

/// <summary>
/// Optional state for a plugin that only sometimes replaces the real-time IQ
/// stream. When false, the host can isolate it on a background worker.
/// </summary>
public interface IConditionalIqOutputPlugin
{
    bool ProducesIqOutput { get; }
}
