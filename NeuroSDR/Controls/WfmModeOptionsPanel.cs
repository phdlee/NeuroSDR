using NeuroSDR.Dsp;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

/// <summary>WFM EXTENDS: stereo, EQ with named presets, L/R meters.</summary>
internal sealed class WfmModeOptionsPanel : ExtendsSectionPanel
{
    private readonly CheckBox _stereo = new();
    private readonly CheckBox _hfSoft = new();
    private readonly CheckBox _hideAfPlugins = new();
    private readonly StatusLedControl _stereoLed = new();
    private readonly Label _status = new();
    private readonly ComboBox _presetBox = new();
    private readonly Button _savePreset = new();
    private readonly Button _resetPreset = new();
    private readonly Button _addPreset = new();
    private readonly Button _delPreset = new();
    private readonly WfmAudioMonitorControl _monitor = new();
    private bool _suppress;
    private bool _ledLatched;
    private int _ledHoldTicks;
    private List<WfmEqPreset> _presets = [];
    private string _selectedPreset = "Normal";

    public event Action? OptionsChanged;
    public event Action? EqChanged;
    public event Action? PresetsChanged;

    public WfmModeOptionsPanel() : base("WFM · AUDIO")
    {
        _stereoLed.Caption = "STEREO";
        _stereoLed.Location = new Point(Width - 68, 2);
        _stereoLed.Size = new Size(62, 18);
        _stereoLed.BackColor = Color.FromArgb(10, 20, 28);
        _stereoLed.IsOn = false;
        Controls.Add(_stereoLed);
        _stereoLed.BringToFront();

        _stereo.Text = "STEREO decode";
        _stereo.AutoSize = false;
        _stereo.Location = new Point(0, 0);
        _stereo.Size = new Size(140, 18);
        _stereo.ForeColor = Color.FromArgb(216, 225, 235);
        _stereo.CheckedChanged += (_, _) => Raise();

        _hfSoft.Text = "HF soft (8 kHz −6 dB)";
        _hfSoft.AutoSize = false;
        _hfSoft.Location = new Point(0, 18);
        _hfSoft.Size = new Size(214, 18);
        _hfSoft.ForeColor = Color.FromArgb(216, 225, 235);
        _hfSoft.Checked = true;
        _hfSoft.CheckedChanged += (_, _) => Raise();

        _hideAfPlugins.Text = "Hide AF plugin panel";
        _hideAfPlugins.AutoSize = false;
        _hideAfPlugins.Location = new Point(0, 36);
        _hideAfPlugins.Size = new Size(214, 18);
        _hideAfPlugins.ForeColor = Color.FromArgb(216, 225, 235);
        _hideAfPlugins.Checked = true;
        _hideAfPlugins.CheckedChanged += (_, _) => Raise();

        _status.Location = new Point(0, 56);
        _status.Size = new Size(214, 14);
        _status.ForeColor = Color.FromArgb(180, 198, 210);
        _status.Font = new Font("Consolas", 7f);
        _status.Text = "Mono";

        _presetBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _presetBox.Location = new Point(0, 72);
        _presetBox.Size = new Size(120, 22);
        _presetBox.FlatStyle = FlatStyle.Flat;
        _presetBox.BackColor = Color.FromArgb(20, 40, 52);
        _presetBox.ForeColor = Color.FromArgb(220, 232, 240);
        _presetBox.SelectedIndexChanged += (_, _) => OnPresetSelected();

        StyleBtn(_savePreset, "SAVE", 122, 72, () => SaveSelectedPreset());
        StyleBtn(_resetPreset, "RST", 168, 72, () => ResetSelectedPreset());
        StyleBtn(_addPreset, "+", 0, 96, () => AddUserPreset());
        StyleBtn(_delPreset, "−", 28, 96, () => DeleteSelectedPreset());
        _addPreset.Size = new Size(24, 22);
        _delPreset.Size = new Size(24, 22);
        _savePreset.Size = new Size(42, 22);
        _resetPreset.Size = new Size(42, 22);

        var presetHint = new Label
        {
            Text = "EQ preset",
            Location = new Point(58, 98),
            Size = new Size(150, 16),
            ForeColor = Color.FromArgb(150, 175, 190),
            Font = new Font("Segoe UI", 7f)
        };

        _monitor.Location = new Point(0, 120);
        _monitor.Size = new Size(214, 130);
        _monitor.EqChanged += () => EqChanged?.Invoke();

        Place(_stereo);
        Place(_hfSoft);
        Place(_hideAfPlugins);
        Place(_status);
        Place(_presetBox);
        Place(_savePreset);
        Place(_resetPreset);
        Place(_addPreset);
        Place(_delPreset);
        Place(presetHint);
        Place(_monitor);
        SetBodyHeight(250);
    }

