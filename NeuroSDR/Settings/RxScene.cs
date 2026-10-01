using NeuroSDR.Core;

namespace NeuroSDR.Settings;

/// <summary>
/// Named full-RX snapshot (extends frequency memory): VFO, RF gain, SUB VFOs, AF plugins, audio, pop-outs.
/// </summary>
internal sealed class RxScene
{
    public const string DefaultId = "default-scene";
    public const string DefaultName = "DEFAULT";
    public const string SatelliteId = "satellite-scene";
    public const string SatelliteName = "SATELLITE";

    public static bool IsBuiltInScene(string? sceneId) =>
        sceneId is not null &&
        (sceneId.Equals(DefaultId, StringComparison.OrdinalIgnoreCase) ||
         sceneId.Equals(SatelliteId, StringComparison.OrdinalIgnoreCase));

    public static bool IsSatelliteScene(string? sceneId) =>
        sceneId is not null && sceneId.Equals(SatelliteId, StringComparison.OrdinalIgnoreCase);

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scene";
    public long TunedFrequency { get; set; } = 28_000_000;
    public long RfCenterFrequency { get; set; } = 28_000_000;
    public long ViewCenterFrequency { get; set; } = 28_000_000;
    public int ViewBandwidth { get; set; } = 1_000_000;
    public RadioMode Mode { get; set; } = RadioMode.AM;
    public int FilterBandwidth { get; set; } = 10_000;
    public int RfGain { get; set; } = 75;
    public bool CwLowerSide { get; set; } = true;
    public bool AfFilterEnabled { get; set; }
    public string FtxMode { get; set; } = "FT8";
    public bool SingleActiveAfPlugin { get; set; } = true;
    public string? SelectedAfInstanceId { get; set; }
    public List<string> EnabledAfPluginIds { get; set; } = [];
    public List<AfPluginInstanceSettings> AfPluginInstances { get; set; } = [];
    public Dictionary<string, string> AfPluginVfoRoutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SubVfoSettings> SubVfos { get; set; } = [];
    /// <summary>Scene-local frequency buttons (non-GLOBAL).</summary>
    public List<SceneFrequencyChannel> FrequencyChannels { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> AfPluginUiState { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int Audio1Volume { get; set; } = 45;
    public bool Audio1SquelchEnabled { get; set; }
    public int Audio1SquelchThreshold { get; set; } = -75;
    public int Audio2Volume { get; set; } = 45;
    public bool Audio2SquelchEnabled { get; set; }
    public int Audio2SquelchThreshold { get; set; } = -75;
    public bool FtxShowOnMainWaterfall { get; set; }
    public bool CwShowOnAfWaterfall { get; set; }
    public bool SatelliteModeEnabled { get; set; }
    public bool SatelliteAutoTrack { get; set; } = true;
    public bool SatelliteAutoRefresh { get; set; }
    public bool SatelliteShowFrequencyOnly { get; set; } = true;
    public double SatelliteLatitude { get; set; }
    public double SatelliteLongitude { get; set; }
    public double SatelliteAltitudeMeters { get; set; } = 50;
    public string SatelliteLocationLabel { get; set; } = "Home";
    public List<SatelliteTrackPreference> SatelliteTrackPreferences { get; set; } = [];
    /// <summary>Main VFO auto peak-follow (min/max/trigger). Per-scene so UHF/HF scenes do not share ranges.</summary>
    public AutoTuneSettings MainAutoTune { get; set; } = new();

    /// <summary>Hardware / virtual sample source name (e.g. RTL-SDR, Virtual KiwiSDR).</summary>
    public string SourceName { get; set; } = "";
    /// <summary>When <see cref="SourceName"/> is a remote Web SDR, the site URL for this scene.</summary>
    public string RemoteServerUrl { get; set; } = "";

    public string SpectrumPluginId { get; set; } = "builtin.spectrum.neon-line";
    public string WaterfallPluginId { get; set; } = "builtin.waterfall.night";
    public List<string> EnabledIqPluginIds { get; set; } = [];
    public int AfPluginDisplayWidth { get; set; } = 600;
    public int AfPluginDisplayHeight { get; set; } = 490;
    public bool Audio1Enabled { get; set; } = true;
    public int Audio1DeviceId { get; set; } = -1;
    public string Audio1DeviceName { get; set; } = "";
    public bool Audio2Enabled { get; set; }
    public int Audio2DeviceId { get; set; } = -1;
    public string Audio2DeviceName { get; set; } = "";

    public bool RfDisplayDetached { get; set; }
    public string RfDisplayScreenDevice { get; set; } = "";
    public bool RfDisplayFullscreen { get; set; }
    public int RfDisplayX { get; set; } = -1;
    public int RfDisplayY { get; set; } = -1;
    public int RfDisplayWidth { get; set; }
    public int RfDisplayHeight { get; set; }

    public bool AfDisplayDetached { get; set; }
    public string AfDisplayScreenDevice { get; set; } = "";
    public bool AfDisplayFullscreen { get; set; }
    public int AfDisplayX { get; set; } = -1;
    public int AfDisplayY { get; set; } = -1;
    public int AfDisplayWidth { get; set; }
    public int AfDisplayHeight { get; set; }

    public override string ToString() => Name;

    public RxScene DeepClone()
    {
        return new RxScene
        {
            Id = Id,
            Name = Name,
            TunedFrequency = TunedFrequency,
            RfCenterFrequency = RfCenterFrequency,
            ViewCenterFrequency = ViewCenterFrequency,
            ViewBandwidth = ViewBandwidth,
            Mode = Mode,
            FilterBandwidth = FilterBandwidth,
            RfGain = RfGain,
            CwLowerSide = CwLowerSide,
            AfFilterEnabled = AfFilterEnabled,
            FtxMode = FtxMode,
            SingleActiveAfPlugin = SingleActiveAfPlugin,
            SelectedAfInstanceId = SelectedAfInstanceId,
            EnabledAfPluginIds = EnabledAfPluginIds.ToList(),
            AfPluginInstances = AfPluginInstances.Select(i => i.Clone()).ToList(),
            AfPluginVfoRoutes = new Dictionary<string, string>(AfPluginVfoRoutes, StringComparer.OrdinalIgnoreCase),
            SubVfos = SubVfos.Select(s => s.Clone()).ToList(),
            FrequencyChannels = FrequencyChannels.Select(c => c.Clone()).ToList(),
            AfPluginUiState = CloneUiState(AfPluginUiState),
            Audio1Volume = Audio1Volume,
            Audio1SquelchEnabled = Audio1SquelchEnabled,
            Audio1SquelchThreshold = Audio1SquelchThreshold,
            Audio2Volume = Audio2Volume,
            Audio2SquelchEnabled = Audio2SquelchEnabled,
            Audio2SquelchThreshold = Audio2SquelchThreshold,
            FtxShowOnMainWaterfall = FtxShowOnMainWaterfall,
            CwShowOnAfWaterfall = CwShowOnAfWaterfall,
            SatelliteModeEnabled = SatelliteModeEnabled,
            SatelliteAutoTrack = SatelliteAutoTrack,
            SatelliteAutoRefresh = SatelliteAutoRefresh,
            SatelliteShowFrequencyOnly = SatelliteShowFrequencyOnly,
            SatelliteLatitude = SatelliteLatitude,
            SatelliteLongitude = SatelliteLongitude,
            SatelliteAltitudeMeters = SatelliteAltitudeMeters,
            SatelliteLocationLabel = SatelliteLocationLabel,
            SatelliteTrackPreferences = SatelliteTrackPreferences.Select(item => item.Clone()).ToList(),
            MainAutoTune = MainAutoTune?.Clone() ?? new AutoTuneSettings(),
            SourceName = SourceName,
            RemoteServerUrl = RemoteServerUrl,
            SpectrumPluginId = SpectrumPluginId,
            WaterfallPluginId = WaterfallPluginId,
            EnabledIqPluginIds = EnabledIqPluginIds.ToList(),
            AfPluginDisplayWidth = AfPluginDisplayWidth,
            AfPluginDisplayHeight = AfPluginDisplayHeight,
            Audio1Enabled = Audio1Enabled,
            Audio1DeviceId = Audio1DeviceId,
            Audio1DeviceName = Audio1DeviceName,
            Audio2Enabled = Audio2Enabled,
            Audio2DeviceId = Audio2DeviceId,
            Audio2DeviceName = Audio2DeviceName,
            RfDisplayDetached = RfDisplayDetached,
            RfDisplayScreenDevice = RfDisplayScreenDevice,
            RfDisplayFullscreen = RfDisplayFullscreen,
            RfDisplayX = RfDisplayX,
            RfDisplayY = RfDisplayY,
            RfDisplayWidth = RfDisplayWidth,
            RfDisplayHeight = RfDisplayHeight,
            AfDisplayDetached = AfDisplayDetached,
            AfDisplayScreenDevice = AfDisplayScreenDevice,
            AfDisplayFullscreen = AfDisplayFullscreen,
            AfDisplayX = AfDisplayX,
            AfDisplayY = AfDisplayY,
            AfDisplayWidth = AfDisplayWidth,
            AfDisplayHeight = AfDisplayHeight
        };
    }

    public static Dictionary<string, Dictionary<string, string>> CloneUiState(
        Dictionary<string, Dictionary<string, string>> source)
    {
        var copy = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, bag) in source)
            copy[key] = new Dictionary<string, string>(bag, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>Seed DEFAULT scene from current flat settings (first run / empty scene list).</summary>
    public static RxScene CreateDefaultFrom(AppSettings settings) => new()
    {
        Id = DefaultId,
        Name = DefaultName,
        TunedFrequency = settings.TunedFrequency,
        RfCenterFrequency = settings.RfCenterFrequency,
        ViewCenterFrequency = settings.ViewCenterFrequency,
        ViewBandwidth = settings.ViewBandwidth,
        Mode = settings.Mode,
        FilterBandwidth = settings.FilterBandwidth,
        RfGain = settings.RfGain,
        CwLowerSide = settings.CwLowerSide,
        AfFilterEnabled = false,
        FtxMode = settings.FtxMode,
        SingleActiveAfPlugin = settings.SingleActiveAfPlugin,
        EnabledAfPluginIds = settings.EnabledAfPluginIds?.ToList() ?? [],
        AfPluginInstances = settings.AfPluginInstances?.Select(i => i.Clone()).ToList() ?? [],
        AfPluginVfoRoutes = new Dictionary<string, string>(
            settings.AfPluginVfoRoutes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        SubVfos = settings.SubVfos?.Select(s => s.Clone()).ToList() ?? [],
        FrequencyChannels = [],
        AfPluginUiState = CloneUiState(settings.AfPluginUiState ??
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)),
        Audio1Volume = settings.Audio1?.Volume ?? 45,
        Audio1SquelchEnabled = settings.Audio1?.SquelchEnabled ?? false,
        Audio1SquelchThreshold = settings.Audio1?.SquelchThreshold ?? -75,
        Audio2Volume = settings.Audio2?.Volume ?? 45,
        Audio2SquelchEnabled = settings.Audio2?.SquelchEnabled ?? false,
        Audio2SquelchThreshold = settings.Audio2?.SquelchThreshold ?? -75,
        FtxShowOnMainWaterfall = settings.FtxShowOnMainWaterfall,
        CwShowOnAfWaterfall = settings.CwShowOnAfWaterfall,
        SatelliteModeEnabled = false,
        SatelliteAutoTrack = settings.SatelliteAutoTrack,
        SatelliteAutoRefresh = settings.SatelliteAutoRefresh,
        SatelliteShowFrequencyOnly = settings.SatelliteShowFrequencyOnly,
        SatelliteLatitude = settings.SatelliteLatitude,
        SatelliteLongitude = settings.SatelliteLongitude,
        SatelliteAltitudeMeters = settings.SatelliteAltitudeMeters,
        SatelliteLocationLabel = settings.SatelliteLocationLabel,
        MainAutoTune = settings.MainAutoTune?.Clone() ?? new AutoTuneSettings(),
        SourceName = settings.SourceName ?? "",
        RemoteServerUrl = settings.SourceName switch
        {
            "Virtual WebSDR" => settings.WebSdrUrl ?? "",
            "Virtual KiwiSDR" => settings.KiwiSdrUrl ?? "",
            "Virtual OpenWebRX" => settings.OpenWebRxUrl ?? "",
            _ => ""
        },
        SpectrumPluginId = settings.SpectrumPluginId ?? "builtin.spectrum.neon-line",
        WaterfallPluginId = settings.WaterfallPluginId ?? "builtin.waterfall.night",
        EnabledIqPluginIds = settings.EnabledIqPluginIds?.ToList() ?? [],
        AfPluginDisplayWidth = settings.AfPluginDisplayWidth,
        AfPluginDisplayHeight = settings.AfPluginDisplayHeight,
        Audio1Enabled = settings.Audio1?.Enabled ?? true,
        Audio1DeviceId = settings.Audio1?.DeviceId ?? -1,
        Audio1DeviceName = settings.Audio1?.DeviceName ?? "",
        Audio2Enabled = settings.Audio2?.Enabled ?? false,
        Audio2DeviceId = settings.Audio2?.DeviceId ?? -1,
        Audio2DeviceName = settings.Audio2?.DeviceName ?? ""
    };

    /// <summary>Immutable satellite tracking scene (observer, passes, auto-track SUB VFOs).</summary>
    public static RxScene CreateSatelliteFrom(AppSettings settings) => new()
    {
        Id = SatelliteId,
        Name = SatelliteName,
        TunedFrequency = 145_800_000,
        RfCenterFrequency = 145_800_000,
        ViewCenterFrequency = 145_800_000,
        ViewBandwidth = 2_500_000,
        Mode = RadioMode.NFM,
        FilterBandwidth = 15_000,
        RfGain = settings.RfGain,
        CwLowerSide = settings.CwLowerSide,
        AfFilterEnabled = false,
        FtxMode = settings.FtxMode,
        SingleActiveAfPlugin = true,
        EnabledAfPluginIds = [],
        AfPluginInstances = [],
        AfPluginVfoRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        SubVfos = [],
        FrequencyChannels = [],
        AfPluginUiState = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase),
        Audio1Volume = settings.Audio1?.Volume ?? 45,
        Audio1SquelchEnabled = settings.Audio1?.SquelchEnabled ?? false,
        Audio1SquelchThreshold = settings.Audio1?.SquelchThreshold ?? -75,
        Audio2Volume = settings.Audio2?.Volume ?? 45,
        Audio2SquelchEnabled = settings.Audio2?.SquelchEnabled ?? false,
        Audio2SquelchThreshold = settings.Audio2?.SquelchThreshold ?? -75,
        SatelliteModeEnabled = true,
        SatelliteAutoTrack = settings.SatelliteAutoTrack,
        SatelliteAutoRefresh = settings.SatelliteAutoRefresh,
        SatelliteShowFrequencyOnly = settings.SatelliteShowFrequencyOnly,
        SatelliteLatitude = settings.SatelliteLatitude,
        SatelliteLongitude = settings.SatelliteLongitude,
        SatelliteAltitudeMeters = settings.SatelliteAltitudeMeters,
        SatelliteLocationLabel = settings.SatelliteLocationLabel,
        MainAutoTune = new AutoTuneSettings(),
        SourceName = settings.SourceName ?? "",
        RemoteServerUrl = settings.SourceName switch
        {
            "Virtual WebSDR" => settings.WebSdrUrl ?? "",
            "Virtual KiwiSDR" => settings.KiwiSdrUrl ?? "",
            "Virtual OpenWebRX" => settings.OpenWebRxUrl ?? "",
            _ => ""
        },
        SpectrumPluginId = settings.SpectrumPluginId ?? "builtin.spectrum.neon-line",
        WaterfallPluginId = settings.WaterfallPluginId ?? "builtin.waterfall.night",
        EnabledIqPluginIds = settings.EnabledIqPluginIds?.ToList() ?? [],
        AfPluginDisplayWidth = settings.AfPluginDisplayWidth,
        AfPluginDisplayHeight = settings.AfPluginDisplayHeight,
        Audio1Enabled = settings.Audio1?.Enabled ?? true,
        Audio1DeviceId = settings.Audio1?.DeviceId ?? -1,
        Audio1DeviceName = settings.Audio1?.DeviceName ?? "",
        Audio2Enabled = settings.Audio2?.Enabled ?? false,
        Audio2DeviceId = settings.Audio2?.DeviceId ?? -1,
        Audio2DeviceName = settings.Audio2?.DeviceName ?? ""
    };
}
