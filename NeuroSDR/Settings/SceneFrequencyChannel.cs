using NeuroSDR.Core;

namespace NeuroSDR.Settings;

/// <summary>Saved frequency button under SCENE (not WFM station list).</summary>
internal sealed class SceneFrequencyChannel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public long Frequency { get; set; } = 448_800_000;
    public RadioMode Mode { get; set; } = RadioMode.NFM;
    public int Bandwidth { get; set; } = 12_500;
    /// <summary>When true, visible in every SCENE; otherwise only the owning scene.</summary>
    public bool Global { get; set; }

    public SceneFrequencyChannel Clone() => (SceneFrequencyChannel)MemberwiseClone();

    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(Name) ? FormatMhz(Frequency) : Name;
        return Global ? "★ " + label : label;
    }

    public static string FormatMhz(long hz) => (hz / 1_000_000d).ToString("0.000");
}

    /// <summary>Auto frequency follow / standby scan window for Main or Sub VFO.</summary>
    internal sealed class AutoTuneSettings
    {
        public bool Enabled { get; set; }
        public long StandbyFrequency { get; set; }
        public long MinFrequency { get; set; }
        public long MaxFrequency { get; set; }
        /// <summary>Peak RF level (dBFS-ish) that triggers a jump toward the signal. Editor prefers spectrum+5 dB.</summary>
        public float TriggerLevelDb { get; set; } = -65f;
        public float ReleaseLevelDb { get; set; } = -80f;
        public int HoldMilliseconds { get; set; } = 1_500;
        /// <summary>
        /// When the operator retunes Main VFO, shift Min/Max/Standby by the same delta
        /// so the search window stays centered on the dial.
        /// </summary>
        public bool WindowFollowsVfo { get; set; } = true;

        public AutoTuneSettings Clone() => (AutoTuneSettings)MemberwiseClone();
    }
