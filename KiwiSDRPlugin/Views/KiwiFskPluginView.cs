using System.Globalization;
using ENSdr.Plugins;

namespace KiwiSDRPlugin.Views;
/// <summary>KiwiFSK UI with encoding / framing / preset (matches KiwiSDR FSK.js).</summary>
public sealed class KiwiFskPluginView : UserControl, IAfResultView, IAfFskTuningView, IPluginViewEvents
{
    private static readonly string[] Presets = ["ham", "wx", "sitor-b", "dsc", "selcall"];
    private static readonly string[] Encodings = ["ITA2", "ASCII", "CCIR476", "DSC", "Selcall"];
    private static readonly string[] Framings = ["5N1", "5N1V", "5N1.5", "5N2", "7N1", "8N1", "4/7", "EFR", "EFR2", "CHU", "7/3"];

    private readonly ComboBox _preset = Combo(90);
    private readonly ComboBox _encoding = Combo(90);
    private readonly ComboBox _framing = Combo(70);
    private readonly NumericUpDown _baud = Num(10, 300, 45.45m, 2, 70);
    private readonly NumericUpDown _center = Num(200, 3000, 1000, 0, 70);
    private readonly NumericUpDown _deviation = Num(10, 1000, 85, 0, 60); // half-shift (AF marker LO/HI)
    private readonly CheckBox _inverse = Chk("INV");
    private readonly CheckBox _showErrs = Chk("ERRS");
    private readonly TextBox _text = ConsoleBox();
    private readonly Label _status = Status();
    private bool _loading;

    public event Action? OptionsChanged;
    public event Action<string>? CommandRequested;

