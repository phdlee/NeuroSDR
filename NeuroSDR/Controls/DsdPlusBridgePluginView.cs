using NeuroSDR.Audio;
using NeuroSDR.Plugins;

namespace NeuroSDR.Controls;

/// <summary>
/// DSD+ Bridge UI: bind host OUT→CABLE Input (manual path) + launch DSDPlus.
/// </summary>
internal sealed class DsdPlusBridgePluginView : UserControl, IAfResultView
{
    private readonly CheckBox _autoLaunch = Check("AUTO LAUNCH");
    private readonly CheckBox _bindOut = Check("BIND OUT→VB");
    private readonly CheckBox _muteMain = Check("MUTE MAIN");
    private readonly ComboBox _output = Combo(72);
    private readonly ComboBox _waveIn = Combo(220);
    private readonly TextBox _dir = new()
    {
        Width = 360,
        Margin = new Padding(4, 6, 2, 2),
        BackColor = Color.FromArgb(5, 17, 24),
        ForeColor = Color.FromArgb(171, 214, 232)
    };
    private readonly TextBox _args = new()
    {
        Width = 160,
        Margin = new Padding(4, 6, 2, 2),
        BackColor = Color.FromArgb(5, 17, 24),
        ForeColor = Color.FromArgb(171, 214, 232),
        Text = "-fr"
    };
    private readonly NumericUpDown _volume = new()
    {
        Minimum = 5,
        Maximum = 100,
        Value = 45,
        Width = 56,
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
        Text = "Uses the normal NeuroSDR OUT WaveOut (same as manual OUT2→CABLE). Disable DSD-FME PCM while using this."
    };
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public DsdPlusBridgePluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 110,
            WrapContents = true,
            BackColor = Color.FromArgb(15, 35, 46),
            Padding = new Padding(3)
        };
        _output.Items.AddRange(["OUT1", "OUT2"]);
        _output.SelectedIndex = 1;
        foreach (var device in WaveInDevices.Enumerate())
            _waveIn.Items.Add(new DeviceItem(device.Id, device.Name));
        SelectPreferred(_waveIn, "CABLE Output");
        _autoLaunch.Checked = true;
        _bindOut.Checked = true;
        _muteMain.Checked = true;
        top.Controls.AddRange([
            Cap("DSD+"), Cap("HOST"), _output,
            Cap("DSD+ -i"), _waveIn,
            _bindOut, _muteMain, _autoLaunch,
            Cap("DIR"), _dir,
            Cap("ARGS"), _args,
            Cap("OUT VOL%"), _volume,
            Btn("BIND OUT", () => CommandRequested?.Invoke("bind")),
            Btn("LAUNCH", () => CommandRequested?.Invoke("launch")),
            Btn("STOP DSD+", () => CommandRequested?.Invoke("stop-dsdplus")),
            Btn("APPLY", () => OptionsChanged?.Invoke())
        ]);
        _autoLaunch.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _bindOut.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _muteMain.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _output.SelectedIndexChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _waveIn.SelectedIndexChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _volume.ValueChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _dir.Leave += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        _args.Leave += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        Controls.Add(_status);
        Controls.Add(top);
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        var waveIn = _waveIn.SelectedItem is DeviceItem wi ? wi.Id : WaveInDevices.FindPreferred("CABLE Output");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["autoLaunch"] = _autoLaunch.Checked.ToString(),
            ["bindOut"] = _bindOut.Checked.ToString(),
            ["muteMain"] = _muteMain.Checked.ToString(),
            ["output"] = (_output.SelectedIndex == 0 ? 1 : 2).ToString(),
            ["dsdPlusInput"] = waveIn.ToString(),
            ["dsdPlusDir"] = _dir.Text.Trim(),
            ["extraArgs"] = _args.Text.Trim(),
            ["feedVolume"] = ((int)_volume.Value).ToString(),
            ["cableOut"] = "CABLE Input"
        };
    }

    public void LoadOptions(IReadOnlyDictionary<string, string> options)
    {
        _loading = true;
        _autoLaunch.Checked = !options.TryGetValue("autoLaunch", out var al) ||
                              !bool.TryParse(al, out var auto) || auto;
        _bindOut.Checked = !options.TryGetValue("bindOut", out var bo) ||
                           !bool.TryParse(bo, out var bind) || bind;
        _muteMain.Checked = !options.TryGetValue("muteMain", out var mm) ||
                            !bool.TryParse(mm, out var mute) || mute;
        if (options.TryGetValue("output", out var outText) && int.TryParse(outText, out var output))
            _output.SelectedIndex = output == 1 ? 0 : 1;
        if (options.TryGetValue("dsdPlusInput", out var wi) && int.TryParse(wi, out var waveIn))
            SelectId(_waveIn, waveIn);
        if (options.TryGetValue("dsdPlusDir", out var dir) && !string.IsNullOrWhiteSpace(dir))
            _dir.Text = dir;
        else
            _dir.Text = @"J:\codex\sdr\samples\DSDPlusFull";
        if (options.TryGetValue("extraArgs", out var args) && !string.IsNullOrWhiteSpace(args))
            _args.Text = args;
        if (options.TryGetValue("feedVolume", out var vol) && int.TryParse(vol, out var volume))
            _volume.Value = Math.Clamp(volume, 5, 100);
        _loading = false;
    }

    public void ApplyResult(AfPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (!string.IsNullOrWhiteSpace(result.Text))
            _status.Text = $"{result.TimestampUtc:HH:mm:ss}  {result.Text}";
    }

    private static void SelectPreferred(ComboBox box, string hint)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is DeviceItem item &&
                item.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static void SelectId(ComboBox box, int id)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is DeviceItem item && item.Id == id)
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }

    private static ComboBox Combo(int width) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = width,
        Margin = new Padding(4, 6, 2, 2),
        BackColor = Color.FromArgb(5, 17, 24),
        ForeColor = Color.FromArgb(171, 214, 232)
    };

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

    private sealed record DeviceItem(int Id, string Name)
    {
        public override string ToString() => $"#{Id} {Name}";
    }
}
