using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

/// <summary>FM-radio style station strip — chip layout, height follows 0–2 chip rows.</summary>
internal sealed class WfmStationPanel : Panel
{
    private const int TitleHeight = 18;
    private const int BottomBarHeight = 30;
    private const int ChipHeight = 44;
    private const int ChipMarginV = 3;
    private const int ChipMarginH = 3;
    private const int MaxVisibleRows = 2;

    private readonly FlowLayoutPanel _flow = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        WrapContents = true,
        FlowDirection = FlowDirection.LeftToRight,
        BackColor = Color.FromArgb(8, 14, 22),
        Padding = new Padding(4, 2, 4, 2)
    };
    private readonly Panel _bottom = new() { Dock = DockStyle.Bottom, Height = BottomBarHeight, BackColor = Color.Transparent };
    private readonly TextBox _nameBox = new();
    private readonly ComboBox _eqBox = new();
    private readonly Button _saveBtn = new();
    private readonly Button _delBtn = new();
    private readonly Label _title = new();
    private List<WfmStation> _stations = [];
    private WfmStation? _selected;
    private Func<long>? _getFrequencyHz;
    private Func<string>? _getEqPreset;
    private Action<long, string>? _tune;
    private bool _syncingHeight;

    public event Action? StationsChanged;
    public event Action? PreferredHeightChanged;

    public static int ChipRowPitch => ChipHeight + ChipMarginV * 2;

    public WfmStationPanel()
    {
        BackColor = Color.FromArgb(6, 12, 18);
        Padding = new Padding(6, 4, 6, 4);

        _title.Text = "FM STATIONS";
        _title.Font = new Font("Segoe UI Semibold", 8f);
        _title.ForeColor = Color.FromArgb(140, 185, 205);
        _title.Dock = DockStyle.Top;
        _title.Height = TitleHeight;

        _nameBox.PlaceholderText = "Station name";
        _nameBox.Location = new Point(0, 3);
        _nameBox.Size = new Size(140, 24);
        _nameBox.BackColor = Color.FromArgb(18, 32, 42);
        _nameBox.ForeColor = Color.FromArgb(220, 232, 240);
        _nameBox.BorderStyle = BorderStyle.FixedSingle;

        _eqBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _eqBox.Location = new Point(144, 3);
        _eqBox.Size = new Size(100, 24);
        _eqBox.FlatStyle = FlatStyle.Flat;
        _eqBox.BackColor = Color.FromArgb(18, 32, 42);
        _eqBox.ForeColor = Color.FromArgb(220, 232, 240);

        StyleBtn(_saveBtn, "SAVE", 250, 2, SaveCurrent);
        StyleBtn(_delBtn, "DEL", 298, 2, DeleteSelected);
        _saveBtn.Size = new Size(44, 24);
        _delBtn.Size = new Size(40, 24);
        _bottom.Controls.AddRange([_nameBox, _eqBox, _saveBtn, _delBtn]);

        Controls.Add(_flow);
        Controls.Add(_bottom);
        Controls.Add(_title);
        Height = ComputePreferredHeight();
    }

    public void Bind(Func<long> getFrequencyHz, Func<string> getEqPreset, Action<long, string> tune)
    {
        _getFrequencyHz = getFrequencyHz;
        _getEqPreset = getEqPreset;
        _tune = tune;
    }

    public void LoadStations(IEnumerable<WfmStation> stations, IEnumerable<string> eqPresetNames)
    {
        _stations = stations.Select(Clone).ToList();
        _eqBox.Items.Clear();
        foreach (var n in eqPresetNames)
            _eqBox.Items.Add(n);
        if (_eqBox.Items.Count == 0) _eqBox.Items.Add("Normal");
        SelectEq("Normal");
        RenderChips();
    }

    public void SetEqPresetChoices(IEnumerable<string> names)
    {
        var current = _eqBox.SelectedItem as string ?? "Normal";
        _eqBox.Items.Clear();
        foreach (var n in names) _eqBox.Items.Add(n);
        if (_eqBox.Items.Count == 0) _eqBox.Items.Add("Normal");
        SelectEq(current);
    }

    public List<WfmStation> ExportStations() => _stations.Select(Clone).ToList();

    public IReadOnlyList<(long Frequency, string Name)> GetMarkerStations() =>
        _stations.Select(s => (s.FrequencyHz, s.Name)).ToList();

    /// <summary>Same as double-clicking a station chip (tune + EQ).</summary>
    public bool RecallByFrequency(long frequencyHz)
    {
        var s = _stations
            .OrderBy(x => Math.Abs(x.FrequencyHz - frequencyHz))
            .FirstOrDefault(x => Math.Abs(x.FrequencyHz - frequencyHz) < 25_000);
        if (s is null) return false;
        SelectStation(s);
        RecallStation(s);
        return true;
    }

    /// <summary>Shrink/grow dock height to title+editor + 0..2 chip rows (scroll beyond 2).</summary>
    public void SyncHeightToContent()
    {
        if (_syncingHeight) return;
        var next = ComputePreferredHeight();
        if (Height == next) return;
        _syncingHeight = true;
        try
        {
            Height = next;
            PreferredHeightChanged?.Invoke();
        }
        finally
        {
            _syncingHeight = false;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_syncingHeight)
            SyncHeightToContent();
    }

    private int ComputePreferredHeight()
    {
        var rows = EstimateChipRows();
        var visible = Math.Min(rows, MaxVisibleRows);
        var flowH = visible == 0
            ? 0
            : visible * ChipRowPitch + _flow.Padding.Vertical;
        return Padding.Vertical + TitleHeight + flowH + BottomBarHeight;
    }

    private int EstimateChipRows()
    {
        if (_stations.Count == 0) return 0;
        var avail = Math.Max(40, ClientSize.Width - Padding.Horizontal - _flow.Padding.Horizontal);
        // Reserve scrollbar gutter when content may wrap past 2 rows.
        if (_stations.Count > 4)
            avail = Math.Max(40, avail - SystemInformation.VerticalScrollBarWidth);

        var x = 0;
        var rows = 1;
        foreach (var s in _stations)
        {
            var w = StationChip.MeasureChipWidth(s) + ChipMarginH * 2;
            if (x > 0 && x + w > avail)
            {
                rows++;
                x = 0;
            }
            x += w;
        }
        return rows;
    }

    private void SaveCurrent()
    {
        if (_getFrequencyHz is null || _getEqPreset is null) return;
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = $"{_getFrequencyHz() / 1_000_000d:0.0} FM";
        var eq = _eqBox.SelectedItem as string ?? _getEqPreset() ?? "Normal";
        var hz = _getFrequencyHz();
        var existing = _stations.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(s.FrequencyHz - hz) < 5_000);
        if (existing is not null)
        {
            existing.Name = name;
            existing.FrequencyHz = hz;
            existing.EqPreset = eq;
            _selected = existing;
        }
        else
        {
            var added = new WfmStation { Name = name, FrequencyHz = hz, EqPreset = eq };
            _stations.Add(added);
            _selected = added;
        }
        _nameBox.Text = name;
        RenderChips();
        StationsChanged?.Invoke();
    }

    private void DeleteSelected()
    {
        if (_selected is null) return;
        _stations.Remove(_selected);
        _selected = null;
        RenderChips();
        StationsChanged?.Invoke();
    }

    private void RecallStation(WfmStation s)
    {
        if (_tune is null) return;
        _nameBox.Text = s.Name;
        SelectEq(s.EqPreset);
        _tune(s.FrequencyHz, s.EqPreset);
    }

    private void SelectStation(WfmStation s)
    {
        _selected = s;
        _nameBox.Text = s.Name;
        SelectEq(s.EqPreset);
        foreach (Control c in _flow.Controls)
        {
            if (c is StationChip chip)
                chip.SetSelected(ReferenceEquals(chip.Station, s));
        }
    }

    private void RenderChips()
    {
        _stations = _stations.OrderBy(s => s.FrequencyHz).ToList();
        _flow.SuspendLayout();
        _flow.Controls.Clear();
        foreach (var s in _stations)
        {
            var chip = new StationChip(s);
            chip.SetSelected(ReferenceEquals(_selected, s) ||
                             (_selected is not null &&
                              _selected.FrequencyHz == s.FrequencyHz &&
                              _selected.Name == s.Name));
            chip.Click += (_, _) => SelectStation(s);
            chip.DoubleClick += (_, _) =>
            {
                SelectStation(s);
                RecallStation(s);
            };
            _flow.Controls.Add(chip);
        }
        _flow.ResumeLayout(true);
        SyncHeightToContent();
    }

    private void SelectEq(string name)
    {
        for (var i = 0; i < _eqBox.Items.Count; i++)
        {
            if (_eqBox.Items[i] is string s && s.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _eqBox.SelectedIndex = i;
                return;
            }
        }
        if (_eqBox.Items.Count > 0) _eqBox.SelectedIndex = 0;
    }

    private static WfmStation Clone(WfmStation s) => new()
    {
        Name = s.Name,
        FrequencyHz = s.FrequencyHz,
        EqPreset = string.IsNullOrWhiteSpace(s.EqPreset) ? "Normal" : s.EqPreset
    };

    private static void StyleBtn(Button b, string text, int x, int y, Action click)
    {
        b.Text = text;
        b.Location = new Point(x, y);
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Color.FromArgb(32, 58, 74);
        b.ForeColor = Color.FromArgb(220, 232, 240);
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 145);
        b.Font = new Font("Segoe UI Semibold", 7.5f);
        b.Click += (_, _) => click();
    }

    private sealed class StationChip : Control
    {
        private bool _selected;
        public WfmStation Station { get; }

        public StationChip(WfmStation station)
        {
            Station = station;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            Margin = new Padding(ChipMarginH, ChipMarginV, ChipMarginH, ChipMarginV);
            Height = ChipHeight;
            Width = MeasureChipWidth(station);
            TabStop = true;
        }

        public void SetSelected(bool selected)
        {
            if (_selected == selected) return;
            _selected = selected;
            Invalidate();
        }

        public static int MeasureChipWidth(WfmStation s)
        {
            using var nameFont = new Font("Segoe UI Semibold", 8.5f);
            using var metaFont = new Font("Segoe UI", 7.2f);
            var name = Math.Max(72, TextRenderer.MeasureText(s.Name, nameFont).Width);
            var meta = TextRenderer.MeasureText(
                $"{s.FrequencyHz / 1_000_000d:0.0} MHz · {s.EqPreset}", metaFont).Width;
            return Math.Clamp(Math.Max(name, meta) + 20, 96, 220);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bg = _selected
                ? Color.FromArgb(28, 72, 96)
                : Color.FromArgb(14, 28, 38);
            var border = _selected
                ? Color.FromArgb(90, 190, 220)
                : Color.FromArgb(48, 78, 98);
            using var path = RoundRect(ClientRectangle, 6);
            using (var brush = new SolidBrush(bg))
                g.FillPath(brush, path);
            using (var pen = new Pen(border, _selected ? 1.5f : 1f))
                g.DrawPath(pen, path);

            using var nameFont = new Font("Segoe UI Semibold", 8.5f);
            using var metaFont = new Font("Segoe UI", 7.2f);
            using var nameBrush = new SolidBrush(Color.FromArgb(230, 240, 246));
            using var metaBrush = new SolidBrush(Color.FromArgb(140, 185, 205));
            g.DrawString(Station.Name, nameFont, nameBrush, 8, 6);
            g.DrawString($"{Station.FrequencyHz / 1_000_000d:0.0} MHz · {Station.EqPreset}",
                metaFont, metaBrush, 8, 24);
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            base.OnClick(e);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