    public bool StereoEnabled => _stereo.Checked;
    public bool HfSoftEnabled => _hfSoft.Checked;
    public bool HideAfPlugins => _hideAfPlugins.Checked;
    public float[] EqGainsDb => _monitor.GetGainsDb();
    public bool StereoLampOn => _ledLatched;
    public string SelectedEqPreset => _selectedPreset;
    public IReadOnlyList<WfmEqPreset> EqPresets => _presets;

    public void ShowForMode(bool visible) => Visible = visible;

    public void LoadOptions(bool stereo, bool hfSoft, bool hideAfPlugins, float[]? eqGains,
        string? selectedPreset, List<WfmEqPreset>? presets)
    {
        _suppress = true;
        _stereo.Checked = stereo;
        _hfSoft.Checked = hfSoft;
        _hideAfPlugins.Checked = hideAfPlugins;
        _presets = presets is { Count: > 0 }
            ? presets.Select(ClonePreset).ToList()
            : WfmEqFactoryPresets.CreateFactoryList();
        WfmEqFactoryPresets.EnsureFactory(_presets);
        _selectedPreset = string.IsNullOrWhiteSpace(selectedPreset) ? "Normal" : selectedPreset.Trim();
        RefreshPresetBox();
        var match = FindPreset(_selectedPreset);
        if (match is not null)
            _monitor.SetGainsDb(match.GainsDb);
        else
            _monitor.SetGainsDb(eqGains);
        _suppress = false;
    }

    public List<WfmEqPreset> ExportPresets() => _presets.Select(ClonePreset).ToList();

    public void SetStatus(string text) => _status.Text = text;

    public void UpdateMonitor(bool stereoLocked, float leftDb, float rightDb)
    {
        if (stereoLocked)
        {
            _ledLatched = true;
            _ledHoldTicks = 2;
        }
        else if (_ledHoldTicks > 0)
            _ledHoldTicks--;
        else
            _ledLatched = false;
        _stereoLed.IsOn = _ledLatched;
        _monitor.UpdateLevels(leftDb, rightDb);
    }

    public void ApplyEqPresetByName(string name)
    {
        var p = FindPreset(name);
        if (p is null) return;
        _suppress = true;
        _selectedPreset = p.Name;
        SelectBoxName(p.Name);
        _monitor.SetGainsDb(p.GainsDb);
        _suppress = false;
        EqChanged?.Invoke();
    }

    private void OnPresetSelected()
    {
        if (_suppress || _presetBox.SelectedItem is not string name) return;
        var p = FindPreset(name);
        if (p is null) return;
        _selectedPreset = p.Name;
        _monitor.SetGainsDb(p.GainsDb);
        EqChanged?.Invoke();
        PresetsChanged?.Invoke();
    }

    private void SaveSelectedPreset()
    {
        var p = FindPreset(_selectedPreset);
        if (p is null) return;
        p.GainsDb = _monitor.GetGainsDb();
        PresetsChanged?.Invoke();
        EqChanged?.Invoke();
        _status.Text = $"Saved EQ · {p.Name}";
    }

