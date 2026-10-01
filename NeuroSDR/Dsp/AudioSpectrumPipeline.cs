using NeuroSDR.Core;
using System.Threading.Channels;

namespace NeuroSDR.Dsp;

internal sealed class AudioSpectrumPipeline : IDisposable
{
    private const int MonoFftSize = 8_192;
    private const int StereoFftSize = 2_048;
    private readonly Channel<(float[] Samples, bool Stereo)> _queue =
        Channel.CreateBounded<(float[], bool)>(new BoundedChannelOptions(12)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly Task _worker;

    public AudioSpectrumPipeline() => _worker = Task.Run(ProcessLoop);
    public event Action<float[]>? SpectrumAvailable;
    public event Action<float[], float[]>? StereoSpectrumAvailable;

    public void Submit(float[] audio)
    {
        if (audio.Length > 0) _queue.Writer.TryWrite((audio, false));
    }

    public void SubmitStereo(float[] interleaved)
    {
        if (interleaved.Length >= 4) _queue.Writer.TryWrite((interleaved, true));
    }

    public void Reset()
    {
        while (_queue.Reader.TryRead(out _)) { }
    }

    private async Task ProcessLoop()
    {
        var processorMono = new SpectrumProcessor(MonoFftSize, 0);
        // Smaller FFT + skip 0 → snappy LED refresh for WFM L/R.
        var processorL = new SpectrumProcessor(StereoFftSize, 0);
        var processorR = new SpectrumProcessor(StereoFftSize, 0);
        var complex = Array.Empty<Complex32>();
        var channelScratch = Array.Empty<float>();
        var publishedMono = new[] { new float[MonoFftSize / 2], new float[MonoFftSize / 2], new float[MonoFftSize / 2] };
        var publishedL = new[] { new float[StereoFftSize / 2], new float[StereoFftSize / 2], new float[StereoFftSize / 2] };
        var publishedR = new[] { new float[StereoFftSize / 2], new float[StereoFftSize / 2], new float[StereoFftSize / 2] };
        var lastL = new float[StereoFftSize / 2];
        var lastR = new float[StereoFftSize / 2];
        var haveL = false;
        var haveR = false;
        var publishSlot = 0;
        await foreach (var (audio, stereo) in _queue.Reader.ReadAllAsync())
        {
            if (!stereo)
            {
                if (complex.Length != audio.Length) complex = new Complex32[audio.Length];
                for (var index = 0; index < audio.Length; index++) complex[index] = new Complex32(audio[index], 0);
                var fullSpectrum = processorMono.Process(complex);
                if (fullSpectrum is null) continue;
                var positive = publishedMono[publishSlot];
                publishSlot = (publishSlot + 1) % publishedMono.Length;
                Array.Copy(fullSpectrum, MonoFftSize / 2, positive, 0, positive.Length);
                try { SpectrumAvailable?.Invoke(positive); } catch { }
                continue;
            }

            var frames = audio.Length / 2;
            if (frames < 16) continue;
            if (channelScratch.Length < frames) channelScratch = new float[frames];
            if (complex.Length != frames) complex = new Complex32[frames];

            // Always feed BOTH processors with the same frame count so they stay in lock-step.
            for (var i = 0; i < frames; i++) channelScratch[i] = audio[i * 2];
            for (var i = 0; i < frames; i++) complex[i] = new Complex32(channelScratch[i], 0);
            var fullL = processorL.Process(complex);

            for (var i = 0; i < frames; i++) channelScratch[i] = audio[i * 2 + 1];
            for (var i = 0; i < frames; i++) complex[i] = new Complex32(channelScratch[i], 0);
            var fullR = processorR.Process(complex);

            if (fullL is not null)
            {
                Array.Copy(fullL, StereoFftSize / 2, lastL, 0, lastL.Length);
                haveL = true;
            }
            if (fullR is not null)
            {
                Array.Copy(fullR, StereoFftSize / 2, lastR, 0, lastR.Length);
                haveR = true;
            }
            if (!haveL || !haveR || (fullL is null && fullR is null)) continue;

            var left = publishedL[publishSlot];
            var right = publishedR[publishSlot];
            var mono = publishedMono[publishSlot % publishedMono.Length];
            publishSlot = (publishSlot + 1) % publishedL.Length;
            Array.Copy(lastL, left, left.Length);
            Array.Copy(lastR, right, right.Length);
            var monoBins = Math.Min(mono.Length, left.Length);
            for (var i = 0; i < monoBins; i++)
                mono[i] = Math.Max(left[i], right[i]);
            for (var i = monoBins; i < mono.Length; i++)
                mono[i] = -140f;
            try { StereoSpectrumAvailable?.Invoke(left, right); } catch { }
            try { SpectrumAvailable?.Invoke(mono); } catch { }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _worker.Wait(2_000); } catch (AggregateException) { }
    }
}
