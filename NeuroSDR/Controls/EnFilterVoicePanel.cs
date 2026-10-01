using NeuroSDR.Core;
using NeuroSDR.Dsp;

namespace NeuroSDR.Controls;

internal sealed class EnFilterVoicePanel : ExtendsSectionPanel
{
    private readonly CheckBox _enable = new();
    private readonly CheckBox _blanker = new();
    private readonly CheckBox _notch = new();
    private readonly CheckBox _agc = new();
    private readonly CheckBox _eq = new();
    private readonly Label _blendLabel = new();
    private readonly TrackBar _blend = new();
    private readonly Label _hint = new();
    private bool _suppress;

    public event Action? OptionsChanged;

    public EnFilterVoicePanel() : base("VOICE · RNNoise")
    {
        _enable.Text = "Apply RNNoise";
        StyleCheck(_enable, 0);
        _blanker.Text = "Blanker";
        StyleCheck(_blanker, 22);
        _notch.Text = "Auto notch";
        StyleCheck(_notch, 44);
        _agc.Text = "AGC";
        StyleCheck(_agc, 66);
        _eq.Text = "Voice EQ";
        StyleCheck(_eq, 88);

        _blendLabel.Location = new Point(0, 110);
        _blendLabel.Size = new Size(214, 16);
        _blendLabel.ForeColor = Color.FromArgb(145, 181, 198);
        _blendLabel.Font = new Font("Segoe UI Semibold", 7.5f);
        _blendLabel.Text = "BLEND  DRY ← → RNNoise  100%";
        Place(_blendLabel);

        _blend.Location = new Point(0, 126);
        _blend.Size = new Size(214, 28);
        _blend.Minimum = 0;
        _blend.Maximum = 100;
        _blend.TickFrequency = 10;
        _blend.Value = 100;
        _blend.ValueChanged += (_, _) =>
        {
            _blendLabel.Text = $"BLEND  DRY ← → RNNoise  {_blend.Value}%";
            if (!_suppress) OptionsChanged?.Invoke();
        };
        Place(_blend);

        _hint.Location = new Point(0, 154);
        _hint.Size = new Size(214, 36);
        _hint.ForeColor = Color.FromArgb(120, 150, 165);
        _hint.Font = new Font("Segoe UI", 7f);
        _hint.Text = "Left mixes original when VAD is low. Right = RNNoise only.";
        Place(_hint);
        SetBodyHeight(190);
    }

    public bool FilterEnabled => _enable.Checked;
    public bool Blanker => _blanker.Checked;
    public bool Notch => _notch.Checked;
    public bool Agc => _agc.Checked;
    public bool Eq => _eq.Checked;
    public int WetPercent => _blend.Value;

    public void ShowForMode(RadioMode mode) =>
        Visible = EnFilterProcessor.IsVoiceMode(mode);

    public void LoadOptions(bool enabled, bool blanker, bool notch, bool agc, bool eq, int wetPercent, bool dllOk)
    {
        _suppress = true;
        _enable.Checked = enabled;
        _blanker.Checked = blanker;
        _notch.Checked = notch;
        _agc.Checked = agc;
        _eq.Checked = eq;
        _blend.Value = Math.Clamp(wetPercent, 0, 100);
        _blendLabel.Text = $"BLEND  DRY ← → RNNoise  {_blend.Value}%";
        _hint.Text = dllOk
            ? "Left mixes original when VAD is low. Right = RNNoise only."
            : "ENFilter.dll missing — native build required";
        _suppress = false;
    }

    private void StyleCheck(CheckBox box, int y)
    {
        box.AutoSize = false;
        box.Location = new Point(0, y);
        box.Size = new Size(214, 20);
        box.ForeColor = Color.FromArgb(216, 225, 235);
        box.CheckedChanged += (_, _) =>
        {
            if (!_suppress) OptionsChanged?.Invoke();
        };
        Place(box);
    }
}
