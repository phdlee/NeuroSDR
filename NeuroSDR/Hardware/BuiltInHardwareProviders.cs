using NeuroSDR.Core;

namespace NeuroSDR.Hardware;

internal sealed class SdrplaySourceProvider : ISampleSourceProvider
{
    public string Name => "SDRplay";
    public IEnumerable<SampleSourceDiscoveryResult> Discover()
    {
        _ = SdrplaySampleSource.TryCreate(out var source, out var status);
        yield return new SampleSourceDiscoveryResult(source, status);
    }
}

internal sealed class RtlSdrSourceProvider : ISampleSourceProvider
{
    public string Name => "RTL-SDR";
    public IEnumerable<SampleSourceDiscoveryResult> Discover()
    {
        _ = RtlSdrSampleSource.TryCreate(out var source, out var status);
        yield return new SampleSourceDiscoveryResult(source, status);
    }
}

internal sealed class HackRfSourceProvider : ISampleSourceProvider
{
    public string Name => "HackRF";
    public IEnumerable<SampleSourceDiscoveryResult> Discover()
    {
        _ = HackRfSampleSource.TryCreate(out var source, out var status);
        yield return new SampleSourceDiscoveryResult(source, status);
    }
}

internal sealed class AirspySourceProvider : ISampleSourceProvider
{
    public string Name => "Airspy";
    public IEnumerable<SampleSourceDiscoveryResult> Discover()
    {
        _ = AirspySampleSource.TryCreate(out var source, out var status);
        yield return new SampleSourceDiscoveryResult(source, status);
    }
}

internal sealed class AirspyHfSourceProvider : ISampleSourceProvider
{
    public string Name => "Airspy HF+";
    public IEnumerable<SampleSourceDiscoveryResult> Discover()
    {
        _ = AirspyHfSampleSource.TryCreate(out var source, out var status);
        yield return new SampleSourceDiscoveryResult(source, status);
    }
}

internal sealed class SoapySdrSourceProvider : ISampleSourceProvider
{
    public string Name => "SoapySDR";
    public IEnumerable<SampleSourceDiscoveryResult> Discover() => SoapySdrSampleSource.DiscoverAll();
}

internal sealed class RtlTcpSourceProvider : ISampleSourceProvider
{
    public string Name => "rtl_tcp";
    public IEnumerable<SampleSourceDiscoveryResult> Discover()
    {
        _ = RtlTcpSampleSource.TryCreate(out var source, out var status);
        yield return new SampleSourceDiscoveryResult(source, status);
    }
}

internal sealed class SyntheticSourceProvider : ISampleSourceProvider
{
    public string Name => "Synthetic";
    public IEnumerable<SampleSourceDiscoveryResult> Discover()
    {
        var source = new SyntheticSampleSource();
        yield return new SampleSourceDiscoveryResult(source, $"{source.Name} ready");
    }
}
