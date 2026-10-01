using NeuroSDR.Plugins;

namespace NeuroSDR.Controls;

/// <summary>OUT selector for DSD-FME PCM (no host-side DMR invert — slicer lives in dsdfme).</summary>
internal sealed class DsdProbePluginView : UserControl, IAfResultView
{
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
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.TopLeft,
        BackColor = Color.FromArgb(2, 10, 15),
        ForeColor = Color.FromArgb(210, 228, 236),
        Font = new Font("Consolas", 9f),
        Padding = new Padding(8),
        Text = ""
    };
    private readonly Label _hint;
    private bool _loading;

    public event Action? OptionsChanged;
#pragma warning disable CS0067
    public event Action<string>? CommandRequested;
#pragma warning restore CS0067

    public DsdProbePluginView(string title, string hint)
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Text = hint,
            ForeColor = Color.FromArgb(150, 180, 195),
            Padding = new Padding(8, 4, 8, 0)
        };
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            WrapContents = false,
            BackColor = Color.FromArgb(15, 35, 46),
            Padding = new Padding(3)
        };
        _output.Items.AddRange(["OUT1", "OUT2"]);
        _output.SelectedIndex = 0;
        top.Controls.AddRange([Cap(title), Cap("PLAY"), _output]);
        _output.SelectedIndexChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        Controls.Add(_status);
        Controls.Add(_hint);
        Controls.Add(top);
    }

    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["output"] = (_output.SelectedIndex == 1 ? 2 : 1).ToString()
    };

    public void LoadOptions(IReadOnlyDictionary<string, string> options)
    {
        _loading = true;
        var output = 1;
        if (options.TryGetValue("output", out var outputText) && int.TryParse(outputText, out var parsed))
            output = parsed;
        _output.SelectedIndex = output == 2 ? 1 : 0;
        _loading = false;
    }

    public void ApplyResult(AfPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (!string.IsNullOrWhiteSpace(result.Text))
            _status.Text = $"{result.TimestampUtc:HH:mm:ss}  {result.Text}";
    }

    private static Label Cap(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(120, 170, 190),
        Margin = new Padding(6, 10, 4, 2)
    };
}
