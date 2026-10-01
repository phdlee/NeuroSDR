namespace NeuroSDR.Settings;

/// <summary>Named 10-band EQ curve for WFM (matches GraphicEqualizer.BandHz).</summary>
internal sealed class WfmEqPreset
{
    public string Name { get; set; } = "Normal";
    public float[] GainsDb { get; set; } = new float[10];
    /// <summary>Factory presets can be overwritten by Save; Reset restores code defaults.</summary>
    public bool IsFactory { get; set; }
}

/// <summary>Broadcast-FM style station memory (frequency + label + EQ preset name).</summary>
internal sealed class WfmStation
{
    public string Name { get; set; } = "";
    public long FrequencyHz { get; set; }
    public string EqPreset { get; set; } = "Normal";
}

/// <summary>Built-in EQ curves (10 bands: 32…16k). Values are typical consumer “genre” shapes.</summary>
internal static class WfmEqFactoryPresets
{
    public static readonly string[] Names =
    [
        "Normal", "Classic", "Rock", "Pop", "Jazz", "Bass", "Treble", "Vocal"
    ];

    public static float[] Gains(string name) => name.Trim().ToLowerInvariant() switch
    {
        "classic" or "classical" => [3, 2, 0, 0, 0, 0, -1, -2, 2, 3],
        "rock" => [5, 4, 2, -1, -3, -2, 1, 3, 4, 4],
        "pop" => [2, 1, 0, 1, 3, 3, 2, 1, 2, 2],
        "jazz" => [3, 2, 1, 2, 3, 2, 1, 0, 1, 2],
        "bass" => [6, 5, 4, 2, 0, 0, 0, 0, 0, 0],
        "treble" => [0, 0, 0, 0, 0, 1, 2, 4, 5, 6],
        "vocal" => [-2, -1, 0, 2, 4, 4, 3, 1, 0, -1],
        _ => new float[10] // Normal / Flat
    };

    public static List<WfmEqPreset> CreateFactoryList() =>
        Names.Select(n => new WfmEqPreset
        {
            Name = n,
            GainsDb = Gains(n),
            IsFactory = true
        }).ToList();

    public static void EnsureFactory(List<WfmEqPreset> presets)
    {
        foreach (var name in Names)
        {
            if (presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            presets.Add(new WfmEqPreset { Name = name, GainsDb = Gains(name), IsFactory = true });
        }
        // Restore factory flag + default gains when list has factory names that lost flag.
        foreach (var p in presets.Where(p => Names.Any(n => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase))))
            p.IsFactory = true;
    }

    public static void ResetFactory(WfmEqPreset preset)
    {
        if (!Names.Any(n => n.Equals(preset.Name, StringComparison.OrdinalIgnoreCase))) return;
        preset.GainsDb = Gains(preset.Name);
        preset.IsFactory = true;
    }
}
