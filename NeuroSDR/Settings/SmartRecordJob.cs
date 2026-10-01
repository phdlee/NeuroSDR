using NeuroSDR.Core;

namespace NeuroSDR.Settings;

internal enum SmartRecordKind
{
    /// <summary>Stream demodulated AF to WAV for Main or a Sub VFO.</summary>
    Audio,
    /// <summary>Append matching FT8/FT4 decodes into a session report (not voice).</summary>
    FtxReport
}

/// <summary>One Smart Record job (timed audio and/or FT8 decode report).</summary>
internal sealed class SmartRecordJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Job";
    public bool Enabled { get; set; } = true;
    public SmartRecordKind Kind { get; set; } = SmartRecordKind.Audio;

    /// <summary>"main" or a Sub VFO Id.</summary>
    public string VfoId { get; set; } = "main";

    /// <summary>Optional absolute tune when the audio window opens (Hz). 0 = leave VFO alone.</summary>
    public long TuneFrequencyHz { get; set; }
    public RadioMode? TuneMode { get; set; }
    public int TuneBandwidthHz { get; set; }

    /// <summary>Local wall-clock window (inclusive start, exclusive end). Empty Days = every day.</summary>
    public string StartTime { get; set; } = "00:00";
    public string EndTime { get; set; } = "01:00";
    public List<int> DaysOfWeek { get; set; } = []; // 0=Sunday … 6=Saturday; empty = all

    /// <summary>Pile-up follow while this audio job is active (Sub/Main).</summary>
    public bool PileupFollowEnabled { get; set; }
    public long PileupRangeHz { get; set; } = 25_000;
    public float PileupTriggerLevelDb { get; set; } = -70f;
    public float PileupReleaseLevelDb { get; set; } = -85f;

    // FT8 report filters (Kind = FtxReport)
    public string CallsignContains { get; set; } = "";
    public string Prefix { get; set; } = "";
    public bool CqOnly { get; set; }
    public string MessageContains { get; set; } = "";

    public SmartRecordJob Clone()
    {
        var copy = (SmartRecordJob)MemberwiseClone();
        copy.DaysOfWeek = DaysOfWeek.ToList();
        return copy;
    }

    public bool TryGetWindow(out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        return TryParseClock(StartTime, out start) && TryParseClock(EndTime, out end);
    }

    public static bool TryParseClock(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim().Replace('.', ':').Replace(';', ':');
        if (TimeSpan.TryParse(trimmed, out value)) return true;
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length is 3 or 4)
        {
            digits = digits.PadLeft(4, '0');
            var hours = int.Parse(digits[..2]);
            var minutes = int.Parse(digits[2..]);
            if (hours is >= 0 and <= 23 && minutes is >= 0 and <= 59)
            {
                value = new TimeSpan(hours, minutes, 0);
                return true;
            }
        }
        return false;
    }

    public static string FormatClock(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}";

    public bool IsScheduledNow(DateTime localNow)
    {
        if (!Enabled) return false;
        if (!TryGetWindow(out var start, out var end)) return false;
        if (DaysOfWeek.Count > 0 && !DaysOfWeek.Contains((int)localNow.DayOfWeek)) return false;
        var tod = localNow.TimeOfDay;
        if (end == start) return true; // 24h
        if (end > start) return tod >= start && tod < end;
        // wraps midnight
        return tod >= start || tod < end;
    }
}
