using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using NeuroSDR.Plugins;
using NeuroSDR.Settings;
using EnsSstv.SlowRx.Managed;

namespace NeuroSDR.Controls;

internal sealed class SstvPluginView : UserControl
{
    private readonly ComboBox _backend = Box(82), _mode = Box(105);
    private readonly NumericUpDown _shift = Number(-500, 500, 0, 55);
    private readonly CheckBox _auto = Check("AUTO VIS"), _adaptive = Check("ADAPT"), _weak = Check("WEAK"),
        _slant = Check("SLANT"), _median = Check("MEDIAN"), _fsk = Check("FSK ID");
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(2, 10, 15) };
    private readonly Label _status = StatusLabel();
    private Bitmap? _bitmap;
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public SstvPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 58, WrapContents = true, AutoScroll = false,
            BackColor = Color.FromArgb(15, 35, 46), Padding = new Padding(3, 3, 3, 1)
        };
        _backend.Items.AddRange(["Managed", "HQ"]);
        _mode.Items.Add(new ModeItem(0, "AUTO"));
        for (var mode = 1; mode <= ModeCatalog.ModeMax; mode++)
            _mode.Items.Add(new ModeItem(mode, ModeCatalog.Get(mode).ShortName));
        top.Controls.AddRange([
            Caption("ENGINE"), _backend, Caption("MODE"), _mode, Caption("SHIFT"), _shift,
            _auto, _adaptive, _weak, _slant, _median, _fsk,
            Button("START", () => CommandRequested?.Invoke("start")),
            Button("RESET", () => CommandRequested?.Invoke("restart")),
            Button("CLEAR", ClearImage)
        ]);
        Controls.Add(_picture);
        Controls.Add(_status);
        Controls.Add(top);
        foreach (var control in new Control[] { _backend, _mode, _shift, _auto, _adaptive, _weak, _slant, _median, _fsk })
        {
            if (control is ComboBox combo) combo.SelectedIndexChanged += (_, _) => Changed();
            else if (control is NumericUpDown number) number.ValueChanged += (_, _) => Changed();
            else if (control is CheckBox check) check.CheckedChanged += (_, _) => Changed();
        }
    }

    public void LoadSettings(AppSettings settings)
    {
        _loading = true;
        _backend.SelectedIndex = settings.SstvBackend.Equals("hq", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        SelectMode(settings.SstvManualMode);
        _shift.Value = Math.Clamp(settings.SstvFrequencyShiftHz, (int)_shift.Minimum, (int)_shift.Maximum);
        _auto.Checked = settings.SstvAutoVis;
        _adaptive.Checked = settings.SstvAdaptive;
        _weak.Checked = settings.SstvWeakSignal;
        _slant.Checked = settings.SstvSlant;
        _median.Checked = settings.SstvMedian;
        _fsk.Checked = settings.SstvFskId;
        _loading = false;
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.SstvBackend = _backend.SelectedIndex == 1 ? "hq" : "managed";
        settings.SstvManualMode = (_mode.SelectedItem as ModeItem)?.Id ?? 0;
        settings.SstvFrequencyShiftHz = (int)_shift.Value;
        settings.SstvAutoVis = _auto.Checked;
        settings.SstvAdaptive = _adaptive.Checked;
        settings.SstvWeakSignal = _weak.Checked;
        settings.SstvSlant = _slant.Checked;
        settings.SstvMedian = _median.Checked;
        settings.SstvFskId = _fsk.Checked;
    }

    public void ApplyResult(AfPluginResult result)
    {
        if (result.Kind.Equals("SSTV_IMAGE", StringComparison.OrdinalIgnoreCase) &&
            SlowModePluginViewHelpers.TryCreateRgbBitmap(result, out var bitmap))
        {
            var old = _bitmap;
            _bitmap = bitmap;
            _picture.Image = bitmap;
            old?.Dispose();
            var name = result.Fields?.GetValueOrDefault("name", "") ?? "";
            var fsk = result.Fields?.GetValueOrDefault("fsk", "") ?? "";
            _status.Text = string.Join(" · ", new[] { name, fsk }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return;
        }
        if (result.Kind.Equals("SSTV_VIS", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = $"VIS · {result.Text} · shift {result.Fields?.GetValueOrDefault("shift", "0")} Hz";
            return;
        }
        if (result.Kind.Equals("SSTV_FSK_ID", StringComparison.OrdinalIgnoreCase)) _status.Text = $"FSK ID · {result.Text}";
        else _status.Text = result.Text;
    }

    public void ClearImage()
    {
        var old = _bitmap;
        _bitmap = null;
        _picture.Image = null;
        old?.Dispose();
        _status.Text = "Display cleared";
    }

    private void SelectMode(int id)
    {
        for (var index = 0; index < _mode.Items.Count; index++)
            if (_mode.Items[index] is ModeItem item && item.Id == id) { _mode.SelectedIndex = index; return; }
        _mode.SelectedIndex = 0;
    }

    private void Changed() { if (!_loading) OptionsChanged?.Invoke(); }

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

    private sealed record ModeItem(int Id, string Name) { public override string ToString() => Name; }
    private static ComboBox Box(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Height = 24, Margin = new Padding(2) };
    private static NumericUpDown Number(int min, int max, int value, int width) => new() { Minimum = min, Maximum = max, Value = value, Width = width, Height = 24, Margin = new Padding(2) };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Margin = new Padding(4, 5, 2, 2) };
    internal static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(126, 174, 195), Margin = new Padding(3, 6, 0, 0), Font = new Font("Segoe UI Semibold", 7f) };
    internal static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = false, Size = new Size(53, 23), Margin = new Padding(2), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(39, 64, 78), ForeColor = Color.FromArgb(220, 231, 237), Font = new Font("Segoe UI Semibold", 7f) };
        button.FlatAppearance.BorderColor = Color.FromArgb(72, 105, 121);
        button.Click += (_, _) => action();
        return button;
    }
    internal static Label StatusLabel() => new() { Dock = DockStyle.Bottom, Height = 20, BackColor = Color.FromArgb(9, 25, 33), ForeColor = Color.FromArgb(134, 174, 193), Font = new Font("Segoe UI", 7.5f), TextAlign = ContentAlignment.MiddleLeft };
}

