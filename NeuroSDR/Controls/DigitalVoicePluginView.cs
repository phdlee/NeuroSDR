using NeuroSDR.Plugins;

namespace NeuroSDR.Controls;

internal sealed class DigitalVoicePluginView : UserControl, IAfResultView
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
    private readonly CheckBox _invert = Check("INVERT 4FSK");
    private readonly ComboBox _output = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 72,
        Margin = new Padding(4, 6, 2, 2),
        BackColor = Color.FromArgb(5, 17, 24),
        ForeColor = Color.FromArgb(171, 214, 232)
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37),
        ForeColor = Color.FromArgb(170, 200, 214),
        Padding = new Padding(6, 0, 0, 0),
        Text = "DMR / D-STAR. Voice: DSD-FME dibits → OUT1/OUT2 (overrides MAIN/SUB analog)."
    };
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public DigitalVoicePluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        foreach (var title in new[] { "UTC", "PROT", "SLOT", "CC", "SRC", "CALL", "NAME", "TG/DST", "TYPE", "RSSI", "BER" })
            _list.Columns.Add(title, title is "NAME" or "CALL" or "TG/DST" ? 88 : 52);
        _list.Columns[0].Width = 68;
        _list.Columns[5].Width = 72;
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 55,
            WrapContents = true,
            BackColor = Color.FromArgb(15, 35, 46),
            Padding = new Padding(3)
        };
        _output.Items.AddRange(["OUT1", "OUT2"]);
        _output.SelectedIndex = 0;
        top.Controls.AddRange([
            Cap("DIGITAL"),
            Cap("PLAY"),
            _output,
            _invert,
            Btn("CLEAR", () => { _list.Items.Clear(); CommandRequested?.Invoke("clear"); }),
            Btn("RELOAD RadioID", () => CommandRequested?.Invoke("reload-radioid"))
        ]);
        _invert.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _output.SelectedIndexChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(top);
        DarkNativeTheme.ApplyListView(_list);
    }

    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["invert"] = _invert.Checked.ToString(),
        ["output"] = (_output.SelectedIndex == 1 ? 2 : 1).ToString()
    };

    public void LoadOptions(IReadOnlyDictionary<string, string> options)
    {
        _loading = true;
        _invert.Checked = options.TryGetValue("invert", out var text) && bool.TryParse(text, out var invert) && invert;
        var output = 1;
        if (options.TryGetValue("output", out var outputText) && int.TryParse(outputText, out var parsed))
            output = parsed;
        _output.SelectedIndex = output == 2 ? 1 : 0;
        _loading = false;
    }

    public void ApplyResult(AfPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (result.Kind.Equals("QSO_CLEAR", StringComparison.OrdinalIgnoreCase))
        {
            _list.BeginUpdate();
            try { _list.Items.Clear(); }
            finally { _list.EndUpdate(); }
            _status.Text = "cleared";
            return;
        }
        if (result.Kind.Equals("QSO_RECORD", StringComparison.OrdinalIgnoreCase))
        {
            var fields = result.Fields ?? new Dictionary<string, string>();
            string F(string key) => fields.GetValueOrDefault(key) ?? "";
            var item = new ListViewItem(F("timestamp"));
            item.SubItems.AddRange([
                F("protocol"), F("slot"), F("cc"), F("source"), F("callsign"), F("name"),
                F("target"), F("callType"), F("rssi"), F("ber")
            ]);
            _list.BeginUpdate();
            try
            {
                _list.Items.Insert(0, item);
                while (_list.Items.Count > 250) _list.Items.RemoveAt(_list.Items.Count - 1);
            }
            finally { _list.EndUpdate(); }
        }
        if (!string.IsNullOrWhiteSpace(result.Text) &&
            !result.Kind.Equals("DSD_SYMBOLS", StringComparison.OrdinalIgnoreCase))
            _status.Text = result.Text;
    }

    private static Label Cap(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(120, 170, 190),
        Margin = new Padding(6, 10, 4, 2)
    };

    private static CheckBox Check(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(200, 220, 230),
        Margin = new Padding(6, 8, 4, 2)
    };

    private static Button Btn(string text, Action click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(200, 230, 240),
            BackColor = Color.FromArgb(20, 48, 62),
            Margin = new Padding(4, 6, 2, 2)
        };
        button.Click += (_, _) => click();
        return button;
    }
}
