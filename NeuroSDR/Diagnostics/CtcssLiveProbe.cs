using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;

namespace NeuroSDR.Diagnostics;

/// <summary>Live NFM CTCSS/DCS probe — tune, listen, print decoder state.</summary>
internal static class CtcssLiveProbe
{
    public static int Run(long frequencyHz, int seconds, int sampleRate = 5_000_000)
    {
        seconds = Math.Clamp(seconds, 5, 120);
        HostNativeDllResolver.EnsureRegistered();
        var statusPath = Path.Combine(Path.GetTempPath(), "neurosdr-ctcss-live.txt");
        try { File.Delete(statusPath); } catch { }

        var discovered = HardwareSourceCatalog.Discover();
        var sources = discovered.Where(r => r.Source is not null).Select(r => r.Source!).ToList();
        var source = sources.FirstOrDefault(s => s is not IRemoteAudioSampleSource)
                     ?? sources.FirstOrDefault();
        if (source is null)
        {
            Console.WriteLine("No sample source available.");
            return 1;
        }

        if (source is IConfigurableSampleRateSource configurable)
        {
            if (!configurable.SupportedSampleRates.Contains(sampleRate))
                sampleRate = configurable.SupportedSampleRates.Contains(5_000_000) ? 5_000_000
                    : configurable.SupportedSampleRates.Contains(2_000_000) ? 2_000_000
                    : configurable.SupportedSampleRates[0];
            configurable.ConfiguredSampleRate = sampleRate;
        }

        Console.WriteLine($"Source: {source.Name}");
        Console.WriteLine($"Tune: {frequencyHz / 1_000_000d:0.000000} MHz NFM  SR={source.SampleRate}");
        Console.WriteLine($"Hold PTT for {seconds}s (CTCSS or DCS)");
        Console.WriteLine($"Status file: {statusPath}");
        Console.WriteLine();

        var demod = new AudioDemodulator { Mode = RadioMode.NFM, Bandwidth = 12_500 };
        var ctcss = new CtcssToneDecoder();
        var dcs = new DcsToneDecoder();
        var afSync = new object();
        long pcm = 0;
        double audioEnergy = 0;
        long audioSamplesWindow = 0;
        source.SamplesAvailable += samples =>
        {
            var audio = demod.Process(samples, source.SampleRate);
            if (audio.Length == 0) return;
            Interlocked.Add(ref pcm, audio.Length);
            double e = 0;
            foreach (var v in audio) e += v * v;
            lock (afSync)
            {
                audioEnergy += e;
                audioSamplesWindow += audio.Length;
            }
            dcs.Process(audio, AudioDemodulator.AudioSampleRate);
            if (dcs.HasActivity) ctcss.Reset();
            else ctcss.Process(audio, AudioDemodulator.AudioSampleRate);
        };

        try
        {
            if (source is IGainControlledSampleSource gain) gain.GainPercent = 75;
            source.CenterFrequency = frequencyHz;
            demod.FrequencyOffset = 0;
            source.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Start failed: " + ex.GetBaseException().Message);
            return 2;
        }

        var end = Environment.TickCount64 + seconds * 1000L;
        var lines = new List<string>();
        void Log(string msg)
        {
            var line = $"{DateTime.Now:HH:mm:ss}  {msg}  pcm={Interlocked.Read(ref pcm)}";
            Console.WriteLine(line);
            lines.Add(line);
            try { File.WriteAllLines(statusPath, lines); } catch { }
        }

        Log("listening");
        while (Environment.TickCount64 < end)
        {
            Thread.Sleep(1000);
            double afRms;
            lock (afSync)
            {
                afRms = audioSamplesWindow > 0 ? Math.Sqrt(audioEnergy / audioSamplesWindow) : 0;
                audioEnergy = 0;
                audioSamplesWindow = 0;
            }
            var tone = ctcss.DetectedToneHz;
            var dcsLabel = dcs.DetectedLabel;
            string msg;
            if (dcsLabel.Length > 0) msg = $"DCS {dcsLabel}";
            else if (dcs.HasActivity)
                msg = $"DCS … corr={dcs.DebugCorr:0.00} dist={dcs.DebugBestDist} votes={dcs.DebugVotes} raw=0x{dcs.DebugTopPattern:X6}";
            else if (tone > 0) msg = $"CTCSS {tone:0.0} Hz";
            else
                msg = $"search ctcssBest={ctcss.DebugBestHz:0.0} dcsVotes={dcs.DebugVotes} dist={dcs.DebugBestDist} raw=0x{dcs.DebugTopPattern:X6} afRms={afRms:0.0000}";
            Log(msg);
        }

        try { source.Stop(); } catch { }
        try { source.Dispose(); } catch { }

        var result = dcs.DetectedLabel.Length > 0
            ? $"RESULT DCS {dcs.DetectedLabel}"
            : ctcss.DetectedToneHz > 0
                ? $"RESULT CTCSS {ctcss.DetectedToneHz:0.0} Hz"
                : "RESULT no tone locked";
        Log(result);
        Console.WriteLine();
        Console.WriteLine(result);
        return dcs.DetectedLabel.Length > 0 || ctcss.DetectedToneHz > 0 ? 0 : 3;
    }
}
