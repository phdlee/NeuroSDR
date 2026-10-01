using NeuroSDR.Plugins.Caption;

namespace NeuroSDR.Controls;

internal static class CaptionPluginChrome
{
    public static readonly Color BarBack = Color.FromArgb(14, 28, 38);
    public static readonly Color OnFill = Color.FromArgb(58, 104, 148);
    public static readonly Color OffFill = Color.FromArgb(46, 54, 64);
    public static readonly Color TinyFill = Color.FromArgb(36, 62, 78);
    public static readonly Color SetupFill = Color.FromArgb(48, 92, 122);

    public static Panel CreateBar() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = BarBack
    };

    public static void StyleVfo(ComboBox vfo)
    {
        vfo.Margin = new Padding(1, 1, 4, 0);
        vfo.Size = new Size(88, 22);
        vfo.Font = new Font("Segoe UI", 7.5f);
        vfo.FlatStyle = FlatStyle.Flat;
        vfo.BackColor = Color.FromArgb(8, 18, 26);
        vfo.ForeColor = Color.FromArgb(255, 193, 69);
    }

    public static Button Tiny(string text, int width, Color fill)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 22,
            FlatStyle = FlatStyle.Flat,
            BackColor = fill,
            ForeColor = Color.FromArgb(228, 236, 242),
            Font = new Font("Segoe UI Semibold", 7.4f),
            Margin = new Padding(1, 1, 1, 0),
            Padding = Padding.Empty,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(
            Math.Min(255, fill.R + 18), Math.Min(255, fill.G + 18), Math.Min(255, fill.B + 18));
        return button;
    }

    public static void PaintCaptionToggle(Button button, bool on)
    {
        button.Text = on ? "CAPTION ON" : "CAPTION OFF";
        button.BackColor = on ? OnFill : OffFill;
        button.ForeColor = on ? Color.FromArgb(242, 248, 252) : Color.FromArgb(168, 180, 192);
        button.FlatAppearance.MouseOverBackColor = on
            ? Color.FromArgb(72, 120, 164)
            : Color.FromArgb(58, 66, 76);
    }

    public static CheckBox GateEnable() => new()
    {
        Text = "GATE",
        AutoSize = true,
        Height = 22,
        Margin = new Padding(8, 2, 2, 0),
        ForeColor = Color.FromArgb(176, 196, 210),
        Font = new Font("Segoe UI", 7.5f),
        FlatStyle = FlatStyle.Flat,
        Checked = false
    };

    public static NumericUpDown GateLevel() => new()
    {
        Minimum = 1,
        Maximum = 10,
        Value = NeuroCaption.DefaultGate,
        Width = 42,
        Height = 22,
        Margin = new Padding(0, 1, 2, 0),
        Font = new Font("Segoe UI", 7.5f),
        Enabled = false
    };
}
