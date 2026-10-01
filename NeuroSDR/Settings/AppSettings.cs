using NeuroSDR.Core;
using NeuroSDR.Plugins.Caption;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeuroSDR.Settings;

internal sealed class DisplayLevelProfile
{
    public float SpectrumOffsetDb { get; set; }
    public float WaterfallOffsetDb { get; set; }
    public bool SpectrumAuto { get; set; }
    public bool WaterfallAuto { get; set; }
}

internal sealed class AppSettings
{
    public int SettingsVersion { get; set; }
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 780;
    public bool WindowMaximized { get; set; }
    public string SpectrumPluginId { get; set; } = "builtin.spectrum.neon-line";
    public string WaterfallPluginId { get; set; } = "builtin.waterfall.night";
    public string SourceName { get; set; } = string.Empty;
    public string WebSdrUrl { get; set; } = "http://websdr.ewi.utwente.nl:8901/";
    public string KiwiSdrUrl { get; set; } = "http://kiwisdr.areg.org.au:8073/";
    public string OpenWebRxUrl { get; set; } = "https://websdr.120v.ac/";
    public List<RemoteSdrFavorite> RemoteSdrFavorites { get; set; } = [];
    public long TunedFrequency { get; set; } = 28_000_000;
    public long RfCenterFrequency { get; set; } = 28_000_000;
    public long ViewCenterFrequency { get; set; } = 28_000_000;
    public int ViewBandwidth { get; set; } = 1_000_000;
    public Dictionary<string, int> HardwareSampleRates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int RfDisplayFramesPerSecond { get; set; } = 15;
    public int RfFftQuality { get; set; } = 1;
    public bool PipelineDiagnosticsEnabled { get; set; }
    public float SpectrumLevelOffsetDb { get; set; }
    public float WaterfallLevelOffsetDb { get; set; }
    /// <summary>
    /// Spectrum/waterfall level and AUTO flags. Local radios use the source name.
    /// WebSDR, KiwiSDR, and OpenWebRX use source name plus site URL.
    /// </summary>
    public Dictionary<string, DisplayLevelProfile> DisplayLevelProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public RadioMode Mode { get; set; } = RadioMode.AM;
    public int FilterBandwidth { get; set; } = 10_000;
    public int RfGain { get; set; } = 75;
    public bool SdrplayHardwareAgcEnabled { get; set; }
    public bool SdrplaySampleRateGainCompensationEnabled { get; set; }
    public bool ExtendedAfAgcRangeEnabled { get; set; }
    public bool HackRfAmplifierEnabled { get; set; }
    public bool AudioEnabled { get; set; } = true;
    public bool CwLowerSide { get; set; } = true;
    public bool AfFilterEnabled { get; set; }
    public int AfLowCutHz { get; set; }
    public int AfHighCutHz { get; set; } = 20_000;
    public int DigitalVoiceOutputChannel { get; set; } = 1;
    public bool DigitalVoiceFeedAgc { get; set; }
    /// <summary>
    /// When set, DMR/D-STAR/C4FM/FreeDV lower RF gain only while the front end is overloaded.
    /// The slider value stays the ceiling. Unchecked leaves RF gain alone.
    /// </summary>
    public bool DigitalModeAgc { get; set; }
    /// <summary>When set, the RX site combo lists only sites that passed a receive check.</summary>
    public bool RemoteSdrReachableOnly { get; set; }
    /// <summary>When set, the RX site combo lists higher reception scores first.</summary>
    public bool RemoteSdrPreferReception { get; set; }
    public int DigitalVoiceFeedVolume { get; set; } = 70;
    /// <summary>Rf | Wav | LineIn — AF into dsdfme (DSD-FME style external feed).</summary>
    public string DigitalVoiceFeedSource { get; set; } = "Rf";
    /// <summary>0-based waveIn device id (−1 = Windows default).</summary>
    public int DigitalVoiceLineInDeviceId { get; set; } = -1;
    public string DigitalVoiceWavPath { get; set; } = "";
    /// <summary>Auto | 700D | 700E | 1600 | 700C</summary>
    public string FreeDvModem { get; set; } = "Auto";
    /// <summary>Auto | LSB | USB</summary>
    public string FreeDvSideband { get; set; } = "Auto";
    public bool AfcEnabled { get; set; }
    public int AfcSpeedIndex { get; set; } = 1;
    public int AfcRangeHz { get; set; } = 1_000;
    public bool WfmStereoEnabled { get; set; }
    public bool WfmHfSoftEnabled { get; set; } = true;
    public bool WfmHideAfPlugins { get; set; } = true;
    public float[] WfmEqGainsDb { get; set; } = new float[10];
    public string WfmEqSelectedPreset { get; set; } = "Normal";
    public List<WfmEqPreset> WfmEqPresets { get; set; } = [];
    public List<WfmStation> WfmStations { get; set; } = [];
    public List<RxScene> RxScenes { get; set; } = [];
    public string SelectedRxSceneId { get; set; } = "";
    public RfDisplayMode RfDisplayMode { get; set; } = RfDisplayMode.SpectrumWaterfall;
    public int CwAfFilterWidthHz { get; set; } = 200;
    public int CwAfFilterCenterHz { get; set; } = 500;
    public bool CwAfFilterAutoPeak { get; set; } = true;
    public bool EnFilterVoiceEnabled { get; set; }
    public bool EnFilterVoiceBlanker { get; set; }
    public bool EnFilterVoiceNotch { get; set; }
    public bool EnFilterVoiceAgc { get; set; }
    public bool EnFilterVoiceEq { get; set; } = true;
    public int EnFilterVoiceWetPercent { get; set; } = 100;
    public bool EnFilterCwEnabled { get; set; }
    public bool EnFilterCwBpf { get; set; } = true;
    public bool EnFilterCwAle { get; set; } = true;
    public bool EnFilterCwApf { get; set; } = true;
    public bool EnFilterCwGate { get; set; } = true;
    public float EnFilterCwCenterHz { get; set; } = 700;
    public int EnFilterCwBandwidthHz { get; set; } = 70;
    public List<string> EnabledAfPluginIds { get; set; } = [];
    public List<AfPluginInstanceSettings> AfPluginInstances { get; set; } = [];
    public List<string> EnabledIqPluginIds { get; set; } = [];
    public bool SingleActiveAfPlugin { get; set; } = true;
    public List<SubVfoSettings> SubVfos { get; set; } = [];
    public Dictionary<string, string> AfPluginVfoRoutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string FtxMode { get; set; } = "FT8";
    public double FtxTimeAdjustSeconds { get; set; }
    public bool FtxAutoTimeAdjust { get; set; }
    public bool FtxShowQsoLines { get; set; } = true;
    public bool FtxShowOnMainWaterfall { get; set; }
    public bool CwShowOnAfWaterfall { get; set; }
    public bool SatelliteModeEnabled { get; set; }
    public bool SatelliteAutoTrack { get; set; } = true;
    public bool SatelliteAutoRefresh { get; set; }
    /// <summary>When true, map/sky/pass list hide satellites with no published TX/RX frequency.</summary>
    public bool SatelliteShowFrequencyOnly { get; set; } = true;
    public bool SatelliteSoloTrack { get; set; }
    public int SatelliteSoloNorad { get; set; }
    public bool SatelliteAutoSdrHandoff { get; set; }
    /// <summary>Handover when the tracked satellite drops below this elevation at the current site.</summary>
    public int SatelliteHandoffMinElevation { get; set; } = 12;
    /// <summary>Handover when VFO-channel SNR vs adjacent FFT bins falls below this (dB).</summary>
    public int SatelliteHandoffSnrDb { get; set; } = 6;
    public double SatelliteLatitude { get; set; }
    public double SatelliteLongitude { get; set; }
    public double SatelliteAltitudeMeters { get; set; } = 50;
    public string SatelliteLocationLabel { get; set; } = "Home";
    /// <summary>How often to refresh TLE cache (hours).</summary>
    public int SatelliteCacheRefreshHours { get; set; } = 12;
    /// <summary>How often to refresh Palewire frequency cache (hours).</summary>
    public int SatelliteFrequencyRefreshHours { get; set; } = 24;
    /// <summary>How often to refresh AMSAT recently-heard status (minutes).</summary>
    public int SatelliteAmsatStatusRefreshMinutes { get; set; } = 15;
    public List<SatelliteHomeLocation> SatelliteHomes { get; set; } = [];
    public string SatelliteSelectedHomeId { get; set; } = "";
    public List<SatelliteTrackPreference> SatelliteTrackPreferences { get; set; } = [];
    public string SstvBackend { get; set; } = "managed";
    public bool SstvAutoVis { get; set; } = true;
    public int SstvManualMode { get; set; }
    public int SstvFrequencyShiftHz { get; set; }
    public bool SstvAdaptive { get; set; } = true;
    public bool SstvWeakSignal { get; set; }
    public bool SstvSlant { get; set; } = true;
    public bool SstvMedian { get; set; } = true;
    public bool SstvFskId { get; set; } = true;
    public double RttyBaud { get; set; } = 50;
    public int RttyCenterHz { get; set; } = 1_000;
    public int RttyDeviationHz { get; set; } = 225;
    public bool RttyInverse { get; set; }
    public bool RttyAutoPolarity { get; set; } = true;
    public bool RttyUsos { get; set; }
    public bool RttyUnshiftOnError { get; set; } = true;
    public double RttyStopBits { get; set; } = 1;
    public int WeatherFaxLpm { get; set; } = 120;
    public double WeatherFaxCalibration { get; set; }
    public bool WeatherFaxGrayscale { get; set; } = true;
    public bool WeatherFaxVideoFilter { get; set; }
    public bool WeatherFaxSkipStartTone { get; set; } = true;
    public int WeatherFaxImageWidth { get; set; } = 1_810;
    public int KiwiNavtexCenterHz { get; set; } = 1_000;
    public int KiwiNavtexDeviationHz { get; set; } = 85;
    public bool KiwiNavtexInverse { get; set; } = true;
    public bool KiwiNavtexPll { get; set; }
    public int KiwiWwvToneHz { get; set; } = 100;
    public bool KiwiWwvInverse { get; set; }
    public double FlRttyBaud { get; set; } = 45.45;
    public int FlRttyCenterHz { get; set; } = 1_000;
    public int FlRttyShiftHz { get; set; } = 170;
    public bool FlRttyInverse { get; set; }
    public bool FlRttyUos { get; set; } = true;
    public double FlRttySquelchDb { get; set; }
    public int FlCwPitchHz { get; set; } = 700;
    public bool FlCwAutoWpm { get; set; } = true;
    public int FlCwFixedWpm { get; set; } = 18;
    public double FlCwSquelchDb { get; set; }
    public int FlFaxLpm { get; set; } = 120;
    public int FlFaxCenterHz { get; set; } = 1_900;
    public int FlFaxShiftHz { get; set; } = 800;
    public bool FlFaxManual { get; set; } = true;
    public bool FlFaxPhasing { get; set; }
    public bool KiwiTimecodePll { get; set; } = true;
    public int KiwiTimecodeExponent { get; set; } = 1;
    public double KiwiTimecodeBandwidthHz { get; set; } = 10;
    public int KiwiTimecodeOffsetHz { get; set; }
    public int KiwiTimecodeGain { get; set; }
    public bool KiwiTimecodeReplaceIq { get; set; }
    public int KiwiTimecodeDisplayMode { get; set; }
    public int AfPluginDisplayWidth { get; set; } = 600;
    public int AfPluginDisplayHeight { get; set; } = 490;
    public Dictionary<string, Dictionary<string, string>> AfPluginUiState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int TunerFrequencyOffsetHz { get; set; }
    public bool AgcEnabled { get; set; } = true;
    public bool NoiseReductionEnabled { get; set; }
    public int NoiseReductionStrength { get; set; } = 55;
    public bool NotchEnabled { get; set; }
    public int NotchFrequency { get; set; } = 1_000;
    public AudioChannelSettings Audio1 { get; set; } = new() { Enabled = true, DeviceId = -1, Volume = 45, SquelchThreshold = -75 };
    public AudioChannelSettings Audio2 { get; set; } = new() { Enabled = false, DeviceId = -1, Volume = 45, SquelchThreshold = -75 };

