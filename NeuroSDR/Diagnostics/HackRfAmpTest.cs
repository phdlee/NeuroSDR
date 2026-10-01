using System.Text.Json;
using NeuroSDR.Core;
using NeuroSDR.Hardware;

namespace NeuroSDR.Diagnostics;

internal static class HackRfAmpTest
{
    public static int Run(long frequency, int sampleRate = 2_000_000)
    {
        if (!HackRfSampleSource.TryCreate(out var source, out var status) || source is null)
        {
            Console.WriteLine(status);
            return 91;
        }

        var report = new Report { Frequency = frequency, SampleRate = sampleRate, Status = status };
        var accumulator = new Accumulator();
        try
        {
            source.ConfiguredSampleRate = source.SupportedSampleRates.Contains(sampleRate) ? sampleRate : source.SupportedSampleRates.Max();
            source.CenterFrequency = frequency;
            source.GainPercent = 60;
            source.SamplesAvailable += accumulator.Add;
            source.RfAmplifierEnabled = false;
            source.Start();

            foreach (var enabled in new[] { false, true, false, true })
            {
                source.RfAmplifierEnabled = enabled;
                Thread.Sleep(700);
                accumulator.Reset();
                Thread.Sleep(1_500);
                var measurement = accumulator.Snapshot(enabled);
                report.Measurements.Add(measurement);
                Console.WriteLine($"AMP {(enabled ? "ON " : "OFF")} {measurement.DbFs:0.00} dBFS, peak {measurement.PeakDbFs:0.00} dBFS, {measurement.Samples:N0} samples");
            }
            report.OffAverageDbFs = report.Measurements.Where(value => !value.Enabled).Average(value => value.DbFs);
            report.OnAverageDbFs = report.Measurements.Where(value => value.Enabled).Average(value => value.DbFs);
            report.OnMinusOffDb = report.OnAverageDbFs - report.OffAverageDbFs;
            report.Message = report.OnMinusOffDb < -3
                ? "RF level falls when AMP is enabled; the external RF amplifier path may be damaged or unsuitable at this frequency."
                : report.OnMinusOffDb > 3 ? "RF level rises when AMP is enabled." : "No material AMP level change.";
            return 0;
        }
        catch (Exception exception)
        {
            report.Message = exception.GetBaseException().ToString();
            return 92;
        }
        finally
        {
            try { source.Stop(); } catch { }
            source.Dispose();
            report.FinishedAt = DateTimeOffset.Now;
            var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "hackrf-amp-comparison.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"AMP delta {report.OnMinusOffDb:0.00} dB; {report.Message}");
            Console.WriteLine(path);
        }
    }

    private sealed class Accumulator
    {
        private readonly object _sync = new();
        private double _sumPower;
        private float _peak;
        private long _samples;

        public void Add(Complex32[] samples)
        {
            if (samples.Length == 0) return;
            double sumI = 0, sumQ = 0;
            var count = 0;
            for (var index = 0; index < samples.Length; index += 16)
            {
                sumI += samples[index].I;
                sumQ += samples[index].Q;
                count++;
            }
            var meanI = sumI / count;
            var meanQ = sumQ / count;
            double power = 0;
            float peak = 0;
            for (var index = 0; index < samples.Length; index += 16)
            {
                var i = samples[index].I - meanI;
                var q = samples[index].Q - meanQ;
                var magnitudeSquared = (float)(i * i + q * q);
                power += magnitudeSquared;
                peak = Math.Max(peak, magnitudeSquared);
            }
            lock (_sync)
            {
                _sumPower += power;
                _samples += count;
                _peak = Math.Max(_peak, peak);
            }
        }

        public void Reset()
        {
            lock (_sync) { _sumPower = 0; _samples = 0; _peak = 0; }
        }

        public Measurement Snapshot(bool enabled)
        {
            lock (_sync)
            {
                var meanPower = _samples == 0 ? 0 : _sumPower / _samples;
                return new Measurement(enabled, _samples, 10 * Math.Log10(meanPower + 1e-20),
                    10 * Math.Log10(_peak + 1e-20));
            }
        }
    }

    private sealed class Report
    {
        public DateTimeOffset FinishedAt { get; set; }
        public long Frequency { get; set; }
        public int SampleRate { get; set; }
        public string Status { get; set; } = string.Empty;
        public List<Measurement> Measurements { get; set; } = [];
        public double OffAverageDbFs { get; set; }
        public double OnAverageDbFs { get; set; }
        public double OnMinusOffDb { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    private sealed record Measurement(bool Enabled, long Samples, double DbFs, double PeakDbFs);
}
