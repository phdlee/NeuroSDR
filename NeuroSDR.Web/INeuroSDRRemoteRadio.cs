namespace NeuroSDR.Web;

/// <summary>UI-thread-safe radio surface exposed to the embedded web remote.</summary>
public interface INeuroSDRRemoteRadio
{
    RadioRemoteSnapshot GetSnapshot();
    IReadOnlyList<string> GetSources();
    IReadOnlyList<string> GetModes();
    IReadOnlyList<AfPluginRemoteEvent> GetAfFeed(int maxItems = 80);
    void SetRunning(bool running);
    void SetFrequency(long hz);
    void SetMode(string mode);
    void SetBandwidth(int hz);
    void SetGain(int percent);
    /// <summary>Desktop OUT1 / OUT2 volume (does not affect web stream gain).</summary>
    void SetVolume(int channel, int percent);
    void SetSquelch(int channel, bool enabled, int thresholdDb);
    void SetSource(string name);
    void ApplyScene(string sceneId);
    void SelectChannel(string channelId);
    void SelectBand(string bandId);
    void CenterViewOnTune();
    void SetViewBandwidth(int hz);
    void NudgeFrequency(long deltaHz);
    IReadOnlyList<RemoteSiteRemoteInfo> GetSites(string query);
    void SetSiteUrl(string url);
    void SetAfDsp(bool agc, bool noiseReduction, int nrStrength, bool notch, bool afFilter);
    void SetAfc(bool enabled, int speedIndex, int rangeHz);
    void SetWfm(bool stereo, bool hfSoft, string? eqPreset);
    void SetFreedv(string modem, string sideband);
    void SetDigitalFeed(int outputChannel, bool pcmAgc, int feedVolumePercent);
    void SetCw(bool lowerSide, int afWidthHz);
    event Action<RadioRemoteSnapshot>? StateChanged;
    event Action<RadioLiveUpdate>? LiveChanged;
    event Action<SpectrumRemoteFrame>? SpectrumAvailable;
    event Action<SpectrumRemoteFrame>? AfSpectrumAvailable;
    event Action<byte[]>? AudioAvailable;
    event Action<AfPluginRemoteEvent>? AfPluginEvent;
}

public sealed class RadioLiveUpdate
{
    public bool Running { get; init; }
    public long FrequencyHz { get; init; }
    public float SignalDb { get; init; }
    public float AudioLevelDb { get; init; }
    public bool Squelch1Open { get; init; }
    public bool Squelch2Open { get; init; }
    public bool StereoLed { get; init; }
    public string Status { get; init; } = "";
}

public sealed class RadioRemoteSnapshot
{
    public bool Running { get; init; }
    public bool WebRemoteEnabled { get; init; }
    public string Source { get; init; } = "";
    public long FrequencyHz { get; init; }
    public long RfCenterHz { get; init; }
    public long ViewCenterHz { get; init; }
    public int ViewBandwidthHz { get; init; }
    public string Mode { get; init; } = "WFM";
    public int FilterBandwidthHz { get; init; }
    public int GainPercent { get; init; }
    public float SignalDb { get; init; }
    public int Volume1 { get; init; }
    public int Volume2 { get; init; }
    public int AudioSampleRate { get; init; } = 48_000;
    public bool Squelch1Enabled { get; init; }
    public int Squelch1Threshold { get; init; }
    public bool Squelch2Enabled { get; init; }
    public int Squelch2Threshold { get; init; }
    public bool Squelch1Open { get; init; }
    public bool Squelch2Open { get; init; }
    public bool Audio2Enabled { get; init; }
    /// <summary>Enabled AF plugin instances with display names (never raw GUIDs alone).</summary>
    public ActiveAfPluginRemoteInfo[] ActiveAfPlugins { get; init; } = [];
    public string[] AvailableSources { get; init; } = [];
    public string[] AvailableModes { get; init; } = [];
    public string Status { get; init; } = "";
    public string WebUrl { get; init; } = "";
    public string SelectedSceneId { get; init; } = "";
    public string SelectedSceneName { get; init; } = "";
    public string SelectedChannelId { get; init; } = "";
    public RxSceneRemoteInfo[] Scenes { get; init; } = [];
    public SceneChannelRemoteInfo[] Channels { get; init; } = [];
    public SubVfoRemoteInfo[] SubVfos { get; init; } = [];
    public BandRemoteInfo[] Bands { get; init; } = [];
    public int[] BandwidthPresets { get; init; } =
        [500, 2_700, 4_000, 7_000, 10_000, 12_500, 180_000];
    public float AudioLevelDb { get; init; }
    public bool StereoLed { get; init; }
    public bool WfmStereo { get; init; }
    public bool WfmHfSoft { get; init; }
    public string WfmEqPreset { get; init; } = "";
    public string[] WfmEqPresets { get; init; } = [];
    public bool AfcEnabled { get; init; }
    public int AfcSpeedIndex { get; init; }
    public int AfcRangeHz { get; init; }
    public string AfcStatus { get; init; } = "";
    public string AnalogTone { get; init; } = "";
    public bool CwLowerSide { get; init; }
    public int CwAfWidthHz { get; init; }
    public bool AgcEnabled { get; init; }
    public bool NoiseReduction { get; init; }
    public int NoiseReductionStrength { get; init; }
    public bool NotchEnabled { get; init; }
    public bool AfFilterEnabled { get; init; }
    public string FreeDvModem { get; init; } = "Auto";
    public string FreeDvSideband { get; init; } = "Auto";
    public int DigitalOutputChannel { get; init; } = 1;
    public bool DigitalPcmAgc { get; init; }
    public int DigitalFeedVolume { get; init; } = 70;
    public string DigitalStatus { get; init; } = "";
    public string DigitalOverlay { get; init; } = "";
    public bool IsRemoteSource { get; init; }
    public string SiteUrl { get; init; } = "";
    public string SiteName { get; init; } = "";
    public RemoteSiteRemoteInfo[] Sites { get; init; } = [];
    public bool SatelliteActive { get; init; }
    public string SatelliteStatus { get; init; } = "";
    public int AfSpanHz { get; init; } = 3_500;
    public long UpdatedUtcTicks { get; init; } = DateTime.UtcNow.Ticks;
}

public sealed class RemoteSiteRemoteInfo
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string Location { get; init; } = "";
}

public sealed class ActiveAfPluginRemoteInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string TypeId { get; init; } = "";
}

public sealed class RxSceneRemoteInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class SceneChannelRemoteInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public long FrequencyHz { get; init; }
    public string Mode { get; init; } = "";
    public int BandwidthHz { get; init; }
    public bool Global { get; init; }
}

public sealed class SubVfoRemoteInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public long FrequencyHz { get; init; }
    public string Mode { get; init; } = "";
    public int BandwidthHz { get; init; }
    public bool Enabled { get; init; }
}

public sealed class BandRemoteInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public long FrequencyHz { get; init; }
    public string Mode { get; init; } = "";
}

public sealed class SpectrumRemoteFrame
{
    public long CenterHz { get; init; }
    public int SpanHz { get; init; }
    public long TunedHz { get; init; }
    public int FilterHz { get; init; }
    public string Mode { get; init; } = "";
    /// <summary>Downsampled dBFS levels, roughly −140…0.</summary>
    public float[] Levels { get; init; } = [];
}

public sealed class AfPluginRemoteEvent
{
    public string PluginId { get; init; } = "";
    public string PluginName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>RF frequency associated with the decode (Hz), when known.</summary>
    public long FrequencyHz { get; init; }
    public long UtcTicks { get; init; }
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
