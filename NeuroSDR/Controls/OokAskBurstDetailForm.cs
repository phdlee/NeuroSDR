using System.Globalization;
using System.Text;

namespace NeuroSDR.Controls;

internal sealed class OokAskBurstDetailForm : Form
{
    public OokAskBurstDetailForm(IReadOnlyDictionary<string, string> fields, string utcText)
    {
        Text = $"OOK/ASK burst · {utcText}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(920, 640);
        MinimumSize = new Size(720, 480);
        BackColor = Color.FromArgb(8, 18, 26);
        ForeColor = Color.FromArgb(220, 232, 240);
        Font = new Font("Segoe UI", 9f);

        var sampleRate = ParseInt(fields, "sampleRate", 48_000);
        var pulses = OokAskBaudAnalyzer.ParsePulseCsv(fields.GetValueOrDefault("pulseSamples"));
        var reportedBaud = ParseDouble(fields, "baud", 0);

        var summary = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 168,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(12, 26, 36),
            ForeColor = Color.FromArgb(220, 232, 240),
            Font = new Font("Consolas", 9f),
            Text = BuildSummary(fields, utcText, sampleRate, pulses.Length, reportedBaud)
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = Color.FromArgb(160, 185, 200),
            Text = "Baud candidates (assuming reported baud may be wrong) — double-click a row to copy HEX"
        };

        var grid = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(4, 12, 18),
            ForeColor = Color.FromArgb(210, 228, 236),
            Font = new Font("Consolas", 8.5f)
        };
        grid.Columns.Add("METHOD", 200);
        grid.Columns.Add("BAUD", 80);
        grid.Columns.Add("HEX", 160);
        grid.Columns.Add("BITS", 220);
        grid.Columns.Add("FIT", 56);
        grid.Columns.Add("NOTE", 220);

        var candidates = OokAskBaudAnalyzer.BuildCandidates(pulses, sampleRate, reportedBaud);
        foreach (var c in candidates)
        {
            var item = new ListViewItem(c.Method)
            {
                BackColor = Color.FromArgb(6, 16, 24),
                Tag = c
            };
            item.SubItems.Add(c.Baud.ToString("0.#", CultureInfo.InvariantCulture));
            item.SubItems.Add(c.Hex);
            item.SubItems.Add(c.Bits.Length > 64 ? c.Bits[..64] + "…" : c.Bits);
            item.SubItems.Add(c.FitError.ToString("0.00", CultureInfo.InvariantCulture));
            item.SubItems.Add(c.Note);
            grid.Items.Add(item);
        }

        grid.DoubleClick += (_, _) =>
        {
            if (grid.SelectedItems.Count == 0) return;
            if (grid.SelectedItems[0].Tag is not OokAskBaudAnalyzer.Candidate c) return;
            var text = $"BAUD {c.Baud:0.#}\r\nHEX {c.Hex}\r\nBITS {c.Bits}\r\n{c.Method}\r\n{c.Note}";
            try { Clipboard.SetText(text); } catch { }
            hint.Text = "Copied candidate to clipboard";
        };

        var close = new Button
        {
            Text = "Close",
            Dock = DockStyle.Bottom,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 66, 83),
            ForeColor = Color.FromArgb(222, 233, 240)
        };
        close.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        close.Click += (_, _) => Close();

        Controls.Add(grid);
        Controls.Add(hint);
        Controls.Add(summary);
        Controls.Add(close);
        DarkNativeTheme.ApplyListView(grid);
    }

    private static string BuildSummary(
        IReadOnlyDictionary<string, string> fields,
        string utc,
        int sampleRate,
        int pulseCount,
        double reportedBaud)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"UTC          {utc}");
        sb.AppendLine($"HEX          {fields.GetValueOrDefault("hex")}");
        sb.AppendLine($"BITS         {fields.GetValueOrDefault("bits")}");
        sb.AppendLine($"Reported BAUD {reportedBaud:0.#}  (mean MARK) · unit {fields.GetValueOrDefault("unitSamples")} samp @ {sampleRate} Hz");
        sb.AppendLine($"Duration     {fields.GetValueOrDefault("durationMs")} ms · pulses {pulseCount}");
        sb.AppendLine($"RF           sig {fields.GetValueOrDefault("signalDb")} dB · noise {fields.GetValueOrDefault("noiseDb")} dB");
        sb.AppendLine($"Pulses (ms)  {fields.GetValueOrDefault("pulses")}");
        sb.AppendLine($"Pulse samp   {Trim(fields.GetValueOrDefault("pulseSamples"), 240)}");
        return sb.ToString();
    }

    private static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    private static int ParseInt(IReadOnlyDictionary<string, string> f, string key, int fallback) =>
        f.TryGetValue(key, out var s) &&
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private static double ParseDouble(IReadOnlyDictionary<string, string> f, string key, double fallback) =>
        f.TryGetValue(key, out var s) &&
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;
}
