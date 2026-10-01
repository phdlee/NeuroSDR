using System.Globalization;
using NeuroSDR.Plugins;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class KiwiNavtexPluginView : UserControl
{
    private readonly NumericUpDown _center = Num(200, 3000, 1000, 0, 70);
    private readonly NumericUpDown _deviation = Num(40, 200, 85, 0, 60);
    private readonly CheckBox _inverse = Chk("INV");
    private readonly CheckBox _pll = Chk("PLL");
    private readonly TextBox _text = ConsoleBox();
    private readonly Label _status = Status();
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public KiwiNavtexPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        var top = Bar();
        top.Controls.AddRange([
            Cap("KiwiNAVTEX"), Cap("CF"), _center, Cap("DEV"), _deviation, _inverse, _pll,
            Btn("CLEAR", () => { _text.Clear(); CommandRequested?.Invoke("clear"); }),
            Btn("RESET", () => CommandRequested?.Invoke("reset"))
        ]);
        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(top);
        Wire(_center, _deviation, _inverse, _pll);
    }

    public void LoadSettings(AppSettings s)
    {
        _loading = true;
        _center.Value = Clamp(s.KiwiNavtexCenterHz, _center);
        _deviation.Value = Clamp(s.KiwiNavtexDeviationHz, _deviation);
        _inverse.Checked = s.KiwiNavtexInverse;
        _pll.Checked = s.KiwiNavtexPll;
        _loading = false;
    }

    public void SaveSettings(AppSettings s)
    {
        s.KiwiNavtexCenterHz = (int)_center.Value;
        s.KiwiNavtexDeviationHz = (int)_deviation.Value;
        s.KiwiNavtexInverse = _inverse.Checked;
        s.KiwiNavtexPll = _pll.Checked;
    }

    public void SetFskTuning(int centerHz, int deviationHz)
    {
        _loading = true;
        _center.Value = Clamp(centerHz, _center);
        _deviation.Value = Clamp(deviationHz, _deviation);
        _loading = false;
    }

    public void ApplyResult(AfPluginResult result)
    {
        if (result.Kind.Equals("KIWINAVTEX_CHAR", StringComparison.OrdinalIgnoreCase))
        {
            _text.AppendText(result.Fields?.GetValueOrDefault("character") ?? result.Text);
            Trim(_text);
        }
        _status.Text = result.Text;
    }

    private void Wire(params Control[] controls)
    {
        foreach (var c in controls)
        {
            if (c is NumericUpDown n) n.ValueChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
            if (c is CheckBox b) b.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
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
