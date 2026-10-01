using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NeuroSDR.Hardware;

[Flags]
internal enum RemoteSdrSpectrum
{
    None = 0,
    Hf = 1,
    Vhf = 2,
    Uhf = 4
}

internal readonly record struct RemoteSdrBandSpan(double LowMhz, double HighMhz);

internal static class RemoteSdrBands
{
    public static IReadOnlyList<RemoteSdrBandSpan> ParseWebSdrObject(string body)
    {
        var rows = new List<RemoteSdrBandSpan>();
        foreach (Match match in Regex.Matches(body,
                     """["']l["']\s*:\s*(-?[\d.]+)\s*,\s*["']h["']\s*:\s*(-?[\d.]+)"""))
        {
            if (!TryMhz(match.Groups[1].Value, out var lo) || !TryMhz(match.Groups[2].Value, out var hi))
                continue;
            if (hi < lo) (lo, hi) = (hi, lo);
            if (hi <= 0) continue;
            rows.Add(new RemoteSdrBandSpan(lo, hi));
        }
        return Merge(rows);
    }

    public static IReadOnlyList<RemoteSdrBandSpan> ParseKiwi(string bandsField, string name)
    {
        var rows = new List<RemoteSdrBandSpan>();
        foreach (Match match in Regex.Matches(bandsField ?? "", @"(\d+(?:\.\d+)?)\s*[-–—]\s*(\d+(?:\.\d+)?)"))
        {
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ||
                !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                continue;
            if (b < a) (a, b) = (b, a);
            // kiwisdr.com stores Hz ("0-30000000"); rare MHz pairs stay as-is.
            if (b >= 1_000)
            {
                a /= 1_000_000;
                b /= 1_000_000;
            }
            if (b > 0) rows.Add(new RemoteSdrBandSpan(a, b));
        }
        if (rows.Count == 0)
            rows.AddRange(ParseFromTitle(name));
        return Merge(rows);
    }

    public static IReadOnlyList<RemoteSdrBandSpan> ParseFromTitle(string? name)
    {
        var rows = new List<RemoteSdrBandSpan>();
        if (string.IsNullOrWhiteSpace(name)) return rows;
        foreach (Match match in Regex.Matches(name,
                     @"(\d+(?:\.\d+)?)\s*-\s*(\d+(?:\.\d+)?)\s*(MHz|MHZ|mhz)?"))
        {
            if (!TryMhz(match.Groups[1].Value, out var lo) || !TryMhz(match.Groups[2].Value, out var hi))
                continue;
            if (hi < lo) (lo, hi) = (hi, lo);
            if (hi <= 90 && lo < 90) rows.Add(new RemoteSdrBandSpan(lo, hi));
            else if (hi > 90) rows.Add(new RemoteSdrBandSpan(lo, hi));
        }
        return Merge(rows);
    }

    public static RemoteSdrSpectrum Coverage(IReadOnlyList<RemoteSdrBandSpan> bands)
    {
        var flags = RemoteSdrSpectrum.None;
        foreach (var band in bands)
        {
            if (Overlaps(band.LowMhz, band.HighMhz, 0, 30)) flags |= RemoteSdrSpectrum.Hf;
            // 0–32 MHz Kiwi “HF 0–30” is not VHF. Real VHF starts around 6 m / 40 MHz+.
            if (Overlaps(band.LowMhz, band.HighMhz, 40, 300)) flags |= RemoteSdrSpectrum.Vhf;
            if (Overlaps(band.LowMhz, band.HighMhz, 300, 40_000)) flags |= RemoteSdrSpectrum.Uhf;
        }
        return flags;
    }

    public static bool Covers(IReadOnlyList<RemoteSdrBandSpan> bands, RemoteSdrSpectrum wanted)
    {
        if (wanted == RemoteSdrSpectrum.None) return true;
        return (Coverage(bands) & wanted) != 0;
    }

    public static long ToHz(double mhz) =>
        (long)Math.Round(mhz * 1_000_000d, MidpointRounding.AwayFromZero);