    /// <summary>Embedded ASP.NET remote UI (phone / desktop browser / PWA).</summary>
    public bool WebRemoteEnabled { get; set; }
    public int WebRemotePort { get; set; } = 8765;
    public bool WebRemoteBindAllInterfaces { get; set; }
    public string WebRemoteAccessToken { get; set; } = "";
    /// <summary>rtl_tcp host:port used when the rtl_tcp source is selected.</summary>
    public string RtlTcpEndpoint { get; set; } = "127.0.0.1:1234";

    /// <summary>Offline voice guidance (eSpeak NG) for frequency / mode changes.</summary>
    public bool VoiceGuidanceEnabled { get; set; }

    /// <summary>GLOBAL SCENE frequency buttons (visible in every scene).</summary>
    public List<SceneFrequencyChannel> GlobalFrequencyChannels { get; set; } = [];
    public string SelectedSceneChannelId { get; set; } = "";
    /// <summary>Main VFO signal-follow / standby scan window.</summary>
    public AutoTuneSettings MainAutoTune { get; set; } = new();

    /// <summary>Timed AF / FT8 Smart Record jobs.</summary>
    public List<SmartRecordJob> SmartRecordJobs { get; set; } = [];
    public string SmartRecordOutputFolder { get; set; } = "";

    /// <summary>Draw on-air EiBi shortwave broadcasts on the main RF waterfall.</summary>
    public bool EibiShowOnMainWaterfall { get; set; }
}

