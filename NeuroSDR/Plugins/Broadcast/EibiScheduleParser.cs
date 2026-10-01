using System.Globalization;

namespace NeuroSDR.Plugins.Broadcast;

internal sealed record EibiEntry(
    long FrequencyHz,
    TimeSpan StartUtc,
    TimeSpan EndUtc,
    string Days,
    string Itu,
    string Station,
    string Language,
    string LanguageName,
    string Target,
    string Site);

internal static class EibiScheduleParser
{
    public static IReadOnlyList<EibiEntry> Parse(string text)
    {
        var rows = new List<EibiEntry>(8_192);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (TryParseLine(line, out var entry) && entry is not null)
                rows.Add(entry);
        }
        return rows;
    }

    public static bool TryParseLine(string line, out EibiEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(line) || line[0] is ';' or '#' or '*') return false;
        if (line.StartsWith("kHz", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = line.Split(';');
        if (parts.Length < 5) return false;
        if (!TryParseKhz(parts[0], out var hz)) return false;
        if (!TryParseWindow(parts[1], out var start, out var end)) return false;
        var days = parts.Length > 2 ? parts[2].Trim() : "";
        var itu = parts.Length > 3 ? parts[3].Trim() : "";
        var station = parts.Length > 4 ? parts[4].Trim() : "";
        if (station.Length == 0) return false;
        var lang = parts.Length > 5 ? parts[5].Trim() : "";
        var target = parts.Length > 6 ? parts[6].Trim() : "";
        var site = parts.Length > 7 ? parts[7].Trim() : "";
        entry = new EibiEntry(hz, start, end, days, itu, station, lang, EibiCodes.Language(lang), target, site);
        return true;
    }

    public static bool IsOnAir(EibiEntry entry, DateTime utc)
    {
        if (!EibiCodes.DayMatches(entry.Days, utc.DayOfWeek)) return false;
        var tod = utc.TimeOfDay;
        var start = entry.StartUtc;
        var end = entry.EndUtc;
        if (end == TimeSpan.Zero) end = TimeSpan.FromDays(1);
        if (end == start) return true;
        if (end > start) return tod >= start && tod < end;
        return tod >= start || tod < end;
    }

    private static bool TryParseKhz(string text, out long hz)
    {
        hz = 0;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var khz))
            return false;
        if (khz < 10 || khz > 30_000) return false;
        hz = (long)Math.Round(khz * 1_000d);
        return true;
    }

    private static bool TryParseWindow(string text, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        var span = text.Trim();
        var dash = span.IndexOf('-');
        if (dash <= 0) return false;
        return TryHmm(span[..dash], out start) && TryHmm(span[(dash + 1)..], out end);
    }

    private static bool TryHmm(string text, out TimeSpan value)
    {
        value = default;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length is < 3 or > 4) return false;
        digits = digits.PadLeft(4, '0');
        if (!int.TryParse(digits[..2], out var hours) || !int.TryParse(digits[2..], out var minutes))
            return false;
        if (hours == 24 && minutes == 0)
        {
            value = TimeSpan.FromDays(1);
            return true;
        }
        if (hours is < 0 or > 23 || minutes is < 0 or > 59) return false;
        value = new TimeSpan(hours, minutes, 0);
        return true;
    }
}
