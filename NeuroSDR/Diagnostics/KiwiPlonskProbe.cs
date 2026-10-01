using WebSdr.Protocol;
using WebSdr.Protocol.Kiwi;

namespace NeuroSDR.Diagnostics;

/// <summary>Headless probe: does plonsk Kiwi die at ~5s with SND-only vs SND+WF?</summary>
internal static class KiwiPlonskProbe
{
    public static async Task<int> RunAsync(string url, bool withWaterfall, int seconds = 15)
    {
        url = string.IsNullOrWhiteSpace(url) ? "http://plonsk.proxy.kiwisdr.com:8073/" : url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Console.WriteLine("bad url");
            return 2;
        }

        long samples = 0;
        var status = new List<string>();
        void Note(string s)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
            status.Add(line);
            Console.WriteLine(line);
        }

        await using var sound = new KiwiSoundClient { UserName = "NeuroSDR", UseCompression = true };
        sound.StatusChanged += Note;
        sound.ErrorOccurred += ex => Note("SND ERR " + ex.Message);
        sound.SamplesAvailable += pcm => Interlocked.Add(ref samples, pcm.Length);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sound.Ready += () => ready.TrySetResult();

        Note($"connect SND {(withWaterfall ? "+WF" : "only")} {uri}");
        await sound.ConnectAsync(uri);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var settings = new ReceiverSettings
        {
            FrequencyKhz = 7_074,
            Mode = DemodMode.Usb,
            LoKhz = 0.05,
            HiKhz = 2.7,
            Name = "NeuroSDR"
        };
        await sound.SendInitialRxAsync(settings);
        var tuneKhz = uri.Host.Contains("2meter", StringComparison.OrdinalIgnoreCase) ? 144_100d : settings.FrequencyKhz;
        if (Math.Abs(tuneKhz - settings.FrequencyKhz) > 1)
        {
            settings.FrequencyKhz = tuneKhz;
            await sound.ApplyRxAsync(settings);
        }
        Note($"session={sound.SessionId} rate={sound.SampleRateHz} tune={tuneKhz:F1} kHz");

        KiwiWaterfallClient? wf = null;
        if (withWaterfall)
        {
            wf = new KiwiWaterfallClient { SessionId = sound.SessionId, Endpoint = sound.Endpoint };
            wf.StatusChanged += Note;
            wf.ErrorOccurred += ex => Note("WF ERR " + ex.Message);
            long rows = 0;
            wf.RowReceived += _ => Interlocked.Increment(ref rows);
            wf.PrepareTune(tuneKhz, 8);
            await wf.ConnectAsync(uri);
            if (wf.FreqOffsetKhz == 0 && tuneKhz > wf.BandwidthKhz)
                await Task.Delay(400);
            await wf.ConfigureAsync(tuneKhz, 8);
            Note($"WF z8 bw={wf.BandwidthKhz:F1} off={wf.FreqOffsetKhz:F1} span={wf.SpanKhz:F1} cf={wf.CenterKhz:F1} rows={rows}");
            await wf.ConfigureAsync(tuneKhz, 0);
            await Task.Delay(400);
            Note($"WF z0 bw={wf.BandwidthKhz:F1} off={wf.FreqOffsetKhz:F1} span={wf.SpanKhz:F1} cf={wf.CenterKhz:F1} rows={rows}");
            await wf.ConfigureAsync(tuneKhz, 4);
            await Task.Delay(400);
            Note($"WF z4 bw={wf.BandwidthKhz:F1} off={wf.FreqOffsetKhz:F1} span={wf.SpanKhz:F1} cf={wf.CenterKhz:F1} rows={rows}");
        }

        var marks = new long[seconds + 1];
        marks[0] = Interlocked.Read(ref samples);
        for (var i = 1; i <= seconds; i++)
        {
            await Task.Delay(1000);
            marks[i] = Interlocked.Read(ref samples);
            var delta = marks[i] - marks[i - 1];
            Note($"t={i}s total={marks[i]} delta={delta} sndOpen={sound.IsConnected}");
        }

        if (wf is not null) await wf.DisposeAsync();

        var living = 0;
        for (var i = 1; i <= seconds; i++)
            if (marks[i] > marks[i - 1]) living++;

        var log = Path.Combine(Path.GetTempPath(), "neurosdr-kiwi-plonsk-probe.log");
        await File.WriteAllTextAsync(log, string.Join("\r\n", status) + $"\r\nlivingSeconds={living}/{seconds}\r\n");
        Note($"done livingSeconds={living}/{seconds} log={log}");
        return living >= Math.Max(10, seconds - 2) ? 0 : 1;
    }
}
