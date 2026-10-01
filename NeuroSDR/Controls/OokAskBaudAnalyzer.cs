using System.Globalization;
using System.Text;

namespace NeuroSDR.Controls;

/// <summary>Re-interpret captured OOK/ASK pulse widths under alternate baud / coding assumptions.</summary>
internal static class OokAskBaudAnalyzer
{
    private static readonly double[] CommonBaud =
    [
        300, 600, 1000, 1200, 2000, 2400, 4000, 4800, 8000, 9600,
        10000, 12000, 16000, 19200, 20000, 24000, 25000, 31250, 38400, 50000, 57600
    ];

    public sealed record Candidate(
        string Method,
        double Baud,
        string Bits,
        string Hex,
        string Note,
        double FitError);

    public static IReadOnlyList<Candidate> BuildCandidates(int[] pulses, int sampleRate, double reportedBaud)
    {
        if (pulses.Length == 0 || sampleRate <= 0) return [];
        var abs = pulses.Select(Math.Abs).Where(v => v > 0).ToArray();
        if (abs.Length == 0) return [];

        var list = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string method, int unitSamples, string coding, string note)
        {
            if (unitSamples < 1) return;
            var baud = sampleRate / (double)unitSamples;
            string bits;
            string hex;
            double err;
            switch (coding)
            {
                case "pwm":
                    (bits, hex, err) = DecodePwm(pulses, unitSamples);
                    break;
                case "manchester":
                    (bits, hex, err) = DecodeManchester(pulses, unitSamples);
                    break;
                default:
                    (bits, hex, err) = DecodeNrz(pulses, unitSamples);
                    break;
            }
            if (bits.Length < 2) return;
            var key = $"{method}|{baud:0.#}|{hex}|{bits.Length}";
            if (!seen.Add(key)) return;
            list.Add(new Candidate(method, baud, bits, hex, note, err));
        }

        var min = abs.Min();
        var max = abs.Max();
        var median = abs.OrderBy(v => v).ElementAt(abs.Length / 2);
        var marks = pulses.Where(w => w > 0).Select(Math.Abs).DefaultIfEmpty(median).ToArray();
        var spaces = pulses.Where(w => w < 0).Select(Math.Abs).DefaultIfEmpty(median).ToArray();
        var meanMark = marks.Average();
        var meanSpace = spaces.Average();
        var meanAll = abs.Average();
        var mode = abs.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

        Add("Median unit · NRZ", median, "nrz", "Primary list estimate (median |pulse|)");
        Add("Min pulse · NRZ", min, "nrz", "Assumes shortest pulse = 1 unit");
        Add("Mode pulse · NRZ", mode, "nrz", "Most common pulse width = 1 unit");
        Add("Mean |pulse| · NRZ", Math.Max(1, (int)Math.Round(meanAll)), "nrz", "Average absolute width");
        Add("Mean MARK · NRZ", Math.Max(1, (int)Math.Round(meanMark)), "nrz", "Baud from average high only (list BAUD)");
        Add("Mean SPACE · NRZ", Math.Max(1, (int)Math.Round(meanSpace)), "nrz", "Average low/gap as unit");
        Add("Half median · NRZ", Math.Max(1, median / 2), "nrz", "If median was 2 units");
        Add("Double median · NRZ", median * 2, "nrz", "If median was half-bit");
        Add("Min·PWM short/long", min, "pwm", "Short≈0 / long≈1 (PWM/PPM style)");
        Add("Median·Manchester", Math.Max(1, median / 2), "manchester", "Half-bit unit Manchester");
        Add("Min·Manchester", min, "manchester", "Shortest edge = half-bit");

        // Cluster centroids via simple 1-D k-means-ish: unique rounded buckets.
        foreach (var u in abs.Distinct().OrderBy(v => v).Take(12))
            Add($"Unit={u} samp · NRZ", u, "nrz", $"{u * 1000.0 / sampleRate:0.##} ms/unit");

        // Snap to common baud rates (unit = rate/baud).
        foreach (var target in CommonBaud)
        {
            var unit = Math.Max(1, (int)Math.Round(sampleRate / target));
            var errVsMean = Math.Abs(sampleRate / (double)unit - reportedBaud);
            Add($"Snap {target:0} baud · NRZ", unit, "nrz",
                $"Common rate; Δ vs reported {errVsMean:0.#}");
            Add($"Snap {target:0} · PWM", unit, "pwm", "Common rate as PWM unit");
            Add($"Snap {target:0} · Manch.", Math.Max(1, unit / 2), "manchester", "Common rate Manchester half");
        }

