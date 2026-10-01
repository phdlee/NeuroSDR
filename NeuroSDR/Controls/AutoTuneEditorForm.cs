using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class AutoTuneEditorForm : Form
{
    private readonly CheckBox _enabled = new();
    private readonly CheckBox _windowFollows = new();
    private readonly TextBox _standby = FrequencyEntry.CreateMhzBox(0);
    private readonly TextBox _min = FrequencyEntry.CreateMhzBox(0);
    private readonly TextBox _max = FrequencyEntry.CreateMhzBox(0);
    private readonly NumericUpDown _trigger = new();
    private readonly NumericUpDown _release = new();
    private readonly NumericUpDown _hold = new();
    private readonly Label _triggerHint = new();

    public AutoTuneSettings Result { get; }

    public AutoTuneEditorForm(string title, AutoTuneSettings settings, float? suggestedTriggerDb = null)
    {
        Result = settings.Clone();
        Text = title;
        ClientSize = new Size(460, 400);
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
            RowCount = 10,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _enabled.Text = "Enable signal follow";
        _enabled.Checked = settings.Enabled;
        _enabled.ForeColor = ForeColor;
        _enabled.AutoSize = true;

        _windowFollows.Text = "Window follows VFO";
        _windowFollows.Checked = settings.WindowFollowsVfo;
        _windowFollows.ForeColor = ForeColor;
        _windowFollows.AutoSize = true;
        _windowFollows.Font = new Font("Segoe UI", 8.5f);

        _standby.Text = FrequencyEntry.FormatMhz(settings.StandbyFrequency);
        _min.Text = FrequencyEntry.FormatMhz(settings.MinFrequency);
        _max.Text = FrequencyEntry.FormatMhz(settings.MaxFrequency);

        var triggerDb = suggestedTriggerDb ?? settings.TriggerLevelDb;
        var releaseDb = suggestedTriggerDb is { } t
            ? t - 15f
            : settings.ReleaseLevelDb;
        ConfigureDb(_trigger, triggerDb);
        ConfigureDb(_release, releaseDb);
        ConfigureNumeric(_hold, 200, 30_000, 100, settings.HoldMilliseconds);

        _triggerHint.Text = suggestedTriggerDb is { } s
            ? $"Trigger auto-set from nearby RF ≈ {s:0.#} dB (noise/adjacent + 5)"
            : "When VFO moves, Min / Max / Standby shift by the same amount.";
        _triggerHint.ForeColor = Color.FromArgb(120, 150, 165);
        _triggerHint.Font = new Font("Segoe UI", 7.5f);
        _triggerHint.Dock = DockStyle.Fill;
        _triggerHint.TextAlign = ContentAlignment.MiddleLeft;

        layout.Controls.Add(new Label { Text = "ACTIVE", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(145, 181, 198) }, 0, 0);
        layout.Controls.Add(_enabled, 1, 0);
        layout.Controls.Add(new Label { Text = "WINDOW", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(145, 181, 198) }, 0, 1);
        layout.Controls.Add(_windowFollows, 1, 1);
        AddRow(layout, 2, "STANDBY (MHz)", _standby);
        AddRow(layout, 3, "MIN (MHz)", _min);
        AddRow(layout, 4, "MAX (MHz)", _max);
        AddRow(layout, 5, "TRIGGER (dB)", _trigger);
        AddRow(layout, 6, "RELEASE (dB)", _release);
        AddRow(layout, 7, "HOLD (ms)", _hold);
        layout.Controls.Add(_triggerHint, 0, 8);
        layout.SetColumnSpan(_triggerHint, 2);

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

    private bool SaveResult()
    {
        if (!FrequencyEntry.TryParseHz(_standby.Text, out var standby) ||
            !FrequencyEntry.TryParseHz(_min.Text, out var minHz) ||
            !FrequencyEntry.TryParseHz(_max.Text, out var maxHz))
            return false;

        Result.Enabled = _enabled.Checked;
        Result.WindowFollowsVfo = _windowFollows.Checked;
        Result.StandbyFrequency = standby;
        Result.MinFrequency = minHz;
        Result.MaxFrequency = maxHz;
        if (Result.MaxFrequency < Result.MinFrequency)
            (Result.MinFrequency, Result.MaxFrequency) = (Result.MaxFrequency, Result.MinFrequency);
        Result.TriggerLevelDb = (float)_trigger.Value;
        Result.ReleaseLevelDb = (float)_release.Value;
        if (Result.ReleaseLevelDb > Result.TriggerLevelDb)
            Result.ReleaseLevelDb = Result.TriggerLevelDb - 5;
        Result.HoldMilliseconds = decimal.ToInt32(_hold.Value);
        return true;
    }

    private static void ConfigureDb(NumericUpDown box, float db)
    {
        ConfigureNumeric(box, -140, 0, 1, (decimal)db);
        box.DecimalPlaces = 0;
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
