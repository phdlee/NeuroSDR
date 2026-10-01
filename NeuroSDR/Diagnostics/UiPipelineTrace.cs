using System.Diagnostics;
using System.Globalization;
using NeuroSDR.Audio;
using NeuroSDR.Core;

namespace NeuroSDR.Diagnostics;

internal enum UiPipelineStage
{
    IqPlugins,
    RfDisplaySubmit,
    Demodulator,
    AfPlugins,
    AfDisplaySubmit,
    Audio1Dsp,
    Audio1Write,
    Audio2Dsp,
    Audio2Write,
    WholeCallback
}

/// <summary>
/// Low-overhead, one-row-per-second trace of the real WinForms receive path.
/// Hot-path calls only update interlocked counters; disk I/O occurs on the UI timer.
/// </summary>
internal sealed class UiPipelineTrace : IDisposable
{
    private const string Header =
        "utc,event,interval_ms,source,sample_rate,mode,frequency,filter_bw,view_bw,gain,rf_amp,rf_fps,fft_quality," +
        "rf_blocks,rf_samples,rf_rate,pcm_samples,pcm_rate,rf_max_gap_ms," +
        "source_total,source_delivered,source_dropped,source_queue,source_queue_max," +
        "iq_avg_ms,iq_max_ms,rf_display_avg_ms,rf_display_max_ms,demod_avg_ms,demod_max_ms," +
        "af_plugins_avg_ms,af_plugins_max_ms,af_display_avg_ms,af_display_max_ms," +
        "audio1_dsp_avg_ms,audio1_dsp_max_ms,audio1_write_avg_ms,audio1_write_max_ms," +
        "audio2_dsp_avg_ms,audio2_dsp_max_ms,audio2_write_avg_ms,audio2_write_max_ms," +
        "callback_avg_ms,callback_max_ms,callback_errors,last_error,audio1_submitted,audio1_completed,audio1_dropped,audio1_underruns,audio1_pending,audio1_max_submit_gap_ms,audio1_pending_min,audio1_pending_max," +
        "audio2_submitted,audio2_completed,audio2_dropped,audio2_underruns,audio2_pending,audio2_max_submit_gap_ms,audio2_pending_min,audio2_pending_max," +
        "gc0,gc1,gc2,managed_mb,threadpool_pending,enabled_af_plugins";

    private readonly StreamWriter _writer;
    private readonly long[] _stageTicks = new long[Enum.GetValues<UiPipelineStage>().Length];
    private readonly long[] _stageMaxTicks = new long[Enum.GetValues<UiPipelineStage>().Length];
    private readonly long[] _stageCounts = new long[Enum.GetValues<UiPipelineStage>().Length];
    private long _rfBlocks, _rfSamples, _pcmSamples, _lastArrivalTick, _maximumArrivalGapTicks;
    private long _callbackErrors;
    private string _lastError = string.Empty;
    private long _lastSnapshotTick = Stopwatch.GetTimestamp();
    private bool _disposed;

