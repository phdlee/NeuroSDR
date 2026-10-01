namespace NeuroSDR
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            HostNativeDllResolver.EnsureRegistered();
            if (args.Contains("--bake-icons", StringComparer.OrdinalIgnoreCase))
            {
                IconMarks.Bake(IconMarks.FindAssetsDirectory());
                return 0;
            }
#if DEBUG
            // Make cross-thread UI bugs deterministic even with Start Without
            // Debugging, instead of allowing them to corrupt the RF callback path.
            Control.CheckForIllegalCrossThreadCalls = true;
#endif
            var pipelineLog = args.Contains("--pipeline-log", StringComparer.OrdinalIgnoreCase);
            var globalRateArgument = args.FirstOrDefault(argument => argument.StartsWith("--sample-rate=", StringComparison.OrdinalIgnoreCase));
            int? forcedUiSampleRate = globalRateArgument is not null && int.TryParse(globalRateArgument.Split('=', 2)[1], out var forcedRate)
                ? forcedRate : null;
            var uiSoakArgument = args.FirstOrDefault(argument => argument.StartsWith("--ui-soak=", StringComparison.OrdinalIgnoreCase));
            if (uiSoakArgument is not null && int.TryParse(uiSoakArgument.Split('=', 2)[1], out var uiSoakSeconds))
            {
                ApplicationConfiguration.Initialize();
                Application.SetColorMode(SystemColorMode.Dark);
                using var testForm = new frmNeuroSDR(pipelineLog, forcedUiSampleRate) { Opacity = 0, ShowInTaskbar = false };
                testForm.BeginAutomatedSoak(Math.Clamp(uiSoakSeconds, 5, 600));
                Application.Run(testForm);
                return testForm.AutomatedExitCode;
            }
            var passiveArgument = args.FirstOrDefault(argument => argument.StartsWith("--passive-rx=", StringComparison.OrdinalIgnoreCase));
            if (passiveArgument is not null && int.TryParse(passiveArgument.Split('=', 2)[1], out var passiveSeconds))
            {
                Control.CheckForIllegalCrossThreadCalls = true;
                ApplicationConfiguration.Initialize();
                Application.SetColorMode(SystemColorMode.Dark);
                // This diagnostic must paint the real UI. A hidden/transparent form
                // cannot reveal GDI rendering stalls seen during normal F5 use.
                using var testForm = new frmNeuroSDR(true);
                testForm.BeginPassiveReceiveDiagnostic(Math.Clamp(passiveSeconds, 5, 120));
                Application.Run(testForm);
                return testForm.AutomatedExitCode;
            }
            if (args.Contains("--verify-caption-ui", StringComparer.OrdinalIgnoreCase))
                return Verification.RunCaptionUi();
            if (args.Contains("--verify", StringComparer.OrdinalIgnoreCase))
                return Verification.Run();
            var groqWhisper = args.FirstOrDefault(argument => argument.StartsWith("--verify-groq-whisper", StringComparison.OrdinalIgnoreCase));
            if (groqWhisper is not null)
            {
                var key = groqWhisper.Contains('=')
                    ? groqWhisper.Split('=', 2)[1]
                    : Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
                return Verification.RunGroqWhisperLive(key);
            }
            var localWhisper = args.FirstOrDefault(argument => argument.StartsWith("--verify-whisper-server", StringComparison.OrdinalIgnoreCase));
            if (localWhisper is not null)
            {
                var endpoint = localWhisper.Contains('=')
                    ? localWhisper.Split('=', 2)[1]
                    : "192.168.227.185:8100";
                return Verification.RunLocalWhisperLive(endpoint);
            }
            if (args.Contains("--verify-lte", StringComparer.OrdinalIgnoreCase))
                return Verification.RunLteCellSearch();
            if (args.Contains("--verify-ens-lte", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("--verify-neuro-lte", StringComparer.OrdinalIgnoreCase))
                return Verification.RunNeuroLteNative();
            if (args.Contains("--verify-lte-live", StringComparer.OrdinalIgnoreCase))
                return Verification.RunLteLiveSdrplay();
            if (args.Contains("--verify-lte-live-native", StringComparer.OrdinalIgnoreCase))
                return Verification.RunLteLiveSdrplayNativeOnly();
            if (args.Contains("--verify-lte-live-compare", StringComparer.OrdinalIgnoreCase))
                return Verification.RunLteLiveManagedVsNative();
            if (args.Contains("--verify-rate-change", StringComparer.OrdinalIgnoreCase))
                return Verification.RunCaptureRateChange();
            if (args.Contains("--verify-sdrplay", StringComparer.OrdinalIgnoreCase))
                return Verification.RunSdrplay();
            if (args.Contains("--verify-audio", StringComparer.OrdinalIgnoreCase))
                return Verification.RunAudio();
            if (args.Contains("--probe-rtlsdr", StringComparer.OrdinalIgnoreCase))
            {
                var result = Hardware.RtlSdrSampleSource.ProbeNativeLibrary();
                Console.WriteLine(result);
                return result.StartsWith("OK", StringComparison.Ordinal) ? 0 : 90;
            }
            if (args.Contains("--probe-airspy", StringComparer.OrdinalIgnoreCase))
            {
                var result = Hardware.AirspySampleSource.ProbeNativeLibrary();
                Console.WriteLine(result);
                return result.StartsWith("OK", StringComparison.Ordinal) ? 0 : 91;
            }
            if (args.Contains("--probe-airspyhf", StringComparer.OrdinalIgnoreCase))
            {
                var result = Hardware.AirspyHfSampleSource.ProbeNativeLibrary();
                Console.WriteLine(result);
                return result.StartsWith("OK", StringComparison.Ordinal) ? 0 : 92;
            }
            if (args.Contains("--probe-soapy", StringComparer.OrdinalIgnoreCase))
            {
                var result = Hardware.SoapySdrSampleSource.ProbeNativeLibrary();
                Console.WriteLine(result);
                return result.StartsWith("OK", StringComparison.Ordinal) ? 0 : 93;
            }
            if (args.Contains("--verify-rtlsdr", StringComparer.OrdinalIgnoreCase))
                return Verification.RunRtlSdr();
            if (args.Contains("--verify-hackrf", StringComparer.OrdinalIgnoreCase))
                return Verification.RunHackRf();
            if (args.Contains("--verify-airspy", StringComparer.OrdinalIgnoreCase))
                return Verification.RunAirspy();
            if (args.Contains("--verify-airspyhf", StringComparer.OrdinalIgnoreCase))
                return Verification.RunAirspyHf();
            if (args.Contains("--reset-hackrf", StringComparer.OrdinalIgnoreCase))
            {
                var reset = Hardware.HackRfSampleSource.TryResetConnectedDevice(out var status);
                Console.WriteLine(status);
                return reset ? 0 : 77;
            }
            if (args.Contains("--diagnose-hackrf-amp", StringComparer.OrdinalIgnoreCase))
            {
                var frequencyArgument = args.FirstOrDefault(argument => argument.StartsWith("--frequency=", StringComparison.OrdinalIgnoreCase));
                var frequency = frequencyArgument is not null && long.TryParse(frequencyArgument.Split('=', 2)[1], out var parsedFrequency)
                    ? parsedFrequency : 89_100_000;
                return Diagnostics.HackRfAmpTest.Run(frequency);
            }
            if (args.Contains("--dump-remote-directory", StringComparer.OrdinalIgnoreCase))
            {
                var ok = Hardware.RemoteSdrCatalog.RefreshOfficialDirectoriesAsync().GetAwaiter().GetResult();
                var all = Hardware.RemoteSdrCatalog.LoadDirectory();
                Console.WriteLine($"refreshed={ok} total={all.Count}");
                foreach (var protocol in new[] { "WebSDR", "KiwiSDR", "OpenWebRX" })
                {
                    Console.WriteLine($"== {protocol} ==");
                    var i = 0;
                    foreach (var entry in all.Where(item => item.Protocol == protocol).Take(5))
                    {
                        i++;
                        Console.WriteLine($"{i}. {entry.Name}");
                        Console.WriteLine($"   {entry.Country} / {entry.City}  gps={entry.Latitude?.ToString("F4")},{entry.Longitude?.ToString("F4")}  {entry.Url}");
                    }
                }
                return 0;
            }
            var webSourceTest = args.FirstOrDefault(argument => argument.StartsWith("--verify-web-source=", StringComparison.OrdinalIgnoreCase));
            if (webSourceTest is not null)
            {
                var values = webSourceTest.Split('=', 2)[1].Split('|', 3);
                return values.Length >= 2
                    ? Verification.RunWebSourceAsync(values[0], values[1]).GetAwaiter().GetResult()
                    : 80;
            }
            var webSoak = args.FirstOrDefault(argument => argument.StartsWith("--soak-web-source=", StringComparison.OrdinalIgnoreCase));
            if (webSoak is not null)
            {
                var values = webSoak.Split('=', 2)[1].Split('|');
                if (values.Length < 2) return 80;
                var secs = values.Length >= 3 && int.TryParse(values[2], out var s) ? s : 120;
                return Verification.RunWebSourceLongSoakAsync(values[0], values[1], secs).GetAwaiter().GetResult();
            }
            var kiwiScan = args.FirstOrDefault(argument => argument.StartsWith("--verify-kiwi-scan", StringComparison.OrdinalIgnoreCase));
            if (kiwiScan is not null)
            {
                var url = kiwiScan.Contains('=') ? kiwiScan.Split('=', 2)[1] : "";
                return Verification.RunKiwiScanOccupancyAsync(url).GetAwaiter().GetResult();
            }
            if (args.Contains("--verify-ai-scan", StringComparer.OrdinalIgnoreCase))
                return Verification.RunAiScanLogicVerification();
            var kiwiCaption = args.FirstOrDefault(argument =>
                argument.StartsWith("--verify-kiwi-caption", StringComparison.OrdinalIgnoreCase));
            if (kiwiCaption is not null)
            {
                var values = kiwiCaption.Contains('=')
                    ? kiwiCaption.Split('=', 2)[1].Split('|')
                    : [];
                var url = values.Length > 0 ? values[0] : "";
                var frequency = values.Length > 1 && long.TryParse(values[1], out var parsedFrequency)
                    ? parsedFrequency
                    : 6_040_000;
                return Verification.RunKiwiCaptionProbeAsync(url, frequency).GetAwaiter().GetResult();
            }
            var kiwiProbe = args.FirstOrDefault(argument => argument.StartsWith("--probe-kiwi=", StringComparison.OrdinalIgnoreCase));
            if (kiwiProbe is not null)
            {
                var rest = kiwiProbe.Split('=', 2)[1];
                var parts = rest.Split('|');
                var url = parts[0];
                var withWf = parts.Length < 2 || !parts[1].Equals("snd", StringComparison.OrdinalIgnoreCase);
                var seconds = parts.Length >= 3 && int.TryParse(parts[2], out var s) ? s : 15;
                return Diagnostics.KiwiPlonskProbe.RunAsync(url, withWf, seconds).GetAwaiter().GetResult();
            }
            var soakArgument = args.FirstOrDefault(argument => argument.StartsWith("--soak-sdrplay=", StringComparison.OrdinalIgnoreCase));
            if (soakArgument is not null && int.TryParse(soakArgument.Split('=', 2)[1], out var soakSeconds))
            {
                var rateArgument = args.FirstOrDefault(argument => argument.StartsWith("--sample-rate=", StringComparison.OrdinalIgnoreCase));
                var sampleRate = rateArgument is not null && int.TryParse(rateArgument.Split('=', 2)[1], out var parsedRate)
                    ? parsedRate : 2_000_000;
                return Diagnostics.PipelineSoakTest.Run(Math.Clamp(soakSeconds, 5, 600), sampleRate);
            }
            var continuityArgument = args.FirstOrDefault(argument => argument.StartsWith("--diagnose-audio=", StringComparison.OrdinalIgnoreCase));
            if (continuityArgument is not null)
            {
                var device = continuityArgument.Split('=', 2)[1];
                var secondsArgument = args.FirstOrDefault(argument => argument.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
                var rateArgument = args.FirstOrDefault(argument => argument.StartsWith("--sample-rate=", StringComparison.OrdinalIgnoreCase));
                var seconds = secondsArgument is not null && int.TryParse(secondsArgument.Split('=', 2)[1], out var parsedSeconds) ? parsedSeconds : 15;
                var rate = rateArgument is not null && int.TryParse(rateArgument.Split('=', 2)[1], out var parsedRate) ? parsedRate : 2_000_000;
                var display = args.Contains("--with-display", StringComparer.OrdinalIgnoreCase);
                var modeArgument = args.FirstOrDefault(argument => argument.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase));
                var mode = modeArgument is not null && Enum.TryParse<Core.RadioMode>(modeArgument.Split('=', 2)[1], true, out var parsedMode)
                    ? parsedMode : Core.RadioMode.WFM;
                var frequencyArgument = args.FirstOrDefault(argument => argument.StartsWith("--frequency=", StringComparison.OrdinalIgnoreCase));
                var frequency = frequencyArgument is not null && long.TryParse(frequencyArgument.Split('=', 2)[1], out var parsedFrequency)
                    ? parsedFrequency : 89_100_000;
                var gainArgument = args.FirstOrDefault(argument => argument.StartsWith("--gain=", StringComparison.OrdinalIgnoreCase));
                var gain = gainArgument is not null && int.TryParse(gainArgument.Split('=', 2)[1], out var parsedGain)
                    ? Math.Clamp(parsedGain, 0, 100) : 60;
                var hardwareAgc = args.Contains("--hardware-agc", StringComparer.OrdinalIgnoreCase);
                var centerArgument = args.FirstOrDefault(argument => argument.StartsWith("--center=", StringComparison.OrdinalIgnoreCase));
                long? center = centerArgument is not null && long.TryParse(centerArgument.Split('=', 2)[1], out var parsedCenter) ? parsedCenter : null;
                var offsetArgument = args.FirstOrDefault(argument => argument.StartsWith("--demod-offset=", StringComparison.OrdinalIgnoreCase));
                long? demodOffset = offsetArgument is not null && long.TryParse(offsetArgument.Split('=', 2)[1], out var parsedOffset) ? parsedOffset : null;
                var postProcess = args.Contains("--post-process", StringComparer.OrdinalIgnoreCase);
                var afLowArgument = args.FirstOrDefault(argument => argument.StartsWith("--af-low=", StringComparison.OrdinalIgnoreCase));
                var afLow = afLowArgument is not null && int.TryParse(afLowArgument.Split('=', 2)[1], out var parsedAfLow) ? parsedAfLow : 0;
                var afHighArgument = args.FirstOrDefault(argument => argument.StartsWith("--af-high=", StringComparison.OrdinalIgnoreCase));
                var afHigh = afHighArgument is not null && int.TryParse(afHighArgument.Split('=', 2)[1], out var parsedAfHigh) ? parsedAfHigh : 20_000;
                var nrArgument = args.FirstOrDefault(argument => argument.StartsWith("--noise-reduction=", StringComparison.OrdinalIgnoreCase));
                var nr = nrArgument is not null && int.TryParse(nrArgument.Split('=', 2)[1], out var parsedNr) ? parsedNr : 0;
                var enfilter = args.Contains("--enfilter", StringComparer.OrdinalIgnoreCase);
                return Diagnostics.AudioContinuityTest.Run(device, Math.Clamp(seconds, 5, 600), rate, display, mode,
                    frequency, gain, hardwareAgc, center, demodOffset, postProcess, afLow, afHigh, nr, enfilter);
            }
            if (args.Contains("--probe-digital-disc", StringComparer.OrdinalIgnoreCase))
                return Diagnostics.DigitalDiscFeedProbe.Run();
            var dsdProbeArgument = args.FirstOrDefault(argument => argument.StartsWith("--dsd-probe=", StringComparison.OrdinalIgnoreCase));
            if (dsdProbeArgument is not null)
            {
                var frequency = long.TryParse(dsdProbeArgument.Split('=', 2)[1], out var parsedFrequency)
                    ? parsedFrequency : 464_500_000L;
                var secondsArgument = args.FirstOrDefault(argument => argument.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
                var seconds = secondsArgument is not null && int.TryParse(secondsArgument.Split('=', 2)[1], out var parsedSeconds)
                    ? Math.Clamp(parsedSeconds, 10, 600) : 45;
                var rateArgument = args.FirstOrDefault(argument => argument.StartsWith("--sample-rate=", StringComparison.OrdinalIgnoreCase));
                var rate = rateArgument is not null && int.TryParse(rateArgument.Split('=', 2)[1], out var parsedRate)
                    ? parsedRate : 2_000_000;
                var gainArgument = args.FirstOrDefault(argument => argument.StartsWith("--gain=", StringComparison.OrdinalIgnoreCase));
                var gain = gainArgument is not null && int.TryParse(gainArgument.Split('=', 2)[1], out var parsedGain)
                    ? Math.Clamp(parsedGain, 0, 100) : 55;
                var bwArgument = args.FirstOrDefault(argument => argument.StartsWith("--bandwidth=", StringComparison.OrdinalIgnoreCase));
                var bandwidth = bwArgument is not null && int.TryParse(bwArgument.Split('=', 2)[1], out var parsedBw)
                    ? parsedBw : 12_500;
                var invert = args.Contains("--invert", StringComparer.OrdinalIgnoreCase);
                var statusPath = Path.Combine(Path.GetTempPath(), "neurosdr-dsd-live-status.txt");
                return Diagnostics.DsdFmeLiveProbe.Run(frequency, seconds, rate, invert, gain, bandwidth, statusPath);
            }
            var kiwiUi = args.FirstOrDefault(argument => argument.StartsWith("--ui-kiwi-start=", StringComparison.OrdinalIgnoreCase));
            if (kiwiUi is not null)
            {
                var rest = kiwiUi.Split('=', 2)[1];
                var parts = rest.Split('|');
                var url = parts[0];
                var seconds = parts.Length >= 2 && int.TryParse(parts[1], out var s) ? Math.Clamp(s, 8, 60) : 18;
                ApplicationConfiguration.Initialize();
                Application.SetColorMode(SystemColorMode.Dark);
                using var testForm = new frmNeuroSDR(true) { Opacity = 0, ShowInTaskbar = false };
                testForm.BeginKiwiConnectDiagnostic(url, seconds);
                Application.Run(testForm);
                return testForm.AutomatedExitCode;
            }
            if (args.Contains("--web-exit-test", StringComparer.OrdinalIgnoreCase))
            {
                ApplicationConfiguration.Initialize();
                Application.SetColorMode(SystemColorMode.Dark);
                using var testForm = new frmNeuroSDR(false) { Opacity = 0, ShowInTaskbar = false };
                testForm.BeginWebExitTest();
                Application.Run(testForm);
                return testForm.AutomatedExitCode;
            }
            var ctcssLive = args.FirstOrDefault(a => a.StartsWith("--ctcss-live", StringComparison.OrdinalIgnoreCase));
            if (ctcssLive is not null)
            {
                long freq = 448_800_000;
                var parts = ctcssLive.Split('=', 2);
                if (parts.Length == 2 && long.TryParse(parts[1], out var parsed)) freq = parsed;
                var secondsArgument = args.FirstOrDefault(a => a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase));
                var seconds = secondsArgument is not null && int.TryParse(secondsArgument.Split('=', 2)[1], out var s) ? s : 20;
                var rateArgument = args.FirstOrDefault(a => a.StartsWith("--sample-rate=", StringComparison.OrdinalIgnoreCase));
                var sampleRate = rateArgument is not null && int.TryParse(rateArgument.Split('=', 2)[1], out var rate) ? rate : 5_000_000;
                return Diagnostics.CtcssLiveProbe.Run(freq, seconds, sampleRate);
            }
            var dcsIq = args.FirstOrDefault(a => a.StartsWith("--dcs-iq=", StringComparison.OrdinalIgnoreCase));
            if (dcsIq is not null)
            {
                var iqPath = dcsIq.Split('=', 2)[1];
                var bwArgument = args.FirstOrDefault(a => a.StartsWith("--bandwidth=", StringComparison.OrdinalIgnoreCase));
                var bw = bwArgument is not null && int.TryParse(bwArgument.Split('=', 2)[1], out var b) ? b : 12_500;
                var tuneArgument = args.FirstOrDefault(a => a.StartsWith("--tune=", StringComparison.OrdinalIgnoreCase));
                long? tune = tuneArgument is not null && long.TryParse(tuneArgument.Split('=', 2)[1], out var thz) ? thz : null;
                var expectArgument = args.FirstOrDefault(a => a.StartsWith("--expect=", StringComparison.OrdinalIgnoreCase));
                int? expect = expectArgument is not null && int.TryParse(expectArgument.Split('=', 2)[1], out var ec) ? ec : null;
                return Diagnostics.DcsIqOffline.Run(iqPath, bw, tune, expect);
            }
            if (args.Contains("--survey-rf", StringComparer.OrdinalIgnoreCase))
                return Diagnostics.RfSurveyTest.Run();

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.SetColorMode(SystemColorMode.Dark);
            Application.Run(new frmNeuroSDR(pipelineLog, forcedUiSampleRate));
            return 0;
        }
    }
}
