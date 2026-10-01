using System.Globalization;
using NeuroSDR.Plugins;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

/// <summary>USB AF WWV/WWVH 100 Hz IRIG-H decoder UI.</summary>
internal sealed class KiwiWwvPluginView : UserControl
{
    private readonly NumericUpDown _tone = Num(50, 2000, 100, 0, 70);
    private readonly CheckBox _inverse = Chk("INV");
    private readonly TextBox _text = ConsoleBox();
    private readonly Label _status = Status();
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public KiwiWwvPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        var top = Bar();
        top.Controls.AddRange([
            Cap("KiwiWWV USB"), Cap("TONE"), _tone, Cap("Hz"),
            _inverse,
            Btn("CLEAR", () => { _text.Clear(); CommandRequested?.Invoke("clear"); }),
            Btn("RESET", () => CommandRequested?.Invoke("reset"))
        ]);
        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(top);
        _tone.ValueChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _inverse.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _status.Text = "USB · 100 Hz IRIG-H · wait for 0.8s M markers, then 60 bits";
    }

    public void LoadSettings(AppSettings s)
    {
        _loading = true;
        _tone.Value = Clamp(s.KiwiWwvToneHz, _tone);
        _inverse.Checked = s.KiwiWwvInverse;
        _loading = false;
    }

    public void SaveSettings(AppSettings s)
    {
        s.KiwiWwvToneHz = (int)_tone.Value;
        s.KiwiWwvInverse = _inverse.Checked;
    }

    public Dictionary<string, string> BuildOptions(bool enabled) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["tone"] = _tone.Value.ToString(CultureInfo.InvariantCulture),
        ["inverse"] = _inverse.Checked ? "true" : "false"
    };

    public void ApplyResult(AfPluginResult result)
    {
        if (result.Kind.Equals("KIWIWWV_LINE", StringComparison.OrdinalIgnoreCase) ||
            result.Kind.Equals("KIWIWWV_TIME", StringComparison.OrdinalIgnoreCase))
        {
            _text.AppendText((result.Fields?.GetValueOrDefault("line") ?? result.Text) + Environment.NewLine);
            Trim(_text);
            if (result.Kind.Equals("KIWIWWV_TIME", StringComparison.OrdinalIgnoreCase))
                _status.Text = result.Text;
            return;
        }
        if (result.Kind.Equals("STATUS", StringComparison.OrdinalIgnoreCase) ||
            result.Kind.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            _status.Text = result.Text;
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
        Minimum = min, Maximum = max, Value = v, DecimalPlaces = d, Increment = 1, Width = w, Margin = new Padding(2)
    };
    private static decimal Clamp(double value, NumericUpDown box) => Math.Clamp((decimal)value, box.Minimum, box.Maximum);
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
