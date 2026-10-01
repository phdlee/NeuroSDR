using System.Globalization;

namespace NeuroSDR.Controls;

/// <summary>
/// Flexible frequency entry: "448.8", "448.800", "448800000", commas/spaces/"MHz" allowed.
/// Values &lt; 10_000 are treated as MHz; larger values as Hz.
/// </summary>
internal static class FrequencyEntry
{
    public static string FormatMhz(long hz)
    {
        hz = Math.Max(0, hz);
        if (hz >= 1_000_000_000) return (hz / 1_000_000d).ToString("0.#####", CultureInfo.InvariantCulture);
        return (hz / 1_000_000d).ToString("0.######", CultureInfo.InvariantCulture);
    }

    public static bool TryParseHz(string? text, out long hz)
    {
        hz = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = text.Trim()
            .Replace("MHz", "", StringComparison.OrdinalIgnoreCase)
            .Replace("MHZ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Hz", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", "")
            .Replace(" ", "")
            .Replace("_", "");
        if (cleaned.Length == 0) return false;
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            !double.TryParse(cleaned, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return false;
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return false;
        hz = value < 10_000
            ? (long)Math.Round(value * 1_000_000d)
            : (long)Math.Round(value);
        return hz > 0;
    }

    public static TextBox CreateMhzBox(long hz)
    {
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = FormatMhz(hz),
            BackColor = Color.FromArgb(5, 17, 24),
            ForeColor = Color.FromArgb(226, 235, 242),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10f)
        };
        box.Enter += (_, _) => box.SelectAll();
        return box;
    }

    public static long ReadHz(TextBox box, long fallback)
    {
        if (TryParseHz(box.Text, out var hz)) return hz;
        return fallback;
    }
}