        // Prefer low quantization error, then closer to a round baud.
        return list
            .OrderBy(c => c.FitError)
            .ThenBy(c => Math.Abs(Math.Log10(Math.Max(1, c.Baud)) - Math.Round(Math.Log10(Math.Max(1, c.Baud)))))
            .ThenBy(c => Math.Abs(c.Baud - reportedBaud))
            .Take(40)
            .ToList();
    }

    private static (string Bits, string Hex, double Err) DecodeNrz(int[] pulses, int unit)
    {
        var bits = new StringBuilder();
        double err = 0;
        var n = 0;
        foreach (var w in pulses)
        {
            var a = Math.Abs(w);
            var q = Math.Clamp((int)Math.Round(a / (double)unit), 1, 16);
            err += Math.Abs(a - q * unit);
            n++;
            bits.Append(w > 0 ? '1' : '0', q);
        }
        var s = bits.ToString();
        return (s, ToHex(s), n > 0 ? err / n / unit : 99);
    }

    private static (string Bits, string Hex, double Err) DecodePwm(int[] pulses, int unit)
    {
        // Use high pulses only; short vs long relative to 1.5× unit.
        var marks = pulses.Where(w => w > 0).Select(Math.Abs).ToArray();
        if (marks.Length < 2) return ("", "", 99);
        var bits = new StringBuilder();
        double err = 0;
        var thr = unit * 1.5;
        foreach (var m in marks)
        {
            var isLong = m >= thr;
            bits.Append(isLong ? '1' : '0');
            err += isLong
                ? Math.Abs(m - Math.Max(unit * 2, m)) / (double)unit * 0.25
                : Math.Abs(m - unit) / (double)unit;
        }
        var s = bits.ToString();
        return (s, ToHex(s), err / marks.Length);
    }

    private static (string Bits, string Hex, double Err) DecodeManchester(int[] pulses, int unit)
    {
        // Expand to half-bits then pair: 10→1, 01→0 (IEEE), also try Thomas.
        var half = new StringBuilder();
        double err = 0;
        var n = 0;
        foreach (var w in pulses)
        {
            var a = Math.Abs(w);
            var q = Math.Clamp((int)Math.Round(a / (double)unit), 1, 8);
            err += Math.Abs(a - q * unit);
            n++;
            half.Append(w > 0 ? '1' : '0', q);
        }
        var h = half.ToString();
        if (h.Length < 4) return ("", "", 99);
        var ieee = new StringBuilder();
        var thomas = new StringBuilder();
        for (var i = 0; i + 1 < h.Length; i += 2)
        {
            var a = h[i];
            var b = h[i + 1];
            if (a == '1' && b == '0') { ieee.Append('1'); thomas.Append('0'); }
            else if (a == '0' && b == '1') { ieee.Append('0'); thomas.Append('1'); }
            else { ieee.Append('?'); thomas.Append('?'); }
        }
        var ieeeS = ieee.ToString().Replace("?", "", StringComparison.Ordinal);
        var thomasS = thomas.ToString().Replace("?", "", StringComparison.Ordinal);
        var pick = ieeeS.Length >= thomasS.Length ? ieeeS : thomasS;
        return (pick, ToHex(pick), n > 0 ? err / n / unit : 99);
    }

    public static string ToHex(string bits)
    {
        var sb = new StringBuilder();
        var clean = new string(bits.Where(c => c is '0' or '1').ToArray());
        var n = clean.Length - clean.Length % 8;
        for (var i = 0; i + 8 <= n && i < 128; i += 8)
        {
            var v = 0;
            for (var b = 0; b < 8; b++)
                v = (v << 1) | (clean[i + b] == '1' ? 1 : 0);
            sb.Append(v.ToString("X2"));
        }
        if (n == 0 && clean.Length > 0)
        {
            var v = 0;
            for (var b = 0; b < clean.Length && b < 8; b++)
                v = (v << 1) | (clean[b] == '1' ? 1 : 0);
            sb.Append(v.ToString("X2"));
        }
        return sb.ToString();
    }

    public static int[] ParsePulseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        var list = new List<int>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                list.Add(v);
        }
        return list.ToArray();
    }
}
