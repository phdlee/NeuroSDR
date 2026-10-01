using NeuroSDR.Core;
using System.Text.Json;

namespace NeuroSDR.Settings;

internal sealed record NeuroSDRPreset(string Group, string Name, long FrequencyHz, RadioMode Mode, int BandwidthHz, int Lpm = 0)
{
    public override string ToString() => Name;

    public long DialFrequencyHz => Group.Equals("WeatherFax", StringComparison.OrdinalIgnoreCase)
        ? FrequencyHz + (Mode == RadioMode.LSB ? 1_900 : -1_900)
        : FrequencyHz;
}

internal static class NeuroSDRPresetCatalog
{
    private static readonly Lazy<IReadOnlyList<NeuroSDRPreset>> Presets = new(Load);

    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "neurosdrpreset.json");

    public static IReadOnlyList<NeuroSDRPreset> ForGroup(string group) => Presets.Value
        .Where(item => item.Group.Equals(group, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    public static IReadOnlyList<NeuroSDRPreset> All => Presets.Value;

    private static IReadOnlyList<NeuroSDRPreset> Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (!document.RootElement.TryGetProperty("presets", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return [];
            var result = new List<NeuroSDRPreset>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("group", out var groupValue) ||
                    !entry.TryGetProperty("name", out var nameValue) ||
                    !entry.TryGetProperty("frequencyHz", out var frequencyValue) ||
                    !entry.TryGetProperty("mode", out var modeValue)) continue;
                var group = groupValue.GetString()?.Trim() ?? string.Empty;
                var name = nameValue.GetString()?.Trim() ?? string.Empty;
                var frequency = frequencyValue.TryGetInt64(out var parsedFrequency) ? parsedFrequency : 0;
                if (group.Length == 0 || name.Length == 0 || frequency < RadioLimits.MinimumFrequency ||
                    frequency > RadioLimits.MaximumFrequency ||
                    !Enum.TryParse<RadioMode>(modeValue.GetString(), true, out var mode)) continue;
                var bandwidth = entry.TryGetProperty("bandwidthHz", out var bandwidthValue) && bandwidthValue.TryGetInt32(out var parsedBandwidth)
                    ? Math.Clamp(parsedBandwidth, 100, 500_000)
                    : mode switch
                    {
                        RadioMode.WFM => 180_000,
                        RadioMode.NFM or RadioMode.DMR or RadioMode.DSTAR => 12_500,
                        RadioMode.C4FM => 16_000,
                        RadioMode.AM or RadioMode.SAM => 10_000,
                        RadioMode.USB or RadioMode.LSB or RadioMode.FREEDV => 2_700,
                        RadioMode.CW => 500, _ => 48_000
                    };
                var lpm = entry.TryGetProperty("lpm", out var lpmValue) && lpmValue.TryGetInt32(out var parsedLpm)
                    ? parsedLpm : 0;
                result.Add(new NeuroSDRPreset(group, name, frequency, mode, bandwidth, lpm));
            }
            return result;
        }
        catch
        {
            return [];
        }
    }
}
