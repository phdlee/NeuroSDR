namespace NeuroSDR.Controls;

internal sealed class EnFilterCwPanel : ExtendsSectionPanel
{
    private static readonly int[] BandwidthChoices = [30, 50, 70, 100, 200];

    private readonly CheckBox _enable = new();
    private readonly CheckBox _bpf = new();
    private readonly ComboBox _bandwidth = new();
    private readonly CheckBox _ale = new();
    private readonly CheckBox _apf = new();
    private readonly CheckBox _gate = new();
    private readonly TrackBar _center = new();
    private readonly Label _centerLabel = new();
    private readonly Button _auto = new();
    private readonly Label _hint = new();
    private bool _suppress;

    public event Action? OptionsChanged;
    public event Action? AutoRequested;

    public EnFilterCwPanel() : base("CW · ENFilter")
    {
        _enable.Text = "Apply CW filter";
        StyleCheck(_enable, 0, 214);

        _bpf.Text = "Narrow BPF";
        StyleCheck(_bpf, 22, 108);
        _bandwidth.DropDownStyle = ComboBoxStyle.DropDownList;
        _bandwidth.Location = new Point(112, 20);
        _bandwidth.Size = new Size(102, 22);
        _bandwidth.BackColor = Color.FromArgb(5, 17, 24);
        _bandwidth.ForeColor = Color.FromArgb(207, 224, 235);
        _bandwidth.FlatStyle = FlatStyle.Flat;
        _bandwidth.Font = new Font("Segoe UI Semibold", 8f);
        foreach (var hz in BandwidthChoices)
            _bandwidth.Items.Add($"{hz} Hz");
        _bandwidth.SelectedIndex = 2;
        _bandwidth.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppress) OptionsChanged?.Invoke();
        };
        Place(_bandwidth);

        _ale.Text = "ALE tone enhance";
        StyleCheck(_ale, 46, 214);
        _apf.Text = "APF peak";
        StyleCheck(_apf, 68, 214);
        _gate.Text = "Noise gate";
        StyleCheck(_gate, 90, 214);

        _centerLabel.Location = new Point(0, 112);
        _centerLabel.Size = new Size(214, 16);
        _centerLabel.ForeColor = Color.FromArgb(145, 181, 198);
        _centerLabel.Font = new Font("Segoe UI Semibold", 7.5f);
        _centerLabel.Text = "CENTER  700 Hz";
        Place(_centerLabel);

        _center.Location = new Point(0, 128);
        _center.Size = new Size(214, 28);
        _center.Minimum = 200;
        _center.Maximum = 1500;
        _center.TickFrequency = 100;
        _center.Value = 700;
        _center.ValueChanged += (_, _) =>
        {
            _centerLabel.Text = $"CENTER  {_center.Value} Hz";
            if (!_suppress) OptionsChanged?.Invoke();
        };
        Place(_center);

        _auto.Text = "AUTO peak";
        _auto.Location = new Point(0, 158);
        _auto.Size = new Size(214, 24);
        _auto.FlatStyle = FlatStyle.Flat;
        _auto.BackColor = Color.FromArgb(35, 66, 83);
        _auto.ForeColor = Color.FromArgb(220, 232, 240);
        _auto.FlatAppearance.BorderColor = Color.FromArgb(65, 104, 121);
        _auto.Click += (_, _) => AutoRequested?.Invoke();
        Place(_auto);

        _hint.Location = new Point(0, 186);
        _hint.Size = new Size(214, 28);
        _hint.ForeColor = Color.FromArgb(120, 150, 165);
        _hint.Font = new Font("Segoe UI", 7f);
        _hint.Text = "Click AF spectrum to move center";
        Place(_hint);
        SetBodyHeight(214);
    }

    public bool FilterEnabled => _enable.Checked;
    public bool Bpf => _bpf.Checked;
    public bool Ale => _ale.Checked;
    public bool Apf => _apf.Checked;
    public bool Gate => _gate.Checked;
    public float CenterHz => _center.Value;
    public int BandwidthHz => BandwidthChoices[Math.Clamp(_bandwidth.SelectedIndex, 0, BandwidthChoices.Length - 1)];

    public void ShowForMode(bool visible) => Visible = visible;

    public void SetCenterHz(float hz)
    {
        var value = (int)Math.Clamp(hz, _center.Minimum, _center.Maximum);
        if (_center.Value == value) return;
        _suppress = true;
        _center.Value = value;
        _centerLabel.Text = $"CENTER  {_center.Value} Hz";
        _suppress = false;
        OptionsChanged?.Invoke();
    }

    public void LoadOptions(bool enabled, bool bpf, bool ale, bool apf, bool gate, float centerHz, int bandwidthHz, bool dllOk)
    {
        _suppress = true;
        _enable.Checked = enabled;
        _bpf.Checked = bpf;
        _ale.Checked = ale;
        _apf.Checked = apf;
        _gate.Checked = gate;
        _center.Value = (int)Math.Clamp(centerHz, 200, 1500);
        _centerLabel.Text = $"CENTER  {_center.Value} Hz";
        var bwIndex = Array.IndexOf(BandwidthChoices, bandwidthHz);
        _bandwidth.SelectedIndex = bwIndex >= 0 ? bwIndex : 2;
        _hint.Text = dllOk ? "Click AF spectrum to move center" : "ENFilter.dll missing — native build required";
        _suppress = false;
    }

    private void StyleCheck(CheckBox box, int y, int width)
    {
        box.AutoSize = false;
        box.Location = new Point(0, y);
        box.Size = new Size(width, 20);
        box.ForeColor = Color.FromArgb(216, 225, 235);
        box.CheckedChanged += (_, _) =>
        {
            if (!_suppress) OptionsChanged?.Invoke();
        };
        Place(box);
    }
}