    private void ResetSelectedPreset()
    {
        var p = FindPreset(_selectedPreset);
        if (p is null) return;
        if (p.IsFactory || WfmEqFactoryPresets.Names.Any(n => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
        {
            WfmEqFactoryPresets.ResetFactory(p);
            p.IsFactory = true;
        }
        else
            Array.Clear(p.GainsDb);
        _monitor.SetGainsDb(p.GainsDb);
        PresetsChanged?.Invoke();
        EqChanged?.Invoke();
        _status.Text = $"Reset EQ · {p.Name}";
    }

    private void AddUserPreset()
    {
        using var dlg = new Form
        {
            Text = "New EQ preset",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(280, 90),
            MaximizeBox = false,
            MinimizeBox = false
        };
        var box = new TextBox { Location = new Point(12, 14), Size = new Size(256, 24) };
        var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, Location = new Point(112, 50), Size = new Size(75, 26) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(193, 50), Size = new Size(75, 26) };
        dlg.Controls.AddRange([box, ok, cancel]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        var name = box.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (FindPreset(name) is not null)
        {
            MessageBox.Show(FindForm(), "A preset with that name already exists.", "EQ preset",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var p = new WfmEqPreset
        {
            Name = name,
            GainsDb = _monitor.GetGainsDb(),
            IsFactory = false
        };
        _presets.Add(p);
        _selectedPreset = name;
        RefreshPresetBox();
        PresetsChanged?.Invoke();
        _status.Text = $"Added EQ · {name}";
    }

    private void DeleteSelectedPreset()
    {
        var p = FindPreset(_selectedPreset);
        if (p is null) return;
        if (p.IsFactory || WfmEqFactoryPresets.Names.Any(n => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(FindForm(), "Built-in presets cannot be deleted. Use RST to restore defaults.",
                "EQ preset", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _presets.Remove(p);
        _selectedPreset = "Normal";
        RefreshPresetBox();
        var n = FindPreset("Normal");
        _monitor.SetGainsDb(n?.GainsDb);
        PresetsChanged?.Invoke();
        EqChanged?.Invoke();
        _status.Text = "Deleted user EQ preset";
    }

    private void RefreshPresetBox()
    {
        _suppress = true;
        _presetBox.Items.Clear();
        foreach (var p in _presets.OrderBy(p => p.IsFactory ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            _presetBox.Items.Add(p.Name);
        SelectBoxName(_selectedPreset);
        _suppress = false;
    }

    private void SelectBoxName(string name)
    {
        for (var i = 0; i < _presetBox.Items.Count; i++)
        {
            if (_presetBox.Items[i] is string s && s.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _presetBox.SelectedIndex = i;
                return;
            }
        }
        if (_presetBox.Items.Count > 0) _presetBox.SelectedIndex = 0;
    }

    private WfmEqPreset? FindPreset(string name) =>
        _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static WfmEqPreset ClonePreset(WfmEqPreset p) => new()
    {
        Name = p.Name,
        GainsDb = (float[])p.GainsDb.Clone(),
        IsFactory = p.IsFactory
    };

    private static void StyleBtn(Button b, string text, int x, int y, Action click)
    {
        b.Text = text;
        b.Location = new Point(x, y);
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Color.FromArgb(35, 66, 83);
        b.ForeColor = Color.FromArgb(220, 232, 240);
        b.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        b.Font = new Font("Segoe UI Semibold", 7f);
        b.Click += (_, _) => click();
    }

    private void Raise()
    {
        if (_suppress) return;
        OptionsChanged?.Invoke();
    }
}

/// <summary>L/R AF meters + draggable 10-band graphic EQ (−12…+12 dB).</summary>
internal sealed class WfmAudioMonitorControl : Control
{
    private float _leftDb = -140, _rightDb = -140;
    private float _leftShown = -140, _rightShown = -140;
    private readonly float[] _gainsDb = new float[GraphicEqualizer.BandHz.Length];
    private int _dragBand = -1;

    public event Action? EqChanged;

    public WfmAudioMonitorControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(6, 14, 20);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.UserMouse, true);
    }

    public float[] GetGainsDb() => (float[])_gainsDb.Clone();

    public void SetGainsDb(float[]? gains)
    {
        for (var i = 0; i < _gainsDb.Length; i++)
            _gainsDb[i] = gains is not null && i < gains.Length ? Math.Clamp(gains[i], -12f, 12f) : 0f;
        Invalidate();
    }

    public void UpdateLevels(float leftDb, float rightDb)
    {
        _leftDb = Math.Clamp(leftDb, -140, 0);
        _rightDb = Math.Clamp(rightDb, -140, 0);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        Smooth(ref _leftShown, _leftDb);
        Smooth(ref _rightShown, _rightDb);

        using var font = new Font("Segoe UI Semibold", 6.5f);
        using var tiny = new Font("Segoe UI", 5.5f);
        using var text = new SolidBrush(Color.FromArgb(160, 185, 200));
        using var empty = new SolidBrush(Color.FromArgb(28, 42, 52));
        using var fillL = new SolidBrush(Color.FromArgb(56, 168, 210));
        using var fillR = new SolidBrush(Color.FromArgb(210, 140, 56));
        using var fillBoost = new SolidBrush(Color.FromArgb(90, 190, 120));
        using var fillCut = new SolidBrush(Color.FromArgb(190, 110, 90));
        using var zeroPen = new Pen(Color.FromArgb(90, 120, 140));

        DrawMeter(g, "L", _leftShown, 0, empty, fillL, font, text);
        DrawMeter(g, "R", _rightShown, 16, empty, fillR, font, text);

        g.DrawString("EQ  drag ±12 dB", font, text, 0, 34);
        var eqTop = 48;
        var eqHeight = Height - eqTop - 12;
        var midY = eqTop + eqHeight / 2;
        var gap = 2;
        var barWidth = Math.Max(8, (Width - gap * (_gainsDb.Length - 1)) / _gainsDb.Length);
        g.DrawLine(zeroPen, 0, midY, Width, midY);

        for (var i = 0; i < _gainsDb.Length; i++)
        {
            var x = i * (barWidth + gap);
            g.FillRectangle(empty, x, eqTop, barWidth, eqHeight);
            var gain = _gainsDb[i];
            var half = eqHeight / 2f;
            var h = (int)Math.Round(Math.Abs(gain) / 12f * half);
            if (gain >= 0)
                g.FillRectangle(fillBoost, x, midY - h, barWidth, Math.Max(1, h));
            else
                g.FillRectangle(fillCut, x, midY, barWidth, Math.Max(1, h));
            var label = GraphicEqualizer.BandHz[i] >= 1000
                ? $"{GraphicEqualizer.BandHz[i] / 1000}k"
                : GraphicEqualizer.BandHz[i].ToString();
            g.DrawString(label, tiny, text, x, Height - 11);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragBand = HitBand(e.X);
        if (_dragBand >= 0) ApplyDrag(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragBand < 0 || e.Button != MouseButtons.Left) return;
        ApplyDrag(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragBand = -1;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var band = HitBand(e.X);
        if (band < 0) return;
        _gainsDb[band] = 0;
        Invalidate();
        EqChanged?.Invoke();
    }

    private int HitBand(int x)
    {
        var eqTop = 48;
        if (Height - eqTop < 20) return -1;
        var gap = 2;
        var barWidth = Math.Max(8, (Width - gap * (_gainsDb.Length - 1)) / _gainsDb.Length);
        var index = x / (barWidth + gap);
        return index >= 0 && index < _gainsDb.Length ? index : -1;
    }

    private void ApplyDrag(int y)
    {
        var eqTop = 48;
        var eqHeight = Height - eqTop - 12;
        var midY = eqTop + eqHeight / 2f;
        var half = eqHeight / 2f;
        var gain = Math.Clamp((midY - y) / half * 12f, -12f, 12f);
        gain = MathF.Round(gain * 2) / 2f;
        if (Math.Abs(gain - _gainsDb[_dragBand]) < 0.01f) return;
        _gainsDb[_dragBand] = gain;
        Invalidate();
        EqChanged?.Invoke();
    }

    private void DrawMeter(Graphics g, string caption, float db, int y,
        Brush empty, Brush fill, Font font, Brush text)
    {
        g.DrawString(caption, font, text, 0, y - 1);
        var bar = new Rectangle(14, y + 2, Width - 14, 8);
        g.FillRectangle(empty, bar);
        var ratio = (db + 140f) / 140f;
        g.FillRectangle(fill, bar.X, bar.Y, (int)Math.Round(bar.Width * Math.Clamp(ratio, 0, 1)), bar.Height);
    }

    private static void Smooth(ref float shown, float target)
    {
        var factor = target > shown ? 0.45f : 0.18f;
        shown += (target - shown) * factor;
        if (Math.Abs(target - shown) < 0.05f) shown = target;
    }
}
