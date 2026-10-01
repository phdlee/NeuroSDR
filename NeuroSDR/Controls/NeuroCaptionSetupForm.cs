using System.Drawing.Drawing2D;
using System.Globalization;
using NeuroSDR.Plugins.Caption;

namespace NeuroSDR.Controls;

internal sealed class NeuroCaptionSetupForm : Form
{
    private static readonly Color Bg = Color.FromArgb(14, 22, 30);
    private static readonly Color Card = Color.FromArgb(20, 34, 46);
    private static readonly Color Input = Color.FromArgb(8, 16, 24);
    private static readonly Color Line = Color.FromArgb(42, 68, 86);
    private static readonly Color Fg = Color.FromArgb(228, 238, 246);
    private static readonly Color Mute = Color.FromArgb(132, 162, 180);
    private static readonly Color Accent = Color.FromArgb(62, 118, 164);
    private static readonly Color AccentHot = Color.FromArgb(78, 138, 186);
    private static readonly Color Idle = Color.FromArgb(32, 46, 58);

    private readonly Button _engGroq = Pill("Groq");
    private readonly Button _engWhisper = Pill("Whisper");
    private readonly Button _engOnnx = Pill("ONNX");
    private readonly TextBox _groqKey = FieldBox(true);
    private readonly CheckBox _showGroqKey = Check("Show");
    private readonly ComboBox _groqModel = Drop(true);
    private readonly Label _quota = new()
    {
        AutoSize = false, Height = 56, Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 8.25f), ForeColor = Mute
    };
    private readonly TextBox _host = FieldBox(false);
    private readonly NumericUpDown _port = Number(1, 65_535, LocalWhisperClient.DefaultPort, 72);
    private readonly CheckBox _https = Check("HTTPS");
    private readonly TextBox _whisperKey = FieldBox(true);
    private readonly ComboBox _whisperModel = Drop(false);
    private readonly Button _whisperTest = ActionBtn("TEST", 68);
    private readonly Button _whisperModels = ActionBtn("MODELS", 78);
    private readonly ComboBox _onnxModel = Drop(true);
    private readonly Button _onnxGet = ActionBtn("GET MODEL", 108);
    private readonly Label _onnxHint = new()
    {
        AutoSize = false, Height = 36, Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 8.25f), ForeColor = Mute,
        Text = "tiny ≈ 104 MB · base ≈ 161 MB · small ≈ 500 MB · medium ≈ 1.5 GB (CPU, slower). GET downloads INT8 Whisper for this PC."
    };
    private readonly ComboBox _language = Drop(true);
    private readonly NumericUpDown _chunk = Number(4, 20, 8, 64);
    private readonly CheckBox _speechGateEnabled = Check("Use gate");
    private readonly NumericUpDown _speechGate = Number(1, 10, NeuroCaption.DefaultGate, 56);
    private readonly CheckBox _showTime = Check("Timestamp");
    private readonly CheckBox _autoWrap = Check("Break sentences  . ! ?");
    private readonly CheckBox _afTicker = Check("AF waterfall captions");
    private readonly ComboBox _translateEngine = Drop(true);
    private readonly ComboBox _translateTo = Drop(true);
    private readonly ComboBox _displayMode = Drop(true);
    private readonly Panel _groqCard = new();
    private readonly Panel _whisperCard = new();
    private readonly Panel _onnxCard = new();
    private readonly Panel _engineHost = new() { Dock = DockStyle.Top, Height = 132, BackColor = Card };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", 8.25f), ForeColor = Mute, Padding = new Padding(16, 0, 8, 0)
    };
    private string _engine = NeuroCaption.Groq;
    private bool _suspendEngineUi;

    public Dictionary<string, string> Options { get; }

    public NeuroCaptionSetupForm(IReadOnlyDictionary<string, string> current)
    {
        Options = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
        Text = "NeuroCaption SETUP";
        Size = new Size(668, 668);
        MinimumSize = new Size(640, 640);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.25f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _groqModel.Items.AddRange(["whisper-large-v3-turbo", "whisper-large-v3"]);
        _whisperModel.Items.AddRange([
            "Systran/faster-whisper-small", "Systran/faster-whisper-tiny", "Systran/faster-whisper-base",
            "Systran/faster-whisper-medium", "Systran/faster-whisper-large-v3"
        ]);
        foreach (var id in SherpaCaptionModels.Ids) _onnxModel.Items.Add(id);
        CaptionLanguages.Fill(_language, CaptionLanguages.Speech);
        CaptionLanguages.Fill(_translateTo, CaptionLanguages.Translate);
        _displayMode.Items.AddRange(["original", "translation", "both"]);
        _speechGate.Enabled = false;

        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(12, 24, 34) };
        header.Controls.Add(new Label
        {
            Text = "SETUP", AutoSize = true, Location = new Point(22, 16),
            Font = new Font("Segoe UI Semibold", 8f), ForeColor = Mute
        });
        header.Controls.Add(new Label
        {
            Text = "NeuroCaption", AutoSize = true, Location = new Point(20, 32),
            Font = new Font("Segoe UI Semibold", 18f), ForeColor = Fg
        });
        var pills = new TableLayoutPanel
        {
            Size = new Size(348, 38), ColumnCount = 3, BackColor = Color.FromArgb(8, 16, 24),
            Padding = new Padding(3)
        };
        pills.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        pills.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        pills.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));
        pills.Controls.Add(_engGroq, 0, 0);
        pills.Controls.Add(_engWhisper, 1, 0);
        pills.Controls.Add(_engOnnx, 2, 0);
        header.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fade = new LinearGradientBrush(header.ClientRectangle,
                Color.FromArgb(18, 36, 50), Color.FromArgb(10, 18, 26), LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(fade, header.ClientRectangle);
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        header.Resize += (_, _) => pills.Location = new Point(Math.Max(210, header.ClientSize.Width - pills.Width - 22), 28);
        header.Controls.Add(pills);

        BuildGroqCard();
        BuildWhisperCard();
        BuildOnnxCard();
        _engineHost.BackColor = Bg;
        _engineHost.Padding = new Padding(16, 10, 16, 4);
        _engineHost.Controls.Add(_groqCard);
        _engineHost.Controls.Add(_whisperCard);
        _engineHost.Controls.Add(_onnxCard);

        var shared = BuildSharedCard();
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(16, 8, 16, 8) };
        body.Controls.Add(shared);
        body.Controls.Add(_engineHost);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(12, 20, 28) };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        _status.Dock = DockStyle.None;
        _status.Location = new Point(8, 0);
        _status.Height = 56;
        var ok = ActionBtn("OK", 92);
        ok.Width = 92;
        ok.Height = 32;
        var cancel = ActionBtn("CANCEL", 92);
        cancel.BackColor = Idle;
        ok.Click += (_, _) => { CaptureOptions(); DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Resize += (_, _) =>
        {
            ok.Left = Math.Max(220, footer.ClientSize.Width - ok.Width - 16);
            cancel.Left = ok.Left - cancel.Width - 8;
            cancel.Top = ok.Top = (footer.Height - ok.Height) / 2;
            _status.Width = Math.Max(80, cancel.Left - 16);
            _status.Height = footer.Height;
        };
        footer.Controls.Add(_status);
        footer.Controls.Add(cancel);
        footer.Controls.Add(ok);

        _engGroq.Click += (_, _) => SetEngine(NeuroCaption.Groq);
        _engWhisper.Click += (_, _) => SetEngine(NeuroCaption.Whisper);
        _engOnnx.Click += (_, _) => SetEngine(NeuroCaption.Onnx);
        _showGroqKey.CheckedChanged += (_, _) => _groqKey.UseSystemPasswordChar = !_showGroqKey.Checked;
        _groqKey.TextChanged += (_, _) =>
        {
            if (!_suspendEngineUi) FillTranslateChoices();
        };
        _speechGateEnabled.CheckedChanged += (_, _) => _speechGate.Enabled = _speechGateEnabled.Checked;
        _chunk.ValueChanged += (_, _) => RefreshQuota();
        _translateEngine.SelectedIndexChanged += (_, _) =>
        {
            if (!_suspendEngineUi) SyncShowForTranslate();
            RefreshQuota();
        };
        _whisperTest.Click += async (_, _) => await TestWhisperAsync();
        _whisperModels.Click += async (_, _) => await RefreshWhisperModelsAsync();
        _onnxGet.Click += async (_, _) => await DownloadOnnxAsync();

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
        LoadCurrent();
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void BuildGroqCard()
    {
        StyleCard(_groqCard);
        var grid = Grid(3);
        AddGridRow(grid, 0, "API KEY", Pair(_groqKey, DockCheck(_showGroqKey, 72), 72));
        AddGridRow(grid, 1, "MODEL", _groqModel);
        AddGridRow(grid, 2, "", _quota);
        grid.RowStyles[2] = new RowStyle(SizeType.Absolute, 58);
        _groqCard.Controls.Add(grid);
        _groqCard.Controls.Add(CardTitle("GROQ CLOUD"));
    }

    private void BuildWhisperCard()
    {
        StyleCard(_whisperCard);
        _https.AutoSize = false;
        _https.Width = 78;
        _https.TextAlign = ContentAlignment.MiddleLeft;
        var grid = Grid(3);
        AddGridRow(grid, 0, "HOST", WhisperEndpointRow());
        AddGridRow(grid, 1, "KEY", _whisperKey);
        AddGridRow(grid, 2, "MODEL", Pair(_whisperModel, Row(_whisperTest, _whisperModels), 168));
        _whisperCard.Controls.Add(grid);
        _whisperCard.Controls.Add(CardTitle("WHISPER SERVER"));
    }

    private void BuildOnnxCard()
    {
        StyleCard(_onnxCard);
        var grid = Grid(2);
        AddGridRow(grid, 0, "MODEL", Pair(_onnxModel, _onnxGet, 116));
        AddGridRow(grid, 1, "", _onnxHint);
        _onnxCard.Controls.Add(grid);
        _onnxCard.Controls.Add(CardTitle("ONNX  ·  THIS PC"));
    }

    private Panel BuildSharedCard()
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Card };
        StyleCard(card);
        var grid = Grid(5);
        AddGridRow(grid, 0, "LANGUAGE", Pair(_language, Pair(Hint("CLIP SEC"), _chunk, 64), 210));
        AddGridRow(grid, 1, "GATE", Pair(DockCheck(_speechGateEnabled, 92), Pair(Hint("LEVEL"), _speechGate, 56), 168));
        AddGridRow(grid, 2, "DISPLAY", Row(_showTime, _autoWrap, _afTicker));
        AddGridRow(grid, 3, "TRANSLATE", TranslateRow());
        AddGridRow(grid, 4, "", new Label
        {
            Text = "Gate off sends every AF clip. Turn it on at level 2–3 for AM squelch.",
            Dock = DockStyle.Fill, ForeColor = Mute, Font = new Font("Segoe UI", 8.25f)
        });
        card.Controls.Add(grid);
        card.Controls.Add(CardTitle("CAPTION"));
        return card;
    }

    private Control WhisperEndpointRow()
    {
        var host = new Panel { Dock = DockStyle.Fill };
        var portWrap = new Panel { Dock = DockStyle.Right, Width = 128 };
        var portLabel = Hint("PORT");
        portLabel.AutoSize = false;
        portLabel.Dock = DockStyle.Left;
        portLabel.Width = 44;
        portLabel.Padding = new Padding(8, 6, 0, 0);
        _port.Dock = DockStyle.Fill;
        _port.Width = 80;
        portWrap.Controls.Add(_port);
        portWrap.Controls.Add(portLabel);
        _https.Dock = DockStyle.Right;
        _https.Width = 78;
        _host.Dock = DockStyle.Fill;
        host.Controls.Add(_host);
        host.Controls.Add(_https);
        host.Controls.Add(portWrap);
        return host;
    }

    private Control TranslateRow()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        _translateEngine.Dock = DockStyle.Fill;
        _translateTo.Dock = DockStyle.Fill;
        _displayMode.Dock = DockStyle.Fill;
        grid.Controls.Add(_translateEngine, 0, 0);
        grid.Controls.Add(FieldHint("INTO"), 1, 0);
        grid.Controls.Add(_translateTo, 2, 0);
        grid.Controls.Add(FieldHint("SHOW"), 3, 0);
        grid.Controls.Add(_displayMode, 4, 0);
        return grid;
    }

    private void LoadCurrent()
    {
        _suspendEngineUi = true;
        _groqKey.Text = Opt("apiKey");
        Select(_groqModel, First("groqModel", "model"), GroqWhisperClient.DefaultModel);
        _host.Text = string.IsNullOrWhiteSpace(Opt("host")) ? LocalWhisperClient.DefaultHost : Opt("host");
        if (int.TryParse(Opt("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            _port.Value = Math.Clamp(port, 1, 65_535);
        _https.Checked = IsTrue("https", false);
        _whisperKey.Text = Opt("whisperKey");
        SelectOrAdd(_whisperModel, First("whisperModel", "model"), LocalWhisperClient.DefaultModel);
        Select(_onnxModel, First("onnxModel", "modelId"), SherpaCaptionModels.DefaultId);
        NeuroCaption.EnsureEngineOptions(Options);
        SetEngine(NeuroCaption.NormalizeEngine(Opt("engine")));
        _suspendEngineUi = false;
        RefreshQuota();
    }

    private void CaptureOptions()
    {
        CaptureEngineFields(_engine);
        Options["engine"] = _engine;
        Options["apiKey"] = _groqKey.Text.Trim();
        Options["groqModel"] = _groqModel.SelectedItem?.ToString() ?? GroqWhisperClient.DefaultModel;
        Options["host"] = _host.Text.Trim();
        Options["port"] = ((int)_port.Value).ToString(CultureInfo.InvariantCulture);
        Options["https"] = _https.Checked.ToString();
        Options["whisperKey"] = _whisperKey.Text.Trim();
        Options["whisperModel"] = string.IsNullOrWhiteSpace(_whisperModel.Text)
            ? LocalWhisperClient.DefaultModel : _whisperModel.Text.Trim();
        Options["onnxModel"] = _onnxModel.SelectedItem?.ToString() ?? SherpaCaptionModels.DefaultId;
        Options["modelId"] = Options["onnxModel"];
        NeuroCaption.WriteActiveAliases(Options, _engine);
    }

    private void CaptureEngineFields(string engine)
    {
        engine = NeuroCaption.NormalizeEngine(engine);
        var translate = _translateEngine.SelectedItem?.ToString() ?? "off";
        var show = _displayMode.SelectedItem?.ToString() ?? "original";
        Options[NeuroCaption.Prefixed(engine, "language")] = CaptionLanguages.SelectedCode(_language, "auto");
        Options[NeuroCaption.Prefixed(engine, "chunkSeconds")] =
            ((int)_chunk.Value).ToString(CultureInfo.InvariantCulture);
        Options[NeuroCaption.Prefixed(engine, "speechGate")] =
            ((int)_speechGate.Value).ToString(CultureInfo.InvariantCulture);
        Options[NeuroCaption.Prefixed(engine, "speechGateEnabled")] = _speechGateEnabled.Checked.ToString();
        Options[NeuroCaption.Prefixed(engine, "showTime")] = _showTime.Checked.ToString();
        Options[NeuroCaption.Prefixed(engine, "autoWrap")] = _autoWrap.Checked.ToString();
        Options[NeuroCaption.Prefixed(engine, "afTicker")] = _afTicker.Checked.ToString();
        NeuroCaption.Put(Options, engine, "translateEngine", translate);
        NeuroCaption.Put(Options, engine, "translateTo", CaptionLanguages.SelectedCode(_translateTo, "ko"));
        NeuroCaption.Put(Options, engine, "displayMode", NeuroCaption.DefaultDisplayMode(translate, show));
    }

    private void ApplyEngineFields(string engine)
    {
        engine = NeuroCaption.NormalizeEngine(engine);
        var groq = engine == NeuroCaption.Groq;
        _chunk.Minimum = groq ? 6 : 4;
        _chunk.Maximum = 20;
        var chunk = NeuroCaption.ChunkSeconds(Options, engine);
        _chunk.Value = Math.Clamp(chunk, _chunk.Minimum, _chunk.Maximum);
        CaptionLanguages.SelectCode(_language, NeuroCaption.Get(Options, engine, "language", "auto"), "auto");
        var gate = NeuroCaption.GateLevel(NeuroCaption.Get(Options, engine, "speechGate",
            NeuroCaption.DefaultGate.ToString(CultureInfo.InvariantCulture)));
        var gateOn = bool.TryParse(NeuroCaption.Get(Options, engine, "speechGateEnabled", "False"), out var on) && on;
        if (!gateOn && gate == 5) gate = NeuroCaption.DefaultGate;
        _speechGate.Value = gate;
        _speechGateEnabled.Checked = gateOn;
        _speechGate.Enabled = gateOn;
        _showTime.Checked = IsEngineTrue(engine, "showTime", false);
        _autoWrap.Checked = IsEngineTrue(engine, "autoWrap", true);
        _afTicker.Checked = IsEngineTrue(engine, "afTicker", false);
        CaptionLanguages.SelectCode(_translateTo, NeuroCaption.Get(Options, engine, "translateTo", "ko"), "ko");
        FillTranslateChoices(NeuroCaption.Get(Options, engine, "translateEngine", "off"));
        var translate = _translateEngine.SelectedItem?.ToString() ?? "off";
        Select(_displayMode, NeuroCaption.DefaultDisplayMode(translate,
            NeuroCaption.Get(Options, engine, "displayMode", "")), "both");
    }

    private bool IsEngineTrue(string engine, string name, bool fallback) =>
        bool.TryParse(NeuroCaption.Get(Options, engine, name, fallback.ToString()), out var value) ? value : fallback;

    private void SetEngine(string engine)
    {
        engine = NeuroCaption.NormalizeEngine(engine);
        if (!_suspendEngineUi && !engine.Equals(_engine, StringComparison.OrdinalIgnoreCase))
            CaptureEngineFields(_engine);
        _engine = engine;
        PaintPill(_engGroq, _engine == NeuroCaption.Groq);
        PaintPill(_engWhisper, _engine == NeuroCaption.Whisper);
        PaintPill(_engOnnx, _engine == NeuroCaption.Onnx);
        _groqCard.Visible = _engine == NeuroCaption.Groq;
        _whisperCard.Visible = _engine == NeuroCaption.Whisper;
        _onnxCard.Visible = _engine == NeuroCaption.Onnx;
        _engineHost.Height = _engine switch
        {
            NeuroCaption.Groq => 168,
            NeuroCaption.Whisper => 168,
            _ => 128
        };
        ApplyEngineFields(_engine);
        RefreshQuota();
        _status.ForeColor = Mute;
        _status.Text = _engine switch
        {
            NeuroCaption.Whisper => "OpenAI-compatible /v1/audio/transcriptions · TEST checks the server",
            NeuroCaption.Onnx => "Local INT8 Whisper · GET MODEL if files are missing",
            _ => "Groq Cloud Whisper · API key stays on this PC"
        };
    }

    private void FillTranslateChoices(string? prefer = null)
    {
        var previous = prefer
            ?? _translateEngine.SelectedItem?.ToString()
            ?? NeuroCaption.Get(Options, _engine, "translateEngine", "off");
        var groqOk = _engine == NeuroCaption.Groq || _groqKey.Text.Trim().Length > 0;
        _translateEngine.Items.Clear();
        _translateEngine.Items.Add("off");
        if (groqOk) _translateEngine.Items.Add("groq");
        if (_engine == NeuroCaption.Whisper) _translateEngine.Items.Add("whisper");
        _translateEngine.Items.Add("mymemory");
        if (previous.Equals("groq", StringComparison.OrdinalIgnoreCase) && !groqOk)
            previous = "off";
        if (previous.Equals("whisper", StringComparison.OrdinalIgnoreCase) && _engine != NeuroCaption.Whisper)
            previous = groqOk ? "groq" : "off";
        Select(_translateEngine, previous, "off");
    }

    private void SyncShowForTranslate()
    {
        var translate = _translateEngine.SelectedItem?.ToString() ?? "off";
        var show = _displayMode.SelectedItem?.ToString() ?? "original";
        var next = NeuroCaption.DefaultDisplayMode(translate, show);
        if (!next.Equals(show, StringComparison.OrdinalIgnoreCase))
            Select(_displayMode, next, next);
    }

    private void RefreshQuota()
    {
        _quota.Text = _engine == NeuroCaption.Groq
            ? GroqCaptionQuota.Hint((int)_chunk.Value, _translateEngine.SelectedItem?.ToString() ?? "off")
            : "";
    }

    private async Task TestWhisperAsync()
    {
        CaptureOptions();
        _status.ForeColor = Mute;
        _status.Text = "Checking /v1/models …";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var models = await LocalWhisperClient.ListModelsAsync(
                Options["host"], int.Parse(Options["port"], CultureInfo.InvariantCulture),
                IsTrue("https", false), Options.GetValueOrDefault("whisperKey"), cts.Token);
            _status.Text = models.Length == 0 ? "Connected · no model ids" : $"Connected · {models.Length} models · {models[0]}";
            _status.ForeColor = Color.FromArgb(168, 214, 196);
        }
        catch (Exception exception)
        {
            _status.Text = exception.GetBaseException().Message;
            _status.ForeColor = Color.FromArgb(255, 150, 150);
        }
    }

    private async Task RefreshWhisperModelsAsync()
    {
        CaptureOptions();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await LocalWhisperClient.ListModelsAsync(
                Options["host"], int.Parse(Options["port"], CultureInfo.InvariantCulture),
                IsTrue("https", false), Options.GetValueOrDefault("whisperKey"), cts.Token);
            var selected = _whisperModel.Text;
            _whisperModel.Items.Clear();
            foreach (var id in models) _whisperModel.Items.Add(id);
            SelectOrAdd(_whisperModel, selected, models.FirstOrDefault() ?? LocalWhisperClient.DefaultModel);
            _status.Text = models.Length == 0 ? "No models returned" : $"Loaded {models.Length} models";
            _status.ForeColor = Mute;
        }
        catch (Exception exception)
        {
            _status.Text = exception.GetBaseException().Message;
            _status.ForeColor = Color.FromArgb(255, 150, 150);
        }
    }

    private async Task DownloadOnnxAsync()
    {
        CaptureOptions();
        _onnxGet.Enabled = false;
        try
        {
            var progress = new Progress<string>(text =>
            {
                _status.Text = text;
                _status.ForeColor = Mute;
            });
            await SherpaCaptionModels.DownloadAsync(Options["onnxModel"], progress, CancellationToken.None);
            _status.Text = "ONNX model ready";
            _status.ForeColor = Color.FromArgb(168, 214, 196);
        }
        catch (Exception exception)
        {
            _status.Text = exception.GetBaseException().Message;
            _status.ForeColor = Color.FromArgb(255, 150, 150);
        }
        finally { _onnxGet.Enabled = true; }
    }

    private string Opt(string key) => Options.TryGetValue(key, out var value) ? value : "";

    private string First(string a, string b)
    {
        var left = Opt(a);
        return string.IsNullOrWhiteSpace(left) ? Opt(b) : left;
    }

    private bool IsTrue(string key, bool fallback) =>
        Options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static void Select(ComboBox box, string? value, string fallback)
    {
        var wanted = string.IsNullOrWhiteSpace(value) ? fallback : value;
        var index = box.FindStringExact(wanted);
        if (index < 0) index = box.FindStringExact(fallback);
        box.SelectedIndex = index >= 0 ? index : 0;
    }

    private static void SelectOrAdd(ComboBox box, string? value, string fallback)
    {
        var wanted = string.IsNullOrWhiteSpace(value) ? fallback : value;
        var index = box.FindStringExact(wanted);
        if (index < 0)
        {
            box.Items.Insert(0, wanted);
            index = 0;
        }
        box.SelectedIndex = index;
    }

    private static Label CardTitle(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(14, 0, 0, 0), Font = new Font("Segoe UI Semibold", 8f),
        ForeColor = Color.FromArgb(120, 178, 214), BackColor = Color.FromArgb(16, 30, 42)
    };

    private static CheckBox DockCheck(CheckBox box, int width)
    {
        box.AutoSize = false;
        box.Width = width;
        box.Dock = DockStyle.Fill;
        box.TextAlign = ContentAlignment.MiddleLeft;
        return box;
    }

    private static void StyleCard(Panel card)
    {
        card.Dock = DockStyle.Fill;
        card.BackColor = Card;
        card.Padding = new Padding(0);
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = card.ClientRectangle;
            r.Width -= 1;
            r.Height -= 1;
            using var path = Round(r, 8);
            using var fill = new SolidBrush(Card);
            using var pen = new Pen(Line);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(pen, path);
        };
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static TableLayoutPanel Grid(int rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = rows, Padding = new Padding(12, 8, 12, 8)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        return grid;
    }

    private static void AddGridRow(TableLayoutPanel grid, int row, string label, Control control)
    {
        if (!string.IsNullOrEmpty(label))
        {
            grid.Controls.Add(new Label
            {
                Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Mute, Font = new Font("Segoe UI Semibold", 8f)
            }, 0, row);
        }
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 3, 0, 3);
        grid.Controls.Add(control, 1, row);
    }

    private static Control Pair(Control left, Control right, int rightWidth)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        right.Dock = DockStyle.Right;
        right.Width = rightWidth;
        left.Dock = DockStyle.Fill;
        host.Controls.Add(left);
        host.Controls.Add(right);
        return host;
    }

    private static Control Row(params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, AutoSize = false
        };
        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 4, 12, 0);
            flow.Controls.Add(control);
        }
        return flow;
    }

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Mute, Font = new Font("Segoe UI Semibold", 7.5f),
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 6, 6, 0)
    };

    private static Label FieldHint(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Mute, Font = new Font("Segoe UI Semibold", 7.5f), Padding = new Padding(6, 0, 0, 0)
    };

    private static TextBox FieldBox(bool password) => new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Input,
        ForeColor = Fg,
        UseSystemPasswordChar = password
    };

    private static ComboBox Drop(bool listOnly) => new()
    {
        DropDownStyle = listOnly ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown,
        FlatStyle = FlatStyle.Flat,
        BackColor = Input,
        ForeColor = Fg
    };

    private static NumericUpDown Number(decimal min, decimal max, decimal value, int width) => new()
    {
        Minimum = min, Maximum = max, Value = value, Width = width,
        BackColor = Input, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle
    };

    private static CheckBox Check(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Fg, FlatStyle = FlatStyle.Flat,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Button Pill(string text)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = Fg,
            BackColor = Idle, Margin = new Padding(2, 0, 2, 0), Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private static void PaintPill(Button button, bool on)
    {
        button.BackColor = on ? Accent : Idle;
        button.ForeColor = on ? Color.White : Mute;
        button.FlatAppearance.MouseOverBackColor = on ? AccentHot : Color.FromArgb(42, 58, 72);
    }

    private static Button ActionBtn(string text, int width)
    {
        var button = new Button
        {
            Text = text, Width = width, Height = 28, FlatStyle = FlatStyle.Flat,
            BackColor = Accent, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 8f),
            Margin = new Padding(6, 0, 0, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = AccentHot;
        return button;
    }
}