    public static long ClampHz(long hz, IReadOnlyList<RemoteSdrBandSpan> bands)
    {
        if (bands is null || bands.Count == 0) return hz;
        long? best = null;
        var bestDelta = long.MaxValue;
        foreach (var band in bands)
        {
            var lo = ToHz(band.LowMhz);
            var hi = ToHz(band.HighMhz);
            if (hi < lo) (lo, hi) = (hi, lo);
            if (hi < lo + 1) hi = lo + 1;
            if (hz >= lo && hz <= hi) return hz;
            var candidate = hz < lo ? lo : hi;
            var delta = Math.Abs(candidate - hz);
            if (delta >= bestDelta) continue;
            bestDelta = delta;
            best = candidate;
        }
        return best ?? hz;
    }

    public static bool ContainsHz(long hz, IReadOnlyList<RemoteSdrBandSpan> bands, long slackHz = 0)
    {
        if (bands is null || bands.Count == 0) return false;
        foreach (var band in bands)
        {
            var lo = ToHz(band.LowMhz);
            var hi = ToHz(band.HighMhz);
            if (hi < lo) (lo, hi) = (hi, lo);
            if (hz >= lo - slackHz && hz <= hi + slackHz) return true;
        }
        return false;
    }

    public static IReadOnlyList<RemoteSdrBandSpan> ParseUserText(string? text)
    {
        var parsed = ParseKiwi(text ?? "", "");
        return parsed.Count > 0 ? parsed : ParseFromTitle(text);
    }

    public static string FormatUserText(IReadOnlyList<RemoteSdrBandSpan> bands)
    {
        if (bands is null || bands.Count == 0) return "";
        return string.Join(", ", bands.Select(band =>
            string.Create(CultureInfo.InvariantCulture, $"{band.LowMhz:0.###}-{band.HighMhz:0.###}")));
    }

    public static string Summarize(IReadOnlyList<RemoteSdrBandSpan> bands)
    {
        if (bands.Count == 0) return "";
        var coverage = Coverage(bands);
        if (bands.Count == 1 && bands[0].LowMhz <= 0.05 && bands[0].HighMhz is >= 29 and <= 32)
            return "HF 0–30 MHz";
        var parts = new List<string>();
        if (coverage.HasFlag(RemoteSdrSpectrum.Hf)) parts.Add("HF");
        if (coverage.HasFlag(RemoteSdrSpectrum.Vhf)) parts.Add("VHF");
        if (coverage.HasFlag(RemoteSdrSpectrum.Uhf)) parts.Add("UHF");
        var detail = new StringBuilder();
        foreach (var band in bands.Take(6))
        {
            if (detail.Length > 0) detail.Append(" · ");
            detail.Append(FormatSpan(band));
        }
        if (bands.Count > 6) detail.Append(" …");
        if (parts.Count == 0) return detail.ToString();
        return $"{string.Join("/", parts)}  {detail}";
    }

    private static string FormatSpan(RemoteSdrBandSpan band)
    {
        if (band.HighMhz >= 1_000)
            return $"{band.LowMhz / 1_000:0.###}–{band.HighMhz / 1_000:0.###} GHz";
        if (band.HighMhz >= 100)
            return $"{band.LowMhz:0.#}–{band.HighMhz:0.#} MHz";
        return $"{band.LowMhz:0.###}–{band.HighMhz:0.###} MHz";
    }

    private static List<RemoteSdrBandSpan> Merge(List<RemoteSdrBandSpan> rows)
    {
        if (rows.Count <= 1) return rows;
        rows.Sort((a, b) => a.LowMhz.CompareTo(b.LowMhz));
        var merged = new List<RemoteSdrBandSpan> { rows[0] };
        foreach (var row in rows.Skip(1))
        {
            var last = merged[^1];
            if (row.LowMhz <= last.HighMhz + 0.05)
                merged[^1] = new RemoteSdrBandSpan(last.LowMhz, Math.Max(last.HighMhz, row.HighMhz));
            else
                merged.Add(row);
        }
        return merged;
    }

    private static bool Overlaps(double a0, double a1, double b0, double b1) => a0 < b1 && b0 < a1;

    private static bool TryMhz(string text, out double mhz) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out mhz);
}
