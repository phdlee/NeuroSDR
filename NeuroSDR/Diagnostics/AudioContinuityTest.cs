using System.Diagnostics;
using System.Text.Json;
using NeuroSDR.Audio;
using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;

namespace NeuroSDR.Diagnostics;

internal static class AudioContinuityTest
{
    public static int Run(string deviceKind, int seconds, int sampleRate, bool includeDisplay,
        RadioMode mode = RadioMode.WFM, long frequency = 89_100_000, int gainPercent = 60,
        bool hardwareAgc = false, long? centerFrequency = null, long? demodOffset = null,
        bool postProcess = false, int afLowCutHz = 0, int afHighCutHz = 20_000,
        int noiseReductionStrength = 0, bool enfilter = false)
    {
        ISampleSource? source = null;
        WaveOutPlayer? output = null;
        SpectrumPipeline? spectrum = null;
        EnFilterProcessor? voiceFilter = null;
        var report = new Report
        {
            DeviceKind = deviceKind,
            RequestedSeconds = seconds,
            RequestedSampleRate = sampleRate,
            GainPercent = Math.Clamp(gainPercent, 0, 100),
            HardwareAgc = hardwareAgc,
            CenterFrequency = centerFrequency ?? frequency,
            DemodOffset = demodOffset ?? 0,
            PostProcess = postProcess,
            AfLowCutHz = afLowCutHz,
            AfHighCutHz = afHighCutHz,
            NoiseReductionStrength = noiseReductionStrength,
            Enfilter = enfilter,
            DisplayPipelineEnabled = includeDisplay,
            StartedAt = DateTimeOffset.Now
        };
        try
        {
            if (!TryCreateSource(deviceKind, out source, out var status) || source is null)
                return Finish(report, 81, status);
            report.Device = source.Name;
            if (source is IConfigurableSampleRateSource configurable)
            {
                if (!configurable.SupportedSampleRates.Contains(sampleRate))
                    return Finish(report, 82, $"Unsupported sample rate {sampleRate}");
                configurable.ConfiguredSampleRate = sampleRate;
            }
            report.ActualSampleRate = source.SampleRate;
            source.CenterFrequency = report.CenterFrequency;
            if (source is IGainControlledSampleSource gain) gain.GainPercent = report.GainPercent;
            if (source is IHardwareAgcSampleSource hardwareAgcSource) hardwareAgcSource.HardwareAgcEnabled = hardwareAgc;

            var demodulator = new AudioDemodulator
            {
                Mode = mode,
                Bandwidth = mode switch { RadioMode.WFM => 180_000, RadioMode.AM or RadioMode.SAM => 10_000, _ => 2_700 },
                FrequencyOffset = report.DemodOffset
            };
            var postProcessor = new AudioPostProcessor
            {
                AgcEnabled = postProcess,
                AfFilterEnabled = postProcess,
                AfLowCutHz = afLowCutHz,
                AfHighCutHz = afHighCutHz,
                NoiseReductionEnabled = postProcess && noiseReductionStrength > 0,
                NoiseReductionStrength = noiseReductionStrength
            };
            output = new WaveOutPlayer(AudioDemodulator.AudioSampleRate, maximumPendingBuffers: sampleRate >= 15_000_000 ? 128 : 32) { VolumePercent = 0 };
            if (enfilter)
            {
                voiceFilter = new EnFilterProcessor
                {
                    VoiceEnabled = true,
                    VoiceEq = true,
                    VoiceWet = 0.93f
                };
                voiceFilter.SetMode(mode);
                voiceFilter.ApplyOptions();
            }
            if (includeDisplay) spectrum = new SpectrumPipeline();

            var processTimes = new List<double>(Math.Max(256, seconds * 400));
            var gapEvents = new List<GapEvent>();
            long rfSamples = 0, pcmSamples = 0, callbacks = 0;
            long firstCallbackTick = 0, lastCallbackTick = 0, previousCallbackTick = 0;
            long previousPcmTick = 0, nextDisplayTick = 0;
            double maximumRfGapMs = 0, maximumPcmGapMs = 0;
            double iqPower = 0, pcmPower = 0;
            float iqPeak = 0, pcmPeak = 0;
            long iqMeasured = 0, pcmMeasured = 0;
            double postPcmPower = 0;
            float postPcmPeak = 0;
            long postPcmMeasured = 0;
            double dryPower = 0;
            long dryMeasured = 0;
            long pcmJumps = 0;
            float previousPcm = 0;

            Action<Complex32[]> handler = samples =>
            {
                var arrival = Stopwatch.GetTimestamp();
                if (firstCallbackTick == 0) firstCallbackTick = arrival;
                if (previousCallbackTick != 0)
                {
                    var actualGapMs = (arrival - previousCallbackTick) * 1000d / Stopwatch.Frequency;
                    var expectedGapMs = samples.Length * 1000d / source.SampleRate;
                    maximumRfGapMs = Math.Max(maximumRfGapMs, actualGapMs);
                    if (actualGapMs > Math.Max(30, expectedGapMs * 2.5) && gapEvents.Count < 100)
                        gapEvents.Add(new GapEvent(callbacks, actualGapMs, expectedGapMs));
                }
                previousCallbackTick = lastCallbackTick = arrival;
                callbacks++;
                rfSamples += samples.Length;
                var iqStride = Math.Max(1, samples.Length / 4096);
                for (var index = 0; index < samples.Length; index += iqStride)
                {
                    var sample = samples[index];
                    iqPower += sample.I * sample.I + sample.Q * sample.Q;
                    iqPeak = Math.Max(iqPeak, Math.Max(Math.Abs(sample.I), Math.Abs(sample.Q)));
                    iqMeasured++;
                }

                if (spectrum is not null && Environment.TickCount64 >= nextDisplayTick)
                {
                    nextDisplayTick = Environment.TickCount64 + 66;
                    spectrum.Submit(samples, source.SampleRate, source.SampleRate, 1);
                }

                var processStart = Stopwatch.GetTimestamp();
                var pcm = demodulator.Process(samples, source.SampleRate);
                if (pcm.Length > 0)
                {
                    foreach (var value in pcm)
                    {
                        dryPower += value * value;
                        dryMeasured++;
                    }
                }
                if (voiceFilter is not null && pcm.Length > 0)
                    pcm = voiceFilter.Process(pcm, mode);
                var processEnd = Stopwatch.GetTimestamp();
                processTimes.Add((processEnd - processStart) * 1000d / Stopwatch.Frequency);
                pcmSamples += pcm.Length;
                if (pcm.Length > 0)
                {
                    foreach (var value in pcm)
                    {
                        pcmPower += value * value;
                        pcmPeak = Math.Max(pcmPeak, Math.Abs(value));
                        if (Math.Abs(value - previousPcm) > 0.25f) pcmJumps++;
                        previousPcm = value;
                    }
                    pcmMeasured += pcm.Length;
                    var outputPcm = postProcess ? postProcessor.Process((float[])pcm.Clone(), 0) : pcm;
                    foreach (var value in outputPcm)
                    {
                        postPcmPower += value * value;
                        postPcmPeak = Math.Max(postPcmPeak, Math.Abs(value));
                    }
                    postPcmMeasured += outputPcm.Length;
                    if (previousPcmTick != 0)
                        maximumPcmGapMs = Math.Max(maximumPcmGapMs,
                            (processEnd - previousPcmTick) * 1000d / Stopwatch.Frequency);
                    previousPcmTick = processEnd;
                    output.Write(outputPcm);
                }
            };

            source.SamplesAvailable += handler;
            var runClock = Stopwatch.StartNew();
            source.Start();
            while (runClock.Elapsed < TimeSpan.FromSeconds(seconds)) Thread.Sleep(10);

            // Stop delivery before reading the collections. The timestamps are captured
            // inside the callback, so device shutdown time is still excluded while the
            // percentile snapshot can no longer race a callback that appends to the list.
            source.SamplesAvailable -= handler;
            if (source is SdrplaySampleSource liveRsp)
            {
                report.DeviceGainDb = liveRsp.CurrentGainDb;
                report.GainReductionDb = liveRsp.CurrentGainReductionDb;
                report.LnaState = liveRsp.CurrentLnaState;
                report.TunerBandwidthKhz = liveRsp.CurrentTunerBandwidth;
            }
            source.Stop();

            report.MeasuredSeconds = firstCallbackTick == 0 ? 0 : (lastCallbackTick - firstCallbackTick) / (double)Stopwatch.Frequency;
            report.RfSamples = rfSamples;
            report.PcmSamples = pcmSamples;
            report.Callbacks = callbacks;
            report.MaximumRfCallbackGapMs = maximumRfGapMs;
            report.MaximumPcmProductionGapMs = maximumPcmGapMs;
            report.RfSamplesPerSecond = report.MeasuredSeconds <= 0 ? 0 : rfSamples / report.MeasuredSeconds;
            report.PcmSamplesPerSecond = report.MeasuredSeconds <= 0 ? 0 : pcmSamples / report.MeasuredSeconds;
            report.ExpectedPcmSamples = rfSamples * (double)AudioDemodulator.AudioSampleRate / source.SampleRate;
            report.PcmSampleError = pcmSamples - report.ExpectedPcmSamples;
            report.IqRmsDbfs = ToDb(iqMeasured == 0 ? 0 : Math.Sqrt(iqPower / iqMeasured));
            report.IqPeakDbfs = ToDb(iqPeak);
            report.DryPcmRmsDbfs = ToDb(dryMeasured == 0 ? 0 : Math.Sqrt(dryPower / dryMeasured));
            report.PcmJumps = pcmJumps;
            report.PcmRmsDbfs = ToDb(pcmMeasured == 0 ? 0 : Math.Sqrt(pcmPower / pcmMeasured));
            report.PcmPeakDbfs = ToDb(pcmPeak);
            report.PostPcmRmsDbfs = ToDb(postPcmMeasured == 0 ? 0 : Math.Sqrt(postPcmPower / postPcmMeasured));
            report.PostPcmPeakDbfs = ToDb(postPcmPeak);
            if (source is SdrplaySampleSource rsp)
            {
                report.OverloadDetectedEvents = rsp.OverloadDetectedEvents;
                report.OverloadCorrectedEvents = rsp.OverloadCorrectedEvents;
            }
            report.GapEvents = gapEvents;
            processTimes.Sort();
            report.DemodMeanMs = processTimes.Count == 0 ? 0 : processTimes.Average();
            report.DemodP95Ms = Percentile(processTimes, .95);
            report.DemodP99Ms = Percentile(processTimes, .99);
            report.DemodMaximumMs = processTimes.Count == 0 ? 0 : processTimes[^1];
            report.AudioSubmittedBuffers = output.SubmittedBuffers;
            report.AudioCompletedBuffers = output.CompletedBuffers;
            report.AudioDroppedBuffers = output.DroppedBuffers;
            report.AudioStarvationEvents = output.StarvationEvents;
            report.AudioMaximumSubmitGapMs = output.MaximumSubmitGapMilliseconds;
            report.AudioMinimumPendingBuffers = output.MinimumPendingBuffers;
            report.AudioMaximumPendingBuffers = output.MaximumPendingBuffers;
            if (source is ISampleSourceMetrics metrics)
            {
                report.NativeSamples = metrics.TotalSamples;
                report.DeliveredSamples = metrics.DeliveredSamples;
                report.DroppedSamples = metrics.DroppedSamples;
            }
            if (source is ISampleQueueMetrics queue)
            {
                report.QueuedBlocksAtEnd = queue.QueuedBlocks;
                report.MaximumQueuedBlocks = queue.MaximumQueuedBlocks;
            }

            var discontinuous = report.AudioStarvationEvents > 0 || report.AudioDroppedBuffers > 0 ||
                report.PcmSamplesPerSecond < 47_000 ||
                report.RfSamplesPerSecond < source.SampleRate * .97;
            return Finish(report, discontinuous ? 83 : 0, discontinuous ? "Discontinuity detected" : "Continuous");
        }
        catch (Exception exception)
        {
            return Finish(report, 89, exception.ToString());
        }
        finally
        {
            try { source?.Stop(); } catch { }
            try { spectrum?.Dispose(); } catch { }
            try { output?.Dispose(); } catch { }
            try { source?.Dispose(); } catch { }
            try { voiceFilter?.Dispose(); } catch { }
        }
    }

