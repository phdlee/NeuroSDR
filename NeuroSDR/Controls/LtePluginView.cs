using NeuroSDR.Core;
using NeuroSDR.Plugins;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

/// <summary>LTE / PS-LTE cell table for the IQ plug-in (broadcast overview, not voice).</summary>
internal sealed class LtePluginView : UserControl
{
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
        Height = 36,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37),
        ForeColor = Color.FromArgb(170, 200, 214),
        Padding = new Padding(6, 0, 0, 0),
        Text = "managed SYNC (PSS/SSS) · ≥1.92 MS/s RAW · ens_lte optional"
    };

    public event Action<string>? CommandRequested;
    public event Action<NeuroSDRPreset>? PresetSelected;

    public LtePluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _list.Columns.Add("EARFCN", 58);
        _list.Columns.Add("FREQ", 68);
        _list.Columns.Add("PCI", 40);
        _list.Columns.Add("PRB", 36);
        _list.Columns.Add("PORTS", 46);
        _list.Columns.Add("CP", 28);
        _list.Columns.Add("PSS dB", 52);
        _list.Columns.Add("CFO", 48);
        _list.Columns.Add("TAC", 48);
        _list.Columns.Add("CID", 72);
        _list.Columns.Add("MCC/MNC", 64);
        _list.Columns.Add("NOTE", 220);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 78,
            WrapContents = true,
            BackColor = Color.FromArgb(15, 35, 46),
            Padding = new Padding(3)
        };
        top.Controls.AddRange([
            Cap("LTE"),
            Btn("B5 874.67", () => Tune(874_670_000, "B5 MIB-known")),
            Btn("B5 875.85", () => Tune(875_850_000, "B5 EARFCN 2469")),
            Btn("B5 875.67", () => Tune(875_670_000, "B5 alt")),
            Btn("B3 1842.5", () => Tune(1_842_500_000, "B3 DL mid")),
            Btn("B7 2655", () => Tune(2_655_000_000, "B7 DL mid")),
            Btn("B28 763", () => Tune(763_000_000, "B28 DL mid")),
            Btn("HYBRID", () => { _mode = "hybrid"; CommandRequested?.Invoke("mode:hybrid"); }),
            Btn("NATIVE", () => { _mode = "native"; CommandRequested?.Invoke("mode:native"); }),
            Btn("MANAGED", () => { _mode = "managed"; CommandRequested?.Invoke("mode:managed"); }),
            Btn("CLEAR", () => { _list.Items.Clear(); CommandRequested?.Invoke("clear"); })
        ]);
        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(top);
        DarkNativeTheme.ApplyListView(_list);
    }

    public void LoadSettings(AppSettings settings) { }

    public void SaveSettings(AppSettings settings) { }

    private string _mode = "managed";

    public Dictionary<string, string> BuildOptions(bool enabled = true) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["mode"] = _mode
    };

    public void ApplyResult(IqPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (result.Kind.Equals("CELLS", StringComparison.OrdinalIgnoreCase))
        {
            // Keep STATUS line stable — do not overwrite with CELLS payload.
            RenderCells(result.Fields?.GetValueOrDefault("rows") ?? "");
            return;
        }
        if (result.Kind.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            _status.Text = result.Text;
    }

    private void Tune(long hz, string name) =>
        PresetSelected?.Invoke(new NeuroSDRPreset("LTE", name, hz, RadioMode.RAW, 5_000_000));

    private void RenderCells(string rows)
    {
        if (_list.IsDisposed || _list.Disposing) return;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var line in rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var cols = line.Split('\t');
                if (cols.Length < 12) continue;
                var item = new ListViewItem(cols[0]) { BackColor = Color.FromArgb(4, 18, 26) };
                for (var i = 1; i < 12; i++) item.SubItems.Add(cols[i]);
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
