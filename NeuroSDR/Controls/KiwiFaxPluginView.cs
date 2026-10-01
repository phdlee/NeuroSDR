using System.Drawing.Imaging;
using NeuroSDR.Plugins;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class KiwiFaxPluginView : UserControl
{
    private readonly ComboBox _lpm = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Margin = new Padding(2) };
    private readonly CheckBox _phasing = Check("PHASING");
    private readonly CheckBox _autoStop = Check("AUTOSTOP");
    private readonly CheckBox _headers = Check("HEADERS");
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(2, 10, 15) };
    private readonly PictureBox _picture = new()
    {
        SizeMode = PictureBoxSizeMode.AutoSize,
        BackColor = Color.FromArgb(2, 10, 15)
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37), ForeColor = Color.FromArgb(170, 200, 214),
        Padding = new Padding(6, 0, 0, 0)
    };
    private Bitmap? _bitmap;
    private bool _loading;
    private bool _followBottom = true;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public KiwiFaxPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 55, WrapContents = true,
            BackColor = Color.FromArgb(15, 35, 46), Padding = new Padding(3)
        };
        _lpm.Items.AddRange(["120 LPM", "60 LPM"]);
        top.Controls.AddRange([
            Caption("RATE"), _lpm, _phasing, _autoStop, _headers,
            Button("RESTART", () => CommandRequested?.Invoke("restart")),
            Button("CLEAR", ClearImage),
            Button("SAVE", SaveImage)
        ]);
        _scroll.Controls.Add(_picture);
        Controls.Add(_scroll);
        Controls.Add(_status);
        Controls.Add(top);
        _lpm.SelectedIndexChanged += (_, _) => Changed();
        _phasing.CheckedChanged += (_, _) => Changed();
        _autoStop.CheckedChanged += (_, _) => Changed();
        _headers.CheckedChanged += (_, _) => Changed();
        _scroll.Scroll += (_, _) =>
        {
            var atBottom = _scroll.VerticalScroll.Value >=
                           Math.Max(0, _scroll.VerticalScroll.Maximum - _scroll.ClientSize.Height - 8);
            _followBottom = atBottom;
        };
    }

    public void LoadSettings(AppSettings settings)
    {
        var options = settings.AfPluginUiState.GetValueOrDefault("builtin.af.kiwifax")
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _loading = true;
        _lpm.SelectedIndex = OptionText(options, "lpm", "120") == "60" ? 1 : 0;
        _phasing.Checked = Flag(options, "phasing", true);
        _autoStop.Checked = Flag(options, "autostop", true);
        _headers.Checked = Flag(options, "headers", true);
        _loading = false;
    }

    public void SaveSettings(AppSettings settings) =>
        settings.AfPluginUiState["builtin.af.kiwifax"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lpm"] = _lpm.SelectedIndex == 1 ? "60" : "120",
            ["phasing"] = _phasing.Checked.ToString(),
            ["autostop"] = _autoStop.Checked.ToString(),
            ["headers"] = _headers.Checked.ToString(),
            ["width"] = "1024"
        };

    public void ApplyResult(AfPluginResult result)
    {
        if ((result.Kind.Equals("KIWIFAX_IMAGE", StringComparison.OrdinalIgnoreCase) ||
             result.Kind.Equals("KIWIFAX_COMPLETE", StringComparison.OrdinalIgnoreCase)) &&
            SlowModePluginViewHelpers.TryCreateRgbBitmap(result, out var bitmap))
        {
            var old = _bitmap;
            _bitmap = bitmap;
            _picture.Image = bitmap;
            old?.Dispose();
            if (_followBottom)
            {
                _scroll.AutoScrollPosition = new Point(0, Math.Max(0, _picture.Height - _scroll.ClientSize.Height));
            }
        }
        _status.Text = result.Kind.Equals("KIWIFAX_COMPLETE", StringComparison.OrdinalIgnoreCase)
            ? $"Complete · {result.Text} · SAVE available"
            : result.Text;
    }

    public void ClearImage()
    {
        var old = _bitmap;
        _bitmap = null;
        _picture.Image = null;
        old?.Dispose();
        _status.Text = "Display cleared";
        CommandRequested?.Invoke("clear");
    }

    private void SaveImage()
    {
        if (_bitmap is null)
        {
            _status.Text = "Nothing to save";
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Title = "Save KiwiFAX image",
            Filter = "PNG image|*.png|JPEG image|*.jpg|Bitmap|*.bmp",
            FileName = $"kiwifax-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        var format = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            _ => ImageFormat.Png
        };
        _bitmap.Save(dialog.FileName, format);
        _status.Text = $"Saved · {Path.GetFileName(dialog.FileName)}";
    }

    private void Changed() { if (!_loading) OptionsChanged?.Invoke(); }
    private static string OptionText(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : fallback;
    private static bool Flag(IReadOnlyDictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;
    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(150, 180, 196),
        Margin = new Padding(4, 8, 2, 2)
    };
    private static CheckBox Check(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Margin = new Padding(4, 5, 2, 2)
    };
    private static Button Button(string text, Action click)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 3, 2, 2),
            BackColor = Color.FromArgb(35, 66, 83), ForeColor = Color.FromArgb(222, 233, 240)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        button.Click += (_, _) => click();
        return button;
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
