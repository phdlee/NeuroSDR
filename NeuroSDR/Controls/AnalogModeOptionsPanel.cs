using NeuroSDR.Core;

namespace NeuroSDR.Controls;

/// <summary>EXTENDS options for AM / SAM / NFM (AFC gated by SQL + RF peak).</summary>
internal sealed class AnalogModeOptionsPanel : ExtendsSectionPanel
{
    private static readonly int[] RangeChoicesHz = [300, 500, 1_000, 2_000, 3_000, 5_000];

    private readonly CheckBox _afc = new();
    private readonly ComboBox _afcSpeed = new();
    private readonly ComboBox _afcRange = new();
    private readonly Label _actualFreq = new();
    private readonly Label _status = new();
    private readonly Label _ctcss = new();
    private readonly Label _hint = new();
    private bool _suppress;

    public event Action? OptionsChanged;

    public AnalogModeOptionsPanel() : base("ANALOG · AFC")
    {
        _afc.Text = "AFC";
        _afc.AutoSize = false;
        _afc.Location = new Point(0, 0);
        _afc.Size = new Size(70, 20);
        _afc.ForeColor = Color.FromArgb(216, 225, 235);
        _afc.CheckedChanged += (_, _) => Raise();

        var speedLabel = MakeLabel("SPEED", 74, 0);
        _afcSpeed.DropDownStyle = ComboBoxStyle.DropDownList;
        _afcSpeed.Location = new Point(74, 14);
        _afcSpeed.Size = new Size(68, 22);
        _afcSpeed.BackColor = Color.FromArgb(5, 17, 24);
        _afcSpeed.ForeColor = Color.FromArgb(207, 224, 235);
        _afcSpeed.FlatStyle = FlatStyle.Flat;
        _afcSpeed.Items.AddRange(["Slow", "Med", "Fast"]);
        _afcSpeed.SelectedIndex = 1;
        _afcSpeed.SelectedIndexChanged += (_, _) => Raise();

        var rangeLabel = MakeLabel("RANGE", 148, 0);
        _afcRange.DropDownStyle = ComboBoxStyle.DropDownList;
        _afcRange.Location = new Point(148, 14);
        _afcRange.Size = new Size(66, 22);
        _afcRange.BackColor = Color.FromArgb(5, 17, 24);
        _afcRange.ForeColor = Color.FromArgb(207, 224, 235);
        _afcRange.FlatStyle = FlatStyle.Flat;
        _afcRange.Items.AddRange(["300", "500", "1k", "2k", "3k", "5k"]);
        _afcRange.SelectedIndex = 2; // 1 kHz default
        _afcRange.SelectedIndexChanged += (_, _) => Raise();

        _actualFreq.Location = new Point(0, 40);
        _actualFreq.Size = new Size(214, 16);
        _actualFreq.ForeColor = Color.FromArgb(255, 193, 69);
        _actualFreq.Font = new Font("Consolas", 7.5f);
        _actualFreq.Text = "AFC  —.— MHz";

        _status.Location = new Point(0, 58);
        _status.Size = new Size(214, 16);
        _status.ForeColor = Color.FromArgb(180, 198, 210);
        _status.Font = new Font("Consolas", 7.2f);
        _status.Text = "AFC idle";

        _ctcss.Location = new Point(0, 76);
        _ctcss.Size = new Size(214, 16);
        _ctcss.ForeColor = Color.FromArgb(255, 193, 69);
        _ctcss.Font = new Font("Consolas", 7.5f);
        _ctcss.Text = "CTCSS —";

        _hint.Location = new Point(0, 94);
        _hint.Size = new Size(214, 28);
        _hint.ForeColor = Color.FromArgb(120, 150, 165);
        _hint.Font = new Font("Segoe UI", 7f);
        _hint.Text = "Main VFO fixed · SQL open + RF peak within RANGE";

        Place(_afc);
        Place(speedLabel);
        Place(_afcSpeed);
        Place(rangeLabel);
        Place(_afcRange);
        Place(_actualFreq);
        Place(_status);
        Place(_ctcss);
        Place(_hint);
        SetBodyHeight(122);
    }

    public bool AfcEnabled => _afc.Checked;
    public int AfcSpeedIndex => Math.Clamp(_afcSpeed.SelectedIndex, 0, 2);
    public int AfcRangeHz => RangeChoicesHz[Math.Clamp(_afcRange.SelectedIndex, 0, RangeChoicesHz.Length - 1)];

    public void ShowForMode(RadioMode mode)
    {
        Visible = mode is RadioMode.AM or RadioMode.SAM or RadioMode.NFM;
        if (!Visible) return;
        SetCaption(mode switch
        {
            RadioMode.AM => "AM · AFC",
            RadioMode.SAM => "SAM · AFC",
            RadioMode.NFM => "NFM · AFC",
            _ => "ANALOG · AFC"
        });
    }

    public void LoadOptions(bool afcEnabled, int speedIndex, int rangeHz)
    {
        _suppress = true;
        _afc.Checked = afcEnabled;
        _afcSpeed.SelectedIndex = Math.Clamp(speedIndex, 0, 2);
        var rangeIndex = Array.IndexOf(RangeChoicesHz, rangeHz);
        _afcRange.SelectedIndex = rangeIndex >= 0 ? rangeIndex : 2;
        _suppress = false;
    }

    public void SetStatus(string text) => _status.Text = text;
    public string StatusText => _status.Text;
    public string ToneText => _ctcss.Text;

    public void SetCtcss(float toneHz, string? dcsCode = null, string? searching = null, string? ani = null)
    {
        if (toneHz > 0)
        {
            _ctcss.Text = $"CTCSS {toneHz:0.0} Hz";
            _ctcss.ForeColor = Color.FromArgb(255, 193, 69);
        }
        else if (!string.IsNullOrWhiteSpace(dcsCode))
        {
            _ctcss.Text = $"DCS {dcsCode}";
            _ctcss.ForeColor = Color.FromArgb(120, 220, 180);
        }
        else if (!string.IsNullOrWhiteSpace(ani))
        {
            _ctcss.Text = ani;
            _ctcss.ForeColor = Color.FromArgb(130, 200, 255);
        }
        else if (!string.IsNullOrWhiteSpace(searching))
        {
            _ctcss.Text = searching;
            _ctcss.ForeColor = Color.FromArgb(120, 150, 165);
        }
        else
        {
            _ctcss.Text = "CTCSS —";
            _ctcss.ForeColor = Color.FromArgb(120, 150, 165);
        }
    }

    public void SetActualFrequency(long frequencyHz, long offsetHz)
    {
        var sign = offsetHz >= 0 ? "+" : "-";
        _actualFreq.Text = $"AFC  {frequencyHz / 1_000_000d:0.000000}  ({sign}{Math.Abs(offsetHz)} Hz)";
    }

    private void Raise()
    {
        if (_suppress) return;
        OptionsChanged?.Invoke();
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = false,
        Location = new Point(x, y),
        Size = new Size(66, 12),
        ForeColor = Color.FromArgb(145, 181, 198),
        Font = new Font("Segoe UI Semibold", 6.5f)
    };
}
