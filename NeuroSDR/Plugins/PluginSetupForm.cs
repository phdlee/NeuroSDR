using NeuroSDR.Audio;
using NeuroSDR.Controls;
using NeuroSDR.Core;
using NeuroSDR.Settings;

namespace NeuroSDR.Plugins;

internal sealed class PluginSetupForm : Form
{
    private readonly ComboBox _spectrum = new();
    private readonly ComboBox _waterfall = new();
    private readonly Label _description = new();
    private readonly string _loadErrors;
    private readonly AppSettings _settings;
    private readonly CheckBox _cwLower = new();
    private readonly CheckBox[] _audioEnabled = [new(), new()];
    private readonly ComboBox[] _audioDevice = [new(), new()];
    private readonly CheckedListBox _afPlugins = new();
    private readonly FlowLayoutPanel _afPluginInstances = new();
    private readonly List<AfPluginSetupRow> _afPluginRows = [];
    private readonly CheckedListBox _iqPlugins = new();
    private readonly CheckBox _singleActiveAfPlugin = new();
    private readonly NumericUpDown _afDisplayWidth = new();
    private readonly NumericUpDown _afDisplayHeight = new();
    private readonly NumericUpDown _tunerFrequencyOffset = new();
    private readonly ComboBox _captureRate = new();
    private readonly ComboBox _displayFps = new();
    private readonly ComboBox _fftQuality = new();
    private readonly CheckBox _pipelineDiagnostics = new();
    private readonly CheckBox _rfAmplifier = new();
    private readonly CheckBox _hardwareAgc = new();
    private readonly CheckBox _sampleRateGainCompensation = new();
    private readonly CheckBox _extendedAfAgcRange = new();
    private readonly CheckBox _webRemoteEnabled = new();
    private readonly NumericUpDown _webRemotePort = new();
    private readonly CheckBox _webRemoteBindAll = new();
    private readonly TextBox _webRemoteToken = new();
    private readonly TextBox _rtlTcpEndpoint = new();
    private readonly CheckBox _voiceGuidance = new();
    private readonly ISampleSource _source;

    public PluginSelection Selection { get; private set; }
    public bool ResetRequested { get; private set; }
    public bool WebSdrDirectoryChanged { get; private set; }

