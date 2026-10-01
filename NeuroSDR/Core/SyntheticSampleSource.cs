namespace NeuroSDR.Core;

public sealed class SyntheticSampleSource : ISampleSource
{
    private const int BlockSize = 4096;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private long _centerFrequency = 100_000_000;

    public string Name => "Synthetic IQ Signal (Development)";
    public int SampleRate => 2_048_000;
    public bool IsRunning => _worker is { IsCompleted: false };
    public event Action<Complex32[]>? SamplesAvailable;

    public long CenterFrequency
    {
        get => Interlocked.Read(ref _centerFrequency);
        set => Interlocked.Exchange(ref _centerFrequency, Math.Clamp(value, 100_000, RadioLimits.MaximumFrequency));
    }

    public void Start()
    {
        if (IsRunning) return;
        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => GenerateAsync(_cancellation.Token));
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        try { _worker?.Wait(500); } catch (AggregateException) { }
        _worker = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task GenerateAsync(CancellationToken cancellationToken)
    {
        var random = new Random(20260811);
        double phase1 = 0, phase2 = 0, phase3 = 0, fmPhase = 0;
        double t = 0;
        var blockDuration = TimeSpan.FromSeconds((double)BlockSize / SampleRate);

        while (!cancellationToken.IsCancellationRequested)
        {
            var samples = new Complex32[BlockSize];
            for (var n = 0; n < samples.Length; n++, t += 1d / SampleRate)
            {
                // Three signals with different modulation, used to check tuning and the filter UI.
                var am = 0.28 * (1 + 0.55 * Math.Sin(2 * Math.PI * 1_000 * t));
                phase1 += 2 * Math.PI * -310_000 / SampleRate;
                fmPhase += 2 * Math.PI * (115_000 + 18_000 * Math.Sin(2 * Math.PI * 700 * t)) / SampleRate;
                phase2 += 2 * Math.PI * 365_000 / SampleRate;
                phase3 += 2 * Math.PI * 18_000 / SampleRate;

                var i = am * Math.Cos(phase1) + 0.48 * Math.Cos(fmPhase) + 0.18 * Math.Cos(phase2) + 0.12 * Math.Cos(phase3);
                var q = am * Math.Sin(phase1) + 0.48 * Math.Sin(fmPhase) + 0.18 * Math.Sin(phase2) + 0.12 * Math.Sin(phase3);
                i += (random.NextDouble() - 0.5) * 0.055;
                q += (random.NextDouble() - 0.5) * 0.055;
                samples[n] = new Complex32((float)i, (float)q);
            }

            SamplesAvailable?.Invoke(samples);
            try { await Task.Delay(blockDuration, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose() => Stop();
}
