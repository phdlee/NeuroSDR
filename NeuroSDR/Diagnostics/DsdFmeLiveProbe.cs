using System.Diagnostics;
using System.Text;
using NeuroSDR.Audio;
using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;
using NeuroSDR.Plugins.DigitalVoice;

namespace NeuroSDR.Diagnostics;

/// <summary>
/// Headless RSP → NFM → DSD-FME PCM live probe for interactive PTT tests.
/// Writes a status file the agent can poll while the user keys the radio.
/// </summary>
internal static class DsdFmeLiveProbe
{
    public static int Run(long frequencyHz, int seconds, int sampleRate, bool invert, int gainPercent,
        int bandwidthHz, string statusPath)
    {
        ISampleSource? source = null;
        DsdFmeSession? dsd = null;
        WaveOutPlayer? speaker = null;
        var log = new StringBuilder();
        void Line(string text)
        {
            Console.WriteLine(text);
            log.AppendLine(text);
            try { File.WriteAllText(statusPath, log.ToString()); } catch { }
        }

        try
        {
            if (!SdrplaySampleSource.TryCreate(out var rsp, out var status) || rsp is null)
            {
                Line("FAIL: " + status);
                return 81;
            }
            source = rsp;
            if (source is IConfigurableSampleRateSource configurable)
            {
                if (!configurable.SupportedSampleRates.Contains(sampleRate))
                    sampleRate = configurable.SupportedSampleRates.Contains(2_000_000) ? 2_000_000
                        : configurable.SupportedSampleRates[0];
                configurable.ConfiguredSampleRate = sampleRate;
            }
            source.CenterFrequency = frequencyHz;
            if (source is IGainControlledSampleSource gain) gain.GainPercent = Math.Clamp(gainPercent, 0, 100);
            if (source is IHardwareAgcSampleSource agc) agc.HardwareAgcEnabled = true;

            var demod = new AudioDemodulator
            {
                Mode = RadioMode.DMR,
                Bandwidth = Math.Clamp(bandwidthHz, 4_000, 25_000),
                FrequencyOffset = 0
            };

            DigitalVoicePlayback.Clear();
            DigitalVoicePlayback.OwnedOutputIndex = 0;
            dsd = new DsdFmeSession();
            if (!dsd.EnsureStarted(pcmMode: true, invertedDmr: invert, protocol: 1))
            {
                Line("FAIL: " + dsd.Status);
                return 82;
            }

            speaker = new WaveOutPlayer(AudioDemodulator.AudioSampleRate, -1, maximumPendingBuffers: 64,
                prerollBuffers: 4, restartOnStarvation: false)
            {
                VolumePercent = 70
            };
            var playBuf = new float[4_800];

            Line($"DSD-FME live probe · {source.Name} @ {frequencyHz / 1e6:0.000} MHz DMR/{demod.Bandwidth} Hz · SR={source.SampleRate} · inv={invert}");
            Line($"status file: {statusPath}");
            Line("Decoded voice plays on the default Windows speaker.");
            Line(">>> PTT now — say 'test' for ~5 seconds when ready <<<");

            var pcmBuf = new short[4_800];
            float peak = 1e-3f;
            long afSamples = 0, pushed = 0;
            var lastStatus = Stopwatch.StartNew();

            void OnSamples(Complex32[] iq)
            {
                var af = demod.Process(iq, source.SampleRate);
                if (af.Length == 0) return;
                afSamples += af.Length;
                foreach (var s in af)
                {
                    var mag = Math.Abs(s);
                    if (mag > peak) peak = mag;
                    else peak = 0.995f * peak + 0.005f * mag;
                }
                var scale = DigitalModeEngine.DiscriminatorFeedGain;

                var offset = 0;
                while (offset < af.Length)
                {
                    var take = Math.Min(pcmBuf.Length, af.Length - offset);
                    for (var i = 0; i < take; i++)
                    {
                        var v = Math.Clamp(af[offset + i] * scale, -1f, 1f);
                        pcmBuf[i] = (short)Math.Clamp((int)Math.Round(v * 32767f), short.MinValue, short.MaxValue);
                    }
                    dsd.PushPcm16(pcmBuf.AsSpan(0, take));
                    pushed += take;
                    offset += take;
                }
                dsd.PumpDecodedAudio();
                if (playBuf.Length < af.Length) Array.Resize(ref playBuf, af.Length);
                DigitalVoicePlayback.Fill48k(playBuf.AsSpan(0, af.Length));
                speaker.Write(playBuf.AsSpan(0, af.Length));

                if (lastStatus.ElapsedMilliseconds >= 500)
                {
                    lastStatus.Restart();
                    var st = dsd.TryGetStatus();
                    var line = st is { } s
                        ? $"t={afSamples / 48_000d:0.0}s peak={peak:0.000} gain={scale:0.00}x " +
                          $"sync={s.SyncType} tg={s.LastTg}/{s.LastTgR} src={s.LastSrc}/{s.LastSrcR} cc={s.ColorCode} " +
                          $"in={s.InQueued} nativeOut={s.OutQueued} voice48k={DigitalVoicePlayback.BufferedSamples} inv={(invert ? 1 : 0)}"
                        : $"t={afSamples / 48_000d:0.0}s peak={peak:0.000} (no status)";
                    var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    Console.WriteLine(stamp + " " + line);
                    try { File.AppendAllText(statusPath, stamp + " " + line + Environment.NewLine); } catch { }
                }
            }

            try { File.WriteAllText(statusPath, log.ToString()); } catch { }

            source.SamplesAvailable += OnSamples;
            source.Start();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
                Thread.Sleep(200);
            source.SamplesAvailable -= OnSamples;
            source.Stop();

            var final = dsd.TryGetStatus();
            Line(final is { } f
                ? $"DONE sync={f.SyncType} nativeOut={f.OutQueued} voice48k={DigitalVoicePlayback.BufferedSamples} af={afSamples / 48_000d:0.0}s pushed={pushed}"
                : "DONE (no final status)");
            return final is { OutQueued: > 0 } || DigitalVoicePlayback.BufferedSamples > 0 ? 0 : 84;
        }
        catch (Exception exception)
        {
            Line("ERROR: " + exception);
            return 89;
        }
        finally
        {
            try { source?.Stop(); } catch { }
            try { dsd?.Dispose(); } catch { }
            try { speaker?.Dispose(); } catch { }
            try { source?.Dispose(); } catch { }
            DigitalVoicePlayback.OwnedOutputIndex = -1;
            DigitalVoicePlayback.Clear();
        }
    }
}