internal sealed class RttyPluginView : UserControl
{
    private readonly NumericUpDown _baud = Number(10, 300, 50, 1, 60), _center = Number(200, 3000, 1000, 0, 65),
        _deviation = Number(10, 700, 225, 0, 55);
    private readonly ComboBox _stopBits = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 54, Margin = new Padding(2) };
    private readonly CheckBox _inverse = Check("INV"), _auto = Check("AUTO"), _usos = Check("USOS"), _unshift = Check("UNSHIFT ERR");
    private readonly TextBox _text = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(2, 12, 18), ForeColor = Color.FromArgb(221, 230, 235), BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 10f)
    };
    private readonly Label _status = SstvPluginView.StatusLabel();
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public RttyPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 55, WrapContents = true, BackColor = Color.FromArgb(15, 35, 46), Padding = new Padding(3) };
        _stopBits.Items.AddRange(["1", "1.5"]);
        top.Controls.AddRange([
            SstvPluginView.Caption("BAUD"), _baud, SstvPluginView.Caption("CENTER"), _center,
            SstvPluginView.Caption("SHIFT/2"), _deviation, SstvPluginView.Caption("STOP"), _stopBits,
            _inverse, _auto, _usos, _unshift,
            SstvPluginView.Button("RESET", () => CommandRequested?.Invoke("reset")),
            SstvPluginView.Button("CLEAR", ClearText)
        ]);
        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(top);
        foreach (var control in new Control[] { _baud, _center, _deviation, _stopBits, _inverse, _auto, _usos, _unshift })
        {
            if (control is ComboBox combo) combo.SelectedIndexChanged += (_, _) => Changed();
            else if (control is NumericUpDown number) number.ValueChanged += (_, _) => Changed();
            else if (control is CheckBox check) check.CheckedChanged += (_, _) => Changed();
        }
    }

    public void LoadSettings(AppSettings settings)
    {
        _loading = true;
        _baud.Value = Math.Clamp((decimal)settings.RttyBaud, _baud.Minimum, _baud.Maximum);
        _center.Value = Math.Clamp(settings.RttyCenterHz, (int)_center.Minimum, (int)_center.Maximum);
        _deviation.Value = Math.Clamp(settings.RttyDeviationHz, (int)_deviation.Minimum, (int)_deviation.Maximum);
        _stopBits.SelectedIndex = settings.RttyStopBits >= 1.25 ? 1 : 0;
        _inverse.Checked = settings.RttyInverse;
        _auto.Checked = settings.RttyAutoPolarity;
        _usos.Checked = settings.RttyUsos;
        _unshift.Checked = settings.RttyUnshiftOnError;
        _loading = false;
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.RttyBaud = (double)_baud.Value;
        settings.RttyCenterHz = (int)_center.Value;
        settings.RttyDeviationHz = (int)_deviation.Value;
        settings.RttyStopBits = _stopBits.SelectedIndex == 1 ? 1.5 : 1;
        settings.RttyInverse = _inverse.Checked;
        settings.RttyAutoPolarity = _auto.Checked;
        settings.RttyUsos = _usos.Checked;
        settings.RttyUnshiftOnError = _unshift.Checked;
    }

    public void SetFskTuning(int centerHz, int deviationHz)
    {
        _loading = true;
        _center.Value = Math.Clamp(centerHz, (int)_center.Minimum, (int)_center.Maximum);
        _deviation.Value = Math.Clamp(deviationHz, (int)_deviation.Minimum, (int)_deviation.Maximum);
        _loading = false;
    }

    public void ApplyResult(AfPluginResult result)
    {
        if (result.Kind.Equals("RTTY_CHAR", StringComparison.OrdinalIgnoreCase))
        {
            _text.AppendText(result.Fields?.GetValueOrDefault("character", result.Text) ?? result.Text);
            if (_text.TextLength > 12_000) _text.Text = _text.Text[^8_000..];
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
            return;
        }
        if (result.Kind.Equals("RTTY_INVERSE", StringComparison.OrdinalIgnoreCase) && bool.TryParse(result.Text, out var inverse))
        {
            _loading = true;
            _inverse.Checked = inverse;
            _loading = false;
        }
        _status.Text = result.Text;
    }

    public void ClearText()
    {
        _text.Clear();
        _status.Text = "Display cleared";
        CommandRequested?.Invoke("clear");
    }

    private void Changed() { if (!_loading) OptionsChanged?.Invoke(); }
    private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals, int width) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .5m, Width = width, Margin = new Padding(2) };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Margin = new Padding(4, 5, 2, 2) };
}