    public static string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "diagnostics");
    public string FilePath { get; }

    public UiPipelineTrace()
    {
        var directory = DirectoryPath;
        try { Directory.CreateDirectory(directory); }
        catch
        {
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NeuroSDR", "Diagnostics");
            Directory.CreateDirectory(directory);
        }
        FilePath = Path.Combine(directory, $"ui-pipeline-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _writer = new StreamWriter(FilePath, false, new System.Text.UTF8Encoding(true)) { AutoFlush = true };
        _writer.WriteLine(Header);
    }

    public void RecordRfArrival(int samples)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastArrivalTick, now);
        if (previous != 0) UpdateMaximum(ref _maximumArrivalGapTicks, now - previous);
        Interlocked.Increment(ref _rfBlocks);
        Interlocked.Add(ref _rfSamples, samples);
    }

    public void RecordPcm(int samples) => Interlocked.Add(ref _pcmSamples, samples);

    public void RecordError(Exception exception)
    {
        Interlocked.Increment(ref _callbackErrors);
        Interlocked.Exchange(ref _lastError, exception.GetBaseException().ToString());
    }

    public void RecordStage(UiPipelineStage stage, long startedAt)
    {
        var elapsed = Stopwatch.GetTimestamp() - startedAt;
        var index = (int)stage;
        Interlocked.Add(ref _stageTicks[index], elapsed);
        Interlocked.Increment(ref _stageCounts[index]);
        UpdateMaximum(ref _stageMaxTicks[index], elapsed);
    }

    public void Snapshot(ISampleSource source, RadioMode mode, long frequency, int filterBandwidth, int viewBandwidth,
        int rfFps, int fftQuality, WaveOutPlayer?[] outputs, IEnumerable<string> activeAfPlugins, string eventName = "TICK")
    {
        if (_disposed) return;
        var culture = CultureInfo.InvariantCulture;
        var now = Stopwatch.GetTimestamp();
        var intervalSeconds = Math.Max(.001, (now - Interlocked.Exchange(ref _lastSnapshotTick, now)) / (double)Stopwatch.Frequency);
        var rfBlocks = Interlocked.Exchange(ref _rfBlocks, 0);
        var rfSamples = Interlocked.Exchange(ref _rfSamples, 0);
        var pcmSamples = Interlocked.Exchange(ref _pcmSamples, 0);
        var gain = source is IGainControlledSampleSource gainSource ? gainSource.GainPercent : 0;
        var rfAmp = source is IRfAmplifierSampleSource amplifierSource && amplifierSource.RfAmplifierEnabled;
        var values = new List<string>
        {
            DateTime.UtcNow.ToString("O", culture), Csv(eventName), (intervalSeconds * 1000).ToString("0.###", culture),
            Csv(source.Name), source.SampleRate.ToString(culture), mode.ToString(), frequency.ToString(culture),
            filterBandwidth.ToString(culture), viewBandwidth.ToString(culture), gain.ToString(culture), rfAmp.ToString(culture), rfFps.ToString(culture),
            fftQuality.ToString(culture), rfBlocks.ToString(culture), rfSamples.ToString(culture),
            (rfSamples / intervalSeconds).ToString("0.0", culture), pcmSamples.ToString(culture),
            (pcmSamples / intervalSeconds).ToString("0.0", culture),
            Milliseconds(Interlocked.Exchange(ref _maximumArrivalGapTicks, 0)).ToString("0.###", culture)
        };
        if (source is ISampleSourceMetrics metrics)
        {
            values.Add(metrics.TotalSamples.ToString(culture));
            values.Add(metrics.DeliveredSamples.ToString(culture));
            values.Add(metrics.DroppedSamples.ToString(culture));
        }
        else values.AddRange(["0", "0", "0"]);
        if (source is ISampleQueueMetrics queue)
            values.AddRange([queue.QueuedBlocks.ToString(culture), queue.MaximumQueuedBlocks.ToString(culture)]);
        else values.AddRange(["0", "0"]);

        foreach (var stage in Enum.GetValues<UiPipelineStage>())
        {
            var index = (int)stage;
            var ticks = Interlocked.Exchange(ref _stageTicks[index], 0);
            var count = Interlocked.Exchange(ref _stageCounts[index], 0);
            var maximum = Interlocked.Exchange(ref _stageMaxTicks[index], 0);
            values.Add(Milliseconds(count == 0 ? 0 : ticks / (double)count).ToString("0.###", culture));
            values.Add(Milliseconds(maximum).ToString("0.###", culture));
        }
        values.Add(Interlocked.Exchange(ref _callbackErrors, 0).ToString(culture));
        values.Add(Csv(Interlocked.Exchange(ref _lastError, string.Empty)));
        for (var index = 0; index < 2; index++)
        {
            var output = outputs[index];
            values.AddRange(output is null
                ? ["0", "0", "0", "0", "0", "0", "0", "0"]
                : [output.SubmittedBuffers.ToString(culture), output.CompletedBuffers.ToString(culture),
                    output.DroppedBuffers.ToString(culture), output.StarvationEvents.ToString(culture),
                    output.PendingBuffers.ToString(culture), output.MaximumSubmitGapMilliseconds.ToString("0.###", culture),
                    output.MinimumPendingBuffers.ToString(culture), output.MaximumPendingBuffers.ToString(culture)]);
        }
        values.AddRange([
            GC.CollectionCount(0).ToString(culture), GC.CollectionCount(1).ToString(culture), GC.CollectionCount(2).ToString(culture),
            (GC.GetTotalMemory(false) / 1_048_576d).ToString("0.0", culture),
            ThreadPool.PendingWorkItemCount.ToString(culture), Csv(string.Join("|", activeAfPlugins))
        ]);
        _writer.WriteLine(string.Join(',', values));
    }

    private static double Milliseconds(double ticks) => ticks * 1000d / Stopwatch.Frequency;
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static void UpdateMaximum(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
    }
}
