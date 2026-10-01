using System.Globalization;
using System.Text;
using NeuroSDR.Plugins;
using NeuroSDR.Plugins.Caption;

namespace NeuroSDR.Controls;

internal sealed class NeuroCaptionPluginView : UserControl, IAfResultView
{
    private readonly Button _analyze = CaptionPluginChrome.Tiny("CAPTION OFF", 96, CaptionPluginChrome.OffFill);
    private readonly Button _setup = CaptionPluginChrome.Tiny("SETUP", 50, CaptionPluginChrome.SetupFill);
    private readonly Button _download = CaptionPluginChrome.Tiny("GET", 44, CaptionPluginChrome.SetupFill);
    private readonly Button _clear = CaptionPluginChrome.Tiny("CLEAR", 48, CaptionPluginChrome.TinyFill);
    private readonly CheckBox _gateEnable = CaptionPluginChrome.GateEnable();
    private readonly NumericUpDown _gateLevel = CaptionPluginChrome.GateLevel();
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(4, 12, 18),
        ForeColor = Color.FromArgb(220, 236, 228),
        Font = new Font("Consolas", 10f),
        WordWrap = true
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(10, 24, 32),
        ForeColor = Color.FromArgb(170, 200, 214),
        Padding = new Padding(8, 0, 0, 0),
        Text = "CAPTION OFF · SETUP to choose Groq / Whisper / ONNX"
    };
    private readonly StringBuilder _buffer = new();
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private bool _analyzeOn;
    private bool _loading;
    private bool _downloading;

    public event Action? OptionsChanged;

    public NeuroCaptionPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(5, 17, 24);
        _gateLevel.Value = NeuroCaption.DefaultGate;
        _analyze.Click += (_, _) =>
        {
            _analyzeOn = !_analyzeOn;
            UpdateChrome();
            OptionsChanged?.Invoke();
        };
        _setup.Click += (_, _) => OpenSetup();
        _download.Click += async (_, _) => await DownloadOnnxAsync();
        _clear.Click += (_, _) =>
        {
            _buffer.Clear();
            _log.Clear();
        };
        _gateEnable.CheckedChanged += (_, _) =>
        {
            _gateLevel.Enabled = _gateEnable.Checked;
            if (!_loading) OptionsChanged?.Invoke();
        };
        _gateLevel.ValueChanged += (_, _) =>
        {
            if (!_loading) OptionsChanged?.Invoke();
        };
        Controls.Add(_log);
        Controls.Add(_status);
        UpdateChrome();
    }

    public Control AttachHostBar(ComboBox vfoBox)
    {
        CaptionPluginChrome.StyleVfo(vfoBox);
        var bar = CaptionPluginChrome.CreateBar();
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(2, 1, 2, 0),
            Margin = Padding.Empty,
            BackColor = CaptionPluginChrome.BarBack
        };
        flow.Controls.AddRange([vfoBox, _analyze, _setup, _download, _clear, _gateEnable, _gateLevel]);
        bar.Controls.Add(flow);
        return bar;
    }

    public Dictionary<string, string> BuildOptions()
    {
        EnsureDefaults();
        _options["analyze"] = _analyzeOn.ToString();
        var engine = NeuroCaption.NormalizeEngine(_options.GetValueOrDefault("engine"));
        NeuroCaption.Put(_options, engine, "speechGateEnabled", _gateEnable.Checked.ToString());
        NeuroCaption.Put(_options, engine, "speechGate",
            ((int)_gateLevel.Value).ToString(CultureInfo.InvariantCulture));
        NeuroCaption.WriteActiveAliases(_options, engine);
        return new Dictionary<string, string>(_options, StringComparer.OrdinalIgnoreCase);
    }

    public void LoadOptions(IReadOnlyDictionary<string, string> options)
    {
        _loading = true;
        try
        {
            foreach (var pair in options)
                _options[pair.Key] = pair.Value;
            EnsureDefaults();
            _analyzeOn = IsTrue("analyze", false);
            var engine = NeuroCaption.NormalizeEngine(_options.GetValueOrDefault("engine"));
            _gateLevel.Value = NeuroCaption.GateLevel(NeuroCaption.Get(_options, engine, "speechGate",
                NeuroCaption.DefaultGate.ToString(CultureInfo.InvariantCulture)));
            _gateEnable.Checked = bool.TryParse(
                NeuroCaption.Get(_options, engine, "speechGateEnabled", "False"), out var gateOn) && gateOn;
            _gateLevel.Enabled = _gateEnable.Checked;
            UpdateChrome();
        }
        finally { _loading = false; }
    }

    public void ApplyResult(AfPluginResult result)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ApplyResult(result));
            return;
        }
        if (result.Kind.Equals("CAPTION", StringComparison.OrdinalIgnoreCase))
        {
            var display = result.Fields?.GetValueOrDefault("display", result.Text) ?? result.Text;
            CaptionText.AppendToLog(_buffer, display, IsTrue("showTime", false), IsTrue("autoWrap", true),
                result.TimestampUtc);
            if (_buffer.Length > 24_000) _buffer.Remove(0, _buffer.Length - 16_000);
            _log.Text = _buffer.ToString();
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            _status.Text = "Caption";
            _status.ForeColor = Color.FromArgb(170, 200, 214);
            return;
        }
        _status.Text = result.Text;
        _status.ForeColor = result.Kind.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(255, 140, 140)
            : Color.FromArgb(170, 200, 214);
    }

    private void OpenSetup()
    {
        using var setup = new NeuroCaptionSetupForm(_options);
        if (setup.ShowDialog(FindForm()) != DialogResult.OK) return;
        foreach (var pair in setup.Options)
            _options[pair.Key] = pair.Value;
        LoadOptions(_options);
        if (!_loading) OptionsChanged?.Invoke();
    }

    private async Task DownloadOnnxAsync()
    {
        if (_downloading) return;
        _downloading = true;
        _download.Enabled = false;
        var modelId = _options.GetValueOrDefault("onnxModel", SherpaCaptionModels.DefaultId);
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (IsDisposed || Disposing) return;
                _status.Text = text;
                _status.ForeColor = Color.FromArgb(170, 200, 214);
            });
            await SherpaCaptionModels.DownloadAsync(modelId, progress, CancellationToken.None);
            UpdateChrome();
            if (!_loading) OptionsChanged?.Invoke();
        }
        catch (Exception exception)
        {
            _status.Text = exception.GetBaseException().Message;
            _status.ForeColor = Color.FromArgb(255, 140, 140);
        }
        finally
        {
            _downloading = false;
            UpdateChrome();
        }
    }

    private void EnsureDefaults()
    {
        if (!_options.ContainsKey("engine")) _options["engine"] = NeuroCaption.Groq;
        _options["engine"] = NeuroCaption.NormalizeEngine(_options["engine"]);
        if (!_options.ContainsKey("language")) _options["language"] = "auto";
        if (!_options.ContainsKey("autoWrap")) _options["autoWrap"] = "True";
        if (!_options.ContainsKey("speechGate"))
            _options["speechGate"] = NeuroCaption.DefaultGate.ToString(CultureInfo.InvariantCulture);
        if (!_options.ContainsKey("speechGateEnabled")) _options["speechGateEnabled"] = "False";
        if (!_options.ContainsKey("host")) _options["host"] = LocalWhisperClient.DefaultHost;
        if (!_options.ContainsKey("port"))
            _options["port"] = LocalWhisperClient.DefaultPort.ToString(CultureInfo.InvariantCulture);
        if (!_options.ContainsKey("groqModel")) _options["groqModel"] = GroqWhisperClient.DefaultModel;
        if (!_options.ContainsKey("whisperModel")) _options["whisperModel"] = LocalWhisperClient.DefaultModel;
        if (!_options.ContainsKey("onnxModel"))
            _options["onnxModel"] = _options.GetValueOrDefault("modelId", SherpaCaptionModels.DefaultId);
        NeuroCaption.EnsureEngineOptions(_options);
    }

    private void UpdateChrome()
    {
        CaptionPluginChrome.PaintCaptionToggle(_analyze, _analyzeOn);
        var engine = NeuroCaption.NormalizeEngine(_options.GetValueOrDefault("engine"));
        var onnx = engine == NeuroCaption.Onnx;
        _download.Visible = onnx;
        _download.Enabled = onnx && !_downloading;
        if (_downloading) return;
        var label = engine switch
        {
            NeuroCaption.Whisper => $"{_options.GetValueOrDefault("host", LocalWhisperClient.DefaultHost)}:{_options.GetValueOrDefault("port", "8100")}",
            NeuroCaption.Onnx => SherpaCaptionModels.IsReady(_options.GetValueOrDefault("onnxModel", SherpaCaptionModels.DefaultId))
                ? $"ONNX {_options.GetValueOrDefault("onnxModel", SherpaCaptionModels.DefaultId)}"
                : "ONNX · GET model",
            _ => "Groq"
        };
        if (_analyzeOn)
            _status.Text = _gateEnable.Checked
                ? $"CAPTION ON · {label} · GATE"
                : $"CAPTION ON · {label} · GATE off";
        else
            _status.Text = $"CAPTION OFF · {label}";
        _status.ForeColor = Color.FromArgb(170, 200, 214);
    }

    private bool IsTrue(string key, bool fallback) =>
        _options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;
}
