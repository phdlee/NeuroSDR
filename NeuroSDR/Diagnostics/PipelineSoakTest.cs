using NeuroSDR.Audio;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;
using System.Diagnostics;
using System.Text.Json;

namespace NeuroSDR.Diagnostics;

internal static class PipelineSoakTest
{
    public static int Run(int seconds, int sampleRate = 2_000_000)
    {
        var report = new SoakReport { StartedAt = DateTimeOffset.Now, RequestedSeconds = seconds };
        SdrplaySampleSource? source = null;
        WaveOutPlayer? audio = null;
        try
        {
            if (!SdrplaySampleSource.TryCreate(out source, out var deviceStatus) || source is null)
                return Finish(report, 41, deviceStatus);
            if (source.SupportedSampleRates.Contains(sampleRate)) source.ConfiguredSampleRate = sampleRate;

            report.Device = source.Name;
            report.SampleRate = source.SampleRate;
            var spectrumProcessor = new SpectrumProcessor();
            var demodulator = new AudioDemodulator { Mode = Core.RadioMode.AM, Bandwidth = 10_000 };
            audio = new WaveOutPlayer(AudioDemodulator.AudioSampleRate) { VolumePercent = 0 };
            long frames = 0, audioSamples = 0, handlerCalls = 0, fftTicks = 0, demodTicks = 0, audioTicks = 0;
            double peakAboveMedian = 0;
            source.SamplesAvailable += samples =>
            {
                Interlocked.Increment(ref handlerCalls);
                var stageStart = Stopwatch.GetTimestamp();
                var spectrum = spectrumProcessor.Process(samples);
                Interlocked.Add(ref fftTicks, Stopwatch.GetTimestamp() - stageStart);
                if (spectrum is not null)
                {
                    Interlocked.Increment(ref frames);
                    var ordered = (float[])spectrum.Clone();
                    Array.Sort(ordered);
                    var contrast = spectrum.Max() - ordered[ordered.Length / 2];
                    Interlocked.Exchange(ref peakAboveMedian, Math.Max(Interlocked.CompareExchange(ref peakAboveMedian, 0, 0), contrast));
                }
                stageStart = Stopwatch.GetTimestamp();
                var decoded = demodulator.Process(samples, source.SampleRate);
                Interlocked.Add(ref demodTicks, Stopwatch.GetTimestamp() - stageStart);
                Interlocked.Add(ref audioSamples, decoded.Length);
                stageStart = Stopwatch.GetTimestamp();
                audio.Write(decoded);
                Interlocked.Add(ref audioTicks, Stopwatch.GetTimestamp() - stageStart);
            };

            source.CenterFrequency = 10_000_000;
            source.GainPercent = 70;
            source.Start();
            var stopwatch = Stopwatch.StartNew();
            var testStage = 0;
            long lastDelivered = 0;
            var stalledChecks = 0;
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                Thread.Sleep(250);
                if (testStage == 0 && stopwatch.Elapsed > TimeSpan.FromSeconds(seconds / 3d))
                {
                    source.CenterFrequency = 7_100_000;
                    demodulator.Mode = Core.RadioMode.USB;
                    demodulator.Bandwidth = 2_700;
                    demodulator.FrequencyOffset = 0;
                    testStage = 1;
                }
                if (testStage == 1 && stopwatch.Elapsed > TimeSpan.FromSeconds(seconds * 2 / 3d))
                {
                    source.CenterFrequency = 100_000_000;
                    demodulator.Mode = Core.RadioMode.WFM;
                    demodulator.Bandwidth = 180_000;
                    demodulator.FrequencyOffset = 300_000;
                    testStage = 2;
                }
                var delivered = source.DeliveredSamples;
                stalledChecks = delivered == lastDelivered ? stalledChecks + 1 : 0;
                lastDelivered = delivered;
                report.MaximumDeliveryAgeMs = Math.Max(report.MaximumDeliveryAgeMs, source.LastDeliveryAgeMilliseconds);
                if (stalledChecks >= 8) return Finish(report, 42, "DSP delivery stalled for more than 2 seconds.", source, audio, frames, audioSamples, peakAboveMedian, handlerCalls, fftTicks, demodTicks, audioTicks);
            }

            var stopWatch = Stopwatch.StartNew();
            source.Stop();
            stopWatch.Stop();
            report.StopMilliseconds = stopWatch.ElapsedMilliseconds;
            if (report.StopMilliseconds > 2_000) return Finish(report, 43, "Stopping SDRplay took more than 2 seconds.", source, audio, frames, audioSamples, peakAboveMedian, handlerCalls, fftTicks, demodTicks, audioTicks);
            if (frames < seconds * 5L) return Finish(report, 44, "Insufficient FFT frame progress.", source, audio, frames, audioSamples, peakAboveMedian, handlerCalls, fftTicks, demodTicks, audioTicks);
            if (audio.CompletedBuffers == 0 || audioSamples == 0) return Finish(report, 45, "The audio pipeline made no progress.", source, audio, frames, audioSamples, peakAboveMedian, handlerCalls, fftTicks, demodTicks, audioTicks);
            if (source.DeliveredSamples < source.SampleRate * Math.Max(1, seconds - 2L)) return Finish(report, 46, "DSP throughput is significantly below the real-time input rate.", source, audio, frames, audioSamples, peakAboveMedian, handlerCalls, fftTicks, demodTicks, audioTicks);
            return Finish(report, 0, "OK", source, audio, frames, audioSamples, peakAboveMedian, handlerCalls, fftTicks, demodTicks, audioTicks);
        }
        catch (Exception exception)
        {
            return Finish(report, 49, exception.ToString(), source, audio);
        }
        finally
        {
            try { source?.Stop(); } catch { }
            try { audio?.Dispose(); } catch { }
            try { source?.Dispose(); } catch { }
        }
    }

    private static int Finish(SoakReport report, int exitCode, string message, SdrplaySampleSource? source = null,
        WaveOutPlayer? audio = null, long frames = 0, long audioSamples = 0, double peakAboveMedian = 0,
        long handlerCalls = 0, long fftTicks = 0, long demodTicks = 0, long audioTicks = 0)
    {
        report.FinishedAt = DateTimeOffset.Now;
        report.ExitCode = exitCode;
        report.Message = message;
        report.NativeSamples = source?.TotalSamples ?? 0;
        report.DeliveredSamples = source?.DeliveredSamples ?? 0;
        report.DroppedSamples = source?.DroppedSamples ?? 0;
        report.SpectrumFrames = frames;
        report.AudioSamples = audioSamples;
        report.AudioSubmittedBuffers = audio?.SubmittedBuffers ?? 0;
        report.AudioCompletedBuffers = audio?.CompletedBuffers ?? 0;
        report.AudioDroppedBuffers = audio?.DroppedBuffers ?? 0;
        report.PeakAboveMedianDb = peakAboveMedian;
        report.HandlerCalls = handlerCalls;
        report.FftCpuMilliseconds = fftTicks * 1000d / Stopwatch.Frequency;
        report.DemodCpuMilliseconds = demodTicks * 1000d / Stopwatch.Frequency;
        report.AudioCpuMilliseconds = audioTicks * 1000d / Stopwatch.Frequency;
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "sdrplay-soak-latest.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return exitCode;
    }

    private sealed class SoakReport
    {
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public int RequestedSeconds { get; set; }
        public string Device { get; set; } = string.Empty;
        public int SampleRate { get; set; }
        public int ExitCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public long NativeSamples { get; set; }
        public long DeliveredSamples { get; set; }
        public long DroppedSamples { get; set; }
        public long SpectrumFrames { get; set; }
        public long AudioSamples { get; set; }
        public long AudioSubmittedBuffers { get; set; }
        public long AudioCompletedBuffers { get; set; }
        public long AudioDroppedBuffers { get; set; }
        public long MaximumDeliveryAgeMs { get; set; }
        public long StopMilliseconds { get; set; }
        public double PeakAboveMedianDb { get; set; }
        public long HandlerCalls { get; set; }
        public double FftCpuMilliseconds { get; set; }
        public double DemodCpuMilliseconds { get; set; }
        public double AudioCpuMilliseconds { get; set; }
    }
}