internal sealed class SubVfoSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "SUB 1";
    public long Frequency { get; set; } = 7_074_000;
    public RadioMode Mode { get; set; } = RadioMode.USB;
    public int Bandwidth { get; set; } = 2_700;
    public bool SquelchEnabled { get; set; }
    public int SquelchLevel { get; set; } = -75;
    // 0 = decoder only, 1 = output CH1, 2 = output CH2.
    public int OutputChannel { get; set; }
    public AutoTuneSettings AutoTune { get; set; } = new();

    public SubVfoSettings Clone()
    {
        var copy = (SubVfoSettings)MemberwiseClone();
        copy.AutoTune = AutoTune?.Clone() ?? new AutoTuneSettings();
        return copy;
    }
}

internal sealed class AfPluginInstanceSettings
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public string PluginId { get; set; } = "builtin.af.ftx";
    public string Variant { get; set; } = "FT8";
    public string VfoId { get; set; } = "main";

    public AfPluginInstanceSettings Clone() => (AfPluginInstanceSettings)MemberwiseClone();
}

internal sealed class AudioChannelSettings
{
    public bool Enabled { get; set; }
    public int DeviceId { get; set; } = -1;
    public string DeviceName { get; set; } = string.Empty;
    public int Volume { get; set; } = 45;
    public bool SquelchEnabled { get; set; }
    public int SquelchThreshold { get; set; } = -75;
}

