namespace NeuroSDR.Core;

public interface ISampleSource : IDisposable
{
    string Name { get; }
    long CenterFrequency { get; set; }
    int SampleRate { get; }
    bool IsRunning { get; }
    event Action<Complex32[]>? SamplesAvailable;
    void Start();
    void Stop();
}

public interface IGainControlledSampleSource
{
    int GainPercent { get; set; }
}

public interface IHardwareAgcSampleSource
{
    bool HardwareAgcEnabled { get; set; }
}

/// <summary>Front-end overload counts. Digital-mode AGC uses this and does not change gain by itself.</summary>
public interface IRfOverloadSource
{
    long RfOverloadEvents { get; }
}

public interface ISampleRateGainCompensationSource
{
    bool SampleRateGainCompensationEnabled { get; set; }
}

public interface IRfAmplifierSampleSource
{
    bool RfAmplifierEnabled { get; set; }
}

/// <summary>A hardware source whose capture sample rate can be selected while stopped.</summary>
public interface IConfigurableSampleRateSource
{
    IReadOnlyList<int> SupportedSampleRates { get; }
    int ConfiguredSampleRate { get; set; }
}

public interface IFixedCenterFrequencySampleSource
{
}

public interface ISampleSourceMetrics
{
    long TotalSamples { get; }
    long DeliveredSamples { get; }
    long DroppedSamples { get; }
    long LastDeliveryAgeMilliseconds { get; }
}

public interface ISampleQueueMetrics
{
    int QueuedBlocks { get; }
    int MaximumQueuedBlocks { get; }
}

public sealed record RemoteSpectrumFrame(byte[] Intensities, long CenterFrequency, int SpanHz);
public readonly record struct RemoteSpectrumViewport(long CenterFrequency, int SpanHz, bool ServerApplied);

/// <summary>A network receiver that supplies already-demodulated mono AF and server spectrum rows.</summary>
public interface IRemoteAudioSampleSource : ISampleSource
{
    string ServerUrl { get; set; }
    int AudioSampleRate { get; }
    bool IsConnected { get; }
    string ConnectionStatus { get; }
    bool SupportsServerSpectrumViewport { get; }
    int MaximumSpectrumSpan { get; }
    event Action<float[], int>? AudioSamplesAvailable;
    event Action<RemoteSpectrumFrame>? RemoteSpectrumAvailable;
    event Action<string>? ConnectionStatusChanged;
    Task ConnectAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Tear down remote sockets without blocking the UI thread. Prefer this over
    /// <see cref="ISampleSource.Stop"/> from WinForms code — sync Stop can deadlock
    /// when an AF callback is mid <c>Control.Invoke</c>.
    /// </summary>
    Task StopConnectionAsync(CancellationToken cancellationToken = default);
    Task ApplyReceiverAsync(RadioMode mode, int bandwidthHz, CancellationToken cancellationToken = default);
    Task<RemoteSpectrumViewport> SetSpectrumViewportAsync(long centerFrequency, int requestedSpanHz,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovers and creates sample sources. Providers run only during discovery;
/// the real-time IQ path remains a direct ISampleSource event.
/// </summary>
public interface ISampleSourceProvider
{
    string Name { get; }
    IEnumerable<SampleSourceDiscoveryResult> Discover();
}

public sealed record SampleSourceDiscoveryResult(ISampleSource? Source, string Status);
