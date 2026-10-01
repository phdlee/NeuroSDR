namespace NeuroSDR.Controls;

internal sealed class OokAskPluginView : UserControl, NeuroSDR.Plugins.IAfResultView
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(4, 12, 18),
        ForeColor = Color.FromArgb(210, 228, 236),
        Font = new Font("Consolas", 8.5f)
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37),
        ForeColor = Color.FromArgb(170, 200, 214),
        Padding = new Padding(6, 0, 0, 0),
        Text = "OOK/ASK · always listening · short chirps OK · RF gate only filters results"
    };
    private readonly Label _thrValue = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(255, 193, 69),
        Margin = new Padding(2, 8, 8, 2),
        Text = "40%"
    };
    private readonly TrackBar _threshold = new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = 40,
        TickFrequency = 10,
        Width = 140,
        AutoSize = false,
        Height = 28
    };
    private readonly CheckBox _rfGate = new()
    {
        Text = "RF noise gate",
        AutoSize = true,
        ForeColor = Color.FromArgb(210, 228, 236),
        Margin = new Padding(10, 6, 2, 2)
    };
    private readonly NumericUpDown _marginDb = new()
    {
        Minimum = 2,
        Maximum = 40,
        Value = 6,
        Width = 48,
        DecimalPlaces = 0
    };
    private readonly NumericUpDown _spanKhz = new()
    {
        Minimum = 25,
        Maximum = 500,
        Value = 100,
        Increment = 25,
        Width = 56,
        DecimalPlaces = 0
    };

    public event Action? OptionsChanged;

    public OokAskPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _list.Columns.Add("UTC", 72);
        _list.Columns.Add("HEX", 140);
        _list.Columns.Add("BITS", 200);
        _list.Columns.Add("BAUD", 52);
        _list.Columns.Add("ms", 44);
        _list.Columns.Add("SIG", 48);
        _list.Columns.Add("NOISE", 52);
        _list.Columns.Add("PULSES", 180);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Color.FromArgb(15, 35, 46),
            Padding = new Padding(4, 2, 4, 2),
            WrapContents = true
        };
        var thrLabel = MakeLabel("Threshold");
        var clear = new Button
        {
            Text = "CLEAR",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 66, 83),
            ForeColor = Color.FromArgb(222, 233, 240),
            Margin = new Padding(8, 4, 3, 3)
        };
        clear.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        clear.Click += (_, _) => _list.Items.Clear();

        var marginLabel = MakeLabel("ΔdB");
        var spanLabel = MakeLabel("span kHz");
        StyleNum(_marginDb);
        StyleNum(_spanKhz);

        _threshold.ValueChanged += (_, _) =>
        {
            _thrValue.Text = $"{_threshold.Value}%";
            OptionsChanged?.Invoke();
        };
        _thrValue.Text = $"{_threshold.Value}%";
        _rfGate.CheckedChanged += (_, _) =>
        {
            UpdateGateEnabled();
            OptionsChanged?.Invoke();
        };
        _marginDb.ValueChanged += (_, _) => OptionsChanged?.Invoke();
        _spanKhz.ValueChanged += (_, _) => OptionsChanged?.Invoke();
        UpdateGateEnabled();

        top.Controls.AddRange([
            thrLabel, _threshold, _thrValue, clear,
            _rfGate, marginLabel, _marginDb, spanLabel, _spanKhz
        ]);

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(top);
        DarkNativeTheme.ApplyListView(_list);
        _list.DoubleClick += (_, _) => OpenSelectedBurst();
    }

    private void OpenSelectedBurst()
    {
        if (_list.SelectedItems.Count == 0) return;
        var item = _list.SelectedItems[0];
        if (item.Tag is not Dictionary<string, string> fields) return;
        using var dlg = new OokAskBurstDetailForm(fields, item.Text);
        dlg.ShowDialog(FindForm());
    }

    private void UpdateGateEnabled()
    {
        _marginDb.Enabled = _rfGate.Checked;
        _spanKhz.Enabled = _rfGate.Checked;
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(160, 185, 200),
        Margin = new Padding(4, 8, 2, 2)
    };

    private static void StyleNum(NumericUpDown n)
    {
        n.BackColor = Color.FromArgb(18, 32, 42);
        n.ForeColor = Color.FromArgb(220, 232, 240);
        n.BorderStyle = BorderStyle.FixedSingle;
        n.Margin = new Padding(2, 5, 6, 2);
    }

    public Dictionary<string, string> BuildOptions() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["threshold"] = (_threshold.Value / 100f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        ["rfNoiseGate"] = _rfGate.Checked.ToString(),
        ["noiseMarginDb"] = ((int)_marginDb.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["noiseSpanKHz"] = ((int)_spanKhz.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    public void LoadOptions(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("threshold", out var t) &&
            float.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var thr))
            _threshold.Value = (int)Math.Clamp(Math.Round(thr * 100), 0, 100);
        _thrValue.Text = $"{_threshold.Value}%";

        if (options.TryGetValue("rfNoiseGate", out var g) && bool.TryParse(g, out var gate))
            _rfGate.Checked = gate;

        if (options.TryGetValue("noiseMarginDb", out var m) &&
            decimal.TryParse(m, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var margin))
            _marginDb.Value = Math.Clamp(margin, _marginDb.Minimum, _marginDb.Maximum);

        if (options.TryGetValue("noiseSpanKHz", out var s) &&
            decimal.TryParse(s, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var span))
            _spanKhz.Value = Math.Clamp(span, _spanKhz.Minimum, _spanKhz.Maximum);

        UpdateGateEnabled();
    }

    public void ApplyResult(NeuroSDR.Plugins.AfPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (result.Kind.Equals("STATUS", StringComparison.OrdinalIgnoreCase) ||
            result.Kind.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = result.Text;
            return;
        }
        if (!result.Kind.Equals("OOK_BURST", StringComparison.OrdinalIgnoreCase)) return;
        _status.Text = result.Text;
        var f = result.Fields;
        var fields = f is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(f, StringComparer.OrdinalIgnoreCase);
        var bits = fields.GetValueOrDefault("bits") ?? result.Text;
        var item = new ListViewItem(result.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"))
        {
            BackColor = Color.FromArgb(6, 16, 24),
            Tag = fields
        };
        item.SubItems.Add(fields.GetValueOrDefault("hex") ?? "");
        item.SubItems.Add(bits.Length > 48 ? bits[..48] + "…" : bits);
        item.SubItems.Add(fields.GetValueOrDefault("baud") ?? "");
        item.SubItems.Add(fields.GetValueOrDefault("durationMs") ?? "");
        item.SubItems.Add(fields.GetValueOrDefault("signalDb") ?? "");
        item.SubItems.Add(fields.GetValueOrDefault("noiseDb") ?? "");
        item.SubItems.Add(fields.GetValueOrDefault("pulses") ?? "");
        _list.Items.Insert(0, item);
        while (_list.Items.Count > 80)
            _list.Items.RemoveAt(_list.Items.Count - 1);
    }
}