    public PluginSetupForm(DisplayPluginCatalog catalog, AfPluginCatalog afCatalog, IqPluginCatalog iqCatalog,
        PluginSelection current, AppSettings settings, IReadOnlyList<WaveOutDeviceInfo> devices, ISampleSource source)
    {
        _settings = settings;
        _source = source;
        var iqErrors = iqCatalog.Errors.Count == 0 ? string.Empty : $" | IQ: {string.Join(" | ", iqCatalog.Errors)}";
        _loadErrors = catalog.Errors.Count == 0 && iqCatalog.Errors.Count == 0
            ? string.Empty
            : $"\r\nLoad errors: {string.Join(" | ", catalog.Errors)}{iqErrors}";
        Selection = current;
        Text = "NeuroSDR SETUP";
        Size = new Size(590, 490);
        MinimumSize = new Size(520, 450);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(18, 31, 42);
        ForeColor = Color.FromArgb(220, 231, 239);
        Font = new Font("Segoe UI", 9f);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 7) };
        var pluginPage = new TabPage("Plugin") { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(18) };
        BuildPluginPage(pluginPage, catalog, current);
        tabs.TabPages.Add(pluginPage);
        var audioPage = new TabPage("Audio") { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(18) };
        BuildAudioPage(audioPage, devices);
        tabs.TabPages.Add(audioPage);
        var afPage = new TabPage("AF Plugins") { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(18) };
        BuildAfPluginInstancePage(afPage, afCatalog);
        tabs.TabPages.Add(afPage);
        var iqPage = new TabPage("IQ Plugins") { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(18) };
        BuildIqPluginPage(iqPage, iqCatalog);
        tabs.TabPages.Add(iqPage);
        var generalPage = new TabPage("General") { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(18) };
        BuildGeneralPage(generalPage);
        tabs.TabPages.Add(generalPage);
        var performancePage = new TabPage("RF / Performance") { BackColor = BackColor, ForeColor = ForeColor, Padding = new Padding(18) };
        BuildPerformancePage(performancePage);
        tabs.TabPages.Add(performancePage);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8), BackColor = Color.FromArgb(12, 24, 33)
        };
        var apply = MakeButton("APPLY", Color.FromArgb(39, 107, 145));
        var cancel = MakeButton("CANCEL", Color.FromArgb(70, 78, 91));
        apply.Click += (_, _) =>
        {
            Selection = new PluginSelection(((PluginItem)_spectrum.SelectedItem!).Id, ((PluginItem)_waterfall.SelectedItem!).Id);
            _settings.CwLowerSide = _cwLower.Checked;
            ApplyAudioSettings(0, _settings.Audio1);
            ApplyAudioSettings(1, _settings.Audio2);
            _settings.AfPluginInstances = _afPluginRows.Select(row => row.ToSettings()).ToList();
            _settings.EnabledAfPluginIds = _settings.AfPluginInstances.Select(instance => instance.PluginId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _settings.AfPluginVfoRoutes = _settings.AfPluginInstances.ToDictionary(
                instance => instance.InstanceId, instance => instance.VfoId, StringComparer.OrdinalIgnoreCase);
            _settings.EnabledIqPluginIds = _iqPlugins.CheckedItems.Cast<AfPluginItem>().Select(item => item.Id).ToList();
            _settings.SingleActiveAfPlugin = _singleActiveAfPlugin.Checked;
            _settings.AfPluginDisplayWidth = (int)_afDisplayWidth.Value;
            _settings.AfPluginDisplayHeight = (int)_afDisplayHeight.Value;
            _settings.TunerFrequencyOffsetHz = (int)_tunerFrequencyOffset.Value;
            if (_source is IConfigurableSampleRateSource)
                _settings.HardwareSampleRates[_source.Name] = ((RateItem)_captureRate.SelectedItem!).Rate;
            _settings.RfDisplayFramesPerSecond = (int)_displayFps.SelectedItem!;
            _settings.RfFftQuality = _fftQuality.SelectedIndex;
            _settings.PipelineDiagnosticsEnabled = _pipelineDiagnostics.Checked;
            if (_source is IRfAmplifierSampleSource) _settings.HackRfAmplifierEnabled = _rfAmplifier.Checked;
            if (_source is IHardwareAgcSampleSource) _settings.SdrplayHardwareAgcEnabled = _hardwareAgc.Checked;
            if (_source is ISampleRateGainCompensationSource)
                _settings.SdrplaySampleRateGainCompensationEnabled = _sampleRateGainCompensation.Checked;
            _settings.ExtendedAfAgcRangeEnabled = _extendedAfAgcRange.Checked;
            _settings.WebRemoteEnabled = _webRemoteEnabled.Checked;
            _settings.WebRemotePort = (int)_webRemotePort.Value;
            _settings.WebRemoteBindAllInterfaces = _webRemoteBindAll.Checked;
            _settings.WebRemoteAccessToken = _webRemoteToken.Text.Trim();
            _settings.RtlTcpEndpoint = string.IsNullOrWhiteSpace(_rtlTcpEndpoint.Text)
                ? "127.0.0.1:1234" : _rtlTcpEndpoint.Text.Trim();
            _settings.VoiceGuidanceEnabled = _voiceGuidance.Checked;
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([apply, cancel]);
        Controls.Add(tabs);
        Controls.Add(buttons);
    }

    private void BuildPerformancePage(Control page)
    {
        var rateLabel = MakeLabel($"CAPTURE BANDWIDTH / SAMPLE RATE · {_source.Name}", 12, 14);
        _captureRate.DropDownStyle = ComboBoxStyle.DropDownList;
        _captureRate.Location = new Point(12, 40);
        _captureRate.Width = 210;
        if (_source is IConfigurableSampleRateSource configurable)
        {
            var selectedRate = _settings.HardwareSampleRates.TryGetValue(_source.Name, out var saved)
                ? saved : configurable.ConfiguredSampleRate;
            foreach (var rate in configurable.SupportedSampleRates)
                _captureRate.Items.Add(new RateItem(rate));
            _captureRate.SelectedIndex = Enumerable.Range(0, _captureRate.Items.Count)
                .FirstOrDefault(index => ((RateItem)_captureRate.Items[index]!).Rate == selectedRate, 0);
        }
        else
        {
            _captureRate.Items.Add(new RateItem(_source.SampleRate));
            _captureRate.SelectedIndex = 0;
            _captureRate.Enabled = false;
        }

        var rateNote = new Label
        {
            Text = "2 MHz is recommended for continuous decoding. Wider rates increase CPU, USB and memory load.\r\n" +
                   "Changing this option restarts the current receiver.",
            Location = new Point(12, 72), Size = new Size(510, 38), ForeColor = Color.FromArgb(151, 181, 198)
        };
        var fpsLabel = MakeLabel("RF SPECTRUM / WATERFALL UPDATE", 12, 122);
        _displayFps.DropDownStyle = ComboBoxStyle.DropDownList;
        _displayFps.Location = new Point(12, 148);
        _displayFps.Width = 120;
        foreach (var fps in new[] { 5, 10, 15, 20, 30 }) _displayFps.Items.Add(fps);
        _displayFps.SelectedItem = new[] { 5, 10, 15, 20, 30 }
            .OrderBy(value => Math.Abs(value - _settings.RfDisplayFramesPerSecond)).First();
        var fpsSuffix = MakeLabel("frames / second", 140, 152);

        var qualityLabel = MakeLabel("FFT RESOLUTION", 12, 188);
        _fftQuality.DropDownStyle = ComboBoxStyle.DropDownList;
        _fftQuality.Location = new Point(12, 214);
        _fftQuality.Width = 210;
        _fftQuality.Items.AddRange(["LOW · least CPU", "BALANCED", "HIGH · fine zoom"]);
        _fftQuality.SelectedIndex = Math.Clamp(_settings.RfFftQuality, 0, 2);
        var qualityNote = new Label
        {
            Text = "Display settings never reduce audio or decoder sample quality.",
            Location = new Point(12, 248), Size = new Size(510, 20), ForeColor = Color.FromArgb(238, 151, 48)
        };
        _pipelineDiagnostics.Text = "RECORD FULL UI AUDIO PIPELINE DIAGNOSTICS";
        _pipelineDiagnostics.Checked = _settings.PipelineDiagnosticsEnabled;
        _pipelineDiagnostics.AutoSize = true;
        _rfAmplifier.Text = "HACKRF RF AMP (+14 dB · OFF recommended)";
        _rfAmplifier.Checked = _settings.HackRfAmplifierEnabled;
        _rfAmplifier.AutoSize = true;
        _rfAmplifier.Location = new Point(12, 274);
        _rfAmplifier.Enabled = _source is IRfAmplifierSampleSource;
        _rfAmplifier.Visible = _source is IRfAmplifierSampleSource;
        _rfAmplifier.ForeColor = Color.FromArgb(238, 151, 48);
        _hardwareAgc.Text = "SDRPLAY HARDWARE AGC (overload protection; may reduce weak signals)";
        _hardwareAgc.Checked = _settings.SdrplayHardwareAgcEnabled;
        _hardwareAgc.AutoSize = true;
        _hardwareAgc.Location = new Point(12, 274);
        _hardwareAgc.Enabled = _source is IHardwareAgcSampleSource;
        _hardwareAgc.Visible = _source is IHardwareAgcSampleSource;
        _hardwareAgc.ForeColor = Color.FromArgb(238, 151, 48);

        var optionY = _source is IRfAmplifierSampleSource or IHardwareAgcSampleSource ? 298 : 274;
        _sampleRateGainCompensation.Text = "SAMPLE RATE GAIN COMPENSATION (RSP1 WIDE CAPTURE)";
        _sampleRateGainCompensation.Checked = _settings.SdrplaySampleRateGainCompensationEnabled;
        _sampleRateGainCompensation.AutoSize = true;
        _sampleRateGainCompensation.Location = new Point(12, optionY);
        _sampleRateGainCompensation.Enabled = _source is ISampleRateGainCompensationSource;
        _sampleRateGainCompensation.Visible = _source is ISampleRateGainCompensationSource;
        _sampleRateGainCompensation.ForeColor = Color.FromArgb(238, 151, 48);
        if (_sampleRateGainCompensation.Visible) optionY += 24;

        _extendedAfAgcRange.Text = "EXTENDED AF AGC RANGE (WEAK-SIGNAL BOOST)";
        _extendedAfAgcRange.Checked = _settings.ExtendedAfAgcRangeEnabled;
        _extendedAfAgcRange.AutoSize = true;
        _extendedAfAgcRange.Location = new Point(12, optionY);
        _extendedAfAgcRange.ForeColor = Color.FromArgb(238, 151, 48);
        optionY += 24;

        _pipelineDiagnostics.Location = new Point(12, optionY);
        _pipelineDiagnostics.ForeColor = Color.FromArgb(238, 151, 48);
        var diagnosticsNote = new Label
        {
            Text = $"One CSV row/second. Folder: {Diagnostics.UiPipelineTrace.DirectoryPath}",
            Location = new Point(12, optionY + 24), Size = new Size(510, 34), ForeColor = Color.FromArgb(151, 181, 198)
        };
        page.Controls.AddRange([rateLabel, _captureRate, rateNote, fpsLabel, _displayFps, fpsSuffix,
            qualityLabel, _fftQuality, qualityNote, _rfAmplifier, _hardwareAgc, _sampleRateGainCompensation,
            _extendedAfAgcRange, _pipelineDiagnostics, diagnosticsNote]);
    }

    private void BuildAfPluginInstancePage(Control page, AfPluginCatalog catalog)
    {
        var title = MakeLabel("AF PLUGIN INSTANCES", 12, 12);
        _afPluginInstances.Location = new Point(12, 36);
        _afPluginInstances.Size = new Size(510, 174);
        _afPluginInstances.AutoScroll = true;
        _afPluginInstances.FlowDirection = FlowDirection.TopDown;
        _afPluginInstances.WrapContents = false;
        _afPluginInstances.BackColor = Color.FromArgb(8, 21, 30);
        DarkNativeTheme.Apply(_afPluginInstances);
        var add = MakeButton("+ ADD", Color.FromArgb(39, 107, 145));
        add.Location = new Point(12, 216);
        add.Size = new Size(82, 27);
        add.Click += (_, _) => AddAfPluginRow(catalog, new AfPluginInstanceSettings());
        foreach (var instance in _settings.AfPluginInstances)
            AddAfPluginRow(catalog, instance.Clone());
        var sizeLabel = MakeLabel("DISPLAY SIZE (PX)", 112, 220);
        ConfigureSizeInput(_afDisplayWidth, 232, 216, 560, 900, _settings.AfPluginDisplayWidth);
        ConfigureSizeInput(_afDisplayHeight, 330, 216, 200, 700, _settings.AfPluginDisplayHeight);
        _singleActiveAfPlugin.Text = "RUN ONLY THE SELECTED AF PLUGIN TAB";
        _singleActiveAfPlugin.Checked = _settings.SingleActiveAfPlugin;
        _singleActiveAfPlugin.AutoSize = true;
        _singleActiveAfPlugin.Location = new Point(12, 252);
        _singleActiveAfPlugin.ForeColor = Color.FromArgb(238, 151, 48);
        var multiply = new Label { Text = "×", AutoSize = true, Location = new Point(316, 220), ForeColor = ForeColor };
        var note = new Label
        {
            Text = "Add the same decoder more than once. Each row owns an independent decoder and input VFO.\r\n" +
                   "FT8 and FT4 are separate variants of the FTX decoder.\r\n" +
                   "AF Record has no tab; it appears in Extends Area (left of spectrum).",
            Location = new Point(12, 278), Size = new Size(510, 42), ForeColor = Color.FromArgb(151, 181, 198)
        };
        page.Controls.AddRange([title, _afPluginInstances, add, sizeLabel, _afDisplayWidth, multiply,
            _afDisplayHeight, _singleActiveAfPlugin, note]);
    }

    private void AddAfPluginRow(AfPluginCatalog catalog, AfPluginInstanceSettings instance)
    {
        var row = new AfPluginSetupRow(instance.InstanceId);
        foreach (var registration in catalog.Plugins)
        {
            if (registration.Info.Id.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase))
            {
                row.Plugin.Items.Add(new AfPluginChoice(registration.Info.Id, "FT8", "FTX Decoder · FT8"));
                row.Plugin.Items.Add(new AfPluginChoice(registration.Info.Id, "FT4", "FTX Decoder · FT4"));
            }
            else row.Plugin.Items.Add(new AfPluginChoice(registration.Info.Id, string.Empty, registration.Info.Name));
        }
        row.Plugin.SelectedItem = row.Plugin.Items.Cast<AfPluginChoice>().FirstOrDefault(choice =>
            choice.PluginId.Equals(instance.PluginId, StringComparison.OrdinalIgnoreCase) &&
            (!choice.PluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase) ||
             choice.Variant.Equals(instance.Variant, StringComparison.OrdinalIgnoreCase))) ?? row.Plugin.Items[0];
        row.Vfo.Items.Add(new SetupVfoItem("main", "MAIN VFO"));
        foreach (var sub in _settings.SubVfos) row.Vfo.Items.Add(new SetupVfoItem(sub.Id, sub.Name));
        row.Vfo.SelectedItem = row.Vfo.Items.Cast<SetupVfoItem>().FirstOrDefault(item =>
            item.Id.Equals(instance.VfoId, StringComparison.OrdinalIgnoreCase)) ?? row.Vfo.Items[0];
        row.Remove.Click += (_, _) =>
        {
            _afPluginRows.Remove(row);
            _afPluginInstances.Controls.Remove(row.Panel);
            row.Panel.Dispose();
        };
        _afPluginRows.Add(row);
        _afPluginInstances.Controls.Add(row.Panel);
    }

    private void BuildAfPluginPage(Control page, AfPluginCatalog catalog)
    {
        var title = MakeLabel("AF INPUT / OUTPUT PLUGINS", 12, 12);
        _afPlugins.Location = new Point(12, 36);
        _afPlugins.Size = new Size(510, 112);
        _afPlugins.CheckOnClick = true;
        _afPlugins.BackColor = Color.FromArgb(8, 21, 30);
        _afPlugins.ForeColor = ForeColor;
        foreach (var registration in catalog.Plugins)
        {
            var item = new AfPluginItem(registration.Info.Id,
                $"{registration.Info.Name}  [{(registration.Info.IsBuiltIn ? "Built-in" : "External")}]");
            var index = _afPlugins.Items.Add(item);
            _afPlugins.SetItemChecked(index, _settings.EnabledAfPluginIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase));
        }
        var sizeLabel = MakeLabel("DISPLAY SIZE (PX)", 12, 160);
        ConfigureSizeInput(_afDisplayWidth, 132, 156, 560, 900, _settings.AfPluginDisplayWidth);
        ConfigureSizeInput(_afDisplayHeight, 230, 156, 200, 700, _settings.AfPluginDisplayHeight);
        _singleActiveAfPlugin.Text = "RUN ONLY THE SELECTED AF PLUGIN TAB";
        _singleActiveAfPlugin.Checked = _settings.SingleActiveAfPlugin;
        _singleActiveAfPlugin.AutoSize = true;
        _singleActiveAfPlugin.Location = new Point(12, 188);
        _singleActiveAfPlugin.ForeColor = Color.FromArgb(238, 151, 48);
        var multiply = new Label { Text = "×", AutoSize = true, Location = new Point(216, 160), ForeColor = ForeColor };
        var note = new Label
        {
            Text = "When enabled, changing the display tab also changes the active decoder.\r\nDecoder-specific options are controlled in each tab header.",
            Location = new Point(12, 211), Size = new Size(510, 38), ForeColor = Color.FromArgb(151, 181, 198)
        };
        page.Controls.AddRange([title, _afPlugins, sizeLabel, _afDisplayWidth, multiply, _afDisplayHeight, _singleActiveAfPlugin, note]);
    }

    private void BuildIqPluginPage(Control page, IqPluginCatalog catalog)
    {
        var title = MakeLabel("IQ INPUT / OUTPUT PLUGINS", 12, 12);
        _iqPlugins.Location = new Point(12, 36);
        _iqPlugins.Size = new Size(510, 160);
        _iqPlugins.CheckOnClick = true;
        _iqPlugins.BackColor = Color.FromArgb(8, 21, 30);
        _iqPlugins.ForeColor = ForeColor;
        foreach (var registration in catalog.Plugins)
        {
            var caps = registration.Info.Capabilities;
            var flags = string.Join("+", new[]
            {
                caps.HasFlag(IqPluginCapabilities.IqInput) ? "IN" : null,
                caps.HasFlag(IqPluginCapabilities.IqOutput) ? "OUT" : null
            }.Where(s => s is not null));
            var item = new AfPluginItem(registration.Info.Id,
                $"{registration.Info.Name}  [{(registration.Info.IsBuiltIn ? "Built-in" : "External")}]  {flags}");
            var index = _iqPlugins.Items.Add(item);
            _iqPlugins.SetItemChecked(index, _settings.EnabledIqPluginIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase));
        }
        var note = new Label
        {
            Text = "IQ plugins run on complex samples before demodulation.\r\n" +
                   "OUT plugins may replace the IQ stream for later stages when configured.\r\n" +
                   "See iq-plugin.md for the external contract.",
            Location = new Point(12, 210), Size = new Size(510, 54), ForeColor = Color.FromArgb(151, 181, 198)
        };
        page.Controls.AddRange([title, _iqPlugins, note]);
    }

    private static void ConfigureSizeInput(NumericUpDown input, int x, int y, int minimum, int maximum, int value)
    {
        input.Location = new Point(x, y);
        input.Size = new Size(78, 25);
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Increment = 10;
        input.Value = Math.Clamp(value, minimum, maximum);
    }

    private void BuildAudioPage(Control page, IReadOnlyList<WaveOutDeviceInfo> devices)
    {
        var settings = new[] { _settings.Audio1, _settings.Audio2 };
        for (var index = 0; index < 2; index++)
        {
            _audioEnabled[index].Text = $"Enable OUTPUT {index + 1}";
            _audioEnabled[index].Checked = settings[index].Enabled;
            _audioEnabled[index].Location = new Point(12, 18 + index * 82);
            _audioEnabled[index].AutoSize = true;
            _audioEnabled[index].ForeColor = ForeColor;
            _audioDevice[index].DropDownStyle = ComboBoxStyle.DropDownList;
            _audioDevice[index].Location = new Point(12, 48 + index * 82);
            _audioDevice[index].Width = 500;
            foreach (var device in devices) _audioDevice[index].Items.Add(device);
            var selected = Enumerable.Range(0, _audioDevice[index].Items.Count).FirstOrDefault(item =>
                _audioDevice[index].Items[item] is WaveOutDeviceInfo device && device.Id == settings[index].DeviceId, -1);
            _audioDevice[index].SelectedIndex = selected >= 0 ? selected : 0;
            page.Controls.AddRange([_audioEnabled[index], _audioDevice[index]]);
        }
    }

    private void BuildGeneralPage(Control page)
    {
        _cwLower.Text = "Default CW direction: CW-L / Icom (-700 Hz)";
        _cwLower.Checked = _settings.CwLowerSide;
        _cwLower.AutoSize = true;
        _cwLower.Location = new Point(12, 20);
        _cwLower.ForeColor = ForeColor;
        var offsetLabel = MakeLabel("TUNER FREQUENCY OFFSET (Hz)", 12, 58);
        _tunerFrequencyOffset.Location = new Point(210, 54);
        _tunerFrequencyOffset.Size = new Size(120, 25);
        _tunerFrequencyOffset.Minimum = -1_000_000;
        _tunerFrequencyOffset.Maximum = 1_000_000;
        _tunerFrequencyOffset.Increment = 1;
        _tunerFrequencyOffset.ThousandsSeparator = true;
        _tunerFrequencyOffset.Value = Math.Clamp(_settings.TunerFrequencyOffsetHz,
            (int)_tunerFrequencyOffset.Minimum, (int)_tunerFrequencyOffset.Maximum);
        var offsetNote = new Label
        {
            Text = "A positive value increases the tuner frequency sent to the device while keeping the displayed frequency unchanged.",
            Location = new Point(12, 82), Size = new Size(500, 18),
            ForeColor = Color.FromArgb(151, 181, 198), Font = new Font("Segoe UI", 8f)
        };

        _webRemoteEnabled.Text = "ENABLE WEB REMOTE (browser / phone PWA)";
        _webRemoteEnabled.Checked = _settings.WebRemoteEnabled;
        _webRemoteEnabled.AutoSize = true;
        _webRemoteEnabled.Location = new Point(12, 112);
        _webRemoteEnabled.ForeColor = Color.FromArgb(238, 151, 48);
        var portLabel = MakeLabel("WEB PORT", 12, 142);
        _webRemotePort.Location = new Point(100, 138);
        _webRemotePort.Size = new Size(90, 25);
        _webRemotePort.Minimum = 1024;
        _webRemotePort.Maximum = 65535;
        _webRemotePort.Value = Math.Clamp(_settings.WebRemotePort, 1024, 65535);
        _webRemoteBindAll.Text = "Allow LAN phones (bind all interfaces)";
        _webRemoteBindAll.Checked = _settings.WebRemoteBindAllInterfaces;
        _webRemoteBindAll.AutoSize = true;
        _webRemoteBindAll.Location = new Point(210, 140);
        var tokenLabel = MakeLabel("ACCESS TOKEN (optional)", 12, 172);
        _webRemoteToken.Location = new Point(190, 168);
        _webRemoteToken.Size = new Size(280, 25);
        _webRemoteToken.Text = _settings.WebRemoteAccessToken ?? "";
        var webNote = new Label
        {
            Text = "Checked = start http://127.0.0.1:<port>/ . Unchecked = stop the web host.",
            Location = new Point(12, 198), Size = new Size(510, 18),
            ForeColor = Color.FromArgb(151, 181, 198), Font = new Font("Segoe UI", 8f)
        };

        var rtlLabel = MakeLabel("RTL_TCP ENDPOINT (host:port)", 12, 228);
        _rtlTcpEndpoint.Location = new Point(210, 224);
        _rtlTcpEndpoint.Size = new Size(260, 25);
        _rtlTcpEndpoint.Text = string.IsNullOrWhiteSpace(_settings.RtlTcpEndpoint)
            ? "127.0.0.1:1234" : _settings.RtlTcpEndpoint;

        _voiceGuidance.Text = "VOICE GUIDANCE";
        _voiceGuidance.Checked = _settings.VoiceGuidanceEnabled;
        _voiceGuidance.AutoSize = true;
        _voiceGuidance.Location = new Point(12, 256);
        _voiceGuidance.ForeColor = Color.FromArgb(238, 151, 48);

        var webSdr = MakeButton("Web SDR Sites...", Color.FromArgb(39, 107, 145));
        webSdr.Location = new Point(12, 292);
        webSdr.Size = new Size(150, 32);
        webSdr.Click += (_, _) =>
        {
            using var sites = new WebSdrSetupForm(_settings);
            sites.ShowDialog(this);
            if (sites.Changed) WebSdrDirectoryChanged = true;
        };
        var reset = MakeButton("Reset All Settings", Color.FromArgb(139, 62, 68));
        reset.Location = new Point(172, 292);
        reset.Size = new Size(150, 32);
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Reset all saved window, device, DSP, and plugin settings?", "Reset Settings",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ResetRequested = true;
            DialogResult = DialogResult.OK;
        };
        page.Controls.AddRange([_cwLower, offsetLabel, _tunerFrequencyOffset, offsetNote,
            _webRemoteEnabled, portLabel, _webRemotePort, _webRemoteBindAll, tokenLabel, _webRemoteToken, webNote,
            rtlLabel, _rtlTcpEndpoint, _voiceGuidance, webSdr, reset]);
    }

    private void ApplyAudioSettings(int index, AudioChannelSettings target)
    {
        target.Enabled = _audioEnabled[index].Checked;
        if (_audioDevice[index].SelectedItem is not WaveOutDeviceInfo device) return;
        target.DeviceId = device.Id;
        target.DeviceName = device.Name;
    }

    private void BuildPluginPage(Control page, DisplayPluginCatalog catalog, PluginSelection current)
    {
        var spectrumLabel = MakeLabel("RF SPECTRUM PLUGIN", 12, 18);
        _spectrum.DropDownStyle = ComboBoxStyle.DropDownList;
        _spectrum.Location = new Point(12, 43);
        _spectrum.Width = 510;
        foreach (var plugin in catalog.SpectrumPlugins)
            _spectrum.Items.Add(new PluginItem(plugin.Info.Id, $"{plugin.Info.Name}  [{(plugin.Info.IsBuiltIn ? "Built-in" : "External")}]", plugin.Info.Description));
        Select(_spectrum, current.SpectrumId);

        var waterfallLabel = MakeLabel("RF WATERFALL PLUGIN", 12, 83);
        _waterfall.DropDownStyle = ComboBoxStyle.DropDownList;
        _waterfall.Location = new Point(12, 108);
        _waterfall.Width = 510;
        foreach (var plugin in catalog.WaterfallPlugins)
            _waterfall.Items.Add(new PluginItem(plugin.Info.Id, $"{plugin.Info.Name}  [{(plugin.Info.IsBuiltIn ? "Built-in" : "External")}]", plugin.Info.Description));
        Select(_waterfall, current.WaterfallId);

        _description.Location = new Point(12, 151);
        _description.Size = new Size(510, 54);
        _description.ForeColor = Color.FromArgb(151, 181, 198);
        _description.BorderStyle = BorderStyle.FixedSingle;
        _description.Padding = new Padding(8);
        _spectrum.SelectedIndexChanged += (_, _) => UpdateDescription();
        _waterfall.SelectedIndexChanged += (_, _) => UpdateDescription();
        page.Controls.AddRange([spectrumLabel, _spectrum, waterfallLabel, _waterfall, _description]);
        UpdateDescription();
    }

    private void UpdateDescription()
    {
        var spectrum = _spectrum.SelectedItem as PluginItem;
        var waterfall = _waterfall.SelectedItem as PluginItem;
        _description.Text = $"Spectrum: {spectrum?.Description}\r\nWaterfall: {waterfall?.Description}{_loadErrors}";
    }

    private static void Select(ComboBox box, string id)
    {
        var index = Enumerable.Range(0, box.Items.Count).FirstOrDefault(index =>
            box.Items[index] is PluginItem item && item.Id.Equals(id, StringComparison.OrdinalIgnoreCase), -1);
        box.SelectedIndex = index >= 0 ? index : 0;
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text, AutoSize = true, Location = new Point(x, y),
        ForeColor = Color.FromArgb(100, 190, 220), Font = new Font("Segoe UI Semibold", 8f)
    };

    private static Button MakeButton(string text, Color color)
    {
        var button = new Button { Text = text, Size = new Size(82, 29), BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private sealed record PluginItem(string Id, string Text, string Description)
    {
        public override string ToString() => Text;
    }

    private sealed record RateItem(int Rate)
    {
        public override string ToString() => $"{Rate / 1_000_000d:0.###} MHz / MS/s";
    }

    private sealed record AfPluginItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record AfPluginChoice(string PluginId, string Variant, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record SetupVfoItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed class AfPluginSetupRow
    {
        public string InstanceId { get; }
        public Panel Panel { get; } = new() { Size = new Size(488, 32), Margin = new Padding(2) };
        public ComboBox Plugin { get; } = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(2, 3), Size = new Size(260, 25)
        };
        public ComboBox Vfo { get; } = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(267, 3), Size = new Size(150, 25)
        };
        public Button Remove { get; } = new()
        {
            Text = "−", Location = new Point(423, 3), Size = new Size(55, 25), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(75, 46, 48), ForeColor = Color.FromArgb(240, 205, 205)
        };

        public AfPluginSetupRow(string instanceId)
        {
            InstanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
            Panel.Controls.AddRange([Plugin, Vfo, Remove]);
        }

        public AfPluginInstanceSettings ToSettings()
        {
            var choice = (AfPluginChoice)Plugin.SelectedItem!;
            var vfo = (SetupVfoItem)Vfo.SelectedItem!;
            return new AfPluginInstanceSettings
            {
                InstanceId = InstanceId, PluginId = choice.PluginId, Variant = choice.Variant, VfoId = vfo.Id
            };
        }
    }
}
