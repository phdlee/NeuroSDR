using NeuroSDR.Plugins;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

/// <summary>Aircraft list + 1090 MHz presets for the dump1090 ADS-B IQ plug-in.</summary>
internal sealed class AdsbPluginView : UserControl
{
    private readonly ComboBox _preset = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        BackColor = Color.FromArgb(5, 17, 24),
        ForeColor = Color.FromArgb(171, 214, 232),
        Font = new Font("Segoe UI", 7.6f),
        DropDownWidth = 360,
        Width = 280,
        Margin = new Padding(4, 6, 2, 2)
    };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(2, 10, 15),
        ForeColor = Color.FromArgb(210, 228, 236),
        Font = new Font("Consolas", 8.5f)
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37),
        ForeColor = Color.FromArgb(170, 200, 214),
        Padding = new Padding(6, 0, 0, 0),
        Text = "1090 MHz Mode S ES (dump1090). 978 UAT is not decoded."
    };
    private bool _loading;

    public event Action<string>? CommandRequested;
    public event Action<NeuroSDRPreset>? PresetSelected;

    public AdsbPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _list.Columns.Add("ICAO", 62);
        _list.Columns.Add("CALL", 72);
        _list.Columns.Add("ALT", 52);
        _list.Columns.Add("SPD", 42);
        _list.Columns.Add("HDG", 40);
        _list.Columns.Add("SQK", 44);
        _list.Columns.Add("LAT", 70);
        _list.Columns.Add("LON", 74);
        _list.Columns.Add("AGE", 36);
        _list.Columns.Add("N", 36);
        _preset.Items.Add("PRESET");
        foreach (var preset in NeuroSDRPresetCatalog.ForGroup("ADS-B")) _preset.Items.Add(preset);
        _preset.SelectedIndex = 0;
        _preset.Enabled = _preset.Items.Count > 1;
        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _preset.SelectedItem is not NeuroSDRPreset preset) return;
            PresetSelected?.Invoke(preset);
        };
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 55,
            WrapContents = true,
            BackColor = Color.FromArgb(15, 35, 46),
            Padding = new Padding(3)
        };
        top.Controls.AddRange([
            Cap("ADS-B"),
            _preset,
            Btn("1090 MHz", () =>
            {
                var first = NeuroSDRPresetCatalog.ForGroup("ADS-B").FirstOrDefault();
                if (first is not null) PresetSelected?.Invoke(first);
            }),
            Btn("CLEAR", () => { _list.Items.Clear(); CommandRequested?.Invoke("clear"); })
        ]);
        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(top);
        DarkNativeTheme.ApplyListView(_list);
    }

    public void LoadSettings(AppSettings settings) => _loading = false;

    public void SaveSettings(AppSettings settings) { }

    public Dictionary<string, string> BuildOptions(bool enabled = true) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false"
    };

    public void ApplyResult(IqPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (result.Kind.Equals("FLEET", StringComparison.OrdinalIgnoreCase))
        {
            RenderFleet(result.Fields?.GetValueOrDefault("rows") ?? "");
            if (!string.IsNullOrWhiteSpace(result.Text)) _status.Text = result.Text;
            return;
        }
        _status.Text = result.Text;
    }

    private void RenderFleet(string rows)
    {
        if (_list.IsDisposed || _list.Disposing) return;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var line in rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var cols = line.Split('\t');
                if (cols.Length < 10) continue;
                var item = new ListViewItem(cols[0]) { BackColor = Color.FromArgb(4, 18, 26) };
                for (var i = 1; i < 10; i++) item.SubItems.Add(cols[i]);
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
            _list.Invalidate();
        }
    }

    private static Label Cap(string t) => new()
    {
        Text = t, AutoSize = true, ForeColor = Color.FromArgb(150, 180, 196), Margin = new Padding(4, 8, 2, 2)
    };

    private static Button Btn(string text, Action click)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(3),
            BackColor = Color.FromArgb(35, 66, 83), ForeColor = Color.FromArgb(222, 233, 240)
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        b.Click += (_, _) => click();
        return b;
    }
}