    private static bool TryCreateSource(string kind, out ISampleSource? source, out string status)
    {
        source = null;
        if (kind.Equals("rsp", StringComparison.OrdinalIgnoreCase) || kind.Equals("sdrplay", StringComparison.OrdinalIgnoreCase))
        {
            var found = SdrplaySampleSource.TryCreate(out var rsp, out status);
            source = rsp;
            return found;
        }
        if (kind.Equals("hackrf", StringComparison.OrdinalIgnoreCase))
        {
            var found = HackRfSampleSource.TryCreate(out var hackrf, out status);
            source = hackrf;
            return found;
        }
        status = $"Unknown device kind: {kind}";
        return false;
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        return sorted[(int)Math.Clamp(Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];
    }

    private static double ToDb(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-12));

    private static int Finish(Report report, int exitCode, string message)
    {
        report.FinishedAt = DateTimeOffset.Now;
        report.ExitCode = exitCode;
        report.Message = message;
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(directory);
            var safeDevice = report.DeviceKind.ToLowerInvariant().Replace("/", "-");
            var path = Path.Combine(directory, $"audio-continuity-{safeDevice}-{report.RequestedSampleRate}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return exitCode;
    }

    private sealed class Report
    {
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public string DeviceKind { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public int RequestedSeconds { get; set; }
        public int RequestedSampleRate { get; set; }
        public int ActualSampleRate { get; set; }
        public int GainPercent { get; set; }
        public bool HardwareAgc { get; set; }
        public long CenterFrequency { get; set; }
        public long DemodOffset { get; set; }
        public bool PostProcess { get; set; }
        public int AfLowCutHz { get; set; }
        public int AfHighCutHz { get; set; }
        public int NoiseReductionStrength { get; set; }
        public bool Enfilter { get; set; }
        public bool DisplayPipelineEnabled { get; set; }
        public int ExitCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public double MeasuredSeconds { get; set; }
        public long NativeSamples { get; set; }
        public long DeliveredSamples { get; set; }
        public long DroppedSamples { get; set; }
        public long RfSamples { get; set; }
        public long PcmSamples { get; set; }
        public double ExpectedPcmSamples { get; set; }
        public double PcmSampleError { get; set; }
        public double IqRmsDbfs { get; set; }
        public double IqPeakDbfs { get; set; }
        public double DryPcmRmsDbfs { get; set; }
        public long PcmJumps { get; set; }
        public double PcmRmsDbfs { get; set; }
        public double PcmPeakDbfs { get; set; }
        public double PostPcmRmsDbfs { get; set; }
        public double PostPcmPeakDbfs { get; set; }
        public long OverloadDetectedEvents { get; set; }
        public long OverloadCorrectedEvents { get; set; }
        public float DeviceGainDb { get; set; }
        public int GainReductionDb { get; set; }
        public int LnaState { get; set; }
        public int TunerBandwidthKhz { get; set; }
        public long Callbacks { get; set; }
        public double RfSamplesPerSecond { get; set; }
        public double PcmSamplesPerSecond { get; set; }
        public double MaximumRfCallbackGapMs { get; set; }
        public double MaximumPcmProductionGapMs { get; set; }
        public double DemodMeanMs { get; set; }
        public double DemodP95Ms { get; set; }
        public double DemodP99Ms { get; set; }
        public double DemodMaximumMs { get; set; }
        public long AudioSubmittedBuffers { get; set; }
        public long AudioCompletedBuffers { get; set; }
        public long AudioDroppedBuffers { get; set; }
        public long AudioStarvationEvents { get; set; }
        public double AudioMaximumSubmitGapMs { get; set; }
        public int AudioMinimumPendingBuffers { get; set; }
        public int AudioMaximumPendingBuffers { get; set; }
        public int QueuedBlocksAtEnd { get; set; }
        public int MaximumQueuedBlocks { get; set; }
        public List<GapEvent> GapEvents { get; set; } = [];
    }

    private sealed record GapEvent(long Callback, double ActualGapMs, double ExpectedGapMs);
}
