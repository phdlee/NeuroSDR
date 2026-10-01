using NeuroSDR.Dsp;
using NeuroSDR.Hardware;
using System.Text.Json;

namespace NeuroSDR.Diagnostics;

internal static class RfSurveyTest
{
    public static int Run()
    {
        SdrplaySampleSource? source = null;
        var report = new SurveyReport { StartedAt = DateTimeOffset.Now };
        try
        {
            if (!SdrplaySampleSource.TryCreate(out source, out var status) || source is null)
                return Finish(report, 51, status);
            report.Device = source.Name;
            source.GainPercent = 78;
            var processor = new SpectrumProcessor(4096);
            float[]? latestSpectrum = null;
            source.SamplesAvailable += samples =>
            {
                var spectrum = processor.Process(samples);
                if (spectrum is not null) Interlocked.Exchange(ref latestSpectrum, spectrum);
            };
            source.CenterFrequency = 89_000_000;
            source.Start();

            var fmCenters = Enumerable.Range(0, 13).Select(index => 89_000_000L + index * 1_500_000L);
            foreach (var center in fmCenters) SurveyCenter(source, center, "FM", report, ref latestSpectrum);
            long[] shortwaveCenters = [3_500_000, 4_800_000, 6_000_000, 7_200_000, 9_600_000, 11_800_000, 13_700_000, 15_500_000, 17_700_000, 21_600_000, 25_800_000];
            foreach (var center in shortwaveCenters) SurveyCenter(source, center, "SW", report, ref latestSpectrum);

            source.Stop();
            report.Strongest = report.Bands.OrderByDescending(band => band.ContrastDb).Take(12).ToList();
            return Finish(report, report.Strongest.Count > 0 && report.Strongest[0].ContrastDb > 8 ? 0 : 52,
                report.Strongest.Count > 0 ? "Survey complete" : "No spectrum data was received.");
        }
        catch (Exception exception)
        {
            return Finish(report, 59, exception.ToString());
        }
        finally
        {
            try { source?.Dispose(); } catch { }
        }
    }

    private static void SurveyCenter(SdrplaySampleSource source, long center, string band, SurveyReport report, ref float[]? latestSpectrum)
    {
        Interlocked.Exchange(ref latestSpectrum, null);
        source.CenterFrequency = center;
        Thread.Sleep(250); // Let the tuner and AGC settle.
        var deadline = Environment.TickCount64 + 500;
        float[]? spectrum = null;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(50);
            var captured = Interlocked.Exchange(ref latestSpectrum, null);
            if (captured is not null) spectrum = (float[])captured.Clone();
        }
        if (spectrum is null) return;

        var start = spectrum.Length / 20;
        var end = spectrum.Length - start;
        var centerBin = spectrum.Length / 2;
        var dcGuard = Math.Max(3, 25_000 * spectrum.Length / source.SampleRate);
        var usable = new List<(int Bin, float Db)>(end - start);
        for (var bin = start; bin < end; bin++)
            if (Math.Abs(bin - centerBin) > dcGuard) usable.Add((bin, spectrum[bin]));
        var ordered = usable.Select(item => item.Db).Order().ToArray();
        var median = ordered[ordered.Length / 2];
        var peak = usable.MaxBy(item => item.Db);
        var frequency = center + (long)((peak.Bin - centerBin) * (double)source.SampleRate / spectrum.Length);
        report.Bands.Add(new BandResult
        {
            Band = band,
            CenterHz = center,
            PeakFrequencyHz = frequency,
            PeakDb = peak.Db,
            MedianDb = median,
            ContrastDb = peak.Db - median
        });
    }

    private static int Finish(SurveyReport report, int exitCode, string message)
    {
        report.FinishedAt = DateTimeOffset.Now;
        report.ExitCode = exitCode;
        report.Message = message;
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "rf-survey-latest.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        return exitCode;
    }

    private sealed class SurveyReport
    {
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public string Device { get; set; } = string.Empty;
        public int ExitCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<BandResult> Bands { get; set; } = [];
        public List<BandResult> Strongest { get; set; } = [];
    }

    private sealed class BandResult
    {
        public string Band { get; set; } = string.Empty;
        public long CenterHz { get; set; }
        public long PeakFrequencyHz { get; set; }
        public float PeakDb { get; set; }
        public float MedianDb { get; set; }
        public float ContrastDb { get; set; }
    }
}
