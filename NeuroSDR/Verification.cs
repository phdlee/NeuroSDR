using NeuroSDR.Core;
using NeuroSDR.Controls;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;
using NeuroSDR.Audio;
using NeuroSDR.Recording;
using NeuroSDR.Memory;
using NeuroSDR.Plugins;
using NeuroSDR.Plugins.DigitalVoice;
using NeuroSDR.Plugins.Caption;
using NeuroSDR.Settings;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WebSdr;

namespace NeuroSDR;

internal static class Verification
{
    public static async Task<int> RunWebSourceAsync(string kindText, string url)
    {
        var kind = kindText.Trim().ToLowerInvariant() switch
        {
            "websdr" or "web" => WebRadioKind.WebSdr,
            "kiwisdr" or "kiwi" => WebRadioKind.KiwiSdr,
            "openwebrx" or "owrx" => WebRadioKind.OpenWebRx,
            _ => (WebRadioKind?)null
        };
        if (kind is null) return 80;
        using var source = new WebRadioSampleSource(kind.Value) { ServerUrl = url };
        source.CenterFrequency = kind == WebRadioKind.OpenWebRx ? 27_555_000 : 7_074_000;
        long audioSamples = 0, spectrumRows = 0;
        source.AudioSamplesAvailable += (samples, _) => Interlocked.Add(ref audioSamples, samples.Length);
        source.RemoteSpectrumAvailable += _ => Interlocked.Increment(ref spectrumRows);
        try
        {
            // Connect with a short timeout token — session must survive after it expires
            // (regression: linking receive loops to this token killed audio ~5s in).
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await source.ConnectAsync(connectTimeout.Token);
            await source.ApplyReceiverAsync(RadioMode.USB, 2_700);
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (Interlocked.Read(ref audioSamples) < source.AudioSampleRate) return 81;
            if (Interlocked.Read(ref spectrumRows) < 2) return 82;

            // Soak past the old 5s death window with the connect timeout already expired.
            var beforeSoak = Interlocked.Read(ref audioSamples);
            var marks = new long[9];
            for (var second = 0; second < 8; second++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                marks[second] = Interlocked.Read(ref audioSamples);
            }
            var soaked = marks[7] - beforeSoak;
            if (soaked < source.AudioSampleRate * 4)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "neurosdr-web-source-test.log"),
                        $"kind={kindText}\r\nurl={url}\r\nfail=89 soak\r\n" +
                        $"rate={source.AudioSampleRate}\r\nbefore={beforeSoak}\r\nsoaked={soaked}\r\n" +
                        $"marks={string.Join(",", marks)}\r\n" +
                        $"connected={source.IsConnected} running={source.IsRunning}\r\n" +
                        $"status={source.ConnectionStatus}\r\nageMs={source.LastDeliveryAgeMilliseconds}\r\n");
                }
                catch { }
                return 89; // must keep streaming ~4s+ of audio
            }
            if (!source.IsConnected || !source.IsRunning) return 83;

            var maximumSpan = source.MaximumSpectrumSpan;
            var rowsBeforeZoom = Interlocked.Read(ref spectrumRows);
            var viewport = await source.SetSpectrumViewportAsync(source.CenterFrequency,
                Math.Max(5_000, maximumSpan / 8));
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (kind == WebRadioKind.OpenWebRx)
            {
                if (viewport.ServerApplied || source.SupportsServerSpectrumViewport) return 85;
            }
            else
            {
                if (!viewport.ServerApplied || !source.SupportsServerSpectrumViewport || viewport.SpanHz >= maximumSpan) return 86;
                if (source.SampleRate >= maximumSpan) return 87;
            }
            if (Interlocked.Read(ref spectrumRows) <= rowsBeforeZoom) return 88;
            return source.IsConnected ? 0 : 83;
        }
        catch (Exception exception)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "neurosdr-web-source-test.log"), $"kind={kindText}\r\nurl={url}\r\n{exception}"); } catch { }
            return 84;
        }
    }

    /// <summary>
    /// Long soak for remote audio+spectrum. Detects silent audio stalls while waterfall
    /// keeps flowing (classic WebSDR ~~stream vs ~~waterfall split).
    /// Args: kind|url|seconds  (seconds clamped 30..600, default 120).
    /// </summary>
    public static async Task<int> RunWebSourceLongSoakAsync(string kindText, string url, int seconds)
    {
        var kind = kindText.Trim().ToLowerInvariant() switch
        {
            "websdr" or "web" => WebRadioKind.WebSdr,
            "kiwisdr" or "kiwi" => WebRadioKind.KiwiSdr,
            "openwebrx" or "owrx" => WebRadioKind.OpenWebRx,
            _ => (WebRadioKind?)null
        };
        if (kind is null) return 80;
        seconds = Math.Clamp(seconds, 30, 600);
        using var source = new WebRadioSampleSource(kind.Value) { ServerUrl = url };
        source.CenterFrequency = kind == WebRadioKind.OpenWebRx ? 27_555_000 : 7_074_000;
        long audioSamples = 0, spectrumRows = 0;
        var intensitySum = 0L;
        var intensityCount = 0L;
        var intensityMax = 0;
        var intensityMin = 255;
        source.AudioSamplesAvailable += (samples, _) => Interlocked.Add(ref audioSamples, samples.Length);
        source.RemoteSpectrumAvailable += frame =>
        {
            Interlocked.Increment(ref spectrumRows);
            var row = frame.Intensities;
            for (var i = 0; i < row.Length; i++)
            {
                var v = row[i];
                Interlocked.Add(ref intensitySum, v);
                Interlocked.Increment(ref intensityCount);
                int prevMax;
                do { prevMax = Volatile.Read(ref intensityMax); } while (v > prevMax && Interlocked.CompareExchange(ref intensityMax, v, prevMax) != prevMax);
                int prevMin;
                do { prevMin = Volatile.Read(ref intensityMin); } while (v < prevMin && Interlocked.CompareExchange(ref intensityMin, v, prevMin) != prevMin);
            }
        };

        var gapEvents = 0;
        var maxGapMs = 0L;
        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await source.ConnectAsync(connectTimeout.Token);
            await source.ApplyReceiverAsync(RadioMode.USB, 2_700);
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (Interlocked.Read(ref audioSamples) < source.AudioSampleRate) return 81;
            if (Interlocked.Read(ref spectrumRows) < 2) return 82;

            var lastAudio = Interlocked.Read(ref audioSamples);
            var lastTick = Environment.TickCount64;
            var startAudio = lastAudio;
            var startRows = Interlocked.Read(ref spectrumRows);
            for (var second = 0; second < seconds; second++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                var audio = Interlocked.Read(ref audioSamples);
                var now = Environment.TickCount64;
                if (audio == lastAudio)
                {
                    var gap = now - lastTick;
                    if (gap > maxGapMs) maxGapMs = gap;
                    if (gap >= 5_000) gapEvents++;
                }
                else
                {
                    lastAudio = audio;
                    lastTick = now;
                }
                if ((second + 1) % 15 == 0)
                {
                    Console.WriteLine(
                        $"soak t={second + 1}s audio={audio - startAudio} rows={Interlocked.Read(ref spectrumRows) - startRows} " +
                        $"ageMs={source.LastDeliveryAgeMilliseconds} status={source.ConnectionStatus}");
                }
            }

            var totalAudio = Interlocked.Read(ref audioSamples) - startAudio;
            var totalRows = Interlocked.Read(ref spectrumRows) - startRows;
            var meanIntensity = intensityCount > 0 ? intensitySum / (double)intensityCount : -1;
            var expectedMinAudio = (long)source.AudioSampleRate * Math.Max(20, seconds * 6 / 10);
            var log =
                $"kind={kindText}\r\nurl={url}\r\nseconds={seconds}\r\n" +
                $"audioDelta={totalAudio}\r\nrowsDelta={totalRows}\r\n" +
                $"gapEvents5s={gapEvents}\r\nmaxGapMs={maxGapMs}\r\n" +
                $"intensityMean={meanIntensity:F1} min={intensityMin} max={intensityMax}\r\n" +
                $"rate={source.AudioSampleRate}\r\nstatus={source.ConnectionStatus}\r\n" +
                $"connected={source.IsConnected} running={source.IsRunning}\r\n";
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "neurosdr-web-soak.log"), log); } catch { }
            Console.Write(log);

            // After normalize, mean should not sit near yellow-limit (~200+) for WebSDR.
            if (kind == WebRadioKind.WebSdr && meanIntensity > 190) return 91;
            if (totalRows < seconds / 2) return 88;
            if (totalAudio < expectedMinAudio) return 89;
            // Allow brief reconnect gaps; fail if audio stayed dead >15s without recovery.
            if (maxGapMs >= 15_000) return 90;
            return source.IsConnected ? 0 : 83;
        }
        catch (Exception exception)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "neurosdr-web-soak.log"), $"kind={kindText}\r\nurl={url}\r\n{exception}"); } catch { }
            return 84;
        }
    }

    public static int RunCaptionUi()
    {
        try
        {
            return VerifyCaptionGateAndAfFilterDefaults() && VerifyRemoteSdrBands() ? 0 : 122;
        }
        catch
        {
            return 99;
        }
    }

    public static int Run()
    {
        try
        {
            if (!RemoteViewportGate.IsStaleFrame(7_000_000, 8_000_000, 2_000_000)) return 120;
            if (RemoteViewportGate.IsStaleFrame(8_000_000, 8_010_000, 2_000_000)) return 120;
            if (!RemoteViewportGate.IsStaleFrame(144_090_000, 144_090_000, 27_500, 2_000_000)) return 120;
            if (NeuroSDR.Plugins.Caption.GroqWhisperClient.ExtractText("{\"text\":\"hello radio\"}") != "hello radio") return 121;
            var wrap = new System.Text.StringBuilder("Hello world. Next sentence");
            NeuroSDR.Plugins.Caption.CaptionText.BreakSentences(wrap);
            if (!wrap.ToString().Contains("world.") || !wrap.ToString().Contains("Next")) return 121;
            if (NeuroSDR.Plugins.Caption.CaptionText.SanitizeStt("Thanks for watching") != "") return 121;
            if (NeuroSDR.Plugins.Caption.CaptionText.SanitizeStt(new string('い', 80)) != "") return 121;
            var goods = string.Join(" ", Enumerable.Repeat("nope", 40));
            if (NeuroSDR.Plugins.Caption.CaptionText.SanitizeStt(goods).Length > 8) return 121;
            if (!NeuroSDR.Plugins.Caption.CaptionText.SameCaption("hello", "hello")) return 121;
            var captionWav = AfAudioFileWriter.ToWavBytes(8_000, new short[8_000]);
            if (captionWav.Length != 44 + 16_000 || Encoding.ASCII.GetString(captionWav, 0, 4) != "RIFF") return 121;
            if (!VerifyCaptionGateAndAfFilterDefaults()) return 122;
            if (!VerifyRemoteSdrBands()) return 123;
            var presets = NeuroSDRPresetCatalog.All;
            if (!File.Exists(NeuroSDRPresetCatalog.FilePath) ||
                presets.Count(item => item.Group.Equals("WeatherFax", StringComparison.OrdinalIgnoreCase)) < 3 ||
                presets.Count(item => item.Group.Equals("FT8", StringComparison.OrdinalIgnoreCase)) < 10 ||
                presets.Count(item => item.Group.Equals("FT4", StringComparison.OrdinalIgnoreCase)) < 8 ||
                !presets.Any(item => item.Group == "FT8" && item.FrequencyHz == 14_074_000 && item.Mode == RadioMode.USB) ||
                !presets.Any(item => item.Group == "WeatherFax" && item.FrequencyHz == 7_433_500 && item.Mode == RadioMode.USB) ||
                presets.Count(item => item.Group.Equals("ADS-B", StringComparison.OrdinalIgnoreCase)) < 3 ||
                !presets.Any(item => item.Group == "ADS-B" && item.FrequencyHz == 1_090_000_000 && item.Mode == RadioMode.RAW) ||
                presets.Count(item => item.Group.Equals("DMR", StringComparison.OrdinalIgnoreCase)) < 3)
                return 93;
            var dispatcherResult = VerifySampleDispatcher();
            if (dispatcherResult != 0) return dispatcherResult;
            if (!VerifyIqWave()) return 8;
            if (!VerifyMemoryChannels()) return 6;
            if (!VerifyAudioPostProcessor()) return 5;
            if (!VerifyAfSpectrum()) return 4;
            if (!VerifyCtcssDecoder()) return 115;
            if (!VerifyDcsDecoder()) return 116;
            if (!VerifyDigitalVoice()) return 114;
            var settingsJson = JsonSerializer.Serialize(new AppSettings
            {
                WindowWidth = 1440, SourceName = "SDRplay", Mode = RadioMode.USB,
                SstvBackend = "hq", RttyBaud = 45.45, WeatherFaxLpm = 60,
                SingleActiveAfPlugin = false,
                SubVfos = [new SubVfoSettings { Id = "sub-test", Name = "SUB 1", Frequency = 14_074_000, Mode = RadioMode.USB, Bandwidth = 2_700, OutputChannel = 2 }],
                AfPluginVfoRoutes = new Dictionary<string, string> { ["builtin.af.ftx"] = "sub-test" },
                Audio2 = new AudioChannelSettings { Enabled = true, DeviceId = 1, Volume = 37, SquelchEnabled = true, SquelchThreshold = -82 }
            }, AppSettingsJsonContext.Default.AppSettings);
            var settingsRoundTrip = JsonSerializer.Deserialize(settingsJson, AppSettingsJsonContext.Default.AppSettings);
            if (settingsRoundTrip?.WindowWidth != 1440 || settingsRoundTrip.Mode != RadioMode.USB || settingsRoundTrip.Audio2.Volume != 37 ||
                settingsRoundTrip.SstvBackend != "hq" || Math.Abs(settingsRoundTrip.RttyBaud - 45.45) > .001 ||
                settingsRoundTrip.WeatherFaxLpm != 60 || settingsRoundTrip.SingleActiveAfPlugin ||
                settingsRoundTrip.SubVfos.Count != 1 || settingsRoundTrip.SubVfos[0].Frequency != 14_074_000 ||
                settingsRoundTrip.AfPluginVfoRoutes.GetValueOrDefault("builtin.af.ftx") != "sub-test") return 50;
            var cleanedVfoState = AppSettingsStore.MigrateForVerification(new AppSettings
            {
                SettingsVersion = 7,
                SubVfos =
                [
                    new SubVfoSettings { Id = "stale-1", Name = "SUB 1" },
                    new SubVfoSettings { Id = "stale-2", Name = "SUB 2" }
                ],
                AfPluginVfoRoutes = new Dictionary<string, string>
                {
                    ["builtin.af.ftx"] = "stale-1",
                    ["builtin.af.cw"] = "stale-2"
                }
            });
            if (cleanedVfoState.SettingsVersion != 23 || cleanedVfoState.SubVfos.Count != 0 ||
                cleanedVfoState.AfPluginInstances.Count != 8 ||
                cleanedVfoState.AfPluginInstances.Any(instance => instance.VfoId != "main") ||
                cleanedVfoState.AfPluginVfoRoutes.Values.Any(vfoId => vfoId != "main") ||
                cleanedVfoState.AfPluginVfoRoutes.ContainsKey("builtin.af.ftx") ||
                cleanedVfoState.AfPluginVfoRoutes.ContainsKey("builtin.af.cw") ||
                !cleanedVfoState.AfPluginInstances.Any(instance =>
                    instance.PluginId.Equals("builtin.af.kiwiwwv", StringComparison.OrdinalIgnoreCase))) return 66;
            if (RadioLimits.ToDeviceFrequency(7_074_000, 125) != 7_074_125 ||
                RadioLimits.ToLogicalFrequency(7_074_125, 125) != 7_074_000 ||
                RadioLimits.ToDeviceFrequency(7_074_000, -125) != 7_073_875) return 65;
            var restartState = RestoredRadioState.From(new AppSettings
            {
                TunedFrequency = 7_074_000,
                RfCenterFrequency = 7_100_000,
                ViewCenterFrequency = 7_074_000,
                ViewBandwidth = 31_300,
                Mode = RadioMode.USB,
                FilterBandwidth = 2_700,
                CwLowerSide = false
            }, 2_000_000);
            var restartDemodulator = new AudioDemodulator();
            restartState.ApplyTo(restartDemodulator);
            if (restartState.TunedFrequency != 7_074_000 || restartState.RfCenterFrequency != 7_100_000 ||
                restartDemodulator.Mode != RadioMode.USB || restartDemodulator.Bandwidth != 2_700 ||
                restartDemodulator.FrequencyOffset != -26_000) return 63;
            if (WaveOutPlayer.EnumerateDevices().Count == 0) return 51;
            using (var digital = new DigitalFrequencyControl { Size = new Size(300, 45), Frequency = 109_999_000 })
            {
                long changed = 0;
                digital.FrequencyChanged += frequency => changed = frequency;
                digital.ChangeDigitForVerification(5, 1);
                if (changed != 110_000_000) return 52;
                digital.ChangeDigitForVerification(5, -1);
                if (changed != 109_999_000) return 53;
                digital.ClickForVerification(20, digital.Height / 2);
                if (digital.Frequency != 109_999_000 || digital.SelectedDigitForVerification != 0) return 55;
                digital.WheelForVerification(175, 120);
                if (digital.Frequency != 110_000_000 || digital.SelectedDigitForVerification != 5) return 56;
                digital.Frequency = 8_999_999_999;
                if (digital.Frequency != 8_999_999_990) return 57;
                digital.ChangeDigitForVerification(8, 1);
                if (digital.Frequency != 9_000_000_000) return 58;
                digital.Frequency = 145_678_923;
                digital.ResetLowerDigitsForVerification(6);
                if (digital.Frequency != 145_678_000) return 66;
                digital.Frequency = 1_234_567_890;
                digital.ResetLowerDigitsForVerification(7);
                if (digital.Frequency != 1_234_567_000) return 67;
            }
            if (SpectrumPipeline.SelectFftSize(2_000_000, 31_300) < 65_536 ||
                SpectrumPipeline.SelectFftSize(2_000_000, 5_000) != 131_072) return 7;
            if (System.Runtime.InteropServices.Marshal.SizeOf<HackRfSampleSource.Transfer>() != 40) return 10;
            if (SdrplaySampleSource.GainCompensationForVerification(2_000_000) != 0 ||
                SdrplaySampleSource.GainCompensationForVerification(5_000_000) != 8 ||
                SdrplaySampleSource.GainCompensationForVerification(8_000_000) != 12 ||
                SdrplaySampleSource.GainCompensationForVerification(10_000_000) != 12) return 68;
            using var source = new SyntheticSampleSource();
            if (source.SampleRate != 2_048_000 || source.Name.Length == 0) return 11;

            var processor = new SpectrumProcessor(1024);
            var tone = new Complex32[1024];
            const int expectedBin = 173;
            for (var n = 0; n < tone.Length; n++)
            {
                var phase = 2 * Math.PI * expectedBin * n / tone.Length;
                tone[n] = new Complex32((float)Math.Cos(phase), (float)Math.Sin(phase));
            }
            var spectrum = processor.Process(tone);
            if (spectrum is null) return 12;
            var peak = Array.IndexOf(spectrum, spectrum.Max());
            if (Math.Abs(peak - (expectedBin + tone.Length / 2)) > 1) return 13;

            var demodulator = new AudioDemodulator { Mode = RadioMode.NFM, Bandwidth = 12_500 };
            var audio = demodulator.Process(tone, 2_048_000);
            if (audio.Length == 0 || audio.Any(value => !float.IsFinite(value))) return 15;
            if (!VerifyWideCaptureAliasRejection()) return 72;

            const int offsetSampleRate = 2_000_000;
            const int offsetHz = 250_000;
            var modulated = new Complex32[offsetSampleRate / 10];
            for (var n = 0; n < modulated.Length; n++)
            {
                var carrier = 2 * Math.PI * offsetHz * n / offsetSampleRate;
                var amplitude = .65 + .3 * Math.Sin(2 * Math.PI * 1_000 * n / offsetSampleRate);
                modulated[n] = new Complex32((float)(amplitude * Math.Cos(carrier)), (float)(amplitude * Math.Sin(carrier)));
            }
            var offsetDemodulator = new AudioDemodulator { Mode = RadioMode.AM, Bandwidth = 10_000, FrequencyOffset = offsetHz };
            var offsetAudio = offsetDemodulator.Process(modulated, offsetSampleRate);
            var offsetRms = Math.Sqrt(offsetAudio.Skip(offsetAudio.Length / 4).Select(value => value * value).Average());
            if (offsetAudio.Length < 4_000 || offsetRms < .02 || offsetAudio.Any(value => !float.IsFinite(value))) return 16;
            if (!VerifyIndependentVfos()) return 101;

            var sidebandInput = CreateComplexTones(offsetSampleRate, .2, [(1_000, .55f), (-1_700, .55f)]);
            var usbAudio = new AudioDemodulator { Mode = RadioMode.USB, Bandwidth = 2_700 }.Process(sidebandInput, offsetSampleRate);
            var usbWanted = MeasureTone(usbAudio, AudioDemodulator.AudioSampleRate, 1_000);
            var usbRejected = MeasureTone(usbAudio, AudioDemodulator.AudioSampleRate, 1_700);
            if (usbWanted < .02 || usbWanted / Math.Max(usbRejected, 1e-9) < 8) return 17;

            var lsbInput = CreateComplexTones(offsetSampleRate, .2, [(-1_500, .55f), (900, .55f)]);
            var lsbAudio = new AudioDemodulator { Mode = RadioMode.LSB, Bandwidth = 2_700 }.Process(lsbInput, offsetSampleRate);
            var lsbWanted = MeasureTone(lsbAudio, AudioDemodulator.AudioSampleRate, 1_500);
            var lsbRejected = MeasureTone(lsbAudio, AudioDemodulator.AudioSampleRate, 900);
            if (lsbWanted < .02 || lsbWanted / Math.Max(lsbRejected, 1e-9) < 8) return 18;

            var cwInput = CreateComplexTones(offsetSampleRate, .15, [(0, .5f)]);
            var cwAudio = new AudioDemodulator { Mode = RadioMode.CW, Bandwidth = 500 }.Process(cwInput, offsetSampleRate);
            if (MeasureTone(cwAudio, AudioDemodulator.AudioSampleRate, 700) < .05) return 19;

            var samInput = new Complex32[offsetSampleRate / 5];
            for (var n = 0; n < samInput.Length; n++)
            {
                var carrier = 2 * Math.PI * 450 * n / offsetSampleRate;
                var amplitude = .65 + .25 * Math.Sin(2 * Math.PI * 1_000 * n / offsetSampleRate);
                samInput[n] = new Complex32((float)(amplitude * Math.Cos(carrier)), (float)(amplitude * Math.Sin(carrier)));
            }
            var samAudio = new AudioDemodulator { Mode = RadioMode.SAM, Bandwidth = 10_000 }.Process(samInput, offsetSampleRate);
            if (MeasureTone(samAudio, AudioDemodulator.AudioSampleRate, 1_000) < .05) return 20;

            using var display = new TestSpectrumWaterfallControl { Size = new Size(640, 360) };
            display.Configure(100_000_000, 100_100_000, 2_048_000, 180_000, RadioMode.WFM, 700, 100_100_000, 256_000);
            long draggedCenter = 0, clickedFrequency = 0;
            var zoomedBandwidth = 0;
            display.CenterFrequencyChanged += center => draggedCenter = center;
            display.ViewChanged += (_, bandwidth) => zoomedBandwidth = bandwidth;
            display.TunedFrequencyChanged += frequency => clickedFrequency = frequency;
            long requestedSubFrequency = 0;
            display.SubVfoRequested += frequency => requestedSubFrequency = frequency;
            float spectrumLevel = 0, waterfallLevel = 0;
            display.DisplayLevelsChanged += (spectrumValue, waterfallValue) =>
                (spectrumLevel, waterfallLevel) = (spectrumValue, waterfallValue);
            display.SetLevelForVerification(true, 10);
            display.SetLevelForVerification(false, display.Height - 10);
            if (spectrumLevel <= 0 || waterfallLevel >= 0) return 54;
            display.DragBackground(80, 180);
            if (draggedCenter == 0 || draggedCenter >= 100_000_000) return 40;
            if (!RemoteViewportGate.IsStaleFrame(7_000_000, 8_000_000, 2_000_000)) return 120;
            if (RemoteViewportGate.IsStaleFrame(8_000_000, 8_010_000, 2_000_000)) return 120;
            display.ZoomIn(320);
            if (zoomedBandwidth <= 0 || zoomedBandwidth >= 256_000) return 41;
            display.Configure(100_000_000, 100_000_000, 125_000, 2_700, RadioMode.USB, 700,
                100_000_000, 100_000, 2_000_000);
            zoomedBandwidth = 0;
            display.ZoomOut(320);
            if (zoomedBandwidth <= 125_000) return 89;
            display.ClickBackground(20);
            if (clickedFrequency == 0) return 42;
            display.RightClickBackground(420);
            if (requestedSubFrequency == 0) return 102;
            display.Configure(7_074_000, 7_074_000, 2_000_000, 2_700, RadioMode.USB, 700, 7_074_000, 31_300);
            if (display.FilterFrequencyRange() != (7_074_000, 7_076_700)) return 44;
            display.Configure(7_074_000, 7_074_000, 2_000_000, 2_700, RadioMode.LSB, 700, 7_074_000, 31_300);
            if (display.FilterFrequencyRange() != (7_071_300, 7_074_000)) return 45;
            display.Configure(7_074_000, 7_074_000, 2_000_000, 500, RadioMode.CW, 700, 7_074_000, 31_300);
            if (display.FilterFrequencyRange() != (7_073_750, 7_074_250)) return 46;
            display.PushSpectrum(spectrum);
            display.PushSpectrum(spectrum);
            using var rendered = new Bitmap(display.Width, display.Height);
            display.DrawToBitmap(rendered, display.ClientRectangle);
            if (rendered.GetPixel(display.Width / 2, 20).ToArgb() == Color.Empty.ToArgb()) return 43;

            var pluginCatalog = new DisplayPluginCatalog();
            if (pluginCatalog.SpectrumPlugins.Count < 2 || pluginCatalog.WaterfallPlugins.Count < 2) return 47;
            var afCatalog = new AfPluginCatalog();
            if (afCatalog.Plugins.Count < 5 || afCatalog.Plugins.Any(plugin =>
                    !plugin.Info.Capabilities.HasFlag(AfPluginCapabilities.AudioInput) &&
                    !plugin.Info.Capabilities.HasFlag(AfPluginCapabilities.Display))) return 59;
            var slowModeIds = afCatalog.Plugins.Select(plugin => plugin.Info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!slowModeIds.IsSupersetOf([
                "builtin.af.sstv", "builtin.af.rtty", "builtin.af.weatherfax",
                "builtin.af.kiwinavtex", "builtin.af.kiwiwwv", "builtin.af.flrtty", "builtin.af.flcw", "builtin.af.flfax",
                "builtin.af.record", "builtin.af.dsdplus", "builtin.af.neurocaption", "builtin.af.eibi"])) return 68;
            // External AF plugins (JSON+DLL next to exe) must never be baked in as built-in.
            // Presence of any specific third-party DLL is optional; if present, contracts must hold.
            foreach (var external in afCatalog.Plugins.Where(plugin => !plugin.Info.IsBuiltIn))
            {
                using var instance = external.Create();
                if (!instance.Info.Capabilities.HasFlag(AfPluginCapabilities.AudioInput)) return 92;
                if (instance.Info.Capabilities.HasFlag(AfPluginCapabilities.Display) &&
                    instance is not IAfVisualPlugin) return 93;
            }
            var recordPlugin = afCatalog.Plugins.First(plugin => plugin.Info.Id.Equals("builtin.af.record", StringComparison.OrdinalIgnoreCase));
            if (recordPlugin.Info.Capabilities.HasFlag(AfPluginCapabilities.Display)) return 91;
            if (!NeuroSDR.Plugins.Broadcast.EibiScheduleParser.TryParseLine(
                    "3955;0000-0100;1234567;G;BBC World Service;E;Eu;w;A26", out var eibi) ||
                eibi is null || eibi.FrequencyHz != 3_955_000 || eibi.LanguageName != "English") return 118;
            if (!NeuroSDR.Plugins.Broadcast.EibiScheduleParser.IsOnAir(eibi, new DateTime(2026, 9, 18, 0, 30, 0, DateTimeKind.Utc))) return 119;
            if (!NeuroSDR.Plugins.Broadcast.EibiScheduleParser.TryParseLine(
                    "16.3;0000-2400;;IND;VTX1 Indian Navy;;SAs;v;1;;", out var vtx) ||
                vtx is null || vtx.FrequencyHz != 16_300) return 122;
            if (NeuroSDR.Plugins.Broadcast.EibiCodes.Country("IND") != "India" ||
                NeuroSDR.Plugins.Broadcast.EibiCodes.Country("NOR") != "Norway") return 122;
            if (!NeuroSDR.Plugins.Broadcast.EibiTransmitterLocations.TryLocate(eibi, out var ukLat, out _) ||
                ukLat < 50 || ukLat > 58) return 126;
            if (NeuroSDR.Plugins.Broadcast.EibiCodes.Language("-CW") != "CW" ||
                NeuroSDR.Plugins.Broadcast.EibiCodes.Language("-TS") != "Time signal" ||
                NeuroSDR.Plugins.Broadcast.EibiCodes.Language("D") != "German" ||
                NeuroSDR.Plugins.Broadcast.EibiCodes.Language("D,E") != "German, English") return 123;
            if (!NeuroSDR.Plugins.Broadcast.EibiCodes.DayMatches("Su", DayOfWeek.Sunday) ||
                NeuroSDR.Plugins.Broadcast.EibiCodes.DayMatches("Su", DayOfWeek.Friday)) return 124;
            var seasons = NeuroSDR.Plugins.Broadcast.EibiScheduleStore.SeasonIds(new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc));
            if (!seasons.Contains("a26")) return 121;
            var cache = NeuroSDR.Plugins.Broadcast.EibiScheduleStore.CachePath;
            if (File.Exists(cache))
            {
                var parsed = NeuroSDR.Plugins.Broadcast.EibiScheduleParser.Parse(File.ReadAllText(cache));
                if (parsed.Count < 1_000) return 125;
                if (!parsed.Any(row => row.FrequencyHz == 16_300 && row.Station.Contains("VTX1", StringComparison.Ordinal))) return 125;
                if (!parsed.Any(row => row.FrequencyHz == 3_955_000 && row.Station.Contains("Channel 292", StringComparison.Ordinal))) return 125;
            }
            NeuroSDR.Plugins.Kiwi.WwvTimeDecode? decoded = null;
            var wwv = new NeuroSDR.Plugins.Kiwi.WwvTimeDecoder();
            wwv.Configure(100, false);
            wwv.TimeDecoded += value => decoded = value;
            var utc = new DateTime(2026, 8, 16, 16, 35, 0, DateTimeKind.Utc);
            wwv.Process(NeuroSDR.Plugins.Kiwi.WwvTimeDecoder.SynthesizeFrame(8_000, 100, utc), 8_000);
            if (decoded is null || decoded.Value.UtcMinute != utc) return 83;
            var iqCatalog = new IqPluginCatalog();
            if (!iqCatalog.Plugins.Any(plugin => plugin.Info.Id.Equals("builtin.iq.kiwitimecode", StringComparison.OrdinalIgnoreCase)))
                return 77;
            if (!iqCatalog.Plugins.Any(plugin => plugin.Info.Id.Equals("builtin.iq.adsb", StringComparison.OrdinalIgnoreCase)))
                return 110;
            if (!iqCatalog.Plugins.Any(plugin => plugin.Info.Id.Equals("builtin.iq.lte", StringComparison.OrdinalIgnoreCase)))
                return 116;
            if (!iqCatalog.Plugins[0].Info.Capabilities.HasFlag(IqPluginCapabilities.IqInput) ||
                !iqCatalog.Plugins[0].Info.Capabilities.HasFlag(IqPluginCapabilities.IqOutput)) return 78;
            using (var iqHost = new IqPluginHost(iqCatalog, ["builtin.iq.kiwitimecode"]))
            {
                iqHost.Apply(["builtin.iq.kiwitimecode"]);
                if (!iqHost.IsEnabled("builtin.iq.kiwitimecode")) return 79;
                var iqTone = CreateComplexTones(12_000, 0.05, [(0, 0.2f)]);
                var processed = iqHost.Process(iqTone, 12_000, 60_000_000, DateTime.UtcNow);
                if (processed.Length != iqTone.Length) return 80;
            }
            if (!VerifyDump1090Adsb()) return 111;
            if (!VerifyLteCellSearch()) return 117;
            var resampler = new StreamingPcmResampler(48_000, 44_100);
            if (resampler.Process(new float[48_000]).Length != 44_100) return 69;
            var remoteResampler = new StreamingFloatResampler(12_000, AudioDemodulator.AudioSampleRate);
            var remoteResampled = remoteResampler.Process(new float[12_000]);
            if (remoteResampled.Length < 47_990 || remoteResampled.Length > 48_010) return 75;
            using (var web = new WebRadioSampleSource(WebRadioKind.WebSdr))
            using (var kiwi = new WebRadioSampleSource(WebRadioKind.KiwiSdr))
            using (var open = new WebRadioSampleSource(WebRadioKind.OpenWebRx))
            {
                if (web.Name != "Virtual WebSDR" || kiwi.Name != "Virtual KiwiSDR" || open.Name != "Virtual OpenWebRX" ||
                    web is not IRemoteAudioSampleSource || kiwi is not IRemoteAudioSampleSource || open is not IRemoteAudioSampleSource)
                    return 76;
            }
            using (var afHost = new AfPluginHost(afCatalog,
                       [new AfPluginInstanceSettings { PluginId = "builtin.af.ftx" },
                        new AfPluginInstanceSettings { PluginId = "builtin.af.cw" }]))
            {
                var ftx = new AfPluginInstanceSettings { PluginId = "builtin.af.ftx" };
                var cw = new AfPluginInstanceSettings { PluginId = "builtin.af.cw" };
                afHost.Rebuild([ftx, cw]);
                afHost.Apply([ftx], "FT8", 0, false);
                if (!afHost.IsEnabled("builtin.af.ftx") || afHost.IsEnabled("builtin.af.cw")) return 61;
                afHost.Apply([ftx, cw], "FT8", 0, false, ["builtin.af.cw"]);
                if (afHost.IsEnabled("builtin.af.ftx") || !afHost.IsEnabled("builtin.af.cw")) return 74;
            }
            var duplicateAfInstances = new[]
            {
                new AfPluginInstanceSettings { PluginId = "builtin.af.ftx", Variant = "FT8", VfoId = "main" },
                new AfPluginInstanceSettings { PluginId = "builtin.af.ftx", Variant = "FT4", VfoId = "sub-a" },
                new AfPluginInstanceSettings { PluginId = "builtin.af.ftx", Variant = "FT8", VfoId = "sub-b" },
                new AfPluginInstanceSettings { PluginId = "builtin.af.cw", VfoId = "main" },
                new AfPluginInstanceSettings { PluginId = "builtin.af.cw", VfoId = "sub-a" }
            };
            using (var multiAfHost = new AfPluginHost(afCatalog, duplicateAfInstances))
            {
                if (multiAfHost.Plugins.Count != 5 || multiAfHost.Plugins.Select(plugin => plugin.Id).Distinct().Count() != 5 ||
                    duplicateAfInstances.Any(instance => multiAfHost.PluginTypeId(instance.InstanceId) != instance.PluginId)) return 107;
                multiAfHost.Apply(duplicateAfInstances, "FT8", 0, false);
                if (duplicateAfInstances.Any(instance => !multiAfHost.IsEnabled(instance.InstanceId))) return 108;
                multiAfHost.Apply(duplicateAfInstances, "FT8", 0, false, ["builtin.af.ftx"]);
                if (duplicateAfInstances.Where(instance => instance.PluginId == "builtin.af.ftx").Any(instance => !multiAfHost.IsEnabled(instance.InstanceId)) ||
                    duplicateAfInstances.Where(instance => instance.PluginId == "builtin.af.cw").Any(instance => multiAfHost.IsEnabled(instance.InstanceId))) return 109;
            }
            if (!Ft8Codec.TryEncodeMessage("CQ K1ABC FN42", out var ftxPayload, out _) ||
                !Ft8Codec.TryDecodeMessage(ftxPayload, out var ftxText, out _) ||
                !ftxText.Contains("K1ABC", StringComparison.Ordinal)) return 60;
            using (var ftxPlugin = new FtxDecoderAfPlugin())
            {
                ftxPlugin.Configure(new Dictionary<string, string>
                {
                    ["enabled"] = "true", ["mode"] = "FT8", ["timeAdjustSeconds"] = "0", ["autoTimeAdjust"] = "true"
                });
                ftxPlugin.ApplyDtSamplesForVerification(.5f, .48f, .52f, .49f, .51f);
                if (Math.Abs(ftxPlugin.TimeAdjustForVerification - .5) > .03) return 62;
            }
            using (var cwPlugin = new CwDecoderAfPlugin())
            {
                cwPlugin.Configure(new Dictionary<string, string> { ["command"] = "reset" });
                if (cwPlugin.ResetCountForVerification != 1) return 64;
                var probeAudio = Enumerable.Range(0, 4_800)
                    .Select(index => .2f * (float)Math.Sin(2 * Math.PI * 700 * index / AudioDemodulator.AudioSampleRate)).ToArray();
                cwPlugin.Process(new AfAudioBlock(probeAudio, AudioDemodulator.AudioSampleRate, DateTime.UtcNow));
            }
            if (!VerifyFlCw()) return 112;
            using (var sstvPlugin = new SstvDecoderAfPlugin())
            {
                sstvPlugin.Configure(new Dictionary<string, string>
                {
                    ["enabled"] = "true", ["backend"] = "managed", ["autoVis"] = "true",
                    ["adaptive"] = "true", ["slant"] = "true", ["median"] = "true"
                });
                sstvPlugin.Process(new AfAudioBlock(new float[4_800], AudioDemodulator.AudioSampleRate, DateTime.UtcNow));
                sstvPlugin.Configure(new Dictionary<string, string> { ["enabled"] = "false" });
            }
            using (var rttyPlugin = new RttyDecoderAfPlugin())
            {
                rttyPlugin.Configure(new Dictionary<string, string>
                {
                    ["enabled"] = "true", ["baud"] = "45.45", ["center"] = "1500", ["deviation"] = "85",
                    ["stopBits"] = "1.5", ["autoPolarity"] = "true"
                });
                if (Math.Abs(rttyPlugin.ConfigurationForVerification.BaudRate - 45.45) > .001 ||
                    rttyPlugin.ConfigurationForVerification.CenterFrequencyHz != 1500 ||
                    rttyPlugin.ConfigurationForVerification.StopBitUnits != 1.5) return 70;
            }
            using (var weatherPlugin = new WeatherFaxDecoderAfPlugin())
            {
                weatherPlugin.Configure(new Dictionary<string, string>
                {
                    ["enabled"] = "false", ["lps"] = "1", ["calibration"] = "0.0025",
                    ["grayscale"] = "true", ["videoFilter"] = "true"
                });
                if (weatherPlugin.ConfigurationForVerification.LinesPerSecond != 1 ||
                    Math.Abs(weatherPlugin.ConfigurationForVerification.Calibration - .0025) > .00001 ||
                    !weatherPlugin.ConfigurationForVerification.VideoFilter) return 71;
            }
            var imageResult = new AfPluginResult("builtin.af.sstv", "SSTV_IMAGE", "test", DateTime.UtcNow,
                Fields: new Dictionary<string, string> { ["width"] = "2", ["height"] = "1", ["stride"] = "6" },
                BinaryData: [255, 0, 0, 0, 0, 255]);
            if (!SlowModePluginViewHelpers.TryCreateRgbBitmap(imageResult, out var slowModeBitmap)) return 72;
            using (slowModeBitmap)
                if (slowModeBitmap.GetPixel(0, 0).R < 240 || slowModeBitmap.GetPixel(1, 0).B < 240) return 73;
            display.SetRenderPlugins(
                pluginCatalog.SpectrumPlugins.First(plugin => plugin.Info.Id == "builtin.spectrum.filled").Create(),
                pluginCatalog.WaterfallPlugins.First(plugin => plugin.Info.Id == "builtin.waterfall.monochrome").Create());
            display.PushSpectrum(spectrum);
            using var alternateRendered = new Bitmap(display.Width, display.Height);
            display.DrawToBitmap(alternateRendered, display.ClientRectangle);
            var waterfallTop = Math.Max(120, display.Height * 44 / 100) + 1;
            if (alternateRendered.GetPixel(display.Width / 2, waterfallTop).ToArgb() == display.BackColor.ToArgb()) return 48;
            using var setupSource = new SyntheticSampleSource();
            using var setupForm = new PluginSetupForm(pluginCatalog, afCatalog, iqCatalog, PluginSelection.Default,
                new NeuroSDR.Settings.AppSettings(), WaveOutPlayer.EnumerateDevices(), setupSource) { Size = new Size(590, 330) };
            using var setupRendered = new Bitmap(setupForm.Width, setupForm.Height);
            setupForm.DrawToBitmap(setupRendered, setupForm.ClientRectangle);
            if (setupForm.Selection != PluginSelection.Default) return 49;
            if (!VerifyCachedVisualPluginView()) return 113;

            _ = SdrplayApiProbe.DescribeInstallation(out _);
            return 0;
        }
        catch
        {
            return 99;
        }
    }

    private static bool VerifyCachedVisualPluginView()
    {
        var catalog = new AfPluginCatalog();
        foreach (var registration in catalog.Plugins.Where(plugin => !plugin.Info.IsBuiltIn))
        {
            using var plugin = registration.Create();
            if (plugin is not IAfVisualPlugin visual) continue;
            var host = new AfPluginUiHost("verify", plugin.Info.Id, () => false, () => 0L,
                () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                _ => { }, _ => { });
            var view = visual.CreateView(host);
            using var tabs = new TabControl { Size = new Size(420, 280) };
            var page = new TabPage();
            page.Controls.Add(view);
            tabs.TabPages.Add(page);
            tabs.TabPages.Clear();
            page.Controls.Clear();
            page.Dispose();
            Control again;
            try { again = visual.CreateView(host); }
            catch (ObjectDisposedException) { return false; }
            if (again.IsDisposed) return false;
            using var rebuilt = new TabPage();
            try { rebuilt.Controls.Add(again); }
            catch (ObjectDisposedException) { return false; }
            if (!rebuilt.Controls.Contains(again)) return false;

            var disposable = visual.CreateView(host);
            using var killer = new TabPage();
            killer.Controls.Add(disposable);
            killer.Dispose();
            Control recovered;
            try { recovered = visual.CreateView(host); }
            catch (ObjectDisposedException) { return false; }
            return !recovered.IsDisposed;
        }
        return true;
    }

    public static int RunGroqWhisperLive(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("GROQ_API_KEY is empty.");
            return 122;
        }
        var pcm = new short[16_000];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 16_000.0) * 12_000);
        var wav = AfAudioFileWriter.ToWavBytes(16_000, pcm);
        try
        {
            var text = NeuroSDR.Plugins.Caption.GroqWhisperClient.TranscribeAsync(
                apiKey.Trim(), wav, "tone.wav",
                NeuroSDR.Plugins.Caption.GroqWhisperClient.DefaultModel, "en",
                CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"groq_whisper_ok bytes={wav.Length} text_len={text.Text.Length}");
            if (text.Text.Length > 0) Console.WriteLine(text.Text);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetBaseException().Message);
            return 122;
        }
    }

    public static int RunLocalWhisperLive(string endpoint)
    {
        var host = LocalWhisperClient.DefaultHost;
        var port = LocalWhisperClient.DefaultPort;
        var https = false;
        endpoint = (endpoint ?? "").Trim();
        if (endpoint.Length > 0)
        {
            try
            {
                var uri = endpoint.Contains("://", StringComparison.Ordinal)
                    ? LocalWhisperClient.BaseUri(endpoint, port, https)
                    : LocalWhisperClient.BaseUri(endpoint, port, false);
                host = uri.Host;
                port = uri.Port;
                https = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.GetBaseException().Message);
                return 123;
            }
        }

        Console.WriteLine($"whisper_server {LocalWhisperClient.BaseUri(host, port, https)}");
        try
        {
            var models = LocalWhisperClient.ListModelsAsync(host, port, https, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine($"models={models.Length}" + (models.Length > 0 ? $" first={models[0]}" : ""));
            var pcm = new short[16_000];
            for (var i = 0; i < pcm.Length; i++)
                pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / 16_000.0) * 12_000);
            var wav = AfAudioFileWriter.ToWavBytes(16_000, pcm);
            var model = models.FirstOrDefault(id =>
                id.Equals(LocalWhisperClient.DefaultModel, StringComparison.OrdinalIgnoreCase))
                ?? (models.Length > 0 ? models[0] : LocalWhisperClient.DefaultModel);
            var text = LocalWhisperClient.TranscribeAsync(
                host, port, https, null, wav, "tone.wav", model, "en", false, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine($"whisper_ok bytes={wav.Length} model={model} text_len={text.Text.Length}");
            if (text.Text.Length > 0) Console.WriteLine(text.Text);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.GetBaseException().Message);
            return 123;
        }
    }

    public static int RunCaptureRateChange()
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        Control.CheckForIllegalCrossThreadCalls = true;
        var code = 120;
        Exception? thrown = null;
        using var form = new frmNeuroSDR(false) { Opacity = 0, ShowInTaskbar = false };
        form.Shown += (_, _) =>
        {
            try { code = form.ExerciseCaptureRateChangeForVerification(); }
            catch (Exception exception)
            {
                thrown = exception;
                code = 121;
            }
            form.Close();
        };
        Application.Run(form);
        if (thrown is not null) Console.Error.WriteLine(thrown);
        Console.WriteLine($"capture-rate-change exit={code}");
        return code;
    }

    private static bool VerifyDigitalVoice()
    {
        Span<byte> golay = stackalloc byte[20];
        Span<byte> info = stackalloc byte[8];
        for (var i = 0; i < 8; i++) info[i] = (byte)((0xA5 >> (7 - i)) & 1);
        DmrFec.Golay208Encode(info, golay);
        golay[12] ^= 1;
        if (!DmrFec.Golay208Decode(golay)) return false;
        for (var i = 0; i < 8; i++)
            if (golay[i] != info[i]) return false;

        if (DsdFmeSymbolBridge.DibitToDsdSymbol(0) != 1 ||
            DsdFmeSymbolBridge.DibitToDsdSymbol(1) != 3 ||
            DsdFmeSymbolBridge.DibitToDsdSymbol(2) != -1 ||
            DsdFmeSymbolBridge.DibitToDsdSymbol(3) != -3)
            return false;

        DigitalVoiceQso? dmr = null;
        var decoder = new DigitalVoiceDecoder();
        decoder.QsoDecoded += qso => dmr = qso;
        var burst = DigitalVoiceBurstFactory.DmrBurst(DigitalVoiceBurstFactory.BsVoiceSync, slot: 1, voice: true);
        decoder.ProcessDibits(burst, DateTime.UtcNow, -70);
        if (dmr is null || dmr.Protocol != "DMR" || dmr.Sync != "BS VOICE" || dmr.Slot != 2) return false;

        DigitalVoiceQso? data = null;
        var dataDecoder = new DigitalVoiceDecoder();
        dataDecoder.QsoDecoded += qso => data = qso;
        var dataBurst = DigitalVoiceBurstFactory.DmrBurst(
            DigitalVoiceBurstFactory.BsDataSync, slot: 0, voice: false, colorCode: 7, dataType: 9);
        dataDecoder.ProcessDibits(dataBurst, DateTime.UtcNow, -65);
        if (data is null || data.ColorCode != 7 || data.CallType != "Idle" || data.Slot != 1) return false;

        DigitalVoiceQso? dstar = null;
        var dstarDecoder = new DigitalVoiceDecoder();
        dstarDecoder.QsoDecoded += qso => dstar = qso;
        dstarDecoder.ProcessDibits(DigitalVoiceBurstFactory.FromPattern(DigitalVoiceBurstFactory.DstarSync), DateTime.UtcNow);
        if (dstar is null || dstar.Protocol != "D-STAR") return false;

        DigitalVoiceQso? audioQso = null;
        var audioDecoder = new DigitalVoiceDecoder();
        audioDecoder.QsoDecoded += qso => audioQso = qso;
        audioDecoder.ProcessAudio(DigitalVoiceBurstFactory.ToDiscriminatorPcm(burst), DigitalVoiceDecoder.AudioRate, DateTime.UtcNow, -60);
        if (audioQso is null || audioQso.Protocol != "DMR") return false;

        using var plugin = new DigitalVoiceAfPlugin();
        if (plugin is not IAfVisualPlugin visual) return false;
        plugin.Configure(new Dictionary<string, string> { ["enabled"] = "true" });
        AfPluginResult? symbols = null;
        plugin.ResultAvailable += result =>
        {
            if (result.Kind == "DSD_SYMBOLS") symbols = result;
        };
        plugin.DecoderForVerification.ProcessDibits(burst, DateTime.UtcNow, -70);
        if (symbols?.BinaryData is not { Length: DigitalVoiceDecoder.BurstDibits }) return false;
        if ((sbyte)symbols.BinaryData[66] != 1 && (sbyte)symbols.BinaryData[66] != 3) return false;

        var host = new AfPluginUiHost("verify-dv", plugin.Info.Id, () => true, () => 0L,
            () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            _ => { }, _ => { });
        var view = visual.CreateView(host);
        if (view is not IAfResultView) return false;
        using var killer = new TabPage();
        killer.Controls.Add(view);
        killer.Dispose();
        var recovered = visual.CreateView(host);
        if (recovered.IsDisposed) return false;

        using var pcmPlugin = new DsdFmePcmAfPlugin();
        if (pcmPlugin is not IAfVisualPlugin pcmVisual) return false;
        pcmPlugin.Configure(new Dictionary<string, string> { ["enabled"] = "false" });
        var pcmHost = new AfPluginUiHost("verify-pcm", pcmPlugin.Info.Id, () => false, () => 0L,
            () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            _ => { }, _ => { });
        using var pcmPage = new TabPage();
        pcmPage.Controls.Add(pcmVisual.CreateView(pcmHost));

        using var plusPlugin = new DsdPlusBridgeAfPlugin();
        if (plusPlugin is not IAfVisualPlugin plusVisual) return false;
        plusPlugin.Configure(new Dictionary<string, string>
        {
            ["enabled"] = "false",
            ["autoLaunch"] = "false"
        });
        var plusHost = new AfPluginUiHost("verify-dsdplus", plusPlugin.Info.Id, () => false, () => 0L,
            () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            _ => { }, _ => { });
        using var plusPage = new TabPage();
        plusPage.Controls.Add(plusVisual.CreateView(plusHost));
        return WaveInDevices.Enumerate().Count >= 0;
    }

    private static bool VerifyDump1090Adsb()
    {
        var packed = NeuroSDR.Plugins.Dump1090.ModeSDecoder.IdentificationFrame(0x40621D, "KLM1023");
        var decoder = new NeuroSDR.Plugins.Dump1090.ModeSDecoder();
        var decoded = decoder.Decode(packed);
        if (!decoded.CrcOk || decoded.Icao != "40621D" ||
            !decoded.Flight.StartsWith("KLM1023", StringComparison.Ordinal))
            return false;

        var iq = NeuroSDR.Plugins.Dump1090.ModeSDecoder.SynthesizeIq(packed);
        var mag = new ushort[iq.Length];
        var peak = NeuroSDR.Plugins.Dump1090.ModeSDecoder.PeakAbs(iq);
        NeuroSDR.Plugins.Dump1090.ModeSDecoder.MagnitudeFromIq(iq, mag, peak);
        NeuroSDR.Plugins.Dump1090.ModeSMessage? detected = null;
        decoder.Detect(mag, mag.Length, message =>
        {
            if (message.CrcOk) detected = message;
        });
        if (detected is null || detected.Icao != "40621D") return false;

        using var plugin = new NeuroSDR.Plugins.Dump1090.AdsbIqPlugin();
        var fleet = "";
        plugin.ResultAvailable += result =>
        {
            if (result.Kind.Equals("FLEET", StringComparison.OrdinalIgnoreCase))
                fleet = result.Fields?.GetValueOrDefault("rows") ?? "";
        };
        plugin.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["enabled"] = "true" });
        plugin.Process(new IqSampleBlock(iq, 2_000_000, 1_090_000_000, DateTime.UtcNow));
        return fleet.Contains("40621D", StringComparison.OrdinalIgnoreCase) &&
               fleet.Contains("KLM1023", StringComparison.OrdinalIgnoreCase);
    }

    private static bool VerifyLteCellSearch()
    {
        const int pci = 101; // N_id_1=33, N_id_2=2
        var frame = NeuroSDR.Plugins.Lte.LteCellSearcher.SynthesizeFrame(pci);
        var pss = new NeuroSDR.Plugins.Lte.LtePssDetector();
        if (!pss.Find(frame, out var nId2, out var peakPos, out var psr, out _))
        {
            Console.Error.WriteLine($"LTE PSS miss PSR={psr} peak={peakPos}");
            return false;
        }
        if (nId2 != pci % 3 || psr < 1.8f)
        {
            Console.Error.WriteLine($"LTE PSS bad nId2={nId2} PSR={psr}");
            return false;
        }

        var sss = new NeuroSDR.Plugins.Lte.LteSssDetector();
        var offset = NeuroSDR.Plugins.Lte.LteSssDetector.SssOffsetFromPssPeak(false);
        var sssStart = peakPos + 128 - offset;
        if (sssStart < 0 || sssStart + 128 > frame.Length)
        {
            Console.Error.WriteLine($"LTE SSS window invalid peak={peakPos} start={sssStart}");
            return false;
        }
        if (!sss.TryDecode(frame.AsSpan(sssStart, 128), nId2, out var nId1, out _, out var score))
        {
            Console.Error.WriteLine($"LTE SSS decode fail score={score}");
            return false;
        }
        if (nId1 * 3 + nId2 != pci)
        {
            Console.Error.WriteLine($"LTE PCI mismatch got={nId1 * 3 + nId2} want={pci}");
            return false;
        }

        if (NeuroSDR.Plugins.Lte.LteEarfcn.FromFrequencyMhz(1842.5) < 0) return false;

        // srsRAN plmn_test.cc golden: {0x89,0x19,0x14} → MCC 123 / MNC 45
        var plmn = NeuroSDR.Plugins.Lte.LtePlmnAsn1.UnpackPlmnIdentity([0x89, 0x19, 0x14]);
        if (plmn.Mcc != "123" || plmn.Mnc != "45")
        {
            Console.Error.WriteLine($"LTE PLMN ASN.1 fail got {plmn.Text}");
            return false;
        }
        if (NeuroSDR.Plugins.Lte.LteEarfcn.FromFrequencyMhz(875.85) != 2469)
        {
            Console.Error.WriteLine("LTE EARFCN 875.85 → 2469 mismatch");
            return false;
        }
        return true;
    }

    public static int RunLteCellSearch() => VerifyLteCellSearch() ? 0 : 117;

    /// <summary>
    /// Smoke-test ens_lte.dll worker: feed synthetic PSS/SSS frames + PCI hint.
    /// Expects native status to leave "ready" and/or report a cell / tracking.
    /// </summary>
    public static int RunNeuroLteNative()
    {
        HostNativeDllResolver.EnsureRegistered();
        if (!NeuroSDR.Plugins.Lte.NeuroLteNative.TryProbe(out var detail))
        {
            Console.Error.WriteLine($"ens_lte probe failed: {detail}");
            return 118;
        }
        Console.WriteLine($"ens_lte probe: {detail}");

        var dec = NeuroSDR.Plugins.Lte.NeuroLteNative.ens_lte_create();
        if (dec == IntPtr.Zero)
        {
            Console.Error.WriteLine("ens_lte_create failed");
            return 118;
        }

        try
        {
            const int pci = 101;
            const int rate = 1_920_000;
            var frame = NeuroSDR.Plugins.Lte.LteCellSearcher.SynthesizeFrame(pci);
            var iq = new float[frame.Length * 2];
            for (var i = 0; i < frame.Length; i++)
            {
                iq[2 * i] = (float)frame[i].Real;
                iq[2 * i + 1] = (float)frame[i].Imaginary;
            }

            NeuroSDR.Plugins.Lte.NeuroLteNative.ens_lte_hint_cell(dec, pci, 0, 0f);

            for (var k = 0; k < 400; k++)
            {
                NeuroSDR.Plugins.Lte.NeuroLteNative.ens_lte_process_iq(dec, iq, frame.Length, rate, 875_850_000L);
                if (k % 40 == 0)
                    Thread.Sleep(20);
            }

            Thread.Sleep(800);
            var status = NeuroSDR.Plugins.Lte.NeuroLteNative.StatusOf(dec);
            var cells = NeuroSDR.Plugins.Lte.NeuroLteNative.ens_lte_cell_count(dec);
            Console.WriteLine($"ens_lte status: {status}");
            Console.WriteLine($"ens_lte cells: {cells}");

            if (cells <= 0 && status.IndexOf("PCI", StringComparison.OrdinalIgnoreCase) < 0 &&
                status.IndexOf("MIB", StringComparison.OrdinalIgnoreCase) < 0 &&
                status.IndexOf("mib", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Console.Error.WriteLine("ens_lte did not accept PCI hint / start MIB");
                return 118;
            }
            return 0;
        }
        finally
        {
            NeuroSDR.Plugins.Lte.NeuroLteNative.ens_lte_destroy(dec);
        }
    }

    /// <summary>
    /// Live SDRplay sweep: feed managed+ens_lte at several LTE DL centers.
    /// Expect at least MIB progress when RF is present (SIB1 needs enough MS/s for cell BW).
    /// </summary>
    public static int RunLteLiveSdrplay() => RunLteLiveSdrplayMode("hybrid", dwellMs: 40_000);

    /// <summary>Native DLL only — no C# PSS/SSS. Expect SYNC rows from ens_lte cellsearch if RF present.</summary>
    public static int RunLteLiveSdrplayNativeOnly() =>
        RunLteLiveSdrplayMode("native", dwellMs: 55_000, rates: [2_000_000], centersHz: [875_670_000L, 875_850_000L]);

    /// <summary>Back-to-back managed vs native-only on same centers (proves relative capability).</summary>
    public static int RunLteLiveManagedVsNative()
    {
        Console.WriteLine("=== MANAGED-ONLY ===");
        var m = RunLteLiveSdrplayMode("managed", dwellMs: 25_000, rates: [2_000_000], centersHz: [875_850_000L]);
        Console.WriteLine();
        Console.WriteLine("=== NATIVE-ONLY ===");
        var n = RunLteLiveSdrplayMode("native", dwellMs: 60_000, rates: [2_000_000], centersHz: [875_670_000L]);
        Console.WriteLine();
        Console.WriteLine($"COMPARE managed_exit={m} native_exit={n}");
        // Pass if either path got SYNC (hardware present). Native blank when managed has SYNC = DLL gap.
        return m == 0 || n == 0 ? 0 : 119;
    }

    private static int RunLteLiveSdrplayMode(string mode, int dwellMs, int[]? rates = null, long[]? centersHz = null)
    {
        HostNativeDllResolver.EnsureRegistered();
        if (!NeuroSDR.Plugins.Lte.NeuroLteNative.TryProbe(out var probe))
        {
            Console.Error.WriteLine($"ens_lte missing: {probe}");
            return 119;
        }
        Console.WriteLine($"ens_lte: {probe} · mode={mode}");

        if (!SdrplaySampleSource.TryCreate(out var source, out var srcStatus) || source is null)
        {
            Console.Error.WriteLine($"SDRplay unavailable: {srcStatus}");
            return 119;
        }

        rates ??= [2_000_000, 10_000_000];
        centersHz ??= [875_850_000L, 876_550_000L];

        using (source)
        {
            source.HardwareAgcEnabled = true;
            source.GainPercent = 60;

            var anyMib = false;
            var anySync = false;

            foreach (var rate in rates)
            {
                source.ConfiguredSampleRate = rate;
                foreach (var center in centersHz)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== [{mode}] RSP {rate / 1e6:0.#} MS/s @ {center / 1e6:0.000} MHz ===");

                    using var plugin = new NeuroSDR.Plugins.Lte.LteIqPlugin();
                    var lastStatus = "";
                    var lastRows = "";
                    plugin.ResultAvailable += r =>
                    {
                        if (r.Kind.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                            lastStatus = r.Text ?? "";
                        if (r.Kind.Equals("CELLS", StringComparison.OrdinalIgnoreCase))
                            lastRows = r.Fields?.GetValueOrDefault("rows") ?? "";
                    };
                    plugin.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["enabled"] = "true",
                        ["mode"] = mode
                    });

                    long samples = 0;
                    Action<Complex32[]> handler = block =>
                    {
                        Interlocked.Add(ref samples, block.Length);
                        plugin.Process(new IqSampleBlock(block, source.SampleRate, center, DateTime.UtcNow));
                    };
                    source.SamplesAvailable += handler;

                    source.CenterFrequency = center;
                    source.Start();
                    var until = Environment.TickCount64 + dwellMs;
                    while (Environment.TickCount64 < until)
                        Thread.Sleep(250);
                    source.Stop();
                    Thread.Sleep(200);
                    source.SamplesAvailable -= handler;

                    Console.WriteLine($"samples={Interlocked.Read(ref samples)}");
                    Console.WriteLine($"status={lastStatus}");
                    Console.WriteLine($"cells:\n{lastRows}");

                    if (lastStatus.Contains("MIB OK", StringComparison.OrdinalIgnoreCase) ||
                        lastRows.Contains("\tMIB", StringComparison.Ordinal) ||
                        lastStatus.Contains("SIB1", StringComparison.OrdinalIgnoreCase))
                        anyMib = true;
                    if (lastRows.Contains("SYNC", StringComparison.OrdinalIgnoreCase) ||
                        lastStatus.Contains("PCI", StringComparison.OrdinalIgnoreCase))
                        anySync = true;

                    plugin.Dispose();
                }
            }

            Console.WriteLine();
            Console.WriteLine($"SUMMARY mode={mode} anySync={anySync} anyMibOrSib={anyMib}");
            return anySync ? 0 : 119;
        }
    }

    private static bool VerifyFlCw()
    {
        var decoded = new StringBuilder();
        var decoder = new NeuroSDR.Plugins.Fldigi.FlCwDecoder();
        decoder.ChannelText += (_, _, text) => { decoded.Append(text); };
        decoder.Configure(18, 0);
        decoder.Process(NeuroSDR.Plugins.Fldigi.FlCwDecoder.SynthesizeMorse("CQ CQ", 8_000, 700, 18));
        if (!decoded.ToString().Replace(" ", "").Contains("CQ", StringComparison.Ordinal))
            return false;

        decoded.Clear();
        using var plugin = new NeuroSDR.Plugins.Fldigi.FlCwAfPlugin();
        plugin.ResultAvailable += result =>
        {
            if (result.Kind.Equals("FLCW_CHAR", StringComparison.OrdinalIgnoreCase))
                decoded.Append(result.Fields?.GetValueOrDefault("character") ?? result.Text);
        };
        plugin.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = "true", ["wpm"] = "18", ["squelch"] = "0"
        });
        var audio = NeuroSDR.Plugins.Fldigi.FlCwDecoder.SynthesizeMorse("CQ CQ", AudioDemodulator.AudioSampleRate, 700, 18);
        const int chunk = 4_800;
        for (var offset = 0; offset < audio.Length; offset += chunk)
        {
            var take = Math.Min(chunk, audio.Length - offset);
            plugin.Process(new AfAudioBlock(audio.AsSpan(offset, take).ToArray(),
                AudioDemodulator.AudioSampleRate, DateTime.UtcNow));
        }
        return decoded.ToString().Replace(" ", "").Contains("CQ", StringComparison.Ordinal);
    }

    private static bool VerifyWideCaptureAliasRejection()
    {
        const int rate = 8_000_000;
        const int length = 800_000;
        // With a 250 kHz first demodulation domain, 251 kHz folds directly
        // onto 1 kHz unless the pre-decimation anti-alias filter rejects it.
        var blocker = new Complex32[length];
        for (var index = 0; index < blocker.Length; index++)
        {
            var phase = 2 * Math.PI * 251_000 * index / rate;
            blocker[index] = new Complex32(.8f * (float)Math.Cos(phase), .8f * (float)Math.Sin(phase));
        }
        var demodulator = new AudioDemodulator { Mode = RadioMode.USB, Bandwidth = 2_700 };
        var audio = demodulator.Process(blocker, rate);
        return audio.Length > 2_000 && MeasureTone(audio.Skip(1_000).ToArray(), AudioDemodulator.AudioSampleRate, 1_000) < .002;
    }

    private static unsafe int VerifySampleDispatcher()
    {
        static bool Near(float actual, float expected) => Math.Abs(actual - expected) < .01f;

        var unsignedResult = new List<Complex32>();
        var unsignedDispatcher = new SampleBlockDispatcher(2);
        unsignedDispatcher.SamplesAvailable += unsignedResult.AddRange;
        unsignedDispatcher.Start();
        byte[] unsignedBytes = [255, 128, 0, 64];
        fixed (byte* data = unsignedBytes) unsignedDispatcher.WriteUnsignedInterleaved(data, (uint)unsignedBytes.Length);
        unsignedDispatcher.Stop();
        if (unsignedResult.Count != 2) return unsignedResult.Count == 0 ? 89 : 90;
        if (!Near(unsignedResult[0].I, .996f)) return 91;
        if (!Near(unsignedResult[0].Q, .004f)) return 94;
        if (!Near(unsignedResult[1].I, -.996f)) return 95;
        if (!Near(unsignedResult[1].Q, -.496f)) return 96;

        var signedResult = new List<Complex32>();
        var signedDispatcher = new SampleBlockDispatcher(2);
        signedDispatcher.SamplesAvailable += signedResult.AddRange;
        signedDispatcher.Start();
        sbyte[] signedBytes = [127, -128, 0, 64];
        fixed (sbyte* data = signedBytes) signedDispatcher.WriteSignedInterleaved(data, signedBytes.Length);
        signedDispatcher.Stop();
        if (signedResult.Count != 2 || !Near(signedResult[0].I, .992f) || !Near(signedResult[0].Q, -1f) ||
            !Near(signedResult[1].I, 0) || !Near(signedResult[1].Q, .5f)) return 92;

        var int16Result = new List<Complex32>();
        var int16Dispatcher = new SampleBlockDispatcher(2);
        int16Dispatcher.SamplesAvailable += int16Result.AddRange;
        int16Dispatcher.Start();
        short[] i = [32767, -32768];
        short[] q = [0, 16384];
        fixed (short* pi = i)
        fixed (short* pq = q) int16Dispatcher.WriteInt16(pi, pq, 2);
        int16Dispatcher.Stop();
        return int16Result.Count == 2 && Near(int16Result[0].I, 1f) && Near(int16Result[0].Q, 0) &&
            Near(int16Result[1].I, -1f) && Near(int16Result[1].Q, .5f) ? 0 : 93;
    }

    private static bool VerifyIqWave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"neurosdr-iq-{Guid.NewGuid():N}.wav");
        try
        {
            const int sampleRate = 128_000;
            const long centerFrequency = 7_074_000;
            var block = new Complex32[32_768];
            for (var n = 0; n < block.Length; n++)
                block[n] = new Complex32((float)(.7 * Math.Sin(2 * Math.PI * 1_000 * n / sampleRate)), -.25f);
            using (var recorder = new IqWaveRecorder(path, sampleRate, centerFrequency, centerFrequency)) recorder.Write(block);

            if (!IqWaveFileSampleSource.TryOpen(path, out var source, out _) || source is null) return false;
            using (source)
            {
                Complex32 first = default;
                long received = 0;
                source.SamplesAvailable += samples =>
                {
                    if (Interlocked.Read(ref received) == 0) first = samples[0];
                    Interlocked.Add(ref received, samples.Length);
                };
                if (source.SampleRate != sampleRate || source.CenterFrequency != centerFrequency) return false;
                source.Start();
                if (!SpinWait.SpinUntil(() => Interlocked.Read(ref received) == block.Length, 2_000)) return false;
                source.Stop();
                if (Math.Abs(first.I - block[0].I) > .001 || Math.Abs(first.Q - block[0].Q) > .001) return false;
            }
            return true;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static bool VerifyMemoryChannels()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"neurosdr-memory-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "channels.json");
        try
        {
            var store = new MemoryChannelStore(path);
            var first = store.Add("FT8 40m", 7_074_000, RadioMode.USB, 2_700);
            var second = store.Add("WeatherFax", 8_682_000, RadioMode.USB, 3_000);
            store.Update(first with { Name = "FT8 40m USB" });
            var reloaded = new MemoryChannelStore(path).Snapshot();
            if (reloaded.Count != 2 || reloaded[0].Name != "FT8 40m USB" || reloaded[1].Frequency != 8_682_000) return false;

            var scanner = new MemoryScanScheduler { DwellMilliseconds = 800, HoldOnSignal = true, SignalThresholdDb = -75 };
            scanner.Start(reloaded, 0);
            if (scanner.Poll(0, -140)?.Id != first.Id) return false;
            if (scanner.Poll(800, -60) is not null) return false;
            if (scanner.Poll(999, -100) is not null) return false;
            if (scanner.Poll(1_000, -100)?.Id != second.Id) return false;
            scanner.Stop();
            if (scanner.IsRunning) return false;

            var range = new RangeScanScheduler { DwellMilliseconds = 500, HoldOnSignal = false };
            range.Start(7_000_000, 7_002_000, 1_000, 0);
            if (range.Poll(0, -140) != 7_000_000 || range.Poll(499, -140) is not null) return false;
            if (range.Poll(500, -140) != 7_001_000 || range.Poll(1_000, -140) != 7_002_000) return false;
            if (range.Poll(1_500, -140) != 7_000_000) return false;
            range.Stop();
            return !range.IsRunning;
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        }
    }

    private static bool VerifyAudioPostProcessor()
    {
        const int rate = AudioDemodulator.AudioSampleRate;
        static float[] Tone(int count, float amplitude, double frequency) => Enumerable.Range(0, count)
            .Select(index => amplitude * MathF.Sin((float)(2 * Math.PI * frequency * index / AudioDemodulator.AudioSampleRate))).ToArray();
        static double Rms(float[] values) => Math.Sqrt(values.Skip(values.Length / 2).Select(value => value * value).Average());

        var notchInput = Tone(rate, .4f, 1_000);
        var notchReference = MeasureTone(notchInput, rate, 1_000);
        var notch = new AudioPostProcessor { NotchEnabled = true, NotchFrequency = 1_000 };
        var notchOutput = notch.Process(notchInput, -40);
        if (MeasureTone(notchOutput, rate, 1_000) > notchReference / 8) return false;

        var weakInput = Tone(rate * 2, .01f, 700);
        var weakRms = Rms(weakInput);
        var agc = new AudioPostProcessor { AgcEnabled = true };
        if (Rms(agc.Process(weakInput, -40)) < weakRms * 2) return false;

        var veryWeakInput = Tone(rate * 2, .001f, 700);
        var wideCaptureAgc = new AudioPostProcessor { AgcEnabled = true, ExtendedAgcRange = true };
        if (Rms(wideCaptureAgc.Process(veryWeakInput, -110)) < .05) return false;

        var squelch = new AudioPostProcessor { SquelchEnabled = true, SquelchThresholdDb = -75 };
        if (Rms(squelch.Process(Tone(rate, .3f, 500), -110)) > .01) return false;

        var noise = Enumerable.Repeat(.01f, rate).ToArray();
        var reducer = new AudioPostProcessor { NoiseReductionEnabled = true, NoiseReductionStrength = 100 };
        if (Rms(reducer.Process(noise, -100)) >= .006) return false;

        var mixed = Enumerable.Range(0, rate).Select(index =>
            .3f * MathF.Sin((float)(2 * Math.PI * 300 * index / rate)) +
            .3f * MathF.Sin((float)(2 * Math.PI * 3_000 * index / rate))).ToArray();
        var bandPass = new AudioPostProcessor { AfFilterEnabled = true, AfLowCutHz = 1_000, AfHighCutHz = 5_000 };
        var filtered = bandPass.Process(mixed, -40);
        if (MeasureTone(filtered, rate, 3_000) / Math.Max(MeasureTone(filtered, rate, 300), 1e-9) < 6) return false;

        var lowPass = new AudioPostProcessor { AfFilterEnabled = true, AfLowCutHz = 0, AfHighCutHz = 1_000 };
        filtered = lowPass.Process(Enumerable.Range(0, rate).Select(index =>
            .3f * MathF.Sin((float)(2 * Math.PI * 300 * index / rate)) +
            .3f * MathF.Sin((float)(2 * Math.PI * 5_000 * index / rate))).ToArray(), -40);
        return MeasureTone(filtered, rate, 300) / Math.Max(MeasureTone(filtered, rate, 5_000), 1e-9) >= 10;
    }

    private static bool VerifyAfSpectrum()
    {
        float[]? spectrum = null;
        using (var ready = new ManualResetEventSlim())
        using (var pipeline = new AudioSpectrumPipeline())
        {
            pipeline.SpectrumAvailable += result => { spectrum = result; ready.Set(); };
            var audio = new float[8_192];
            for (var index = 0; index < audio.Length; index++)
                audio[index] = .5f * MathF.Sin((float)(2 * Math.PI * 1_000 * index / AudioDemodulator.AudioSampleRate));
            pipeline.Submit(audio);
            if (!ready.Wait(2_000) || spectrum is null) return false;
        }
        var peak = Array.IndexOf(spectrum, spectrum.Max());
        var peakFrequency = peak * (AudioDemodulator.AudioSampleRate / 2d) / spectrum.Length;
        if (Math.Abs(peakFrequency - 1_000) > 12) return false;

        using var display = new AudioSpectrumWaterfallControl { Size = new Size(640, 180) };
        var lowEdge = 0;
        var highEdge = 0;
        display.FilterRangeChanged += (low, high) => (lowEdge, highEdge) = (low, high);
        display.Configure(3_500, 0, 3_500, true);
        display.SetFilterFromXForVerification(true, 160);
        display.SetFilterFromXForVerification(false, 480);
        if (Math.Abs(lowEdge - 875) > 20 || Math.Abs(highEdge - 2_625) > 20) return false;
        display.PushSpectrum(spectrum);
        if (AudioSpectrumWaterfallControl.ExtractFtxCallsign("CQ HL1ABC PM37") != "HL1ABC") return false;
        if (AudioSpectrumWaterfallControl.ExtractFtxCallsign("CQ HL1ABC") != "HL1ABC") return false;
        if (AudioSpectrumWaterfallControl.ExtractFtxCallsign("HL2XYZ HL1ABC -12") != "HL1ABC") return false;
        if (AudioSpectrumWaterfallControl.ExtractCalledCallsign("HL2XYZ HL1ABC -12") != "HL2XYZ") return false;
        if (!AudioSpectrumWaterfallControl.IsUnresolvedCallsign("<...>")) return false;
        if (AudioSpectrumWaterfallControl.IsUnresolvedCallsign("HL1ABC")) return false;
        if (!AudioSpectrumWaterfallControl.IsCqMessage("CQ HL1ABC PM37")) return false;
        if (AudioSpectrumWaterfallControl.IsCqMessage("HL2XYZ HL1ABC -12")) return false;
        display.PushFtxMessage(1_000, "CQ TEST PM37", 10);
        display.PushFtxMessage(1_020, "K1ABC HL1ABC -10", 10);
        if (display.PendingFtxMessageCountForVerification != 2) return false;
        display.PushSpectrum(spectrum);
        if (display.PendingFtxMessageCountForVerification != 0) return false;
        display.PushFtxMessage(1_500, "HL1ABC K1ABC R-08", 11);
        display.ShowQsoLines = true;
        display.PushSpectrum(spectrum);
        display.PushFtxMessage(1_700, "CQ HL3ABC PM36", 12);
        display.ResetDisplay("SUB 2");
        if (display.DisplayVfoName != "SUB 2" || display.PendingFtxMessageCountForVerification != 0) return false;
        display.PushSpectrum(spectrum);
        using var rendered = new Bitmap(display.Width, display.Height);
        display.DrawToBitmap(rendered, display.ClientRectangle);
        return rendered.GetPixel(100, 80).ToArgb() != Color.Empty.ToArgb();
    }

    private static bool VerifyCtcssDecoder()
    {
        var rate = AudioDemodulator.AudioSampleRate;
        var decoder = new CtcssToneDecoder();
        // Quiet 67.0 Hz (realistic after FM scale) for 2.5 s.
        var samples = new float[rate * 5 / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.012f * MathF.Sin((float)(2 * Math.PI * 67.0 * i / rate));
        decoder.Process(samples, rate);
        if (Math.Abs(decoder.DetectedToneHz - 67.0f) > 1.2f)
        {
            Console.Error.WriteLine(
                $"ctcss:67 got={decoder.DetectedToneHz:0.0} best={decoder.DebugBestHz:0.0} snr={decoder.DebugBestSnr:0.00} rms={decoder.DebugBandRms:0.######}");
            return false;
        }

        decoder.Reset();
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.012f * MathF.Sin((float)(2 * Math.PI * 88.5 * i / rate));
        decoder.Process(samples, rate);
        if (Math.Abs(decoder.DetectedToneHz - 88.5f) > 1.2f)
        {
            Console.Error.WriteLine(
                $"ctcss:88.5 got={decoder.DetectedToneHz:0.0} best={decoder.DebugBestHz:0.0} snr={decoder.DebugBestSnr:0.00}");
            return false;
        }
        return true;
    }

    private static bool VerifyDcsDecoder()
    {
        if (!DcsToneDecoder.SelfCheck023())
        {
            // Fall through to detailed soft-corr probe.
        }

        var rate = AudioDemodulator.AudioSampleRate;
        var word = DcsToneDecoder.BuildCodeword(023);
        var samplesPerBit = rate / 134.4;
        var bitsNeeded = 23 * 16;
        var samples = new float[(int)(bitsNeeded * samplesPerBit)];
        for (var i = 0; i < samples.Length; i++)
        {
            var bitIndex = (int)(i / samplesPerBit) % 23;
            var bit = ((word >> bitIndex) & 1) != 0;
            samples[i] = bit ? -0.08f : 0.08f;
        }

        var decoder = new DcsToneDecoder { DeepSearch = true };
        var mid = samples.Length / 2;
        decoder.Process(samples.AsSpan(0, mid), rate);
        decoder.Process(samples.AsSpan(mid), rate);
        if (decoder.DetectedCode != 023)
        {
            Console.Error.WriteLine(
                $"dcs:023 got='{decoder.DetectedLabel}' code={decoder.DetectedCode} corr={decoder.DebugCorr:0.00} votes={decoder.DebugVotes} raw=0x{decoder.DebugTopPattern:X6} act={decoder.HasActivity} word=0x{word:X6}");
            return false;
        }
        return true;
    }

    public static unsafe int RunSdrplay()
    {
        try
        {
            if (System.Runtime.InteropServices.Marshal.SizeOf<SdrplaySampleSource.Device>() != 96) return 21;
            if (System.Runtime.InteropServices.Marshal.SizeOf<SdrplaySampleSource.DeviceParameters>() != 64) return 22;
            if (System.Runtime.InteropServices.Marshal.SizeOf<SdrplaySampleSource.TunerParameters>() != 72) return 23;
            if (System.Runtime.InteropServices.Marshal.SizeOf<SdrplaySampleSource.ControlParameters>() != 32) return 24;
            if (System.Runtime.InteropServices.Marshal.SizeOf<SdrplaySampleSource.RxChannelParams>() != 144) return 25;

            if (!SdrplaySampleSource.TryCreate(out var source, out _) || source is null) return 26;
            source.ConfiguredSampleRate = 10_000_000;
            if (source.SampleRate != 10_000_000) return 37;
            var iqPath = Path.Combine(Path.GetTempPath(), $"neurosdr-rsp1-{Guid.NewGuid():N}.wav");
            try
            {
                using (source)
                using (var recorder = new IqWaveRecorder(iqPath, source.SampleRate, 100_000_000, 100_000_000))
                {
                    long callbackSamples = 0;
                    var processor = new SpectrumProcessor();
                    var demodulator = new AudioDemodulator { Mode = RadioMode.WFM, Bandwidth = 180_000 };
                    var spectrumFrames = 0;
                    long audioSamples = 0;
                    source.SamplesAvailable += samples =>
                    {
                        recorder.Write(samples);
                        Interlocked.Add(ref callbackSamples, samples.Length);
                        if (processor.Process(samples) is not null) Interlocked.Increment(ref spectrumFrames);
                        Interlocked.Add(ref audioSamples, demodulator.Process(samples, source.SampleRate).Length);
                    };
                    source.CenterFrequency = 100_000_000;
                    source.GainPercent = 55;
                    source.Start();
                    Thread.Sleep(700);
                    recorder.Stop();
                    Thread.Sleep(800);
                    source.CenterFrequency = 100_100_000;
                    Thread.Sleep(300);
                    source.Stop();
                    if (callbackSamples < source.SampleRate / 4 || source.TotalSamples < callbackSamples) return 27;
                    if (spectrumFrames == 0 || audioSamples == 0) return 28;
                    if (recorder.WrittenSamples < source.SampleRate / 4) return 33;
                    if (recorder.DroppedSamples != 0) return 34;
                    if (!IqWaveFileSampleSource.TryOpen(iqPath, out var playback, out _) || playback is null) return 35;
                    using (playback)
                        if (playback.SampleRate != source.SampleRate || playback.CenterFrequency != 100_000_000) return 36;
                }
            }
            finally { try { File.Delete(iqPath); } catch { } }

            if (!SdrplaySampleSource.TryCreate(out var restartSource, out var restartStatus) || restartSource is null)
            {
                Console.Error.WriteLine(restartStatus);
                return 38;
            }
            using (restartSource)
            {
                long restartSamples = 0;
                restartSource.HardwareAgcEnabled = false;
                restartSource.SamplesAvailable += samples => Interlocked.Add(ref restartSamples, samples.Length);
                foreach (var rate in new[] { 2_000_000, 5_000_000, 10_000_000, 2_000_000 })
                {
                    restartSource.ConfiguredSampleRate = rate;
                    var before = Interlocked.Read(ref restartSamples);
                    restartSource.Start();
                    if (!SpinWait.SpinUntil(() => Interlocked.Read(ref restartSamples) - before >= rate / 10, 3_000))
                        return 39;
                    restartSource.Stop();
                    Console.WriteLine($"SDRplay restart rate={rate} samples={Interlocked.Read(ref restartSamples) - before}");
                }
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 29;
        }
    }

    public static unsafe int RunRtlSdr()
    {
        try
        {
            if (!RtlSdrSampleSource.TryCreate(out var source, out var status) || source is null)
            {
                Console.Error.WriteLine(status);
                return 61;
            }

            Console.WriteLine(status);
            using (source)
            {
                long callbackSamples = 0;
                var processor = new SpectrumProcessor();
                var demodulator = new AudioDemodulator { Mode = RadioMode.WFM, Bandwidth = 180_000 };
                var spectrumFrames = 0;
                long audioSamples = 0;
                source.SamplesAvailable += samples =>
                {
                    Interlocked.Add(ref callbackSamples, samples.Length);
                    if (processor.Process(samples) is not null) Interlocked.Increment(ref spectrumFrames);
                    Interlocked.Add(ref audioSamples, demodulator.Process(samples, source.SampleRate).Length);
                };

                source.CenterFrequency = 89_100_000;
                source.GainPercent = 60;
                source.Start();
                if (!SpinWait.SpinUntil(() => Interlocked.Read(ref callbackSamples) >= source.SampleRate / 2, 3_000))
                    return 62;
                source.CenterFrequency = 96_000_000;
                Thread.Sleep(400);
                source.Stop();

                Console.WriteLine($"RTL-SDR samples={callbackSamples} total={source.TotalSamples} delivered={source.DeliveredSamples} dropped={source.DroppedSamples} spectrum={spectrumFrames} audio={audioSamples}");
                if (callbackSamples < source.SampleRate / 2 || source.TotalSamples < callbackSamples) return 63;
                if (spectrumFrames == 0 || audioSamples == 0) return 64;
                if (source.DroppedSamples > source.DeliveredSamples / 10) return 65;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 66;
        }
    }

    public static int RunHackRf()
    {
        try
        {
            if (!HackRfSampleSource.TryCreate(out var source, out var status) || source is null)
            {
                Console.Error.WriteLine(status);
                return 71;
            }
            Console.WriteLine(status);
            using (source)
            {
                source.ConfiguredSampleRate = 8_000_000;
                long callbackSamples = 0;
                var processor = new SpectrumProcessor();
                var demodulator = new AudioDemodulator { Mode = RadioMode.WFM, Bandwidth = 180_000 };
                var spectrumFrames = 0;
                long audioSamples = 0;
                source.SamplesAvailable += samples =>
                {
                    Interlocked.Add(ref callbackSamples, samples.Length);
                    if (processor.Process(samples) is not null) Interlocked.Increment(ref spectrumFrames);
                    Interlocked.Add(ref audioSamples, demodulator.Process(samples, source.SampleRate).Length);
                };
                source.CenterFrequency = 89_100_000;
                source.GainPercent = 55;
                source.Start();
                if (!SpinWait.SpinUntil(() => Interlocked.Read(ref callbackSamples) >= source.SampleRate / 2, 4_000))
                    return 72;
                source.CenterFrequency = 96_000_000;
                Thread.Sleep(400);
                source.Stop();
                Console.WriteLine($"HackRF samples={callbackSamples} total={source.TotalSamples} delivered={source.DeliveredSamples} dropped={source.DroppedSamples} spectrum={spectrumFrames} audio={audioSamples}");
                if (callbackSamples < source.SampleRate / 2 || source.TotalSamples < callbackSamples) return 73;
                if (spectrumFrames == 0 || audioSamples == 0) return 74;
                if (source.DroppedSamples > source.DeliveredSamples / 10) return 75;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 76;
        }
    }

    public static int RunAirspy()
    {
        try
        {
            if (!AirspySampleSource.TryCreate(out var source, out var status) || source is null)
            {
                Console.Error.WriteLine(status);
                return 81;
            }
            Console.WriteLine(status);
            using (source)
            {
                long callbackSamples = 0;
                var processor = new SpectrumProcessor();
                var demodulator = new AudioDemodulator { Mode = RadioMode.WFM, Bandwidth = 180_000 };
                var spectrumFrames = 0;
                long audioSamples = 0;
                source.SamplesAvailable += samples =>
                {
                    Interlocked.Add(ref callbackSamples, samples.Length);
                    if (processor.Process(samples) is not null) Interlocked.Increment(ref spectrumFrames);
                    Interlocked.Add(ref audioSamples, demodulator.Process(samples, source.SampleRate).Length);
                };
                source.CenterFrequency = 89_100_000;
                source.GainPercent = 55;
                source.Start();
                if (!SpinWait.SpinUntil(() => Interlocked.Read(ref callbackSamples) >= source.SampleRate / 2, 4_000))
                    return 82;
                source.CenterFrequency = 96_000_000;
                Thread.Sleep(400);
                source.Stop();
                Console.WriteLine($"Airspy samples={callbackSamples} total={source.TotalSamples} delivered={source.DeliveredSamples} dropped={source.DroppedSamples}");
                if (callbackSamples < source.SampleRate / 2) return 83;
                if (spectrumFrames == 0 || audioSamples == 0) return 84;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 85;
        }
    }

    public static int RunAirspyHf()
    {
        try
        {
            if (!AirspyHfSampleSource.TryCreate(out var source, out var status) || source is null)
            {
                Console.Error.WriteLine(status);
                return 86;
            }
            Console.WriteLine(status);
            using (source)
            {
                long callbackSamples = 0;
                source.SamplesAvailable += samples => Interlocked.Add(ref callbackSamples, samples.Length);
                source.CenterFrequency = 7_200_000;
                source.GainPercent = 60;
                source.Start();
                if (!SpinWait.SpinUntil(() => Interlocked.Read(ref callbackSamples) >= source.SampleRate / 4, 4_000))
                    return 87;
                source.Stop();
                Console.WriteLine($"Airspy HF+ samples={callbackSamples} total={source.TotalSamples}");
                if (callbackSamples < source.SampleRate / 4) return 88;
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 89;
        }
    }

    public static int RunAudio()
    {
        try
        {
            var output = new WaveOutPlayer(AudioDemodulator.AudioSampleRate) { VolumePercent = 0 };
            var output2 = new WaveOutPlayer(AudioDemodulator.AudioSampleRate) { VolumePercent = 0 };
            try
            {
                var silentBlock = new float[960];
                for (var i = 0; i < 100; i++)
                {
                    output.Write(silentBlock);
                    output2.Write(silentBlock);
                    Thread.Sleep(10);
                }
                if (!output.IsOpen || !output2.IsOpen || output.SubmittedBuffers == 0 || output.CompletedBuffers == 0 ||
                    output2.SubmittedBuffers == 0 || output2.CompletedBuffers == 0) return 31;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                output.Dispose();
                output2.Dispose();
                return stopwatch.ElapsedMilliseconds <= 2_000 ? 0 : 32;
            }
            finally { output.Dispose(); output2.Dispose(); }
        }
        catch
        {
            return 39;
        }
    }

    private static Complex32[] CreateComplexTones(int sampleRate, double seconds, (double Frequency, float Amplitude)[] tones)
    {
        var samples = new Complex32[(int)(sampleRate * seconds)];
        for (var n = 0; n < samples.Length; n++)
        {
            double i = 0, q = 0;
            foreach (var tone in tones)
            {
                var phase = 2 * Math.PI * tone.Frequency * n / sampleRate;
                i += tone.Amplitude * Math.Cos(phase);
                q += tone.Amplitude * Math.Sin(phase);
            }
            samples[n] = new Complex32((float)i, (float)q);
        }
        return samples;
    }

    private static bool VerifyIndependentVfos()
    {
        const int sampleRate = 2_000_000;
        const int carrier1 = -240_000, carrier2 = 310_000;
        var samples = new Complex32[sampleRate / 5];
        for (var n = 0; n < samples.Length; n++)
        {
            var phase1 = 2 * Math.PI * carrier1 * n / sampleRate;
            var phase2 = 2 * Math.PI * carrier2 * n / sampleRate;
            var amplitude1 = .35 + .16 * Math.Sin(2 * Math.PI * 700 * n / sampleRate);
            var amplitude2 = .35 + .16 * Math.Sin(2 * Math.PI * 1_400 * n / sampleRate);
            samples[n] = new Complex32(
                (float)(amplitude1 * Math.Cos(phase1) + amplitude2 * Math.Cos(phase2)),
                (float)(amplitude1 * Math.Sin(phase1) + amplitude2 * Math.Sin(phase2)));
        }
        var first = new AudioDemodulator { Mode = RadioMode.AM, Bandwidth = 8_000, FrequencyOffset = carrier1 }.Process(samples, sampleRate);
        var second = new AudioDemodulator { Mode = RadioMode.AM, Bandwidth = 8_000, FrequencyOffset = carrier2 }.Process(samples, sampleRate);
        var firstWanted = MeasureTone(first, AudioDemodulator.AudioSampleRate, 700);
        var firstOther = MeasureTone(first, AudioDemodulator.AudioSampleRate, 1_400);
        var secondWanted = MeasureTone(second, AudioDemodulator.AudioSampleRate, 1_400);
        var secondOther = MeasureTone(second, AudioDemodulator.AudioSampleRate, 700);
        return firstWanted > .01 && secondWanted > .01 && firstWanted > firstOther * 5 && secondWanted > secondOther * 5;
    }

    private static double MeasureTone(float[] samples, int sampleRate, double frequency)
    {
        var start = samples.Length / 3;
        double cosine = 0, sine = 0;
        for (var n = start; n < samples.Length; n++)
        {
            var phase = 2 * Math.PI * frequency * n / sampleRate;
            cosine += samples[n] * Math.Cos(phase);
            sine += samples[n] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(cosine * cosine + sine * sine) / Math.Max(1, samples.Length - start);
    }

    private static bool VerifyRemoteSdrBands()
    {
        var hf = RemoteSdrBands.ParseWebSdrObject("""{ "bands": [{ "c":"g10", "l":0, "h":29.1596 }] }""");
        if (hf.Count != 1 || !RemoteSdrBands.Covers(hf, RemoteSdrSpectrum.Hf) ||
            RemoteSdrBands.Covers(hf, RemoteSdrSpectrum.Vhf)) return false;
        var kiwi = RemoteSdrBands.ParseKiwi("0-30000000", "SA4BNA 0-32 MHZ SDR");
        if (!RemoteSdrBands.Covers(kiwi, RemoteSdrSpectrum.Hf) ||
            RemoteSdrBands.Covers(kiwi, RemoteSdrSpectrum.Vhf)) return false;
        var kiwi32 = RemoteSdrBands.ParseKiwi("0-32000000", "0-32 MHZ");
        if (RemoteSdrBands.Covers(kiwi32, RemoteSdrSpectrum.Vhf)) return false;
        var vhf = RemoteSdrBands.ParseWebSdrObject("""{ "c":"m2", "l":143.976, "h":146.024 }""");
        if (!RemoteSdrBands.Covers(vhf, RemoteSdrSpectrum.Vhf)) return false;
        var qo = RemoteSdrBands.ParseWebSdrObject("""{ "c":"muhf", "l":10489.2, "h":10490.2 }""");
        if (!RemoteSdrBands.Covers(qo, RemoteSdrSpectrum.Uhf)) return false;
        if (RemoteSdrBands.ClampHz(14_074_000, vhf) != RemoteSdrBands.ToHz(143.976)) return false;
        if (RemoteSdrBands.ClampHz(144_300_000, vhf) != 144_300_000) return false;
        if (RemoteSdrBands.ClampHz(50_000_000, kiwi) != 30_000_000) return false;
        var kiwi2m = RemoteSdrBands.ParseKiwi("144000000-148000000", "sdr2kiwi2meter");
        if (!RemoteSdrBands.Covers(kiwi2m, RemoteSdrSpectrum.Vhf) ||
            RemoteSdrBands.Covers(kiwi2m, RemoteSdrSpectrum.Hf)) return false;
        if (RemoteSdrBands.ClampHz(14_100_000, kiwi2m) != 144_000_000) return false;
        if (!RemoteSdrBands.ContainsHz(145_800_000, kiwi2m) || RemoteSdrBands.ContainsHz(14_100_000, kiwi2m)) return false;
        if (!VerifySpectrumOccupancy()) return false;
        if (Math.Abs(WebSdr.Protocol.Kiwi.KiwiWaterfallClient.BandwidthMessageToKhz(30_000_000) - 30_000) > 0.1) return false;
        if (Math.Abs(WebSdr.Protocol.Kiwi.KiwiWaterfallClient.BandwidthMessageToKhz(30_000) - 30_000) > 0.1) return false;
        var dir = RemoteSdrCatalog.LoadDirectory();
        var websdr = dir.Where(item => item.Protocol == "WebSDR").ToList();
        var kiwiRows = dir.Where(item => item.Protocol == "KiwiSDR").ToList();
        if (websdr.Count < 8 || kiwiRows.Count < 8) return false;
        if (websdr.Count(item => item.Bands.Count > 0) < 5) return false;
        if (kiwiRows.Count(item => item.HasCoordinates) < 5) return false;
        return true;
    }

    private static bool VerifySpectrumOccupancy()
    {
        var noise = Enumerable.Repeat(-90f, 128).ToArray();
        var quiet = SpectrumOccupancy.Evaluate(noise, 60, 68, 6);
        if (quiet.Occupied) return false;
        var bump = (float[])noise.Clone();
        for (var i = 60; i <= 68; i++) bump[i] = -70f;
        var signal = SpectrumOccupancy.Evaluate(bump, 60, 68, 6);
        if (!signal.Occupied || signal.SnrDb < 6) return false;

        const long center = 6_040_000;
        const int span = 200_000;
        var broadcast = Enumerable.Repeat(-95f, 1024).ToArray();
        SetSpectrumRange(broadcast, center, span, center - 1_200, center + 1_200, -72f);
        var present = SpectrumOccupancy.EvaluateBroadcast(broadcast, center, span, center, 10_000);
        if (!present.Occupied || present.SnrDb < 10) return false;

        // A strong adjacent station outside the target's center must not mark the target occupied.
        var adjacentOnly = Enumerable.Repeat(-95f, 1024).ToArray();
        SetSpectrumRange(adjacentOnly, center, span, center + 8_000, center + 11_000, -60f);
        var absent = SpectrumOccupancy.EvaluateBroadcast(adjacentOnly, center, span, center, 10_000);
        return !absent.Occupied;
    }

    public static int RunAiScanLogicVerification() => VerifySpectrumOccupancy() ? 0 : 127;

    private static void SetSpectrumRange(
        float[] spectrum, long centerHz, int spanHz, long lowHz, long highHz, float level)
    {
        var left = centerHz - spanHz / 2d;
        var first = Math.Clamp((int)Math.Floor((lowHz - left) * spectrum.Length / spanHz), 0, spectrum.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((highHz - left) * spectrum.Length / spanHz), first, spectrum.Length - 1);
        for (var i = first; i <= last; i++) spectrum[i] = level;
    }

    private static bool VerifyCaptionGateAndAfFilterDefaults()
    {
        if (new AppSettings().AfFilterEnabled) return false;
        if (new RxScene().AfFilterEnabled) return false;
        var factory = AppSettingsStore.FactoryDefaults();
        if (factory.Mode != RadioMode.AM || factory.TunedFrequency != 28_000_000 ||
            factory.FilterBandwidth != 10_000 ||
            factory.AfPluginInstances.Count != 0 || factory.EnabledAfPluginIds.Count != 0 ||
            factory.EnabledIqPluginIds.Count != 0) return false;
        var legacy = JsonSerializer.Deserialize("""{"RxScenes":[{"Name":"legacy"}]}""", AppSettingsJsonContext.Default.AppSettings);
        if (legacy?.RxScenes.Count != 1 || legacy.RxScenes[0].AfFilterEnabled) return false;
        var savedOn = JsonSerializer.Deserialize(
            """{"RxScenes":[{"Name":"on","AfFilterEnabled":true}]}""",
            AppSettingsJsonContext.Default.AppSettings);
        if (savedOn?.RxScenes[0].AfFilterEnabled != true) return false;

        var gate = new SpeechActivityGate { Sensitivity = 5, Enabled = true };
        var silence = new float[512];
        for (var i = 0; i < 12; i++)
            if (gate.Observe(silence, 12_000)) return false;
        gate.Enabled = false;
        if (!gate.Observe(silence, 12_000)) return false;

        var tone = new float[512];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = 0.08f * MathF.Sin(2 * MathF.PI * 1_000 * i / 12_000f);
        gate.Enabled = true;
        gate.Reset();
        gate.Sensitivity = 1;
        var heard = false;
        for (var i = 0; i < 8; i++)
            heard |= gate.Observe(tone, 12_000);
        if (!heard) return false;

        using var view = new NeuroCaptionPluginView();
        var vfo = new ComboBox();
        var bar = view.AttachHostBar(vfo);
        var flow = bar.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
        if (flow is null || flow.Controls[0] != vfo) return false;
        if (flow.Controls.OfType<Button>().All(b => b.Height > 24)) return false;
        var caption = flow.Controls.OfType<Button>().First();
        if (caption.BackColor.G > caption.BackColor.B) return false;
        var opts = view.BuildOptions();
        if (!opts.TryGetValue("analyze", out var analyze) ||
            !analyze.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        if (!opts.TryGetValue("speechGateEnabled", out var gateOn) ||
            !gateOn.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;
        if (NeuroCaption.GateLevel(opts.GetValueOrDefault("speechGate")) != NeuroCaption.DefaultGate) return false;
        if (NeuroCaption.NormalizeEngine(opts.GetValueOrDefault("engine")) != NeuroCaption.Groq) return false;
        if (NeuroCaption.ChunkSeconds(opts, NeuroCaption.Groq) != NeuroCaption.GroqChunkSeconds) return false;
        if (NeuroCaption.ChunkSeconds(opts, NeuroCaption.Whisper) != NeuroCaption.FastChunkSeconds) return false;
        if (NeuroCaption.DefaultDisplayMode("mymemory", "original") != "both") return false;
        if (NeuroCaption.DefaultDisplayMode("off", "") != "original") return false;
        var perEngine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = NeuroCaption.Onnx,
            ["translateEngine"] = "mymemory",
            ["groq.translateEngine"] = "mymemory",
            ["onnx.translateEngine"] = "off"
        };
        if (NeuroCaption.Get(perEngine, NeuroCaption.Onnx, "translateEngine", "mymemory") != "off") return false;
        if (NeuroCaption.Get(perEngine, NeuroCaption.Groq, "translateEngine", "off") != "mymemory") return false;
        NeuroCaption.WriteActiveAliases(perEngine, NeuroCaption.Onnx);
        if (perEngine["translateEngine"] != "off") return false;
        var split = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = NeuroCaption.Whisper,
            ["chunkSeconds"] = "4",
            ["whisper.chunkSeconds"] = "4",
            ["groq.chunkSeconds"] = "20"
        };
        if (NeuroCaption.ChunkSeconds(split, NeuroCaption.Whisper) != 4) return false;
        if (NeuroCaption.ChunkSeconds(split, NeuroCaption.Groq) != 20) return false;
        if (CaptionTickerMotion.GapBeforeNewText(1_200, 800) > CaptionTickerMotion.JoinGapPx) return false;
        if (Math.Abs(CaptionTickerMotion.GapBeforeNewText(220, 800) - 580) > 1f) return false;
        var idleSpeed = CaptionTickerMotion.PixelsPerSecond(60, 800, 0);
        var busySpeed = CaptionTickerMotion.PixelsPerSecond(2_400, 800, 0.85f);
        var trailSecIdle = CaptionTickerMotion.TrailPixels() / idleSpeed;
        var trailSecBusy = CaptionTickerMotion.TrailPixels() / busySpeed;
        if (Math.Abs(idleSpeed - CaptionTickerMotion.IdlePxPerSec) > 8f) return false;
        if (busySpeed < idleSpeed || busySpeed > CaptionTickerMotion.MaxPxPerSec) return false;
        if (trailSecIdle < 5f || trailSecIdle > 10.5f || trailSecBusy < 5f || trailSecBusy > 10.5f) return false;
        var endpoint = LocalWhisperClient.TranscriptionUri("192.168.227.185", 8100, false);
        if (endpoint.ToString() != "http://192.168.227.185:8100/v1/audio/transcriptions") return false;
        if (LocalWhisperClient.BaseUri("http://192.168.227.185:8100", 9, true).ToString() !=
            "http://192.168.227.185:8100/") return false;
        var plugin = new NeuroCaptionAfPlugin();
        plugin.Configure(opts);
        var samples = new float[256];
        plugin.Process(new AfAudioBlock(samples, 12_000, DateTime.UtcNow));
        var migrated = AppSettingsStore.MigrateForVerification(new AppSettings
        {
            SettingsVersion = 22,
            AfPluginInstances =
            [
                new AfPluginInstanceSettings { PluginId = "builtin.af.groqwhisper", VfoId = "main" },
                new AfPluginInstanceSettings { PluginId = "builtin.af.whisper", VfoId = "main" }
            ],
            AfPluginUiState = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["builtin.af.groqwhisper"] = new(StringComparer.OrdinalIgnoreCase) { ["apiKey"] = "gsk_test" },
                ["builtin.af.whisper"] = new(StringComparer.OrdinalIgnoreCase) { ["host"] = "192.168.227.185" }
            }
        });
        if (migrated.AfPluginInstances.Count != 1 ||
            migrated.AfPluginInstances[0].PluginId != NeuroCaption.PluginId ||
            migrated.AfPluginUiState.GetValueOrDefault(NeuroCaption.PluginId)?.GetValueOrDefault("engine") != NeuroCaption.Groq ||
            migrated.AfPluginUiState.GetValueOrDefault(NeuroCaption.PluginId)?.GetValueOrDefault("apiKey") != "gsk_test")
            return false;

        using var setup = new NeuroCaptionSetupForm(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = NeuroCaption.Whisper,
            ["host"] = "192.168.227.185",
            ["port"] = "8100",
            ["speechGateEnabled"] = "false",
            ["speechGate"] = "5"
        });
        setup.ShowInTaskbar = false;
        setup.StartPosition = FormStartPosition.Manual;
        setup.Location = new Point(40, 40);
        setup.Opacity = 0;
        setup.Show();
        setup.PerformLayout();
        Application.DoEvents();
        if (!HasVisibleText(setup, "MODELS") || !HasVisibleText(setup, "TEST") ||
            !HasVisibleText(setup, "WHISPER SERVER") || !HasVisibleText(setup, "KEY") ||
            HasVisibleText(setup, "API KEY"))
        {
            setup.Close();
            return false;
        }
        var gateLevel = FindGateLevel(setup);
        if (gateLevel is null || gateLevel.Value != NeuroCaption.DefaultGate)
        {
            setup.Close();
            return false;
        }
        setup.Close();
        return true;
    }

    private static NumericUpDown? FindGateLevel(Control root)
    {
        if (root is NumericUpDown box && box.Minimum == 1 && box.Maximum == 10)
            return box;
        foreach (Control child in root.Controls)
        {
            var found = FindGateLevel(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static bool HasVisibleText(Control root, string text)
    {
        if (root.Visible && string.Equals(root.Text, text, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (Control child in root.Controls)
        {
            if (HasVisibleText(child, text))
                return true;
        }
        return false;
    }

    public static async Task<int> RunKiwiScanOccupancyAsync(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) url = AppSettingsStore.Load().KiwiSdrUrl;
        Console.WriteLine($"kiwi scan probe url={url}");
        using var source = new WebRadioSampleSource(WebRadioKind.KiwiSdr) { ServerUrl = url };
        var frames = new ConcurrentQueue<RemoteSpectrumFrame>();
        var frameReady = new SemaphoreSlim(0);
        source.RemoteSpectrumAvailable += frame =>
        {
            frames.Enqueue(frame);
            if (frameReady.CurrentCount < 20) frameReady.Release();
        };
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await source.ConnectAsync(connectTimeout.Token);

        NeuroSDR.Plugins.Broadcast.EibiScheduleStore.LoadCacheIfPresent();
        var scheduled = NeuroSDR.Plugins.Broadcast.EibiScheduleStore
            .Filter("All", "All", "", true, DateTime.UtcNow, 6_040_000, 30_000_000)
            .GroupBy(entry => entry.FrequencyHz)
            .Select(group => group.First())
            .Take(5)
            .ToArray();
        var probes = scheduled.Length > 0
            ? scheduled.Select(entry => (entry.FrequencyHz, entry.Station)).ToArray()
            : new (long FrequencyHz, string Station)[]
            {
                (6_040_000, "6040 kHz"), (6_055_000, "6055 kHz"), (6_070_000, "6070 kHz"),
                (6_085_000, "6085 kHz"), (6_100_000, "6100 kHz")
            };

        var occupied = 0;
        foreach (var (hz, station) in probes)
        {
            while (frames.TryDequeue(out _)) { }
            while (frameReady.Wait(0)) { }
            source.CenterFrequency = hz;
            await source.ApplyReceiverAsync(RadioMode.AM, 10_000);
            await source.SetSpectrumViewportAsync(hz, 250_000);

            var positives = 0;
            var validFrames = 0;
            var best = new SpectrumOccupancy.Result(-140f, -140f, 0f, false);
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && validFrames < 4)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero ||
                    !await frameReady.WaitAsync(remaining))
                    break;
                while (frames.TryDequeue(out var frame))
                {
                    if (frame.Intensities.Length < 8) continue;
                    const int requiredMargin = 31_500;
                    var frameLeft = frame.CenterFrequency - frame.SpanHz / 2L;
                    var frameRight = frame.CenterFrequency + frame.SpanHz / 2L;
                    if (hz - requiredMargin < frameLeft || hz + requiredMargin > frameRight)
                        continue;
                    var db = new float[frame.Intensities.Length];
                    for (var i = 0; i < db.Length; i++)
                        db[i] = -130f + frame.Intensities[i] * (130f / 255f);
                    var sample = SpectrumOccupancy.EvaluateBroadcast(
                        db, frame.CenterFrequency, frame.SpanHz, hz, 10_000);
                    validFrames++;
                    if (sample.Occupied) positives++;
                    if (sample.SnrDb > best.SnrDb) best = sample;
                    if (validFrames >= 4) break;
                }
            }

            var live = positives >= 2 || best.SnrDb >= 7f;
            if (live) occupied++;
            Console.WriteLine(
                $"{hz / 1000d:F1} kHz  {station}  frames={validFrames} positive={positives} " +
                $"snr={best.SnrDb:F1} sig={best.SignalDb:F1} noise={best.NoiseDb:F1} => {(live ? "SIGNAL" : "skip")}");
        }
        Console.WriteLine($"occupied {occupied}/{probes.Length}");
        return occupied > 0 ? 0 : 84;
    }

    public static async Task<int> RunKiwiCaptionProbeAsync(string url, long frequencyHz)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) url = AppSettingsStore.Load().KiwiSdrUrl;
        frequencyHz = frequencyHz > 0 ? frequencyHz : 6_040_000;
        var settings = AppSettingsStore.Load();
        var captionOptions = settings.AfPluginUiState.GetValueOrDefault(NeuroCaption.PluginId);
        var model = captionOptions?.GetValueOrDefault("onnxModel", SherpaCaptionModels.DefaultId)
                    ?? SherpaCaptionModels.DefaultId;
        if (!SherpaCaptionModels.IsReady(model))
        {
            Console.WriteLine($"ONNX model is not ready: {model}");
            return 128;
        }

        using var source = new WebRadioSampleSource(WebRadioKind.KiwiSdr) { ServerUrl = url };
        using var caption = new NeuroCaptionAfPlugin();
        var done = new TaskCompletionSource<AfPluginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        caption.ResultAvailable += result =>
        {
            if (result.Kind.Equals("CAPTION", StringComparison.OrdinalIgnoreCase))
                done.TrySetResult(result);
            else if (result.Kind.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"caption error: {result.Text}");
        };
        caption.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["analyze"] = "True",
            ["engine"] = NeuroCaption.Onnx,
            ["onnxModel"] = model,
            ["onnx.language"] = "auto",
            ["onnx.chunkSeconds"] = "5",
            ["onnx.speechGateEnabled"] = "False",
            ["onnx.translateEngine"] = "off",
            ["onnx.translateTo"] = "en",
            ["onnx.displayMode"] = "original"
        });
        caption.SetLanguageProbe(true);
        source.AudioSamplesAvailable += (samples, rate) =>
            caption.Process(new AfAudioBlock(samples, rate, DateTime.UtcNow));
        source.CenterFrequency = frequencyHz;

        Console.WriteLine(
            $"kiwi caption probe url={url} frequency={frequencyHz / 1000d:F1} kHz model={model}");
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await source.ConnectAsync(connectTimeout.Token);
        await source.ApplyReceiverAsync(RadioMode.AM, 10_000);
        using var resultTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var result = await done.Task.WaitAsync(resultTimeout.Token);
            var language = result.Fields?.GetValueOrDefault("language", "") ?? "";
            Console.WriteLine($"language={language} caption={result.Text}");
            return language.Length > 0 && language != "auto" ? 0 : 129;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("No speech/language was detected within 45 seconds.");
            return 129;
        }
        finally
        {
            caption.SetLanguageProbe(false);
        }
    }

    private sealed class TestSpectrumWaterfallControl : SpectrumWaterfallControl
    {
        public void DragBackground(int fromX, int toX)
        {
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, fromX, 100, 0));
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, toX, 100, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, toX, 100, 0));
        }

        public void ClickBackground(int x)
        {
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, 100, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, 100, 0));
        }

        public void RightClickBackground(int x)
        {
            OnMouseDown(new MouseEventArgs(MouseButtons.Right, 1, x, 100, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Right, 1, x, 100, 0));
        }

        public void ZoomIn(int x) => OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, x, 100, 120));
        public void ZoomOut(int x) => OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, x, 100, -120));
    }
}
