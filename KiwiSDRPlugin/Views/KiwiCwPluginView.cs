using System.Globalization;
using ENSdr.Plugins;

namespace KiwiSDRPlugin.Views;

/// <summary>
/// Text-console UI for KiwiCW (CLEAR/RESET, pitch Hz, optional fixed WPM).
/// Host wires OptionsChanged / CommandRequested; AppSettings persistence is left to the parent.
/// </summary>
public sealed class KiwiCwPluginView : UserControl, IAfResultView, IPluginViewEvents
{
    private readonly NumericUpDown _pitch = Num(200, 3000, 700, 0, 70);
    private readonly CheckBox _autoWpm = Chk("AUTO WPM");
    private readonly NumericUpDown _wpm = Num(5, 60, 10, 0, 50);
    private readonly TextBox _text = ConsoleBox();
    private readonly Label _status = Status();
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public int PitchHz => (int)_pitch.Value;
    public bool AutoWpm => _autoWpm.Checked;
    public int FixedWpm => (int)_wpm.Value;

    public KiwiCwPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _autoWpm.Checked = true;
        _wpm.Enabled = false;
        var top = Bar();
        top.Controls.AddRange([
            Cap("KiwiCW"), Cap("PITCH"), _pitch, Cap("Hz"),
            _autoWpm, Cap("WPM"), _wpm,
            Btn("CLEAR", () => { _text.Clear(); CommandRequested?.Invoke("clear"); }),
            Btn("RESET", () => CommandRequested?.Invoke("reset")),
            Btn("TRAIN", () =>
            {
                _loading = true;
                _autoWpm.Checked = true;
                _wpm.Enabled = false;
                _loading = false;
                CommandRequested?.Invoke("train");
                OptionsChanged?.Invoke();
            })
        ]);
        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(top);
        _autoWpm.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _wpm.Enabled = !_autoWpm.Checked;
            OptionsChanged?.Invoke();
        };
        Wire(_pitch, _wpm);
    }

    public void SetOptions(int pitchHz, bool autoWpm, int fixedWpm)
    {
        _loading = true;
        _pitch.Value = Clamp(pitchHz, _pitch);
        _autoWpm.Checked = autoWpm;
        _wpm.Value = Clamp(fixedWpm <= 0 ? 10 : fixedWpm, _wpm);
        _wpm.Enabled = !autoWpm;
        _loading = false;
    }

    public void LoadOptions(IReadOnlyDictionary<string, string> options) =>
        SetOptions(Int(options, "pitch", 700), Flag(options, "autoWpm", true), Int(options, "wpm", 10));

    public Dictionary<string, string> Snapshot() => BuildOptions(true);

    public Dictionary<string, string> BuildOptions(bool enabled = true) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["pitch"] = PitchHz.ToString(CultureInfo.InvariantCulture),
        ["autoWpm"] = AutoWpm ? "true" : "false",
        ["wpm"] = (AutoWpm ? 0 : FixedWpm).ToString(CultureInfo.InvariantCulture)
    };

    public void ApplyResult(AfPluginResult result)
    {
        if (result.Kind.Equals("KIWICW_CHAR", StringComparison.OrdinalIgnoreCase))
        {
            _text.AppendText(result.Fields?.GetValueOrDefault("character") ?? result.Text);
            Trim(_text);
        }
        else if (result.Kind.Equals("WPM", StringComparison.OrdinalIgnoreCase)
                 && result.Fields?.TryGetValue("wpm", out var wpm) == true
                 && AutoWpm)
        {
            _status.Text = $"WPM {wpm} · {result.Text}";
            return;
        }
        _status.Text = result.Text;
    }

    private void Wire(params Control[] controls)
    {
        foreach (var c in controls)
        {
            if (c is NumericUpDown n) n.ValueChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        }
    }

    private static void Trim(TextBox box)
    {
        if (box.TextLength > 20_000) box.Text = box.Text[^12_000..];
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    private static FlowLayoutPanel Bar() => new()
    {
        Dock = DockStyle.Top, Height = 55, WrapContents = true,
        BackColor = Color.FromArgb(15, 35, 46), Padding = new Padding(3)
    };
    private static TextBox ConsoleBox() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        BackColor = Color.FromArgb(2, 10, 15), ForeColor = Color.FromArgb(210, 228, 236),
        Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None, WordWrap = false
    };
    private static Label Status() => new()
    {
        Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37), ForeColor = Color.FromArgb(170, 200, 214), Padding = new Padding(6, 0, 0, 0)
    };
    private static Label Cap(string t) => new() { Text = t, AutoSize = true, ForeColor = Color.FromArgb(150, 180, 196), Margin = new Padding(4, 8, 2, 2) };
    private static CheckBox Chk(string t) => new() { Text = t, AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Margin = new Padding(4, 5, 2, 2) };
    private static NumericUpDown Num(decimal min, decimal max, decimal v, int d, int w) => new()
    {
        Minimum = min, Maximum = max, Value = v, DecimalPlaces = d, Increment = d == 0 ? 1 : .5m, Width = w, Margin = new Padding(2)
    };
    private static decimal Clamp(double value, NumericUpDown box) => Math.Clamp((decimal)value, box.Minimum, box.Maximum);
    private static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback) =>
        options.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : fallback;
    private static bool Flag(IReadOnlyDictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;
    private static Button Btn(string text, Action click)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(3),
            BackColor = Color.FromArgb(35, 66, 83), ForeColor = Color.FromArgb(222, 233, 240)
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        b.Click += (_, _) => click();
        return b;
    }
}
