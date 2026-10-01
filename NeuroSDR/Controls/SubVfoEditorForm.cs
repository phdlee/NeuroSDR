using NeuroSDR.Core;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class SubVfoEditorForm : Form
{
    private readonly TextBox _frequency = FrequencyEntry.CreateMhzBox(0);
    private readonly ComboBox _mode = new();
    private readonly NumericUpDown _bandwidth = new();
    private readonly CheckBox _squelch = new();
    private readonly NumericUpDown _squelchLevel = new();
    private readonly ComboBox _output = new();
    private readonly CheckBox _autoTune = new();
    private readonly Button _autoTuneCfg = new();
    private AutoTuneSettings _autoTuneSettings;
    private readonly Func<AutoTuneSettings, float?>? _suggestTrigger;
    private readonly MainVfoCopy? _main;

    public SubVfoSettings Result { get; }

    public SubVfoEditorForm(
        SubVfoSettings settings,
        Func<AutoTuneSettings, float?>? suggestTrigger = null,
        MainVfoCopy? main = null)
    {
        Result = settings.Clone();
        _suggestTrigger = suggestTrigger;
        _main = main;
        _autoTuneSettings = Result.AutoTune?.Clone() ?? new AutoTuneSettings();
        if (_autoTuneSettings.StandbyFrequency <= 0) _autoTuneSettings.StandbyFrequency = settings.Frequency;
        if (_autoTuneSettings.MinFrequency <= 0) _autoTuneSettings.MinFrequency = settings.Frequency - 12_500;
        if (_autoTuneSettings.MaxFrequency <= 0) _autoTuneSettings.MaxFrequency = settings.Frequency + 12_500;
        Text = string.IsNullOrWhiteSpace(settings.Name) ? "Add Sub VFO" : $"Edit {settings.Name}";
        ClientSize = new Size(400, 398);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 27, 37);
        ForeColor = Color.FromArgb(216, 226, 236);
        Font = new Font("Segoe UI", 9f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 12), ColumnCount = 2, RowCount = 10,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _frequency.Text = FrequencyEntry.FormatMhz(settings.Frequency);
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Items.AddRange(Enum.GetValues<RadioMode>().Cast<object>().ToArray());
        _mode.SelectedItem = settings.Mode;
        ConfigureNumeric(_bandwidth, 100, 500_000, 100, settings.Bandwidth);
        _bandwidth.ThousandsSeparator = true;
        _squelch.Text = "Enabled";
        _squelch.Checked = settings.SquelchEnabled;
        ConfigureNumeric(_squelchLevel, -140, 0, 1, settings.SquelchLevel);
        _output.DropDownStyle = ComboBoxStyle.DropDownList;
        _output.Items.AddRange(["NOT USE (decoder only)", "CH1", "CH2"]);
        _output.SelectedIndex = Math.Clamp(settings.OutputChannel, 0, 2);

        _autoTune.Text = "Signal follow";
        _autoTune.Checked = _autoTuneSettings.Enabled;
        _autoTune.ForeColor = ForeColor;
        _autoTune.AutoSize = true;
        _autoTuneCfg.Text = "CFG";
        _autoTuneCfg.Size = new Size(52, 26);
        _autoTuneCfg.FlatStyle = FlatStyle.Flat;
        _autoTuneCfg.BackColor = Color.FromArgb(35, 66, 83);
        _autoTuneCfg.ForeColor = Color.White;
        _autoTuneCfg.Click += (_, _) =>
        {
            // Seed from current frequency field when possible.
            if (FrequencyEntry.TryParseHz(_frequency.Text, out var hz))
            {
                if (_autoTuneSettings.StandbyFrequency <= 0) _autoTuneSettings.StandbyFrequency = hz;
                if (_autoTuneSettings.MinFrequency <= 0) _autoTuneSettings.MinFrequency = hz - 12_500;
                if (_autoTuneSettings.MaxFrequency <= 0) _autoTuneSettings.MaxFrequency = hz + 12_500;
            }
            var suggested = _suggestTrigger?.Invoke(_autoTuneSettings);
            using var dlg = new AutoTuneEditorForm($"Auto tune · {Result.Name}", _autoTuneSettings, suggested);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _autoTuneSettings = dlg.Result;
            _autoTune.Checked = _autoTuneSettings.Enabled;
        };
        var autoRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        autoRow.Controls.Add(_autoTune);
        autoRow.Controls.Add(_autoTuneCfg);

        var freqHint = new Label
        {
            Text = "MHz · e.g. 448.8 (comma optional)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(120, 150, 165),
            Font = new Font("Segoe UI", 7.5f)
        };

        AddRow(layout, 0, "FREQUENCY", _frequency);
        layout.Controls.Add(freqHint, 1, 1);
        var copyMain = MakeButton("COPY MAIN", DialogResult.None, Color.FromArgb(35, 66, 83));
        copyMain.Size = new Size(118, 26);
        copyMain.Enabled = _main is not null;
        copyMain.Click += (_, _) => CopyFromMain();
        var copyRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false
        };
        copyRow.Controls.Add(copyMain);
        layout.Controls.Add(new Label
        {
            Text = "FROM MAIN", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(145, 181, 198)
        }, 0, 2);
        layout.Controls.Add(copyRow, 1, 2);
        AddRow(layout, 3, "MODE", _mode);
        AddRow(layout, 4, "BANDWIDTH (Hz)", _bandwidth);
        AddRow(layout, 5, "SQUELCH", _squelch);
        AddRow(layout, 6, "SQL LEVEL (dB)", _squelchLevel);
        AddRow(layout, 7, "AUDIO OUTPUT", _output);
        layout.Controls.Add(new Label { Text = "AUTO TUNE", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(145, 181, 198) }, 0, 8);
        layout.Controls.Add(autoRow, 1, 8);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0) };
        var ok = MakeButton("OK", DialogResult.OK, Color.FromArgb(196, 112, 39));
        var cancel = MakeButton("CANCEL", DialogResult.Cancel, Color.FromArgb(47, 66, 78));
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 9);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (!SaveResult())
            {
                DialogResult = DialogResult.None;
                System.Media.SystemSounds.Beep.Play();
            }
        };
    }

    private void CopyFromMain()
    {
        if (_main is not { } main) return;
        _frequency.Text = FrequencyEntry.FormatMhz(main.FrequencyHz);
        _mode.SelectedItem = main.Mode;
        _bandwidth.Value = Math.Clamp((decimal)main.BandwidthHz, _bandwidth.Minimum, _bandwidth.Maximum);
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

    private bool SaveResult()
    {
        if (!FrequencyEntry.TryParseHz(_frequency.Text, out var hz)) return false;
        Result.Frequency = hz;
        Result.Mode = _mode.SelectedItem is RadioMode mode ? mode : RadioMode.USB;
        Result.Bandwidth = decimal.ToInt32(_bandwidth.Value);
        Result.SquelchEnabled = _squelch.Checked;
        Result.SquelchLevel = decimal.ToInt32(_squelchLevel.Value);
        Result.OutputChannel = _output.SelectedIndex;
        _autoTuneSettings.Enabled = _autoTune.Checked;
        Result.AutoTune = _autoTuneSettings;
        return true;
    }
}

internal readonly record struct MainVfoCopy(long FrequencyHz, RadioMode Mode, int BandwidthHz);
