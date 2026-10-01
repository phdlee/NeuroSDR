namespace NeuroSDR.Controls;

/// <summary>EXTENDS CW AF-filter presets (width buttons + optional peak center).</summary>
internal sealed class CwModeOptionsPanel : ExtendsSectionPanel
{
    private readonly Button[] _widthButtons;
    private readonly CheckBox _autoPeak = new();
    private readonly Label _hint = new();
    private int _selectedWidthHz = 200;
    private bool _suppress;

    public event Action<int>? FilterSelected;
    public event Action? OptionsChanged;

    public CwModeOptionsPanel() : base("CW · AF FILTER")
    {
        var widths = new[] { 100, 200, 300, 400 };
        _widthButtons = new Button[widths.Length];
        for (var i = 0; i < widths.Length; i++)
        {
            var width = widths[i];
            var button = new Button
            {
                Text = $"{width} Hz",
                Location = new Point((i % 2) * 106, (i / 2) * 28),
                Size = new Size(100, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 66, 83),
                ForeColor = Color.FromArgb(220, 232, 240),
                Font = new Font("Segoe UI Semibold", 8f),
                Tag = width
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(65, 104, 121);
            button.Click += (_, _) => OnWidthClick(width);
            _widthButtons[i] = button;
            Place(button);
        }

        _autoPeak.Text = "Auto center on CW peak";
        _autoPeak.AutoSize = false;
        _autoPeak.Location = new Point(0, 60);
        _autoPeak.Size = new Size(214, 20);
        _autoPeak.ForeColor = Color.FromArgb(216, 225, 235);
        _autoPeak.CheckedChanged += (_, _) =>
        {
            if (_suppress) return;
            OptionsChanged?.Invoke();
        };
        Place(_autoPeak);

        _hint.Location = new Point(0, 82);
        _hint.Size = new Size(214, 32);
        _hint.ForeColor = Color.FromArgb(120, 150, 165);
        _hint.Font = new Font("Segoe UI", 7f);
        _hint.Text = "Select width → enable AF FILTER · center ~500 Hz (or last / peak)";
        Place(_hint);

        SetBodyHeight(114);
        HighlightSelected();
    }

    public int SelectedWidthHz => _selectedWidthHz;
    public bool AutoPeakEnabled => _autoPeak.Checked;

    public void ShowForMode(bool visible) => Visible = visible;

    public void LoadOptions(int widthHz, bool autoPeak)
    {
        _suppress = true;
        _selectedWidthHz = widthHz is 100 or 200 or 300 or 400 ? widthHz : 200;
        _autoPeak.Checked = autoPeak;
        _suppress = false;
        HighlightSelected();
    }

    private void OnWidthClick(int widthHz)
    {
        _selectedWidthHz = widthHz;
        HighlightSelected();
        FilterSelected?.Invoke(widthHz);
    }

    private void HighlightSelected()
    {
        foreach (var button in _widthButtons)
        {
            var width = (int)button.Tag!;
            var selected = width == _selectedWidthHz;
            button.BackColor = selected ? Color.FromArgb(184, 118, 40) : Color.FromArgb(35, 66, 83);
        }
    }
}
