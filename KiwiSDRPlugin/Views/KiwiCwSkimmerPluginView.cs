using System.Globalization;
using ENSdr.Plugins;

namespace KiwiSDRPlugin.Views;

/// <summary>Frequency / text table UI for KiwiCWSkimmer.</summary>
public sealed class KiwiCwSkimmerPluginView : UserControl, IAfResultView, IPluginViewEvents
{
    private readonly ComboBox _pwrCalc = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Margin = new Padding(2) };
    private readonly CheckBox _neighbors = Chk("NEIGH FILTER");
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
        BackColor = Color.FromArgb(2, 10, 15), ForeColor = Color.FromArgb(210, 228, 236),
        Font = new Font("Consolas", 9f), HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None
    };
    private readonly Label _status = Status();
    private readonly Dictionary<int, ListViewItem> _rows = [];
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public int PwrCalc => _pwrCalc.SelectedIndex < 0 ? 0 : _pwrCalc.SelectedIndex;
    public bool FilterNeighbors => _neighbors.Checked;

    public KiwiCwSkimmerPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _pwrCalc.Items.AddRange(["AVG RATIO", "AVG BOTTOM", "THRESHOLD"]);
        _pwrCalc.SelectedIndex = 0;
        _list.Columns.Add("Hz", 70);
        _list.Columns.Add("WPM", 44);
        _list.Columns.Add("Text", 360);
        var top = Bar();
        top.Controls.AddRange([
            Cap("KiwiCWSkimmer"), Cap("PWR"), _pwrCalc, _neighbors,
            Btn("CLEAR", () => { ClearRows(); CommandRequested?.Invoke("clear"); }),
            Btn("RESET", () => { ClearRows(); CommandRequested?.Invoke("reset"); })
        ]);
        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(top);
        _pwrCalc.SelectedIndexChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _neighbors.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        Resize += (_, _) =>
        {
            if (_list.Columns.Count >= 3)
                _list.Columns[2].Width = Math.Max(120, _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - 8);
        };
    }

    public void LoadOptions(IReadOnlyDictionary<string, string> options)
    {
        _loading = true;
        _pwrCalc.SelectedIndex = Math.Clamp(Int(options, "pwrCalc", 0), 0, 2);
        _neighbors.Checked = Flag(options, "filterNeighbors", false);
        _loading = false;
    }

    public Dictionary<string, string> Snapshot() => BuildOptions(true);

    public Dictionary<string, string> BuildOptions(bool enabled = true) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["pwrCalc"] = PwrCalc.ToString(CultureInfo.InvariantCulture),
        ["filterNeighbors"] = FilterNeighbors ? "true" : "false"
    };

    public void ApplyResult(AfPluginResult result)
    {
        if (!result.Kind.Equals("SKIMMER_CHAR", StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = result.Text;
            return;
        }
        if (result.Fields is null ||
            !int.TryParse(result.Fields.GetValueOrDefault("frequency"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var freq))
            return;
        var wpm = result.Fields.GetValueOrDefault("wpm", "0");
        var line = result.Fields.GetValueOrDefault("line", result.Text);
        if (!_rows.TryGetValue(freq, out var item))
        {
            item = new ListViewItem([freq.ToString(CultureInfo.InvariantCulture), wpm, line]);
            _rows[freq] = item;
            // Keep sorted by frequency
            var insertAt = 0;
            for (; insertAt < _list.Items.Count; insertAt++)
                if (int.TryParse(_list.Items[insertAt].Text, out var existing) && existing > freq) break;
            _list.Items.Insert(insertAt, item);
        }
        else
        {
            item.SubItems[1].Text = wpm;
            item.SubItems[2].Text = line.Length > 80 ? line[^80..] : line;
        }
        _status.Text = $"{freq} Hz · WPM {wpm} · '{result.Fields.GetValueOrDefault("character")}'";
    }

    private void ClearRows()
    {
        _rows.Clear();
        _list.Items.Clear();
        _status.Text = string.Empty;
    }

    private static FlowLayoutPanel Bar() => new()
    {
        Dock = DockStyle.Top, Height = 55, WrapContents = true,
        BackColor = Color.FromArgb(15, 35, 46), Padding = new Padding(3)
    };
    private static Label Status() => new()
    {
        Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37), ForeColor = Color.FromArgb(170, 200, 214), Padding = new Padding(6, 0, 0, 0)
    };
    private static Label Cap(string t) => new() { Text = t, AutoSize = true, ForeColor = Color.FromArgb(150, 180, 196), Margin = new Padding(4, 8, 2, 2) };
    private static CheckBox Chk(string t) => new() { Text = t, AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Margin = new Padding(4, 5, 2, 2) };
    private static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback) =>
        options.TryGetValue(key, out var text) && int.TryParse(text, out var value) ? value : fallback;
    private static bool Flag(IReadOnlyDictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;
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
