using NeuroSDR.Core;
using System.Numerics;
using System.Threading.Channels;
using System.Buffers;

namespace NeuroSDR.Dsp;

internal sealed class SpectrumPipeline : IDisposable
{
    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(2)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Task _worker;
    private int _generation;

    public SpectrumPipeline()
    {
        _worker = Task.Run(ProcessLoop);
    }

    public event Action<float[]>? SpectrumAvailable;

    public void Submit(Complex32[] samples, int sampleRate, int viewBandwidth, int quality = 1)
    {
        // Hardware dispatch buffers are recycled immediately after the callback.
        // Display submission is throttled, so copying here is far cheaper than
        // allocating every full-rate RF block on the large object heap.
        var snapshot = ArrayPool<Complex32>.Shared.Rent(samples.Length);
        samples.AsSpan().CopyTo(snapshot);
        if (!_queue.Writer.TryWrite(new WorkItem(snapshot, samples.Length, sampleRate, Math.Max(1, viewBandwidth),
                Math.Clamp(quality, 0, 2), Volatile.Read(ref _generation))))
            ArrayPool<Complex32>.Shared.Return(snapshot);
    }

    public void Reset() => Interlocked.Increment(ref _generation);

    internal static int SelectFftSize(int sampleRate, int viewBandwidth, int quality = 1)
    {
        var targetBins = quality switch { 0 => 512, 2 => 2048, _ => 1024 };
        var requested = (long)Math.Ceiling(sampleRate * (double)targetBins / Math.Max(1, viewBandwidth));
        var size = 1_024;
        while (size < requested && size < 131_072) size <<= 1;
        return Math.Clamp(size, 1_024, 131_072);
    }

    private async Task ProcessLoop()
    {
        SpectrumProcessor? processor = null;
        var processorSize = 0;
        var generation = -1;
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (work.Generation != Volatile.Read(ref _generation)) continue;
                var desiredSize = SelectFftSize(work.SampleRate, work.ViewBandwidth, work.Quality);
                if (processor is null || processorSize != desiredSize || generation != work.Generation)
                {
                    var skippedFrames = desiredSize <= 16_384 ? 3 : 0;
                    processor = new SpectrumProcessor(desiredSize, skippedFrames);
                    processorSize = desiredSize;
                    generation = work.Generation;
                }
                var spectrum = processor.Process(work.Samples.AsSpan(0, work.Count));
                if (spectrum is not null && work.Generation == Volatile.Read(ref _generation))
                    try { SpectrumAvailable?.Invoke(spectrum); } catch { }
            }
            finally { ArrayPool<Complex32>.Shared.Return(work.Samples); }
        }
    }

    public void Dispose()
    {
        Reset();
        _queue.Writer.TryComplete();
        try { _worker.Wait(2_000); } catch (AggregateException) { }
        while (_queue.Reader.TryRead(out var work)) ArrayPool<Complex32>.Shared.Return(work.Samples);
    }

    private readonly record struct WorkItem(Complex32[] Samples, int Count, int SampleRate, int ViewBandwidth, int Quality, int Generation);
}