internal sealed class WeatherFaxPluginView : UserControl
{
    private readonly ComboBox _lpm = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Margin = new Padding(2) };
    private readonly NumericUpDown _calibration = new() { Minimum = -.05m, Maximum = .05m, DecimalPlaces = 4, Increment = .0005m, Width = 72, Margin = new Padding(2) };
    private readonly CheckBox _gray = Check("GRAY"), _video = Check("VIDEO LPF"), _skip = Check("SKIP TONE");
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(2, 10, 15) };
    private readonly Label _status = SstvPluginView.StatusLabel();
    private Bitmap? _bitmap;
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public WeatherFaxPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 55, WrapContents = true, BackColor = Color.FromArgb(15, 35, 46), Padding = new Padding(3) };
        _lpm.Items.AddRange(["120 LPM", "60 LPM"]);
        top.Controls.AddRange([
            SstvPluginView.Caption("RATE"), _lpm, SstvPluginView.Caption("CAL"), _calibration,
            _gray, _video, _skip,
            SstvPluginView.Button("LOCK", () => CommandRequested?.Invoke("lock")),
            SstvPluginView.Button("NOW", () => CommandRequested?.Invoke("immediate")),
            SstvPluginView.Button("UNLOCK", () => CommandRequested?.Invoke("unlock")),
            SstvPluginView.Button("RESTART", () => CommandRequested?.Invoke("restart")),
            SstvPluginView.Button("CLEAR", ClearImage)
        ]);
        Controls.Add(_picture);
        Controls.Add(_status);
        Controls.Add(top);
        _lpm.SelectedIndexChanged += (_, _) => Changed();
        _calibration.ValueChanged += (_, _) => Changed();
        _gray.CheckedChanged += (_, _) => Changed();
        _video.CheckedChanged += (_, _) => Changed();
        _skip.CheckedChanged += (_, _) => Changed();
    }

    public void LoadSettings(AppSettings settings)
    {
        _loading = true;
        _lpm.SelectedIndex = settings.WeatherFaxLpm == 60 ? 1 : 0;
        _calibration.Value = Math.Clamp((decimal)settings.WeatherFaxCalibration, _calibration.Minimum, _calibration.Maximum);
        _gray.Checked = settings.WeatherFaxGrayscale;
        _video.Checked = settings.WeatherFaxVideoFilter;
        _skip.Checked = settings.WeatherFaxSkipStartTone;
        _loading = false;
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.WeatherFaxLpm = _lpm.SelectedIndex == 1 ? 60 : 120;
        settings.WeatherFaxCalibration = (double)_calibration.Value;
        settings.WeatherFaxGrayscale = _gray.Checked;
        settings.WeatherFaxVideoFilter = _video.Checked;
        settings.WeatherFaxSkipStartTone = _skip.Checked;
    }

    public void ApplyResult(AfPluginResult result)
    {
        if ((result.Kind.Equals("WEFAX_IMAGE", StringComparison.OrdinalIgnoreCase) ||
             result.Kind.Equals("WEFAX_COMPLETE", StringComparison.OrdinalIgnoreCase)) &&
            SlowModePluginViewHelpers.TryCreateRgbBitmap(result, out var bitmap))
        {
            var old = _bitmap;
            _bitmap = bitmap;
            _picture.Image = bitmap;
            old?.Dispose();
        }
        _status.Text = result.Text;
    }

    public void ClearImage()
    {
        var old = _bitmap;
        _bitmap = null;
        _picture.Image = null;
        old?.Dispose();
        _status.Text = "Display cleared";
    }

    private void Changed() { if (!_loading) OptionsChanged?.Invoke(); }
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Margin = new Padding(4, 5, 2, 2) };

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

internal static class SlowModePluginViewHelpers
{
    public static bool TryCreateRgbBitmap(AfPluginResult result, out Bitmap bitmap)
    {
        bitmap = null!;
        if (result.BinaryData is not { Length: > 0 } rgb || result.Fields is null ||
            !int.TryParse(result.Fields.GetValueOrDefault("width"), out var width) ||
            !int.TryParse(result.Fields.GetValueOrDefault("height"), out var height) ||
            !int.TryParse(result.Fields.GetValueOrDefault("stride"), out var sourceStride) ||
            width <= 0 || height <= 0 || sourceStride < width * 3 || rgb.Length < sourceStride * height) return false;
        var created = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = created.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < height; y++)
            {
                Array.Clear(row);
                for (var x = 0; x < width; x++)
                {
                    var source = y * sourceStride + x * 3;
                    var target = x * 3;
                    row[target] = rgb[source + 2];
                    row[target + 1] = rgb[source + 1];
                    row[target + 2] = rgb[source];
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        catch
        {
            created.UnlockBits(data);
            created.Dispose();
            return false;
        }
        created.UnlockBits(data);
        bitmap = created;
        return true;
    }
}
