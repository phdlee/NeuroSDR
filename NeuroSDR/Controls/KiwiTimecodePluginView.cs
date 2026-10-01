using System.Globalization;
using NeuroSDR.Plugins;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

/// <summary>UI for KiwiTIMECODE IQ plug-in (PLL controls + tick log + scope strip).</summary>
internal sealed class KiwiTimecodePluginView : UserControl
{
    private readonly CheckBox _pll = Chk("PLL");
    private readonly NumericUpDown _exponent = Num(1, 8, 1, 0, 40);
    private readonly NumericUpDown _bandwidth = Num(0.5m, 100, 10, 1, 60);
    private readonly NumericUpDown _offset = Num(-5000, 5000, 0, 0, 70);
    private readonly NumericUpDown _gain = Num(0, 100, 0, 0, 50);
    private readonly CheckBox _replaceIq = Chk("REPLACE IQ");
    private readonly CheckBox _carrierOnly = Chk("CARRIER");
    private readonly TextBox _log = ConsoleBox();
    private readonly Label _status = Status();
    private readonly PictureBox _scope = new()
    {
        Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(2, 10, 15),
        SizeMode = PictureBoxSizeMode.StretchImage
    };
    private Bitmap? _scopeBitmap;
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public KiwiTimecodePluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _pll.Checked = true;
        var top = Bar();
        top.Controls.AddRange([
            Cap("KiwiTIMECODE"), _pll, Cap("EXP"), _exponent, Cap("BW"), _bandwidth,
            Cap("OFF"), _offset, Cap("GAIN"), _gain, _carrierOnly, _replaceIq,
            Btn("CLEAR", () => { _log.Clear(); CommandRequested?.Invoke("clear"); }),
            Btn("RESET", () => CommandRequested?.Invoke("reset"))
        ]);
        Controls.Add(_log);
        Controls.Add(_status);
        Controls.Add(_scope);
        Controls.Add(top);
        Wire(_pll, _exponent, _bandwidth, _offset, _gain, _replaceIq, _carrierOnly);
    }

    public void LoadSettings(AppSettings settings)
    {
        _loading = true;
        _pll.Checked = settings.KiwiTimecodePll;
        _exponent.Value = Clamp(settings.KiwiTimecodeExponent is 1 or 2 or 4 or 8 ? settings.KiwiTimecodeExponent : 1, _exponent);
        _bandwidth.Value = Clamp(settings.KiwiTimecodeBandwidthHz, _bandwidth);
        _offset.Value = Clamp(settings.KiwiTimecodeOffsetHz, _offset);
        _gain.Value = Clamp(settings.KiwiTimecodeGain, _gain);
        _replaceIq.Checked = settings.KiwiTimecodeReplaceIq;
        _carrierOnly.Checked = settings.KiwiTimecodeDisplayMode != 0;
        _loading = false;
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.KiwiTimecodePll = _pll.Checked;
        var exp = (int)_exponent.Value;
        settings.KiwiTimecodeExponent = exp is 1 or 2 or 4 or 8 ? exp : 1;
        settings.KiwiTimecodeBandwidthHz = (double)_bandwidth.Value;
        settings.KiwiTimecodeOffsetHz = (int)_offset.Value;
        settings.KiwiTimecodeGain = (int)_gain.Value;
        settings.KiwiTimecodeReplaceIq = _replaceIq.Checked;
        settings.KiwiTimecodeDisplayMode = _carrierOnly.Checked ? 1 : 0;
    }

    public Dictionary<string, string> BuildOptions(bool enabled = true) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["pll"] = _pll.Checked ? "true" : "false",
        ["exponent"] = ((int)_exponent.Value is 1 or 2 or 4 or 8 ? (int)_exponent.Value : 1)
            .ToString(CultureInfo.InvariantCulture),
        ["bandwidth"] = _bandwidth.Value.ToString(CultureInfo.InvariantCulture),
        ["offset"] = _offset.Value.ToString(CultureInfo.InvariantCulture),
        ["gain"] = _gain.Value.ToString(CultureInfo.InvariantCulture),
        ["displayMode"] = (_carrierOnly.Checked ? 1 : 0).ToString(CultureInfo.InvariantCulture),
        ["replaceIq"] = _replaceIq.Checked ? "true" : "false",
        ["emitScope"] = "true"
    };

    public void ApplyResult(IqPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (result.Kind.Equals("TICK", StringComparison.OrdinalIgnoreCase))
        {
            _log.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {result.Text}\r\n");
            Trim(_log);
            _status.Text = result.Text;
            return;
        }
        if (result.Kind.Equals("SCOPE", StringComparison.OrdinalIgnoreCase) && result.BinaryData is { Length: > 0 } bytes)
        {
            DrawScope(bytes);
            return;
        }
        _status.Text = result.Text;
    }

    private void DrawScope(byte[] bytes)
    {
        if (IsDisposed || Disposing || _scope.IsDisposed) return;
        var width = Math.Max(64, _scope.Width);
        var height = Math.Max(24, _scope.Height);
        if (_scopeBitmap is null || _scopeBitmap.Width != width || _scopeBitmap.Height != height)
        {
            var next = new Bitmap(width, height);
            var old = _scopeBitmap;
            _scopeBitmap = next;
            _scope.Image = next;
            old?.Dispose();
        }
        using var g = Graphics.FromImage(_scopeBitmap);
        g.Clear(Color.FromArgb(2, 10, 15));
        using var pen = new Pen(Color.FromArgb(90, 200, 160), 1f);
        var mid = height / 2f;
        var step = Math.Max(1, bytes.Length / width);
        PointF? prev = null;
        for (var x = 0; x < width; x++)
        {
            var index = Math.Min(bytes.Length - 1, x * step);
            var y = mid - (bytes[index] - 127) / 127f * (mid - 2);
            var pt = new PointF(x, y);
            if (prev is { } p) g.DrawLine(pen, p, pt);
            prev = pt;
        }
        _scope.Image = _scopeBitmap;
    }

    private void Wire(params Control[] controls)
    {
        foreach (var c in controls)
        {
            if (c is NumericUpDown n) n.ValueChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
            if (c is CheckBox box) box.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scope.Image = null;
            _scopeBitmap?.Dispose();
            _scopeBitmap = null;
        }
        base.Dispose(disposing);
    }
}
