using System.Globalization;
using NeuroSDR.Plugins;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class FlRttyPluginView : UserControl
{
    private static readonly string[] Bauds = ["45", "45.45", "50", "56", "75", "100"];
    private readonly ComboBox _baud = Combo(70);
    private readonly NumericUpDown _shift = Num(20, 900, 170, 0, 60);
    private readonly NumericUpDown _sql = Num(0, 24, 0, 0, 50);
    private readonly CheckBox _inverse = Chk("INV");
    private readonly CheckBox _uos = Chk("UOS");
    private readonly TextBox _text = ConsoleBox();
    private readonly Label _status = Status();
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public FlRttyPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _baud.Items.AddRange(Bauds);
        _baud.SelectedIndex = 1;
        _uos.Checked = true;
        var top = Bar();
        top.Controls.AddRange([
            Cap("flrtty VIEW"), Cap("BAUD"), _baud, Cap("SHIFT"), _shift, Cap("SQL"), _sql, _inverse, _uos,
            Btn("CLEAR", () => { _text.Clear(); CommandRequested?.Invoke("clear"); }),
            Btn("RESET", () => CommandRequested?.Invoke("reset"))
        ]);
        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(top);
        _baud.SelectedIndexChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        Wire(_shift, _sql, _inverse, _uos);
    }

    public void LoadSettings(AppSettings s)
    {
        _loading = true;
        SelectBaud(s.FlRttyBaud);
        _shift.Value = Clamp(s.FlRttyShiftHz, _shift);
        _sql.Value = Clamp(s.FlRttySquelchDb, _sql);
        _inverse.Checked = s.FlRttyInverse;
        _uos.Checked = s.FlRttyUos;
        _loading = false;
    }

    public void SaveSettings(AppSettings s)
    {
        s.FlRttyBaud = double.TryParse(_baud.SelectedItem?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 45.45;
        s.FlRttyShiftHz = (int)_shift.Value;
        s.FlRttySquelchDb = (double)_sql.Value;
        s.FlRttyInverse = _inverse.Checked;
        s.FlRttyUos = _uos.Checked;
    }

    public Dictionary<string, string> BuildOptions(bool enabled) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["baud"] = (_baud.SelectedItem?.ToString() ?? "45.45"),
        ["shift"] = _shift.Value.ToString(CultureInfo.InvariantCulture),
        ["squelch"] = _sql.Value.ToString(CultureInfo.InvariantCulture),
        ["inverse"] = _inverse.Checked ? "true" : "false",
        ["uos"] = _uos.Checked ? "true" : "false"
    };

    public void ApplyResult(AfPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (result.Kind.Equals("FLRTTY_CHAR", StringComparison.OrdinalIgnoreCase))
        {
            if (result.Fields?.TryGetValue("text", out var dump) == true)
            {
                _text.Text = dump;
                _text.SelectionStart = _text.TextLength;
                _text.ScrollToCaret();
            }
            else
            {
                _text.AppendText(result.Fields?.GetValueOrDefault("character") ?? result.Text);
                Trim(_text);
            }
            return;
        }
        if (result.Kind.Equals("STATUS", StringComparison.OrdinalIgnoreCase) ||
            result.Kind.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            _status.Text = result.Text;
    }

    private void SelectBaud(double baud)
    {
        var text = baud.ToString("0.##", CultureInfo.InvariantCulture);
        var i = _baud.Items.IndexOf(text);
        if (i < 0) i = _baud.Items.IndexOf(baud.ToString("0", CultureInfo.InvariantCulture));
        _baud.SelectedIndex = i >= 0 ? i : 1;
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
    private static ComboBox Combo(int w) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = w, Margin = new Padding(2)
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

internal sealed class FlCwPluginView : UserControl
{
    private readonly NumericUpDown _wpm = Num(5, 60, 18, 0, 50);
    private readonly NumericUpDown _sql = Num(0, 24, 0, 0, 50);
    private readonly TextBox _text = ConsoleBox();
    private readonly Label _status = Status();
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public FlCwPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        var top = Bar();
        top.Controls.AddRange([
            Cap("flcw VIEW"), Cap("WPM"), _wpm, Cap("SQL"), _sql,
            Btn("CLEAR", () => { _text.Clear(); CommandRequested?.Invoke("clear"); }),
            Btn("RESET", () => CommandRequested?.Invoke("reset"))
        ]);
        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(top);
        _wpm.ValueChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _sql.ValueChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
    }

    public void ClearDisplay() => _text.Clear();

    public void LoadSettings(AppSettings s)
    {
        _loading = true;
        _wpm.Value = Clamp(s.FlCwFixedWpm <= 0 ? 18 : s.FlCwFixedWpm, _wpm);
        _sql.Value = Clamp(s.FlCwSquelchDb, _sql);
        _loading = false;
    }

    public void SaveSettings(AppSettings s)
    {
        s.FlCwFixedWpm = (int)_wpm.Value;
        s.FlCwSquelchDb = (double)_sql.Value;
        s.FlCwAutoWpm = true;
    }

    public Dictionary<string, string> BuildOptions(bool enabled) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["wpm"] = ((int)_wpm.Value).ToString(CultureInfo.InvariantCulture),
        ["squelch"] = _sql.Value.ToString(CultureInfo.InvariantCulture)
    };

    public void ApplyResult(AfPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (result.Kind.Equals("FLCW_CHAR", StringComparison.OrdinalIgnoreCase))
        {
            if (result.Fields?.TryGetValue("text", out var dump) == true)
            {
                _text.Text = dump;
                _text.SelectionStart = _text.TextLength;
                _text.ScrollToCaret();
            }
            else
                _text.AppendText(result.Fields?.GetValueOrDefault("character") ?? result.Text);
            return;
        }
        _status.Text = result.Text;
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

internal sealed class FlFaxPluginView : UserControl
{
    private readonly ComboBox _lpm = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Margin = new Padding(2) };
    private readonly NumericUpDown _center = new() { Minimum = 1000, Maximum = 2800, Value = 1900, Width = 70, Margin = new Padding(2) };
    private readonly NumericUpDown _shift = new() { Minimum = 400, Maximum = 1000, Value = 800, Width = 60, Margin = new Padding(2) };
    private readonly CheckBox _manual = new() { Text = "MANUAL", AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Checked = true, Margin = new Padding(4, 5, 2, 2) };
    private readonly CheckBox _phasing = new() { Text = "PHASING", AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Margin = new Padding(4, 5, 2, 2) };
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(2, 10, 15) };
    private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.AutoSize, BackColor = Color.FromArgb(2, 10, 15) };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37), ForeColor = Color.FromArgb(170, 200, 214), Padding = new Padding(6, 0, 0, 0)
    };
    private Bitmap? _bitmap;
    private bool _loading;
    private bool _followBottom = true;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public FlFaxPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _lpm.Items.AddRange(["120 LPM", "60 LPM"]);
        _lpm.SelectedIndex = 0;
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 55, WrapContents = true,
            BackColor = Color.FromArgb(15, 35, 46), Padding = new Padding(3)
        };
        top.Controls.AddRange([
            Lab("flfax"), Lab("RATE"), _lpm, Lab("CF"), _center, Lab("SHIFT"), _shift, _manual, _phasing,
            Btn("RESTART", () => CommandRequested?.Invoke("restart")),
            Btn("CLEAR", ClearImage)
        ]);
        _scroll.Controls.Add(_picture);
        Controls.Add(_scroll);
        Controls.Add(_status);
        Controls.Add(top);
        _lpm.SelectedIndexChanged += (_, _) => Changed();
        _center.ValueChanged += (_, _) => Changed();
        _shift.ValueChanged += (_, _) => Changed();
        _manual.CheckedChanged += (_, _) => Changed();
        _phasing.CheckedChanged += (_, _) => Changed();
        _scroll.Scroll += (_, _) =>
        {
            _followBottom = _scroll.VerticalScroll.Value >=
                            Math.Max(0, _scroll.VerticalScroll.Maximum - _scroll.ClientSize.Height - 8);
        };
    }

    public void LoadSettings(AppSettings s)
    {
        _loading = true;
        _lpm.SelectedIndex = s.FlFaxLpm == 60 ? 1 : 0;
        _center.Value = Math.Clamp(s.FlFaxCenterHz, 1000, 2800);
        _shift.Value = Math.Clamp(s.FlFaxShiftHz, 400, 1000);
        _manual.Checked = s.FlFaxManual;
        _phasing.Checked = s.FlFaxPhasing;
        _loading = false;
    }

    public void SaveSettings(AppSettings s)
    {
        s.FlFaxLpm = _lpm.SelectedIndex == 1 ? 60 : 120;
        s.FlFaxCenterHz = (int)_center.Value;
        s.FlFaxShiftHz = (int)_shift.Value;
        s.FlFaxManual = _manual.Checked;
        s.FlFaxPhasing = _phasing.Checked;
    }

    public Dictionary<string, string> BuildOptions(bool enabled) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["lpm"] = (_lpm.SelectedIndex == 1 ? 60 : 120).ToString(CultureInfo.InvariantCulture),
        ["center"] = _center.Value.ToString(CultureInfo.InvariantCulture),
        ["shift"] = _shift.Value.ToString(CultureInfo.InvariantCulture),
        ["manual"] = _manual.Checked ? "true" : "false",
        ["phasing"] = _phasing.Checked ? "true" : "false"
    };

    public void ApplyResult(AfPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if ((result.Kind.Equals("FLFAX_IMAGE", StringComparison.OrdinalIgnoreCase) ||
             result.Kind.Equals("FLFAX_COMPLETE", StringComparison.OrdinalIgnoreCase)) &&
            SlowModePluginViewHelpers.TryCreateRgbBitmap(result, out var bitmap))
        {
            var old = _bitmap;
            _bitmap = bitmap;
            _picture.Image = bitmap;
            old?.Dispose();
            if (_followBottom)
                _scroll.AutoScrollPosition = new Point(0, Math.Max(0, _picture.Height - _scroll.ClientSize.Height));
        }
        if (!result.Kind.Equals("FLFAX_IMAGE", StringComparison.OrdinalIgnoreCase))
            _status.Text = result.Text;
    }

    private void ClearImage()
    {
        var old = _bitmap;
        _bitmap = null;
        _picture.Image = null;
        old?.Dispose();
        CommandRequested?.Invoke("clear");
    }

    private void Changed() { if (!_loading) OptionsChanged?.Invoke(); }
    private static Label Lab(string t) => new()
    {
        Text = t, AutoSize = true, ForeColor = Color.FromArgb(150, 180, 196), Margin = new Padding(4, 8, 2, 2)
    };
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picture.Image = null;
            _bitmap?.Dispose();
            _bitmap = null;
        }
        base.Dispose(disposing);
    }
}