    public KiwiFskPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _preset.Items.AddRange(Presets);
        _encoding.Items.AddRange(Encodings);
        _framing.Items.AddRange(Framings);
        _preset.SelectedIndex = 0;
        _encoding.SelectedIndex = 0;
        _framing.SelectedIndex = 2; // 5N1.5
        var top = Bar();
        top.Height = 78;
        top.Controls.AddRange([
            Cap("KiwiFSK"), Cap("PRESET"), _preset, Cap("ENC"), _encoding, Cap("FR"), _framing,
            Cap("BAUD"), _baud, Cap("CF"), _center, Cap("DEV"), _deviation, _inverse, _showErrs,
            Btn("CLEAR", () => { _text.Clear(); CommandRequested?.Invoke("clear"); }),
            Btn("RESET", () => CommandRequested?.Invoke("reset"))
        ]);
        Controls.Add(_text);
        Controls.Add(_status);
        Controls.Add(top);
        _preset.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            ApplyPresetUi(_preset.SelectedItem?.ToString() ?? "ham");
            OptionsChanged?.Invoke();
        };
        _encoding.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            SuggestFramingForEncoding(_encoding.SelectedItem?.ToString() ?? "ITA2");
            OptionsChanged?.Invoke();
        };
        _framing.SelectedIndexChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        Wire(_baud, _center, _deviation, _inverse, _showErrs);
    }

    public void LoadOptions(IReadOnlyDictionary<string, string> options)
    {
        _loading = true;
        SelectCombo(_preset, Get(options, "preset", "ham"), 0);
        SelectCombo(_encoding, Get(options, "encoding", "ITA2"), 0);
        SelectCombo(_framing, Get(options, "framing", "5N1.5"), 2);
        _baud.Value = Clamp(Dbl(options, "baud", 45.45), _baud);
        _center.Value = Clamp(Dbl(options, "center", 1000), _center);
        _deviation.Value = Clamp(Dbl(options, "deviation", 85), _deviation);
        _inverse.Checked = Flag(options, "inverse", false);
        _showErrs.Checked = Flag(options, "showErrs", false);
        _loading = false;
    }

    public Dictionary<string, string> Snapshot(double rfHz = 0) => BuildOptions(true, rfHz);

    public bool TryGetFskMarkers(out int centerHz, out int deviationHz)
    {
        centerHz = (int)_center.Value;
        deviationHz = (int)_deviation.Value;
        return true;
    }

    public void SetFskTuning(int centerHz, int deviationHz)
    {
        _loading = true;
        _center.Value = Clamp(centerHz, _center);
        _deviation.Value = Clamp(deviationHz, _deviation);
        _loading = false;
    }

    public Dictionary<string, string> BuildOptions(bool enabled, double rfHz = 0) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["enabled"] = enabled ? "true" : "false",
        ["preset"] = "", // empty so Configure does not re-apply preset over manual encoding
        ["encoding"] = _encoding.SelectedItem?.ToString() ?? "ITA2",
        ["framing"] = _framing.SelectedItem?.ToString() ?? "5N1.5",
        ["baud"] = _baud.Value.ToString(CultureInfo.InvariantCulture),
        ["center"] = _center.Value.ToString(CultureInfo.InvariantCulture),
        ["deviation"] = _deviation.Value.ToString(CultureInfo.InvariantCulture),
        ["inverse"] = _inverse.Checked ? "true" : "false",
        ["showErrs"] = _showErrs.Checked ? "true" : "false",
        ["rfHz"] = rfHz.ToString(CultureInfo.InvariantCulture)
    };

    public void ApplyResult(AfPluginResult result)
    {
        if (result.Kind.Equals("KIWIFSK_CHAR", StringComparison.OrdinalIgnoreCase))
        {
            _text.AppendText(result.Fields?.GetValueOrDefault("character") ?? result.Text);
            Trim(_text);
            return;
        }
        if (result.Kind.Equals("STATUS", StringComparison.OrdinalIgnoreCase) ||
            result.Kind.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            _status.Text = result.Text;
    }

    private void ApplyPresetUi(string preset)
    {
        _loading = true;
        switch (preset.ToLowerInvariant())
        {
            case "wx":
                _baud.Value = 50; _deviation.Value = 225; // shift 450
                SelectCombo(_framing, "5N1.5", 2); SelectCombo(_encoding, "ITA2", 0);
                _inverse.Checked = true;
                break;
            case "sitor-b":
                _baud.Value = 100; _deviation.Value = 85;
                SelectCombo(_framing, "4/7", 6); SelectCombo(_encoding, "CCIR476", 2);
                _inverse.Checked = false;
                break;
            case "dsc":
                _baud.Value = 100; _deviation.Value = 85;
                SelectCombo(_framing, "7/3", 10); SelectCombo(_encoding, "DSC", 3);
                _inverse.Checked = true;
                break;
            case "selcall":
                _baud.Value = 100; _deviation.Value = 85;
                SelectCombo(_framing, "7/3", 10); SelectCombo(_encoding, "Selcall", 4);
                _inverse.Checked = false;
                break;
            default: // ham
                _baud.Value = 45.45m; _deviation.Value = 85;
                SelectCombo(_framing, "5N1.5", 2); SelectCombo(_encoding, "ITA2", 0);
                _inverse.Checked = false;
                break;
        }
        _loading = false;
    }

    private void SuggestFramingForEncoding(string encoding)
    {
        _loading = true;
        switch (encoding.ToUpperInvariant())
        {
            case "ASCII": SelectCombo(_framing, "8N1", 5); break;
            case "CCIR476": SelectCombo(_framing, "4/7", 6); break;
            case "DSC":
            case "SELCALL": SelectCombo(_framing, "7/3", 10); break;
            default: SelectCombo(_framing, "5N1.5", 2); break;
        }
        _loading = false;
    }

    private void Wire(params Control[] controls)
    {
        foreach (var c in controls)
        {
            if (c is NumericUpDown n) n.ValueChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
            if (c is CheckBox b) b.CheckedChanged += (_, _) => { if (!_loading) OptionsChanged?.Invoke(); };
        }
    }

    private static void SelectCombo(ComboBox box, string? value, int fallback)
    {
        var i = box.Items.IndexOf(value ?? "");
        box.SelectedIndex = i >= 0 ? i : Math.Clamp(fallback, 0, Math.Max(0, box.Items.Count - 1));
    }

    private static void Trim(TextBox box)
    {
        if (box.TextLength > 20_000) box.Text = box.Text[^12_000..];
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    private static FlowLayoutPanel Bar() => new()
    {
        Dock = DockStyle.Top, Height = 55, WrapContents = true,
        BackColor = Color.FromArgb(15, 35, 46), Padding = new Padding(3)
    };
    private static ComboBox Combo(int w) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = w, Margin = new Padding(2)
    };
    private static TextBox ConsoleBox() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        BackColor = Color.FromArgb(2, 10, 15), ForeColor = Color.FromArgb(210, 228, 236),
        Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None, WordWrap = false
    };
    private static Label Status() => new()
    {
        Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 28, 37), ForeColor = Color.FromArgb(170, 200, 214), Padding = new Padding(6, 0, 0, 0)
    };
    private static Label Cap(string t) => new() { Text = t, AutoSize = true, ForeColor = Color.FromArgb(150, 180, 196), Margin = new Padding(4, 8, 2, 2) };
    private static CheckBox Chk(string t) => new() { Text = t, AutoSize = true, ForeColor = Color.FromArgb(206, 221, 231), Margin = new Padding(4, 5, 2, 2) };
    private static NumericUpDown Num(decimal min, decimal max, decimal v, int d, int w) => new()
    {
        Minimum = min, Maximum = max, Value = v, DecimalPlaces = d, Increment = d == 0 ? 1 : .5m, Width = w, Margin = new Padding(2)
    };
    private static decimal Clamp(double value, NumericUpDown box) => Math.Clamp((decimal)value, box.Minimum, box.Maximum);
    private static string Get(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var text) ? text : fallback;
    private static double Dbl(IReadOnlyDictionary<string, string> options, string key, double fallback) =>
        options.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : fallback;
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

