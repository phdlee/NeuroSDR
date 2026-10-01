using NeuroSDR.Core;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class SceneFrequencyChannelEditorForm : Form
{
    private readonly TextBox _name = new();
    private readonly NumericUpDown _frequency = new();
    private readonly ComboBox _mode = new();
    private readonly NumericUpDown _bandwidth = new();
    private readonly CheckBox _global = new();

    public SceneFrequencyChannel Result { get; }

    public SceneFrequencyChannelEditorForm(SceneFrequencyChannel channel)
    {
        Result = channel.Clone();
        Text = string.IsNullOrWhiteSpace(channel.Name) ? "Add frequency" : $"Edit {channel.Name}";
        ClientSize = new Size(400, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 27, 37);
        ForeColor = Color.FromArgb(216, 226, 236);
        Font = new Font("Segoe UI", 9f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 12),
            ColumnCount = 2,
            RowCount = 6,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _name.Dock = DockStyle.Fill;
        _name.Text = channel.Name;
        _name.BackColor = Color.FromArgb(5, 17, 24);
        _name.ForeColor = Color.FromArgb(226, 235, 242);
        _name.BorderStyle = BorderStyle.FixedSingle;

        ConfigureNumeric(_frequency, 1, 7_500_000_000m, 1, channel.Frequency);
        _frequency.ThousandsSeparator = true;
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Items.AddRange(Enum.GetValues<RadioMode>().Cast<object>().ToArray());
        _mode.SelectedItem = channel.Mode;
        ConfigureNumeric(_bandwidth, 100, 500_000, 100, channel.Bandwidth);
        _bandwidth.ThousandsSeparator = true;
        _global.Text = "GLOBAL (all SCENE)";
        _global.Checked = channel.Global;
        _global.ForeColor = ForeColor;
        _global.AutoSize = true;

        AddRow(layout, 0, "NAME", _name);
        AddRow(layout, 1, "FREQUENCY (Hz)", _frequency);
        AddRow(layout, 2, "MODE", _mode);
        AddRow(layout, 3, "BANDWIDTH (Hz)", _bandwidth);
        layout.Controls.Add(new Label { Text = "SCOPE", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(145, 181, 198) }, 0, 4);
        layout.Controls.Add(_global, 1, 4);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0) };
        var ok = MakeButton("OK", DialogResult.OK, Color.FromArgb(196, 112, 39));
        var cancel = MakeButton("CANCEL", DialogResult.Cancel, Color.FromArgb(47, 66, 78));
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => SaveResult();
    }

    private void SaveResult()
    {
        Result.Name = _name.Text.Trim();
        Result.Frequency = decimal.ToInt64(_frequency.Value);
        Result.Mode = _mode.SelectedItem is RadioMode mode ? mode : RadioMode.NFM;
        Result.Bandwidth = decimal.ToInt32(_bandwidth.Value);
        Result.Global = _global.Checked;
    }

    private static void ConfigureNumeric(NumericUpDown box, decimal minimum, decimal maximum, decimal increment, decimal value)
    {
        box.Dock = DockStyle.Fill;
        box.Minimum = minimum;
        box.Maximum = maximum;
        box.Increment = increment;
        box.Value = Math.Clamp(value, minimum, maximum);
        box.BackColor = Color.FromArgb(5, 17, 24);
        box.ForeColor = Color.FromArgb(226, 235, 242);
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string text, Control control)
    {
        layout.Controls.Add(new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(145, 181, 198) }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private static Button MakeButton(string text, DialogResult result, Color color) => new()
    {
        Text = text, DialogResult = result, Size = new Size(82, 29), FlatStyle = FlatStyle.Flat,
        BackColor = color, ForeColor = Color.White
    };
}