internal sealed class RemoteSdrFavorite
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Protocol { get; set; } = "WebSDR";
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Url : Name;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(DisplayLevelProfile))]
[JsonSerializable(typeof(Dictionary<string, DisplayLevelProfile>))]
[JsonSerializable(typeof(SmartRecordJob))]
[JsonSerializable(typeof(List<SmartRecordJob>))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

internal static class AppSettingsStore
{
    internal const string ProductFolder = "NeuroSDR";
    internal const string LegacyProductFolder = "EN SDR";
    /// <summary>Stamp factory-default objects so historical “add every decoder” migrations do not run.</summary>
    internal const int CurrentSettingsVersion = 23;

    /// <summary>
    /// Debug keeps the personal settings file. Release (including published builds)
    /// uses a separate file so a distribution run does not open the developer settings.
    /// </summary>
#if DEBUG
    internal static string SettingsPath { get; } = Path.Combine(ProductAppData, "settings.json");
#else
    internal static string SettingsPath { get; } = Path.Combine(ProductAppData, "settings.release.json");
#endif

    internal static string ProductAppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);

    internal static void EnsureLegacyAppDataMigrated()
    {
        var dest = ProductAppData;
        var src = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyProductFolder);
        if (!Directory.Exists(src)) return;
        try
        {
            if (!Directory.Exists(dest))
            {
                CopyDirectory(src, dest);
                return;
            }
            var destSettings = Path.Combine(dest, "settings.json");
            var srcSettings = Path.Combine(src, "settings.json");
            if (!File.Exists(destSettings) && File.Exists(srcSettings))
                File.Copy(srcSettings, destSettings);
        }
        catch { /* keep using empty/new settings if copy fails */ }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    public static AppSettings Load()
    {
        EnsureLegacyAppDataMigrated();
        try
        {
            if (!File.Exists(SettingsPath)) return FactoryDefaults();
            var settings = JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), AppSettingsJsonContext.Default.AppSettings) ?? FactoryDefaults();
            var loadedVersion = settings.SettingsVersion;
            // 378 was the short-lived old default. Preserve deliberate custom sizes,
            // while migrating that default to the balanced 370 px RX/AF layout.
            if (settings.AfPluginDisplayHeight == 378) settings.AfPluginDisplayHeight = 370;
            if (settings.AfPluginDisplayHeight == 370) settings.AfPluginDisplayHeight = 420;
            if (settings.AfPluginDisplayHeight == 420) settings.AfPluginDisplayHeight = 490;
            if (settings.AfPluginDisplayWidth is 515 or 0) settings.AfPluginDisplayWidth = 600;
            settings = Migrate(settings);
            if (settings.SettingsVersion != loadedVersion) Save(settings);
            return settings;
        }
        catch { return FactoryDefaults(); }
    }

    /// <summary>
    /// First run / Config Reset. Empty decoder list, 28 MHz AM. Version is current so
    /// old migrations that auto-enabled FT8/CW/SSTV/fldigi do not run.
    /// </summary>
    public static AppSettings FactoryDefaults()
    {
        return Migrate(new AppSettings
        {
            SettingsVersion = CurrentSettingsVersion,
            TunedFrequency = 28_000_000,
            RfCenterFrequency = 28_000_000,
            ViewCenterFrequency = 28_000_000,
            ViewBandwidth = 1_000_000,
            Mode = RadioMode.AM,
            FilterBandwidth = 10_000,
            EnabledAfPluginIds = [],
            AfPluginInstances = [],
            EnabledIqPluginIds = []
        });
    }

    private static readonly object SaveGate = new();

    public static void Save(AppSettings settings)
    {
        WriteSerialized(Serialize(settings));
    }

    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);

    public static bool TryLoadFromJson(string json, out AppSettings settings, out string? error)
    {
        settings = new AppSettings();
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The file is empty.";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            if (parsed is null)
            {
                error = "The file is not a NeuroSDR settings JSON.";
                return false;
            }
            settings = Migrate(parsed);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void WriteSerialized(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        lock (SaveGate)
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, SettingsPath, true);
        }
    }

    public static void Reset()
    {
        try { if (File.Exists(SettingsPath)) File.Delete(SettingsPath); } catch { }
        PluginSelectionStoreCompatibility.DeleteLegacy();
    }

    private static AppSettings MigrateLegacyPluginSelection(AppSettings settings)
    {
        var legacy = PluginSelectionStoreCompatibility.LoadLegacy();
        if (legacy is null) return settings;
        settings.SpectrumPluginId = legacy.Value.Spectrum;
        settings.WaterfallPluginId = legacy.Value.Waterfall;
        return settings;
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        settings = MigrateLegacyPluginSelection(settings);
        if (settings.SettingsVersion < 1)
        {
            foreach (var id in new[] {
                "builtin.af.sstv", "builtin.af.rtty", "builtin.af.weatherfax",
                "builtin.af.kiwinavtex" })
                if (!settings.EnabledAfPluginIds.Contains(id, StringComparer.OrdinalIgnoreCase)) settings.EnabledAfPluginIds.Add(id);
            settings.SettingsVersion = 1;
        }
        if (settings.SettingsVersion < 2)
        {
            settings.EnabledIqPluginIds ??= [];
            if (!settings.EnabledIqPluginIds.Contains("builtin.iq.kiwitimecode", StringComparer.OrdinalIgnoreCase))
                settings.EnabledIqPluginIds.Add("builtin.iq.kiwitimecode");
            settings.SettingsVersion = 2;
        }
        if (settings.SettingsVersion < 3)
        {
            // Formerly auto-enabled built-in CW skimmer; that decoder is an optional external AF plugin now.
            settings.SettingsVersion = 3;
        }
        if (settings.SettingsVersion < 4)
        {
            // Until version 3, RSP1 captured 2 MS/s and 2 MHz represented its
            // full view. Expand that legacy full-view value once now that the
            // source runs at 10 MS/s. Later user-selected zoom levels persist.
            if (settings.ViewBandwidth == 2_000_000 &&
                settings.SourceName.StartsWith("RSP", StringComparison.OrdinalIgnoreCase))
                settings.ViewBandwidth = 10_000_000;
            settings.SettingsVersion = 4;
        }
        settings.HardwareSampleRates ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (settings.SettingsVersion < 5)
        {
            // Version 4 temporarily made 10 MS/s the unconditional RSP1 rate.
            // Return existing installations to the real-time-safe default;
            // users can explicitly select 5/10 MS/s in SETUP.
            if (settings.SourceName.StartsWith("RSP", StringComparison.OrdinalIgnoreCase))
                settings.HardwareSampleRates[settings.SourceName] = 2_000_000;
            settings.RfDisplayFramesPerSecond = 15;
            settings.RfFftQuality = 1;
            settings.SettingsVersion = 5;
        }
        if (settings.SettingsVersion < 6)
        {
            // Official HackRF guidance recommends RF amp OFF as the safe
            // starting point. It can overload, and a damaged amp path may
            // attenuate instead of amplify when selected.
            settings.HackRfAmplifierEnabled = false;
            settings.SettingsVersion = 6;
        }
        if (settings.SettingsVersion < 7)
        {
            // The first multi-VFO development build could persist diagnostic SUB
            // receivers without making their restored state clear. Remove those
            // one-time development entries; VFOs created with version 7+ persist.
            settings.SubVfos ??= [];
            settings.SubVfos.Clear();
            settings.AfPluginVfoRoutes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings.AfPluginVfoRoutes.Clear();
            settings.SettingsVersion = 7;
        }
        if (settings.SettingsVersion < 8)
        {
            // Version 7 development builds could save the temporary decoder
            // routing VFOs again after the first cleanup. Start the released
            // multi-VFO state empty once; VFOs explicitly added afterwards
            // continue to persist normally.
            settings.SubVfos ??= [];
            settings.SubVfos.Clear();
            settings.AfPluginVfoRoutes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings.AfPluginVfoRoutes.Clear();
            settings.SettingsVersion = 8;
        }
        if (settings.SettingsVersion < 9)
        {
            // Wide-band hardware AGC follows total capture energy and can bury
            // a weak wanted channel. The calibrated manual gain path is safer.
            settings.SdrplayHardwareAgcEnabled = false;
            settings.SettingsVersion = 9;
        }
        if (settings.SettingsVersion < 10)
        {
            // Experimental wide-capture level compensation must be an explicit
            // user choice, never part of the default receiver path.
            settings.SdrplaySampleRateGainCompensationEnabled = false;
            settings.ExtendedAfAgcRangeEnabled = false;
            settings.SettingsVersion = 10;
        }
        if (settings.SettingsVersion < 11)
        {
            settings.AfPluginInstances ??= [];
            if (settings.AfPluginInstances.Count == 0)
            {
                foreach (var pluginId in settings.EnabledAfPluginIds.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    settings.AfPluginInstances.Add(new AfPluginInstanceSettings
                    {
                        PluginId = pluginId,
                        Variant = pluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase)
                            ? settings.FtxMode : string.Empty,
                        VfoId = settings.AfPluginVfoRoutes.GetValueOrDefault(pluginId, "main")
                    });
                }
            }
            settings.SettingsVersion = 11;
        }
        if (settings.SettingsVersion < 12)
        {
            const string wwv = "builtin.af.kiwiwwv";
            if (!settings.EnabledAfPluginIds.Contains(wwv, StringComparer.OrdinalIgnoreCase))
                settings.EnabledAfPluginIds.Add(wwv);
            settings.AfPluginInstances ??= [];
            if (!settings.AfPluginInstances.Any(instance =>
                    instance.PluginId.Equals(wwv, StringComparison.OrdinalIgnoreCase)))
            {
                settings.AfPluginInstances.Add(new AfPluginInstanceSettings
                {
                    PluginId = wwv,
                    VfoId = "main"
                });
            }
            settings.SettingsVersion = 12;
        }
        if (settings.SettingsVersion < 13)
        {
            foreach (var id in new[] { "builtin.af.flrtty", "builtin.af.flcw", "builtin.af.flfax" })
            {
                if (!settings.EnabledAfPluginIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                    settings.EnabledAfPluginIds.Add(id);
                settings.AfPluginInstances ??= [];
                if (!settings.AfPluginInstances.Any(instance =>
                        instance.PluginId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    settings.AfPluginInstances.Add(new AfPluginInstanceSettings
                    {
                        PluginId = id,
                        VfoId = "main"
                    });
                }
            }
            settings.SettingsVersion = 13;
        }
        if (settings.SettingsVersion < 14)
        {
            settings.EnabledIqPluginIds ??= [];
            if (!settings.EnabledIqPluginIds.Contains("builtin.iq.adsb", StringComparer.OrdinalIgnoreCase))
                settings.EnabledIqPluginIds.Add("builtin.iq.adsb");
            settings.SettingsVersion = 14;
        }
        if (settings.SettingsVersion < 15)
        {
            // LTE is opt-in via Plugin Setup (needs wideband IQ + future PHY bridge).
            settings.SettingsVersion = 15;
        }
        if (settings.SettingsVersion < 16)
        {
            settings.GlobalFrequencyChannels ??= [];
            settings.SelectedSceneChannelId ??= "";
            settings.MainAutoTune ??= new AutoTuneSettings();
            foreach (var scene in settings.RxScenes ?? [])
                scene.FrequencyChannels ??= [];
            foreach (var sub in settings.SubVfos ?? [])
                sub.AutoTune ??= new AutoTuneSettings();
            settings.SettingsVersion = 16;
        }
        if (settings.SettingsVersion < 17)
        {
            settings.SmartRecordJobs ??= [];
            settings.SmartRecordOutputFolder ??= "";
            settings.SettingsVersion = 17;
        }
        if (settings.SettingsVersion < 18)
        {
            // Always operate under at least one SCENE (DEFAULT).
            settings.SettingsVersion = 18;
        }
        if (settings.SettingsVersion < 19)
        {
            // Main VFO auto-follow is per SCENE (was global — UHF ranges leaked into HF scenes).
            settings.MainAutoTune ??= new AutoTuneSettings();
            settings.RxScenes ??= [];
            var selectedId = settings.SelectedRxSceneId ?? "";
            foreach (var scene in settings.RxScenes)
            {
                scene.MainAutoTune ??= new AutoTuneSettings();
                // Preserve the previously global AUTO only on the scene that was active.
                if (!string.IsNullOrWhiteSpace(selectedId) &&
                    scene.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase) &&
                    settings.MainAutoTune.Enabled)
                {
                    scene.MainAutoTune = settings.MainAutoTune.Clone();
                }
            }
            settings.SettingsVersion = 19;
        }
        if (settings.SettingsVersion < 20)
        {
            // New AUTO option: search window follows manual VFO retunes (default on).
            void EnableWindowFollow(AutoTuneSettings? auto)
            {
                if (auto is null) return;
                auto.WindowFollowsVfo = true;
            }
            EnableWindowFollow(settings.MainAutoTune);
            settings.SubVfos ??= [];
            foreach (var sub in settings.SubVfos)
                EnableWindowFollow(sub.AutoTune);
            settings.RxScenes ??= [];
            foreach (var scene in settings.RxScenes)
            {
                EnableWindowFollow(scene.MainAutoTune);
                scene.SubVfos ??= [];
                foreach (var sub in scene.SubVfos)
                    EnableWindowFollow(sub.AutoTune);
            }
            settings.SettingsVersion = 20;
        }
        if (settings.SettingsVersion < 21)
        {
            // SATELLITE is a built-in scene; satellite mode is selected via SCENE, not a toggle.
            settings.RxScenes ??= [];
            var wasSatelliteMode = settings.SatelliteModeEnabled;
            if (!settings.RxScenes.Any(scene => scene.Id.Equals(RxScene.SatelliteId, StringComparison.OrdinalIgnoreCase)))
            {
                var satelliteScene = RxScene.CreateSatelliteFrom(settings);
                if (wasSatelliteMode)
                {
                    satelliteScene.SatelliteAutoTrack = settings.SatelliteAutoTrack;
                    satelliteScene.SatelliteAutoRefresh = settings.SatelliteAutoRefresh;
                    satelliteScene.SatelliteShowFrequencyOnly = settings.SatelliteShowFrequencyOnly;
                    satelliteScene.SatelliteLatitude = settings.SatelliteLatitude;
                    satelliteScene.SatelliteLongitude = settings.SatelliteLongitude;
                    satelliteScene.SatelliteAltitudeMeters = settings.SatelliteAltitudeMeters;
                    satelliteScene.SatelliteLocationLabel = settings.SatelliteLocationLabel;
                }
                settings.RxScenes.Add(satelliteScene);
            }
            else
            {
                var satelliteScene = settings.RxScenes.First(scene =>
                    scene.Id.Equals(RxScene.SatelliteId, StringComparison.OrdinalIgnoreCase));
                satelliteScene.Name = RxScene.SatelliteName;
                satelliteScene.SatelliteModeEnabled = true;
            }

            foreach (var scene in settings.RxScenes.Where(scene =>
                         !scene.Id.Equals(RxScene.SatelliteId, StringComparison.OrdinalIgnoreCase)))
                scene.SatelliteModeEnabled = false;

            if (wasSatelliteMode)
                settings.SelectedRxSceneId = RxScene.SatelliteId;
            settings.SatelliteModeEnabled = false;
            settings.SettingsVersion = 21;
        }
        if (settings.SettingsVersion < 22)
        {
            settings.SatelliteHomes ??= [];
            if (settings.SatelliteHomes.Count == 0)
            {
                settings.SatelliteHomes.Add(new SatelliteHomeLocation
                {
                    Name = string.IsNullOrWhiteSpace(settings.SatelliteLocationLabel) ? "Home" : settings.SatelliteLocationLabel,
                    Latitude = settings.SatelliteLatitude,
                    Longitude = settings.SatelliteLongitude,
                    AltitudeMeters = settings.SatelliteAltitudeMeters
                });
            }
            settings.SatelliteTrackPreferences ??= [];
            settings.RxScenes ??= [];
            foreach (var scene in settings.RxScenes)
                scene.SatelliteTrackPreferences ??= [];
            settings.SettingsVersion = 22;
        }
        if (settings.SettingsVersion < 23)
        {
            NeuroCaption.MigrateSettings(settings);
            settings.SettingsVersion = 23;
        }
        if (settings.SettingsVersion < 24)
        {
            settings.DisplayLevelProfiles = new Dictionary<string, DisplayLevelProfile>(
            settings.DisplayLevelProfiles ?? [], StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(settings.SourceName))
            {
                var key = settings.SourceName;
                if (settings.SourceName.Contains("WebSDR", StringComparison.OrdinalIgnoreCase))
                    key = settings.SourceName + "\n" + NormalizeLevelSite(settings.WebSdrUrl);
                else if (settings.SourceName.Contains("Kiwi", StringComparison.OrdinalIgnoreCase))
                    key = settings.SourceName + "\n" + NormalizeLevelSite(settings.KiwiSdrUrl);
                else if (settings.SourceName.Contains("OpenWeb", StringComparison.OrdinalIgnoreCase))
                    key = settings.SourceName + "\n" + NormalizeLevelSite(settings.OpenWebRxUrl);
                settings.DisplayLevelProfiles[key] = new DisplayLevelProfile
                {
                    SpectrumOffsetDb = settings.SpectrumLevelOffsetDb,
                    WaterfallOffsetDb = settings.WaterfallLevelOffsetDb
                };
            }
            settings.SettingsVersion = 24;
        }
        settings.DisplayLevelProfiles = new Dictionary<string, DisplayLevelProfile>(
            settings.DisplayLevelProfiles ?? [], StringComparer.OrdinalIgnoreCase);
        settings.RfDisplayFramesPerSecond = Math.Clamp(settings.RfDisplayFramesPerSecond, 5, 30);
        settings.RfFftQuality = Math.Clamp(settings.RfFftQuality, 0, 2);
        settings.SubVfos ??= [];
        settings.AfPluginInstances ??= [];
        settings.AfPluginVfoRoutes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in settings.SubVfos)
        {
            if (string.IsNullOrWhiteSpace(sub.Id)) sub.Id = Guid.NewGuid().ToString("N");
            sub.Bandwidth = Math.Clamp(sub.Bandwidth, 100, 500_000);
            sub.SquelchLevel = Math.Clamp(sub.SquelchLevel, -140, 0);
            sub.OutputChannel = Math.Clamp(sub.OutputChannel, 0, 2);
        }
        var claimedOutputs = new HashSet<int>();
        foreach (var sub in settings.SubVfos)
            if (sub.OutputChannel != 0 && !claimedOutputs.Add(sub.OutputChannel)) sub.OutputChannel = 0;
        var validVfos = settings.SubVfos.Select(sub => sub.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        validVfos.Add("main");
        var instanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in settings.AfPluginInstances)
        {
            if (string.IsNullOrWhiteSpace(instance.InstanceId) || !instanceIds.Add(instance.InstanceId))
            {
                do instance.InstanceId = Guid.NewGuid().ToString("N");
                while (!instanceIds.Add(instance.InstanceId));
            }
            if (!validVfos.Contains(instance.VfoId)) instance.VfoId = "main";
            normalizedRoutes[instance.InstanceId] = instance.VfoId;
        }
        settings.AfPluginVfoRoutes = normalizedRoutes;
        settings.EnabledAfPluginIds = settings.AfPluginInstances.Select(instance => instance.PluginId)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.RemoteSdrFavorites ??= [];
        settings.AfPluginUiState ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        settings.WfmEqPresets ??= [];
        settings.WfmStations ??= [];
        settings.RxScenes ??= [];
        settings.SelectedRxSceneId ??= "";
        settings.GlobalFrequencyChannels ??= [];
        settings.SelectedSceneChannelId ??= "";
        settings.MainAutoTune ??= new AutoTuneSettings();
        settings.SmartRecordJobs ??= [];
        settings.SmartRecordOutputFolder ??= "";
        foreach (var scene in settings.RxScenes)
            scene.FrequencyChannels ??= [];
        foreach (var sub in settings.SubVfos)
            sub.AutoTune ??= new AutoTuneSettings();
        EnsureBuiltInRxScenes(settings);
        WfmEqFactoryPresets.EnsureFactory(settings.WfmEqPresets);
        if (string.IsNullOrWhiteSpace(settings.WfmEqSelectedPreset))
            settings.WfmEqSelectedPreset = "Normal";
        if (settings.WfmEqGainsDb is null || settings.WfmEqGainsDb.Length != 10)
            settings.WfmEqGainsDb = WfmEqFactoryPresets.Gains(settings.WfmEqSelectedPreset);
        return settings;
    }

    internal static AppSettings MigrateForVerification(AppSettings settings) => Migrate(settings);

    private static string NormalizeLevelSite(string? url) =>
        (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>Program is always SCENE-based: DEFAULT and SATELLITE built-in scenes must exist.</summary>
    public static void EnsureBuiltInRxScenes(AppSettings settings)
    {
        settings.RxScenes ??= [];
        if (!settings.RxScenes.Any(scene => scene.Id.Equals(RxScene.DefaultId, StringComparison.OrdinalIgnoreCase)))
            settings.RxScenes.Insert(0, RxScene.CreateDefaultFrom(settings));
        else
        {
            var defaultScene = settings.RxScenes.First(scene =>
                scene.Id.Equals(RxScene.DefaultId, StringComparison.OrdinalIgnoreCase));
            defaultScene.Name = RxScene.DefaultName;
            defaultScene.SatelliteModeEnabled = false;
        }

        if (!settings.RxScenes.Any(scene => scene.Id.Equals(RxScene.SatelliteId, StringComparison.OrdinalIgnoreCase)))
            settings.RxScenes.Add(RxScene.CreateSatelliteFrom(settings));
        else
        {
            var satelliteScene = settings.RxScenes.First(scene =>
                scene.Id.Equals(RxScene.SatelliteId, StringComparison.OrdinalIgnoreCase));
            satelliteScene.Name = RxScene.SatelliteName;
            satelliteScene.SatelliteModeEnabled = true;
        }

        if (!string.IsNullOrWhiteSpace(settings.SelectedRxSceneId) &&
            settings.RxScenes.Any(scene => scene.Id.Equals(settings.SelectedRxSceneId, StringComparison.OrdinalIgnoreCase)))
            return;

        settings.SelectedRxSceneId = RxScene.DefaultId;
    }

    [Obsolete("Use EnsureBuiltInRxScenes")]
    public static void EnsureDefaultRxScene(AppSettings settings) => EnsureBuiltInRxScenes(settings);
}

internal static class PluginSelectionStoreCompatibility
{
    private static readonly string LegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppSettingsStore.LegacyProductFolder, "plugins.ini");

    public static (string Spectrum, string Waterfall)? LoadLegacy()
    {
        try
        {
            if (!File.Exists(LegacyPath)) return null;
            var values = File.ReadAllLines(LegacyPath).Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
            return (values.GetValueOrDefault("spectrum", "builtin.spectrum.neon-line"),
                values.GetValueOrDefault("waterfall", "builtin.waterfall.night"));
        }
        catch { return null; }
    }

    public static void DeleteLegacy()
    {
        try { if (File.Exists(LegacyPath)) File.Delete(LegacyPath); } catch { }
    }
}
