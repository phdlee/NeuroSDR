using NeuroSDR.Audio;
using NeuroSDR.Controls;
using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;
using NeuroSDR.Plugins;
using NeuroSDR.Plugins.Caption;
using NeuroSDR.Plugins.DigitalVoice;
using NeuroSDR.Plugins.Broadcast;
using NeuroSDR.Settings;
using System.Diagnostics;

namespace NeuroSDR;

public partial class frmNeuroSDR
{

    private Panel BuildFtxControlBar(AfPluginInstanceSettings instance, ComboBox vfoBox, bool primary)
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 31, 41) };
        vfoBox.Location = new Point(3, 2);
        vfoBox.Size = new Size(74, 25);
        var preset = BuildFrequencyPresetBox(instance, instance.Variant.Equals("FT4", StringComparison.OrdinalIgnoreCase) ? "FT4" : "FT8");
        preset.Location = new Point(80, 2);
        preset.Size = new Size(112, 25);
        var timeLabel = new Label
        {
            Text = "T", AutoSize = false, TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(195, 3), Size = new Size(12, 21), ForeColor = Color.FromArgb(151, 181, 198)
        };
        var timeAdjust = primary ? _ftxTimeAdjust : new NumericUpDown();
        timeAdjust.Location = new Point(209, 2);
        timeAdjust.Size = new Size(52, 25);
        timeAdjust.Minimum = -5;
        timeAdjust.Maximum = 30;
        timeAdjust.DecimalPlaces = 2;
        timeAdjust.Increment = .05m;
        timeAdjust.Tag = "ftx-time";
        timeAdjust.Value = Math.Clamp((decimal)_appSettings.FtxTimeAdjustSeconds, timeAdjust.Minimum, timeAdjust.Maximum);
        var autoAdjust = primary ? _ftxAutoAdjust : new CheckBox();
        autoAdjust.Text = "AUTO";
        autoAdjust.AutoSize = true;
        autoAdjust.Location = new Point(264, 4);
        autoAdjust.ForeColor = Color.FromArgb(216, 225, 235);
        autoAdjust.Tag = "ftx-auto";
        autoAdjust.Checked = _appSettings.FtxAutoTimeAdjust;
        var qsoLines = primary ? _ftxQsoLines : new CheckBox();
        qsoLines.Text = "QSO";
        qsoLines.AutoSize = true;
        qsoLines.Location = new Point(318, 4);
        qsoLines.ForeColor = Color.FromArgb(216, 225, 235);
        qsoLines.Tag = "ftx-qso";
        qsoLines.Checked = _appSettings.FtxShowQsoLines;
        _afDisplay.ShowQsoLines = _appSettings.FtxShowQsoLines;
        var mainWf = new CheckBox
        {
            Text = "MAIN WF",
            AutoSize = true,
            Location = new Point(368, 4),
            ForeColor = Color.FromArgb(216, 225, 235),
            Tag = "ftx-mainwf",
            Checked = _appSettings.FtxShowOnMainWaterfall
        };
        mainWf.CheckedChanged += (_, _) =>
        {
            _appSettings.FtxShowOnMainWaterfall = mainWf.Checked;
            _display.ShowFtxOnMain = mainWf.Checked;
            if (!mainWf.Checked) _display.ClearFtxOverlays();
            SaveSettings();
            SchedulePersistCurrentSceneLayout();
        };
        _display.ShowFtxOnMain = _appSettings.FtxShowOnMainWaterfall;
        var clear = MakeDecoderButton("CLEAR", 0);
        clear.Size = new Size(56, 24);
        clear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        clear.Location = new Point(Math.Max(430, bar.ClientSize.Width - 60), 2);
        clear.Click += (_, _) => ClearFtxDisplay(instance.InstanceId);
        bar.Resize += (_, _) =>
        {
            clear.Left = Math.Max(430, bar.ClientSize.Width - clear.Width - 4);
            clear.BringToFront();
        };
        if (!primary)
        {
            timeAdjust.ValueChanged += (_, _) => ApplyFtxSharedControls(timeAdjust, autoAdjust, qsoLines);
            autoAdjust.CheckedChanged += (_, _) => ApplyFtxSharedControls(timeAdjust, autoAdjust, qsoLines);
            qsoLines.CheckedChanged += (_, _) => ApplyFtxSharedControls(timeAdjust, autoAdjust, qsoLines);
        }
        bar.Controls.AddRange([vfoBox, preset, timeLabel, timeAdjust, autoAdjust, qsoLines, mainWf, clear]);
        return bar;
    }

    private Panel BuildCwControlBar(AfPluginInstanceSettings instance, ComboBox vfoBox, bool primary)
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 31, 41) };
        vfoBox.Location = new Point(3, 2);
        vfoBox.Size = new Size(92, 25);
        var title = primary ? _cwResultTitle : new Label();
        ConfigureDecoderTitle(title, "CEC CW");
        title.Dock = DockStyle.None;
        title.Location = new Point(101, 4);
        title.Size = new Size(72, 20);
        var afWf = new CheckBox
        {
            Text = "AF WF",
            AutoSize = true,
            Location = new Point(178, 4),
            ForeColor = Color.FromArgb(216, 225, 235),
            Tag = "cw-afwf",
            Checked = _appSettings.CwShowOnAfWaterfall
        };
        afWf.CheckedChanged += (_, _) =>
        {
            _appSettings.CwShowOnAfWaterfall = afWf.Checked;
            if (!afWf.Checked) _afDisplay.ClearCwMessages();
            SaveSettings();
            SchedulePersistCurrentSceneLayout();
            SynchronizeCwControls();
        };
        var reset = MakeDecoderButton("RESET", 248);
        var clear = MakeDecoderButton("CLEAR", 313);
        reset.Click += (_, _) => ResetCwDecoder(instance.InstanceId, "manual reset");
        clear.Click += (_, _) => ClearCwDisplay(instance.InstanceId);
        bar.Controls.AddRange([vfoBox, title, afWf, reset, clear]);
        return bar;
    }

    private static Button MakeDecoderButton(string text, int x)
    {
        var button = new Button
        {
            Text = text, Location = new Point(x, 2), Size = new Size(60, 24), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 61, 75), ForeColor = Color.FromArgb(211, 226, 235),
            Font = new Font("Segoe UI Semibold", 7.5f)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(72, 103, 119);
        return button;
    }

    private static void ConfigureDecoderTitle(Label label, string text)
    {
        label.Text = text;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.ForeColor = Color.FromArgb(104, 193, 222);
        label.Font = new Font("Segoe UI Semibold", 8f);
    }

    private Panel BuildDecoderContent(ListView list, Label status)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(3, 14, 21) };
        var header = BuildDecoderHeader(list);
        status.Dock = DockStyle.Bottom;
        status.Height = 20;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.ForeColor = Color.FromArgb(125, 162, 180);
        status.BackColor = Color.FromArgb(9, 24, 32);
        status.Font = new Font("Segoe UI", 7.5f);
        panel.Controls.Add(list);
        panel.Controls.Add(header);
        panel.Controls.Add(status);
        return panel;
    }

    private static Panel BuildDecoderHeader(ListView list)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 23, BackColor = Color.FromArgb(24, 46, 59) };
        foreach (ColumnHeader column in list.Columns)
        {
            var label = new Label
            {
                Text = column.Text, AutoSize = false, Height = 23,
                TextAlign = column.TextAlign == HorizontalAlignment.Right ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
                Padding = new Padding(5, 0, 5, 0), ForeColor = Color.FromArgb(164, 202, 219),
                BackColor = Color.FromArgb(24, 46, 59), Font = new Font("Segoe UI Semibold", 8.5f)
            };
            header.Controls.Add(label);
        }
        list.Tag = header;
        header.Resize += (_, _) => LayoutDecoderHeader(list);
        LayoutDecoderHeader(list);
        return header;
    }

    private static void LayoutDecoderHeader(ListView list)
    {
        if (list.Tag is not Panel header || header.Controls.Count != list.Columns.Count) return;
        var x = 0;
        for (var index = 0; index < list.Columns.Count; index++)
        {
            var label = header.Controls[index];
            label.Location = new Point(x, 0);
            label.Width = list.Columns[index].Width;
            x += label.Width;
        }
    }

    private static void ConfigureDecoderList(ListView list)
    {
        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Color.FromArgb(3, 14, 21);
        list.ForeColor = Color.FromArgb(215, 227, 235);
        list.Font = new Font("Segoe UI Semibold", 9.5f);
        list.View = View.Details;
        list.FullRowSelect = true;
        list.ShowItemToolTips = true;
        list.GridLines = false;
        list.HeaderStyle = ColumnHeaderStyle.None;
        DarkNativeTheme.ApplyListView(list);
    }

    private void ConfigureFtxList()
    {
        ConfigureDecoderList(_ftxResultList);
        _ftxResultList.Columns.Add("UTC", 62);
        _ftxResultList.Columns.Add("dB", 40, HorizontalAlignment.Right);
        _ftxResultList.Columns.Add("DT", 42, HorizontalAlignment.Right);
        _ftxResultList.Columns.Add("Freq", 55, HorizontalAlignment.Right);
        _ftxResultList.Columns.Add("Message", 260);
        _ftxResultList.Resize += (_, _) => ResizeFtxColumns();
    }

    private void ConfigureCwList()
    {
        ConfigureDecoderList(_cwResultList);
        _cwResultList.Columns.Add("Track", 75, HorizontalAlignment.Right);
        _cwResultList.Columns.Add("WPM", 52, HorizontalAlignment.Right);
        _cwResultList.Columns.Add("Decoded", 330);
        _cwResultList.Resize += (_, _) => ResizeCwColumns();
    }

    private void ResizeFtxColumns()
    {
        ResizeFtxColumns(_ftxResultList);
    }

    private static void ResizeFtxColumns(ListView list)
    {
        if (list.Columns.Count < 5) return;
        if (list.ClientSize.Width < 48) return;
        var available = Math.Max(180, list.ClientSize.Width - 24);
        int[] fixedWidths = [62, 40, 42, 55];
        for (var index = 0; index < fixedWidths.Length; index++) list.Columns[index].Width = fixedWidths[index];
        list.Columns[4].Width = Math.Max(80, available - fixedWidths.Sum());
        LayoutDecoderHeader(list);
    }

    private void ResizeCwColumns()
    {
        if (_resizingCwColumns || !_cwResultList.IsHandleCreated || _cwResultList.IsDisposed) return;
        if (_cwResultList.Columns.Count < 3) return;
        if (_cwResultList.ClientSize.Width < 48) return;
        _resizingCwColumns = true;
        try
        {
            var available = Math.Max(180, _cwResultList.ClientSize.Width - 24);
            SetColumnWidth(_cwResultList.Columns[0], 75);
            SetColumnWidth(_cwResultList.Columns[1], 52);
            SetColumnWidth(_cwResultList.Columns[2], Math.Max(80, available - 127));
            LayoutDecoderHeader(_cwResultList);
            // Snapshot + skip detached rows: Clear/Sort can mutate Items while Resize runs.
            foreach (var row in _cwResultList.Items.Cast<ListViewItem>().ToArray())
            {
                if (row is null || row.ListView is null || row.SubItems.Count < 3) continue;
                var cell = row.SubItems[2];
                if (cell is null) continue;
                cell.Text = TrimCwTextToTwoLines(cell.Text);
            }
        }
        finally
        {
            _resizingCwColumns = false;
        }
    }

    private static void ResizeCwColumns(ListView list)
    {
        if (list.Columns.Count < 3) return;
        var available = Math.Max(180, list.ClientSize.Width - 24);
        SetColumnWidth(list.Columns[0], 75);
        SetColumnWidth(list.Columns[1], 52);
        SetColumnWidth(list.Columns[2], Math.Max(80, available - 127));
        LayoutDecoderHeader(list);
    }

    private static void SetColumnWidth(ColumnHeader column, int width)
    {
        if (column.Width != width) column.Width = width;
    }

    private void UpdateAfPluginDisplayLayout()
    {
        var kiwiTimecodeEnabled = _appSettings.EnabledIqPluginIds.Contains("builtin.iq.kiwitimecode", StringComparer.OrdinalIgnoreCase);
        var adsbEnabled = _appSettings.EnabledIqPluginIds.Contains("builtin.iq.adsb", StringComparer.OrdinalIgnoreCase);
        var lteEnabled = _appSettings.EnabledIqPluginIds.Contains("builtin.iq.lte", StringComparer.OrdinalIgnoreCase);
        var selectedInstanceId = SelectedAfPluginBinding()?.InstanceId;
        // Drop bindings before Clear/Dispose. Those WinForms calls pump the UI
        // queue, and plugin BeginInvoke handlers would otherwise write into
        // controls whose TabPages are being destroyed.
        _afPluginTabs.Clear();
        _decoderTabs.TabPages.Clear();
        foreach (var page in _ownedAfPluginTabs)
        {
            // TabPage.Dispose() disposes children. External AF.Visual plugins
            // (Kiwi FAX/FSK/CW) cache a single view in CreateView; killing it here
            // makes the next layout return a disposed control.
            page.Controls.Clear();
            page.Dispose();
        }
        _ownedAfPluginTabs.Clear();

        var usedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in _appSettings.AfPluginInstances)
        {
            if (!AfPluginHasDisplay(instance.PluginId)) continue;
            var primary = usedTypes.Add(instance.PluginId);
            var ordinalKey = AfPluginOrdinalKey(instance);
            ordinals[ordinalKey] = ordinals.GetValueOrDefault(ordinalKey) + 1;
            var page = primary ? PrimaryAfPluginPage(instance.PluginId) : new TabPage();
            if (page is null)
            {
                if (!AfPluginHasDisplay(instance.PluginId)) continue;
                page = new TabPage();
                _ownedAfPluginTabs.Add(page);
            }
            else if (!primary) _ownedAfPluginTabs.Add(page);
            page.Controls.Clear();
            page.Text = AfPluginTabCaption(instance, ordinals[ordinalKey]);
            page.BackColor = Color.FromArgb(5, 17, 24);
            page.ForeColor = Color.FromArgb(207, 225, 234);
            page.Padding = Padding.Empty;
            // A WinForms control can only belong to one parent. Every plugin
            // instance therefore needs its own route selector; sharing the old
            // primary selector caused a later plugin tab to steal it and copy
            // its VFO route into the first FTX instance.
            var vfoBox = new ComboBox();
            ConfigureAfInstanceVfoBox(vfoBox, instance);
            _afPluginTabs[instance.InstanceId] = BuildAfPluginBinding(instance, page, vfoBox, primary);
            _decoderTabs.TabPages.Add(page);
        }

        if (kiwiTimecodeEnabled) _decoderTabs.TabPages.Add(_kiwiTimecodeDecoderTab);
        if (adsbEnabled) _decoderTabs.TabPages.Add(_adsbDecoderTab);
        if (lteEnabled) _decoderTabs.TabPages.Add(_lteDecoderTab);
        _decoderPanel.Visible = _afPluginTabs.Count > 0 || kiwiTimecodeEnabled || adsbEnabled || lteEnabled;
        _decoderPanel.Width = Math.Clamp(_appSettings.AfPluginDisplayWidth, AfPluginMinWidth, 900);
        _afPanel.Height = Math.Clamp(_appSettings.AfPluginDisplayHeight + ReclaimedHeaderAfHeight + _channelAfGrow, AfPanelMinHeight, 720 + _channelAfGrow);
        var selectedBinding = selectedInstanceId is null ? null : _afPluginTabs.GetValueOrDefault(selectedInstanceId);
        if (selectedBinding is not null)
        {
            _decoderTabs.SelectedTab = selectedBinding.Page;
            _lastSelectedAfInstanceId = selectedBinding.InstanceId;
        }
        else if (_decoderTabs.TabPages.Count > 0)
        {
            _decoderTabs.SelectedIndex = 0;
            var fallback = AfBindingForPage(_decoderTabs.SelectedTab!);
            if (fallback is not null) _lastSelectedAfInstanceId = fallback.InstanceId;
        }
        UpdateExtendsPluginViews();
        UpdateAfFskMarkers();
        ApplyWfmAfChrome();
        SynchronizeCwControls();
        EnsureAfPopOutChrome();
        ApplyDetachedMainLayout();
        EnsureAfPanelDockOrder();
        RestoreAfPluginTab(selectedInstanceId ?? _lastSelectedAfInstanceId);
        RefreshEibiWaterfall(force: true);
    }

    private void ApplyWfmAfChrome()
    {
        if (IsSatelliteSceneActive)
        {
            ApplySatelliteGraphChrome();
            return;
        }
        var wfm = _demodulator.Mode == RadioMode.WFM;
        if (!_wfmAudioVisual.IsDisposed)
        {
            _wfmAudioVisual.Visible = wfm;
            if (wfm)
            {
                if (!_graphPanel.IsDisposed)
                    _wfmAudioVisual.SetSpectrumHeightHint(Math.Max(40, _graphPanel.ClientSize.Height / 4));
                _wfmAudioVisual.BringToFront();
            }
        }
        if (!_wfmStationPanel.IsDisposed)
        {
            if (wfm)
                _wfmStationPanel.SyncHeightToContent();
            _wfmStationPanel.Visible = wfm;
            if (wfm) _wfmStationPanel.BringToFront();
        }
        if (!_afDisplay.IsDisposed)
            _afDisplay.Visible = !wfm;
        if (wfm)
        {
            _afFilterCheck.Visible = !_appSettings.WfmHideAfPlugins;
            if (_appSettings.WfmHideAfPlugins)
                _decoderPanel.Visible = false;
            else if (_decoderTabs.TabPages.Count > 0)
                _decoderPanel.Visible = true;
        }
        else
        {
            _afFilterCheck.Visible = true;
            if (_decoderTabs.TabPages.Count > 0)
                _decoderPanel.Visible = true;
        }
        // WFM hide/show and mode switches must not leave AF Plugin left of RX CONTROL.
        EnsureAfPanelDockOrder();
        UpdateWfmStationMarkers();
        PositionRfDisplayOverlay();
    }

    private void UpdateWfmStationMarkers()
    {
        if (_display.IsDisposed) return;
        if (_demodulator.Mode != RadioMode.WFM || _wfmStationPanel.IsDisposed)
        {
            _display.SetWfmStationMarkers([]);
            return;
        }
        _display.SetWfmStationMarkers(_wfmStationPanel.GetMarkerStations());
    }

    private void RecallWfmStation(long frequencyHz, string eqPreset)
    {
        if (_modeBox.Items.Contains(RadioMode.WFM.ToString()))
            _modeBox.SelectedItem = RadioMode.WFM.ToString();
        ChangeVfoFromDigitalDisplay(frequencyHz);
        _wfmModePanel.ApplyEqPresetByName(eqPreset);
        _appSettings.WfmEqSelectedPreset = eqPreset;
        _appSettings.WfmEqGainsDb = _wfmModePanel.EqGainsDb;
        ApplyWfmEqualizer();
    }

    private bool AfPluginHasDisplay(string pluginId) =>
        _afPlugins.Plugins.Any(plugin => plugin.Info.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase) &&
            plugin.Info.Capabilities.HasFlag(AfPluginCapabilities.Display));

    private void UpdateExtendsPluginViews()
    {
        while (_extendsPluginsPanel.Controls.Count > 0)
        {
            var control = _extendsPluginsPanel.Controls[0];
            _extendsPluginsPanel.Controls.RemoveAt(0);
            if (control is ExtendsSectionPanel &&
                control is not AnalogModeOptionsPanel and
                not WfmModeOptionsPanel and
                not CwModeOptionsPanel and
                not DigitalModeOptionsPanel and
                not EnFilterVoicePanel and
                not EnFilterCwPanel)
            {
                while (control.Controls.Count > 0)
                {
                    // Detach plugin views before disposing the wrapper box.
                    var child = control.Controls[0];
                    control.Controls.RemoveAt(0);
                    if (child is Panel body)
                    {
                        while (body.Controls.Count > 0)
                            body.Controls.RemoveAt(0);
                    }
                }
                control.Dispose();
            }
        }

        void AddBoxed(Control panel)
        {
            if (!panel.Visible) return;
            _extendsPluginsPanel.Controls.Add(panel);
        }

        AddBoxed(_analogModePanel);
        AddBoxed(_enFilterVoicePanel);
        AddBoxed(_wfmModePanel);
        AddBoxed(_cwModePanel);
        AddBoxed(_enFilterCwPanel);
        AddBoxed(_digitalModePanel);

        foreach (var instance in _appSettings.AfPluginInstances)
        {
            if (AfPluginHasDisplay(instance.PluginId)) continue;
            if (_afPluginHost.Plugin(instance.InstanceId) is not IAfSidebarPlugin sidebar) continue;
            var box = new ExtendsSectionPanel(instance.PluginId
                .Replace("builtin.af.", "", StringComparison.OrdinalIgnoreCase)
                .ToUpperInvariant());
            var view = sidebar.SidebarView;
            view.Location = new Point(0, 0);
            box.Place(view);
            box.SetBodyHeight(Math.Max(view.Height, 40));
            _extendsPluginsPanel.Controls.Add(box);
        }
    }

    private void UpdateAfPluginDisplayLayoutLegacy()
    {
        var ftxEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.ftx", StringComparer.OrdinalIgnoreCase);
        var cwEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.cw", StringComparer.OrdinalIgnoreCase);
        var sstvEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.sstv", StringComparer.OrdinalIgnoreCase);
        var rttyEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.rtty", StringComparer.OrdinalIgnoreCase);
        var weatherFaxEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.weatherfax", StringComparer.OrdinalIgnoreCase);
        var kiwiNavtexEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.kiwinavtex", StringComparer.OrdinalIgnoreCase);
        var kiwiWwvEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.kiwiwwv", StringComparer.OrdinalIgnoreCase);
        var flRttyEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.flrtty", StringComparer.OrdinalIgnoreCase);
        var flCwEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.flcw", StringComparer.OrdinalIgnoreCase);
        var flFaxEnabled = _appSettings.EnabledAfPluginIds.Contains("builtin.af.flfax", StringComparer.OrdinalIgnoreCase);
        var kiwiTimecodeEnabled = _appSettings.EnabledIqPluginIds.Contains("builtin.iq.kiwitimecode", StringComparer.OrdinalIgnoreCase);
        var adsbEnabled = _appSettings.EnabledIqPluginIds.Contains("builtin.iq.adsb", StringComparer.OrdinalIgnoreCase);
        var lteEnabled = _appSettings.EnabledIqPluginIds.Contains("builtin.iq.lte", StringComparer.OrdinalIgnoreCase);
        int InstanceCount(string id) => _appSettings.AfPluginInstances.Count(instance =>
            instance.PluginId.Equals(id, StringComparison.OrdinalIgnoreCase));
        string Caption(string text, string id) => InstanceCount(id) > 1 ? $"{text} ×{InstanceCount(id)}" : text;
        _ftxDecoderTab.Text = Caption("FTX", "builtin.af.ftx");
        _cwDecoderTab.Text = Caption("CEC CW", "builtin.af.cw");
        _sstvDecoderTab.Text = Caption("SSTV", "builtin.af.sstv");
        _rttyDecoderTab.Text = Caption("RTTY", "builtin.af.rtty");
        _weatherFaxDecoderTab.Text = Caption("WEFAX", "builtin.af.weatherfax");
        _kiwiNavtexDecoderTab.Text = Caption("KiwiNAVTEX", "builtin.af.kiwinavtex");
        _kiwiWwvDecoderTab.Text = Caption("KiwiWWV", "builtin.af.kiwiwwv");
        _flRttyDecoderTab.Text = Caption("flrtty", "builtin.af.flrtty");
        _flCwDecoderTab.Text = Caption("flcw", "builtin.af.flcw");
        _flFaxDecoderTab.Text = Caption("flfax", "builtin.af.flfax");
        var ftxModes = _appSettings.AfPluginInstances.Where(instance =>
                instance.PluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase))
            .GroupBy(instance => instance.Variant, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}×{group.Count()}");
        _ftxResultTitle.Text = string.Join(" + ", ftxModes);
        _ftxModeBox.Enabled = false;
        _decoderPanel.Visible = ftxEnabled || cwEnabled || sstvEnabled || rttyEnabled || weatherFaxEnabled || kiwiNavtexEnabled || kiwiWwvEnabled || flRttyEnabled || flCwEnabled || flFaxEnabled || kiwiTimecodeEnabled || adsbEnabled || lteEnabled;
        _decoderPanel.Width = Math.Clamp(_appSettings.AfPluginDisplayWidth, AfPluginMinWidth, 900);
        _afPanel.Height = Math.Clamp(_appSettings.AfPluginDisplayHeight + ReclaimedHeaderAfHeight + _channelAfGrow, AfPanelMinHeight, 720 + _channelAfGrow);
        var selected = _decoderTabs.SelectedTab;
        _decoderTabs.TabPages.Clear();
        if (ftxEnabled) _decoderTabs.TabPages.Add(_ftxDecoderTab);
        if (cwEnabled) _decoderTabs.TabPages.Add(_cwDecoderTab);
        if (sstvEnabled) _decoderTabs.TabPages.Add(_sstvDecoderTab);
        if (rttyEnabled) _decoderTabs.TabPages.Add(_rttyDecoderTab);
        if (weatherFaxEnabled) _decoderTabs.TabPages.Add(_weatherFaxDecoderTab);
        if (kiwiNavtexEnabled) _decoderTabs.TabPages.Add(_kiwiNavtexDecoderTab);
        if (kiwiWwvEnabled) _decoderTabs.TabPages.Add(_kiwiWwvDecoderTab);
        if (flRttyEnabled) _decoderTabs.TabPages.Add(_flRttyDecoderTab);
        if (flCwEnabled) _decoderTabs.TabPages.Add(_flCwDecoderTab);
        if (flFaxEnabled) _decoderTabs.TabPages.Add(_flFaxDecoderTab);
        if (kiwiTimecodeEnabled) _decoderTabs.TabPages.Add(_kiwiTimecodeDecoderTab);
        if (adsbEnabled) _decoderTabs.TabPages.Add(_adsbDecoderTab);
        if (lteEnabled) _decoderTabs.TabPages.Add(_lteDecoderTab);
        // Handle may not exist yet at startup; without an explicit selection SelectedIndex stays -1
        // and SingleActiveAfPlugin would enable no AF plugins until the user clicks a tab.
        if (selected is not null && _decoderTabs.TabPages.Contains(selected)) _decoderTabs.SelectedTab = selected;
        else if (_decoderTabs.TabPages.Count > 0) _decoderTabs.SelectedIndex = 0;
        if (!ftxEnabled)
        {
            _ftxResultList.Items.Clear();
            _ftxSlots.Clear();
            _ftxStatusLabel.Text = string.Empty;
        }
        if (!cwEnabled)
        {
            _cwResultList.Items.Clear();
            _cwRows.Clear();
            _cwStatusLabel.Text = string.Empty;
        }
        UpdateAfFskMarkers();
    }

    private AfPluginTabBinding BuildAfPluginBinding(AfPluginInstanceSettings instance, TabPage page,
        ComboBox vfoBox, bool primary)
    {
        if (instance.PluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase))
        {
            var list = primary ? _ftxResultList : CreateFtxResultList();
            var status = primary ? _ftxStatusLabel : new Label();
            var slots = primary ? _ftxSlots : new Queue<long>();
            page.Controls.Add(BuildDecoderTabLayout(BuildFtxControlBar(instance, vfoBox, primary),
                BuildDecoderContent(list, status)));
            return new(instance.InstanceId, instance.PluginId, page, vfoBox, list, status, slots, null, null);
        }
        if (instance.PluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase))
        {
            var list = primary ? _cwResultList : CreateCwResultList();
            var status = primary ? _cwStatusLabel : new Label();
            var rows = primary ? _cwRows : new Dictionary<string, ListViewItem>();
            page.Controls.Add(BuildDecoderTabLayout(BuildCwControlBar(instance, vfoBox, primary),
                BuildDecoderContent(list, status)));
            return new(instance.InstanceId, instance.PluginId, page, vfoBox, list, status, null, rows, null);
        }

        var view = ResolveAfPluginView(instance, primary);
        if (view is null) return new(instance.InstanceId, instance.PluginId, page, vfoBox, null, null, null, null, null);
        if (!primary && view is not IAfResultView) WireAdditionalAfPluginView(view);
        var bar = view switch
        {
            NeuroCaptionPluginView caption => caption.AttachHostBar(vfoBox),
            EibiBroadcastPluginView eibi => WireEibiAiScan(eibi.AttachHostBar(vfoBox), eibi),
            _ => BuildAfInstanceRouteBar(instance, vfoBox, AfPluginBaseCaption(instance.PluginId))
        };
        page.Controls.Add(BuildDecoderTabLayout(bar, view));
        return new(instance.InstanceId, instance.PluginId, page, vfoBox, null, null, null, null, view);
    }

    private ListView CreateFtxResultList()
    {
        var list = new ListView();
        ConfigureDecoderList(list);
        list.Columns.Add("UTC", 62);
        list.Columns.Add("dB", 40, HorizontalAlignment.Right);
        list.Columns.Add("DT", 42, HorizontalAlignment.Right);
        list.Columns.Add("Freq", 55, HorizontalAlignment.Right);
        list.Columns.Add("Message", 260);
        list.Resize += (_, _) => ResizeFtxColumns(list);
        return list;
    }

    private ListView CreateCwResultList()
    {
        var list = new ListView();
        ConfigureDecoderList(list);
        list.Columns.Add("Track", 75, HorizontalAlignment.Right);
        list.Columns.Add("WPM", 52, HorizontalAlignment.Right);
        list.Columns.Add("Decoded", 330);
        list.Resize += (_, _) => ResizeCwColumns(list);
        return list;
    }

    private Panel BuildAfInstanceRouteBar(AfPluginInstanceSettings instance, ComboBox vfoBox, string caption)
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 31, 41) };
        vfoBox.Location = new Point(3, 2);
        vfoBox.Size = new Size(112, 25);
        bar.Controls.Add(vfoBox);
        var presetPlugin = _afPluginHost.Plugin(instance.InstanceId) as IAfFrequencyPresetPlugin;
        var isFax = instance.PluginId.Equals("builtin.af.weatherfax", StringComparison.OrdinalIgnoreCase) ||
                    instance.PluginId.Equals("builtin.af.flfax", StringComparison.OrdinalIgnoreCase) ||
                    presetPlugin is not null;
        var captionX = 123;
        if (isFax)
        {
            var presetGroup = presetPlugin?.PresetGroup ?? "WeatherFax";
            var preset = BuildFrequencyPresetBox(instance, presetGroup);
            preset.Location = new Point(123, 2);
            preset.Size = new Size(200, 25);
            bar.Controls.Add(preset);
            captionX = 331;
        }
        bar.Controls.Add(new Label
        {
            Text = caption, Location = new Point(captionX, 3), Size = new Size(140, 21),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(104, 193, 222),
            Font = new Font("Segoe UI Semibold", 8f)
        });
        return bar;
    }

    private ComboBox BuildFrequencyPresetBox(AfPluginInstanceSettings instance, string group)
    {
        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(5, 17, 24),
            ForeColor = Color.FromArgb(171, 214, 232),
            Font = new Font("Segoe UI", 7.6f),
            DropDownWidth = 340,
            Tag = "frequency-preset"
        };
        box.Items.Add("PRESET");
        foreach (var preset in NeuroSDRPresetCatalog.ForGroup(group)) box.Items.Add(preset);
        box.SelectedIndex = 0;
        box.Enabled = box.Items.Count > 1;
        box.SelectedIndexChanged += (_, _) =>
        {
            if (box.SelectedItem is NeuroSDRPreset preset) ApplyFrequencyPreset(instance.InstanceId, preset);
        };
        return box;
    }

    private void ConfigureAfInstanceVfoBox(ComboBox box, AfPluginInstanceSettings instance)
    {
        box.Items.Clear();
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.BackColor = Color.FromArgb(5, 17, 24);
        box.ForeColor = Color.FromArgb(255, 193, 69);
        box.Items.Add(new VfoChoice("main", "MAIN VFO"));
        if (_source is not IRemoteAudioSampleSource)
        {
            foreach (var sub in _appSettings.SubVfos) box.Items.Add(new VfoChoice(sub.Id, sub.Name));
        }
        var selectedId = _source is IRemoteAudioSampleSource ? "main" : instance.VfoId;
        box.SelectedItem = box.Items.Cast<VfoChoice>().FirstOrDefault(choice =>
            choice.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? box.Items[0];
        box.Enabled = _source is not IRemoteAudioSampleSource;
        box.Visible = true;
        box.SelectedIndexChanged += (_, _) =>
        {
            if (_synchronizingAfPluginVfo || box.SelectedItem is not VfoChoice choice) return;
            var current = _appSettings.AfPluginInstances.FirstOrDefault(item =>
                item.InstanceId.Equals(instance.InstanceId, StringComparison.OrdinalIgnoreCase));
            if (current is null) return;
            current.VfoId = choice.Id;
            _appSettings.AfPluginVfoRoutes[current.InstanceId] = choice.Id;
            _afPluginHost.SetRoutes(_appSettings.AfPluginVfoRoutes);
            _lastSubVfoRangeSignature = string.Empty;
            UpdateSubVfoRangeVisuals();
            EnsureAfDisplayFollowsMultiChannelCw();
            SyncCaptionTickerToSelectedAfVfo();
            SchedulePersistCurrentSceneLayout();
        };
    }

    private void ApplyFrequencyPreset(string instanceId, NeuroSDRPreset preset)
    {
        var instance = _appSettings.AfPluginInstances.FirstOrDefault(item =>
            item.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (instance is null) return;
        var tuneHz = Math.Clamp(preset.DialFrequencyHz, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        var captureReady = true;
        if (instance.VfoId.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            if (_modeBox.Items.Contains(preset.Mode.ToString())) _modeBox.SelectedItem = preset.Mode.ToString();
            _bandwidthBox.Value = Math.Clamp(preset.BandwidthHz, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
            ChangeVfoFromDigitalDisplay(tuneHz);
        }
        else
        {
            var sub = _appSettings.SubVfos.FirstOrDefault(item =>
                item.Id.Equals(instance.VfoId, StringComparison.OrdinalIgnoreCase));
            if (sub is null) return;
            sub.Frequency = tuneHz;
            sub.Mode = preset.Mode;
            sub.Bandwidth = preset.BandwidthHz;
            captureReady = EnsurePresetInsideCapture(sub.Frequency);
            RebuildSubVfoReceivers();
        }
        if (!captureReady) return;
        if (preset.Group.Equals("WeatherFax", StringComparison.OrdinalIgnoreCase) && preset.Lpm is 60 or 120)
            ApplyWeatherFaxPresetLpm(preset.Lpm);
        _statusLabel.Text = preset.Group.Equals("WeatherFax", StringComparison.OrdinalIgnoreCase)
            ? $"PRESET · {preset.Name} · {preset.Mode} · dial {tuneHz / 1_000_000d:0.000000} MHz (base {preset.FrequencyHz / 1_000d:0.0} kHz {(preset.Mode == RadioMode.LSB ? "+" : "−")} 1.9)"
            : $"PRESET · {preset.Name} · {preset.Mode} · {preset.FrequencyHz / 1_000_000d:0.000000} MHz";
    }

    private void ApplyWeatherFaxPresetLpm(int lpm)
    {
        _appSettings.WeatherFaxLpm = lpm;
        _appSettings.FlFaxLpm = lpm;
        _weatherFaxView.LoadSettings(_appSettings);
        _flFaxView.LoadSettings(_appSettings);
        ConfigureWeatherFaxPlugin();
        ConfigureFlFaxPlugin();
        foreach (var instance in _appSettings.AfPluginInstances)
        {
            if (_afPluginHost.Plugin(instance.InstanceId) is not IAfFrequencyPresetPlugin presetPlugin) continue;
            if (!presetPlugin.PresetGroup.Equals("WeatherFax", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_appSettings.AfPluginUiState.TryGetValue(instance.PluginId, out var bag))
                bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bag["lpm"] = lpm.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _appSettings.AfPluginUiState[instance.PluginId] = bag;
            var options = new Dictionary<string, string>(bag, StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = AfPluginRunning(instance.PluginId).ToString()
            };
            _afPluginHost.Configure(instance.InstanceId, options);
        }
    }

    private bool EnsurePresetInsideCapture(long frequency)
    {
        if (_source is IRemoteAudioSampleSource || Math.Abs(frequency - _rfCenterFrequency) <= _source.SampleRate * .45) return true;
        if (_source is IFixedCenterFrequencySampleSource)
        {
            _statusLabel.Text = "This fixed-center source cannot place the preset inside its capture range.";
            return false;
        }
        if (_iqRecorder is not null)
        {
            _statusLabel.Text = "Stop IQ recording before tuning this preset outside the current capture range.";
            return false;
        }
        _rfCenterFrequency = frequency;
        _viewCenterFrequency = frequency;
        Interlocked.Exchange(ref _pendingCenterFrequency, frequency);
        if (!_uiTimer.Enabled) ApplyLogicalCenterToSource();
        _spectrumPipeline.Reset();
        return true;
    }

    private TabPage? PrimaryAfPluginPage(string pluginId) => pluginId.ToLowerInvariant() switch
    {
        "builtin.af.ftx" => _ftxDecoderTab, "builtin.af.cw" => _cwDecoderTab,
        "builtin.af.sstv" => _sstvDecoderTab, "builtin.af.rtty" => _rttyDecoderTab,
        "builtin.af.weatherfax" => _weatherFaxDecoderTab,
        "builtin.af.kiwinavtex" => _kiwiNavtexDecoderTab,
        "builtin.af.kiwiwwv" => _kiwiWwvDecoderTab,
        "builtin.af.flrtty" => _flRttyDecoderTab, "builtin.af.flcw" => _flCwDecoderTab,
        "builtin.af.flfax" => _flFaxDecoderTab,
        _ => null
    };

    private Control? PrimaryAfPluginView(string pluginId) => pluginId.ToLowerInvariant() switch
    {
        "builtin.af.sstv" => _sstvView, "builtin.af.rtty" => _rttyView,
        "builtin.af.weatherfax" => _weatherFaxView,
        "builtin.af.kiwinavtex" => _kiwiNavtexView,
        "builtin.af.kiwiwwv" => _kiwiWwvView,
        "builtin.af.flrtty" => _flRttyView, "builtin.af.flcw" => _flCwView,
        "builtin.af.flfax" => _flFaxView,
        _ => null
    };

    private Control? CreateAfPluginView(string pluginId)
    {
        Control? view = pluginId.ToLowerInvariant() switch
        {
            "builtin.af.sstv" => new SstvPluginView(), "builtin.af.rtty" => new RttyPluginView(),
            "builtin.af.weatherfax" => new WeatherFaxPluginView(),
            "builtin.af.kiwinavtex" => new KiwiNavtexPluginView(),
            "builtin.af.kiwiwwv" => new KiwiWwvPluginView(),
            "builtin.af.flrtty" => new FlRttyPluginView(), "builtin.af.flcw" => new FlCwPluginView(),
            "builtin.af.flfax" => new FlFaxPluginView(),
            _ => null
        };
        LoadAfPluginViewSettings(view);
        return view;
    }

    private void LoadAfPluginViewSettings(Control? view)
    {
        switch (view)
        {
            case SstvPluginView value: value.LoadSettings(_appSettings); break;
            case RttyPluginView value: value.LoadSettings(_appSettings); break;
            case WeatherFaxPluginView value: value.LoadSettings(_appSettings); break;
            case KiwiNavtexPluginView value: value.LoadSettings(_appSettings); break;
            case KiwiWwvPluginView value: value.LoadSettings(_appSettings); break;
            case FlRttyPluginView value: value.LoadSettings(_appSettings); break;
            case FlCwPluginView value: value.LoadSettings(_appSettings); break;
            case FlFaxPluginView value: value.LoadSettings(_appSettings); break;
        }
    }

    private void WireAdditionalAfPluginView(Control view)
    {
        switch (view)
        {
            case SstvPluginView value:
                value.OptionsChanged += () => { value.SaveSettings(_appSettings); ConfigureSstvPlugin(); };
                value.CommandRequested += command => { value.SaveSettings(_appSettings); ConfigureSstvPlugin(command); };
                break;
            case RttyPluginView value:
                value.OptionsChanged += () => { value.SaveSettings(_appSettings); ConfigureRttyPlugin(); UpdateAfFskMarkers(); };
                value.CommandRequested += command => { value.SaveSettings(_appSettings); ConfigureRttyPlugin(command); };
                break;
            case WeatherFaxPluginView value:
                value.OptionsChanged += () => { value.SaveSettings(_appSettings); ConfigureWeatherFaxPlugin(); };
                value.CommandRequested += command => { value.SaveSettings(_appSettings); ConfigureWeatherFaxPlugin(command); };
                break;
            case KiwiNavtexPluginView value:
                value.OptionsChanged += () => { value.SaveSettings(_appSettings); ConfigureKiwiNavtexPlugin(); UpdateAfFskMarkers(); };
                value.CommandRequested += command => { value.SaveSettings(_appSettings); ConfigureKiwiNavtexPlugin(command); };
                break;
            case KiwiWwvPluginView value:
                value.OptionsChanged += () => { value.SaveSettings(_appSettings); ConfigureKiwiWwvPlugin(); };
                value.CommandRequested += command => { value.SaveSettings(_appSettings); ConfigureKiwiWwvPlugin(command); };
                break;
            case FlRttyPluginView value:
                value.OptionsChanged += () => { value.SaveSettings(_appSettings); ConfigureFlRttyPlugin(); UpdateAfFskMarkers(); };
                value.CommandRequested += command => { value.SaveSettings(_appSettings); ConfigureFlRttyPlugin(command); };
                break;
            case FlCwPluginView value:
                value.OptionsChanged += () => { value.SaveSettings(_appSettings); ConfigureFlCwPlugin(); };
                value.CommandRequested += command => { value.SaveSettings(_appSettings); ConfigureFlCwPlugin(command); };
                break;
            case FlFaxPluginView value:
                value.OptionsChanged += () => { value.SaveSettings(_appSettings); ConfigureFlFaxPlugin(); };
                value.CommandRequested += command => { value.SaveSettings(_appSettings); ConfigureFlFaxPlugin(command); };
                break;
        }
    }

    private void OnAfPluginResult(AfPluginResult result)
    {
        if (_pluginUiSuspended || IsDisposed || Disposing) return;
        // Publish to web off the UI marshaling path so CW character floods still stream live
        // even when the WinForms list updates keep the UI thread busy.
        try { PublishRemoteAfPlugin(result); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }

        if (InvokeRequired)
        {
            try { BeginInvoke(() => ApplyAfPluginResultOnUi(result)); } catch (InvalidOperationException) { }
            return;
        }
        ApplyAfPluginResultOnUi(result);
    }

    private void ApplyAfPluginResultOnUi(AfPluginResult result)
    {
        if (_pluginUiSuspended || IsDisposed || Disposing) return;
        try { ApplyAfPluginResult(result); }
        catch (ObjectDisposedException) { }
    }

    private void ApplyAfPluginResult(AfPluginResult result)
    {
        var pluginTypeId = _afPluginHost.PluginTypeId(result.PluginId) ?? result.PluginId;
        var instanceTab = _afPluginTabs.GetValueOrDefault(result.PluginId);
        if (!_appSettings.EnabledAfPluginIds.Contains(pluginTypeId, StringComparer.OrdinalIgnoreCase)) return;
        var isFtx = pluginTypeId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase);
        if (isFtx &&
            result.Kind.Equals("TIME_ADJUST", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(result.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var adjusted))
        {
            _synchronizingFtxControls = true;
            _appSettings.FtxTimeAdjustSeconds = Math.Clamp(adjusted, -5, 30);
            _ftxTimeAdjust.Value = Math.Clamp((decimal)_appSettings.FtxTimeAdjustSeconds,
                _ftxTimeAdjust.Minimum, _ftxTimeAdjust.Maximum);
            _synchronizingFtxControls = false;
            SynchronizeFtxControls();
        }
        else if (isFtx &&
                 result.Kind.Equals("AUTO_DT_STATE", StringComparison.OrdinalIgnoreCase) &&
                 result.Text.Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            _synchronizingFtxControls = true;
            _appSettings.FtxAutoTimeAdjust = false;
            _ftxAutoAdjust.Checked = false;
            _synchronizingFtxControls = false;
            SynchronizeFtxControls();
        }
        if (isFtx)
        {
            if (result.Kind.Equals("FTX_DECODE", StringComparison.OrdinalIgnoreCase)) AddFtxDecode(result, instanceTab);
            else if (UiAlive(instanceTab?.StatusLabel ?? _ftxStatusLabel))
                (instanceTab?.StatusLabel ?? _ftxStatusLabel).Text = result.Text;
        }
        else if (pluginTypeId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase))
        {
            if (result.Kind.Equals("CW_DECODE", StringComparison.OrdinalIgnoreCase)) AddCwDecode(result, instanceTab);
            else if (UiAlive(instanceTab?.StatusLabel ?? _cwStatusLabel))
                (instanceTab?.StatusLabel ?? _cwStatusLabel).Text = result.Text;
        }
        else if (pluginTypeId.Equals("builtin.af.sstv", StringComparison.OrdinalIgnoreCase))
            ((instanceTab?.PluginView as SstvPluginView) ?? _sstvView).ApplyResult(result);
        else if (pluginTypeId.Equals("builtin.af.rtty", StringComparison.OrdinalIgnoreCase))
            ((instanceTab?.PluginView as RttyPluginView) ?? _rttyView).ApplyResult(result);
        else if (pluginTypeId.Equals("builtin.af.weatherfax", StringComparison.OrdinalIgnoreCase))
            ((instanceTab?.PluginView as WeatherFaxPluginView) ?? _weatherFaxView).ApplyResult(result);
        else if (pluginTypeId.Equals("builtin.af.kiwinavtex", StringComparison.OrdinalIgnoreCase))
            ((instanceTab?.PluginView as KiwiNavtexPluginView) ?? _kiwiNavtexView).ApplyResult(result);
        else if (pluginTypeId.Equals("builtin.af.kiwiwwv", StringComparison.OrdinalIgnoreCase))
            ((instanceTab?.PluginView as KiwiWwvPluginView) ?? _kiwiWwvView).ApplyResult(result);
        else if (pluginTypeId.Equals("builtin.af.flrtty", StringComparison.OrdinalIgnoreCase))
            ((instanceTab?.PluginView as FlRttyPluginView) ?? _flRttyView).ApplyResult(result);
        else if (pluginTypeId.Equals("builtin.af.flcw", StringComparison.OrdinalIgnoreCase))
            ((instanceTab?.PluginView as FlCwPluginView) ?? _flCwView).ApplyResult(result);
        else if (pluginTypeId.Equals("builtin.af.flfax", StringComparison.OrdinalIgnoreCase))
            ((instanceTab?.PluginView as FlFaxPluginView) ?? _flFaxView).ApplyResult(result);
        else if (pluginTypeId.Equals(EibiBroadcastAfPlugin.PluginId, StringComparison.OrdinalIgnoreCase))
        {
            if (result.Kind.Equals("TUNE", StringComparison.OrdinalIgnoreCase) &&
                result.Fields is not null &&
                result.Fields.TryGetValue("frequencyHz", out var hzText) &&
                long.TryParse(hzText, out var hz))
                TuneToShortwaveBroadcast(hz, result.Text);
            else if (result.Kind.Equals("OVERLAY", StringComparison.OrdinalIgnoreCase))
            {
                _appSettings.EibiShowOnMainWaterfall = result.Text.Equals("on", StringComparison.OrdinalIgnoreCase);
                _nextEibiWaterfallTick = 0;
                RefreshEibiWaterfall(force: true);
            }
            if (instanceTab?.PluginView is IAfResultView eibiView)
                eibiView.ApplyResult(result);
        }
        else if (instanceTab?.PluginView is IAfResultView visual)
            visual.ApplyResult(result);
        if (NeuroCaption.IsCaptionPlugin(pluginTypeId))
            ApplyCaptionTicker(pluginTypeId, result);
    }

    private void ApplyCaptionTicker(string pluginTypeId, AfPluginResult result)
    {
        if (_afDisplay.IsDisposed) return;
        var bag = _appSettings.AfPluginUiState.GetValueOrDefault(pluginTypeId);
        var tickerOn = bag is not null &&
                       bag.TryGetValue("afTicker", out var flag) &&
                       bool.TryParse(flag, out var on) && on;
        var captionVfo = ResolveAfPluginVfoId(result.PluginId);
        var sameVfo = captionVfo.Equals(SelectedAfVfoId(), StringComparison.OrdinalIgnoreCase);
        _afDisplay.SetCaptionTickerEnabled(tickerOn && sameVfo, clearWhenOff: !tickerOn);
        if (!tickerOn || !sameVfo || !result.Kind.Equals("CAPTION", StringComparison.OrdinalIgnoreCase)) return;
        var original = result.Fields?.GetValueOrDefault("original", result.Text) ?? result.Text;
        var translation = result.Fields?.GetValueOrDefault("translation", "") ?? "";
        IReadOnlyDictionary<string, string> captionOpts = bag ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var engine = captionOpts.GetValueOrDefault("engine", NeuroCaption.Groq);
        var mode = NeuroCaption.DefaultDisplayMode(
            NeuroCaption.Get(captionOpts, engine, "translateEngine", "off"),
            NeuroCaption.Get(captionOpts, engine, "displayMode", "original"));
        if (mode.Equals("both", StringComparison.OrdinalIgnoreCase) && translation.Length > 0)
            _afDisplay.PushCaptionTicker(original, translation);
        else
            _afDisplay.PushCaptionTicker(result.Fields?.GetValueOrDefault("display", result.Text) ?? result.Text);
    }

    private void OnIqPluginResult(IqPluginResult result)
    {
        if (_pluginUiSuspended || IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnIqPluginResult(result)); } catch (InvalidOperationException) { }
            return;
        }
        if (_pluginUiSuspended || IsDisposed || Disposing) return;
        try
        {
            if (!_appSettings.EnabledIqPluginIds.Contains(result.PluginId, StringComparer.OrdinalIgnoreCase)) return;
            if (result.PluginId.Equals("builtin.iq.kiwitimecode", StringComparison.OrdinalIgnoreCase))
                _kiwiTimecodeView.ApplyResult(result);
            else if (result.PluginId.Equals("builtin.iq.adsb", StringComparison.OrdinalIgnoreCase))
                _adsbView.ApplyResult(result);
            else if (result.PluginId.Equals("builtin.iq.lte", StringComparison.OrdinalIgnoreCase))
                _lteView.ApplyResult(result);
        }
        catch (ObjectDisposedException) { }
    }

    private static bool UiAlive(Control? control) => control is { IsDisposed: false, Disposing: false };

    private void AddFtxDecode(AfPluginResult result, AfPluginTabBinding? instanceTab = null)
    {
        var fields = result.Fields;
        if (fields is null || !long.TryParse(fields.GetValueOrDefault("slot"), out var slot)) return;
        var list = instanceTab?.ResultList ?? _ftxResultList;
        if (!UiAlive(list)) return;
        var slots = instanceTab?.FtxSlots ?? _ftxSlots;
        if (!slots.Contains(slot)) slots.Enqueue(slot);
        var rawMessage = fields.GetValueOrDefault("message", result.Text);
        if (list.Items.Count > 0 && list.Items[^1].Tag is long prevSlot && prevSlot != slot)
        {
            var sep = new ListViewItem(["", "", "", "", "─────────"])
            {
                Tag = prevSlot,
                ForeColor = Color.FromArgb(110, 140, 155),
                BackColor = Color.FromArgb(4, 18, 26)
            };
            list.Items.Add(sep);
        }
        var row = new ListViewItem([
            fields.GetValueOrDefault("utc", ""), fields.GetValueOrDefault("db", ""),
            fields.GetValueOrDefault("dt", ""), fields.GetValueOrDefault("freq", ""),
            rawMessage]) { Tag = slot, BackColor = Color.FromArgb(4, 18, 26) };
        if (rawMessage.StartsWith("CQ", StringComparison.OrdinalIgnoreCase))
            row.BackColor = Color.FromArgb(94, 65, 24);
        list.Items.Add(row);
        var decodedVfoId = instanceTab is null ? "main" :
            _appSettings.AfPluginInstances.FirstOrDefault(instance =>
                instance.InstanceId.Equals(instanceTab.InstanceId, StringComparison.OrdinalIgnoreCase))?.VfoId ?? "main";
        if (decodedVfoId.Equals(SelectedAfVfoId(), StringComparison.OrdinalIgnoreCase) &&
            float.TryParse(fields.GetValueOrDefault("freq"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var audioFrequency))
            _afDisplay.PushFtxMessage(audioFrequency, rawMessage, slot);
        if (_appSettings.FtxShowOnMainWaterfall)
        {
            var rfHz = _tunedFrequency;
            if (decodedVfoId.Equals("main", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(fields.GetValueOrDefault("freq"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var afHz))
                    rfHz = _tunedFrequency + (long)Math.Round(afHz - 1500);
            }
            else
            {
                var sub = _appSettings.SubVfos.FirstOrDefault(s => s.Id.Equals(decodedVfoId, StringComparison.OrdinalIgnoreCase));
                if (sub is not null) rfHz = sub.Frequency;
            }
            _display.PushFtxOverlay(rfHz, rawMessage, slot);
        }
        _smartRecord.OnFtxDecode(
            fields.GetValueOrDefault("utc", ""),
            fields.GetValueOrDefault("db", ""),
            fields.GetValueOrDefault("dt", ""),
            fields.GetValueOrDefault("freq", ""),
            rawMessage,
            decodedVfoId);
        if (list.Items.Count > 50)
        {
            while (slots.Count > 3)
            {
                var expired = slots.Dequeue();
                foreach (var item in list.Items.Cast<ListViewItem>().Where(item => item.Tag is long tag && tag == expired).ToArray())
                    list.Items.Remove(item);
            }
            while (list.Items.Count > 50) list.Items.RemoveAt(0);
        }
        row.EnsureVisible();
        list.Invalidate();
    }

    private void AddCwDecode(AfPluginResult result, AfPluginTabBinding? instanceTab)
    {
        var fields = result.Fields;
        if (fields is null || !int.TryParse(fields.GetValueOrDefault("channel"), out var channel)) return;
        var list = instanceTab?.ResultList ?? _cwResultList;
        if (!UiAlive(list)) return;
        var rows = instanceTab?.CwRows ?? _cwRows;
        var status = instanceTab?.StatusLabel ?? _cwStatusLabel;
        var rowKey = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var incoming = NormalizeCwText(fields.GetValueOrDefault("text", result.Text));
        if (incoming == " " && (!rows.TryGetValue(rowKey, out var existing) ||
                               string.IsNullOrWhiteSpace(existing.SubItems[2].Text))) return;
        if (!rows.TryGetValue(rowKey, out var row))
        {
            row = new ListViewItem(["", "", ""]) { Tag = channel, BackColor = Color.FromArgb(4, 18, 26) };
            rows[rowKey] = row;
            list.Items.Add(row);
            SortCwRows(list);
        }
        row.SubItems[0].Text = fields.GetValueOrDefault("trackedHz", "");
        row.SubItems[1].Text = fields.GetValueOrDefault("wpm", "");
        row.SubItems[2].Text = TrimCwTextToTwoLines(row.SubItems[2].Text + incoming, list);
        status.Text = $"Track {row.SubItems[0].Text} Hz · {row.SubItems[1].Text} WPM";
        PushCwDecodeToAfWaterfall(instanceTab, fields, incoming);
        row.EnsureVisible();
        list.Invalidate();
    }

    private void AddCwDecode(AfPluginResult result)
    {
        var fields = result.Fields;
        if (fields is null || !int.TryParse(fields.GetValueOrDefault("channel"), out var channel)) return;
        var rowKey = $"{result.PluginId}:{channel}";
        var incoming = fields.GetValueOrDefault("text", result.Text);
        incoming = NormalizeCwText(incoming);
        if (incoming == " " && (!_cwRows.TryGetValue(rowKey, out var existing) ||
                               string.IsNullOrWhiteSpace(existing.SubItems[2].Text))) return;
        if (!_cwRows.TryGetValue(rowKey, out var row))
        {
            row = new ListViewItem(["", "", ""])
            {
                Tag = rowKey, BackColor = Color.FromArgb(4, 18, 26),
                ToolTipText = AfPluginInstanceLabel(result.PluginId, "builtin.af.cw")
            };
            _cwRows[rowKey] = row;
            _cwResultList.Items.Add(row);
            SortCwRows();
        }
        var trackedHz = fields.GetValueOrDefault("trackedHz", "");
        var ordinal = AfPluginInstanceOrdinal(result.PluginId, "builtin.af.cw");
        row.SubItems[0].Text = ordinal > 0 ? $"{ordinal}:{trackedHz}" : trackedHz;
        row.SubItems[1].Text = fields.GetValueOrDefault("wpm", "");
        var text = TrimCwTextToTwoLines(row.SubItems[2].Text + incoming);
        row.SubItems[2].Text = text;
        _cwStatusLabel.Text = $"Track {row.SubItems[0].Text} Hz · {row.SubItems[1].Text} WPM";
        PushCwDecodeToAfWaterfall(result.PluginId, fields, incoming);
        row.EnsureVisible();
        _cwResultList.Invalidate();
    }

    private void PushCwDecodeToAfWaterfall(AfPluginTabBinding? instanceTab, IReadOnlyDictionary<string, string> fields, string incoming)
    {
        PushCwDecodeToAfWaterfall(instanceTab?.InstanceId, fields, incoming);
    }

    private void PushCwDecodeToAfWaterfall(string? instanceId, IReadOnlyDictionary<string, string> fields, string incoming)
    {
        if (!_appSettings.CwShowOnAfWaterfall || string.IsNullOrWhiteSpace(incoming)) return;
        var decodedVfoId = string.IsNullOrWhiteSpace(instanceId) ? "main" :
            _appSettings.AfPluginInstances.FirstOrDefault(instance =>
                instance.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))?.VfoId ?? "main";
        if (!decodedVfoId.Equals(SelectedAfVfoId(), StringComparison.OrdinalIgnoreCase)) return;
        if (!float.TryParse(fields.GetValueOrDefault("trackedHz"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var trackedHz)) return;
        _afDisplay.PushCwMessage(trackedHz, incoming);
    }

    private int AfPluginInstanceOrdinal(string instanceId, string pluginId)
    {
        var matching = _appSettings.AfPluginInstances
            .Where(instance => instance.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length <= 1) return 0;
        var index = Array.FindIndex(matching,
            instance => instance.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index + 1;
    }

    private string AfPluginInstanceLabel(string instanceId, string pluginId)
    {
        var matching = _appSettings.AfPluginInstances
            .Where(instance => instance.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length <= 1) return string.Empty;
        var index = Array.FindIndex(matching,
            instance => instance.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return string.Empty;
        var instance = matching[index];
        var decoder = pluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase)
            ? instance.Variant.ToUpperInvariant() : "CEC CW";
        var vfo = instance.VfoId.Equals("main", StringComparison.OrdinalIgnoreCase)
            ? "MAIN"
            : _appSettings.SubVfos.FirstOrDefault(item => item.Id.Equals(instance.VfoId, StringComparison.OrdinalIgnoreCase))?.Name
              ?? "VFO";
        return $"{decoder}#{index + 1} · {vfo}";
    }

    private string TrimCwTextToTwoLines(string? text)
    {
        text ??= string.Empty;
        if (text.Length <= 1 || _cwResultList.IsDisposed) return text;
        var width = _cwResultList.Columns.Count >= 3 ? Math.Max(60, _cwResultList.Columns[2].Width - 10) : 300;
        var font = _cwResultList.Font;
        if (font is null) return text;
        while (text.Length > 1 && TextRenderer.MeasureText(text, font, new Size(width, 1_000),
                   TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height > 34)
            text = text[1..];
        return text;
    }

    private static string TrimCwTextToTwoLines(string? text, ListView list)
    {
        text ??= string.Empty;
        if (text.Length <= 1 || list.IsDisposed) return text;
        var width = list.Columns.Count >= 3 ? Math.Max(60, list.Columns[2].Width - 10) : 300;
        while (text.Length > 1 && TextRenderer.MeasureText(text, list.Font, new Size(width, 1_000),
                   TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height > 34)
            text = text[1..];
        return text;
    }

    private static string NormalizeCwText(string text) => text
        .Replace("Ä", "A", StringComparison.Ordinal)
        .Replace("Ö", "O", StringComparison.Ordinal)
        .Replace("Ü", "U", StringComparison.Ordinal)
        .Replace("É", "E", StringComparison.Ordinal);

    private void SortCwRows()
    {
        var rows = _cwResultList.Items.Cast<ListViewItem>().OrderBy(item => item.Tag?.ToString()).ToArray();
        _resizingCwColumns = true;
        _cwResultList.BeginUpdate();
        try
        {
            _cwResultList.Items.Clear();
            _cwResultList.Items.AddRange(rows);
        }
        finally
        {
            _cwResultList.EndUpdate();
            _resizingCwColumns = false;
            _cwResultList.Invalidate();
        }
    }

    private static void SortCwRows(ListView list)
    {
        var rows = list.Items.Cast<ListViewItem>().OrderBy(item => item.Tag is int value ? value : int.MaxValue).ToArray();
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            list.Items.AddRange(rows);
        }
        finally
        {
            list.EndUpdate();
            list.Invalidate();
        }
    }

    private const int CwFrequencyResetThresholdHz = 500;

    private static bool IsAfCwDecoderWithReset(string pluginId) =>
        pluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase) ||
        pluginId.Equals("builtin.af.flcw", StringComparison.OrdinalIgnoreCase) ||
        pluginId.Equals("builtin.af.kiwicw", StringComparison.OrdinalIgnoreCase) ||
        pluginId.Equals("builtin.af.kiwicwskimmer", StringComparison.OrdinalIgnoreCase);

    private void ResetCwIfFrequencyChanged(long previousFrequency)
    {
        if (_tunedFrequency == previousFrequency) return;
        ResetAfcOffset();
        if (Math.Abs(_tunedFrequency - previousFrequency) >= 2_000)
            DiscardAfCaptions();
        if (Math.Abs(_tunedFrequency - previousFrequency) < CwFrequencyResetThresholdHz) return;
        ResetAllAfCwDecoders($"frequency {_tunedFrequency / 1_000_000d:0.000000} MHz");
    }

    private void DiscardAfCaptions()
    {
        _afDisplay.ClearCaptionTicker();
        var reset = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["command"] = "reset" };
        _afPluginHost.Configure(NeuroCaptionAfPlugin.PluginId, reset);
    }

    private void ResetAllAfCwDecoders(string reason)
    {
        foreach (var instance in _appSettings.AfPluginInstances)
        {
            if (!IsAfCwDecoderWithReset(instance.PluginId)) continue;
            ClearAfCwDecoderDisplay(instance);
            _afPluginHost.Reset(instance.InstanceId);
            if (_afPluginTabs.GetValueOrDefault(instance.InstanceId)?.StatusLabel is { } status)
                status.Text = $"Reset · {reason}";
        }
        if (_appSettings.AfPluginInstances.Any(instance =>
                instance.PluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase)))
            _cwStatusLabel.Text = $"Reset · {reason}";
        if (_appSettings.CwShowOnAfWaterfall)
            _afDisplay.ClearCwMessages();
    }

    private void ClearAfCwDecoderDisplay(AfPluginInstanceSettings instance)
    {
        if (instance.PluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase))
        {
            ClearCwDisplay(instance.InstanceId);
            return;
        }
        if (_afPluginTabs.GetValueOrDefault(instance.InstanceId)?.PluginView is FlCwPluginView flCw)
        {
            flCw.ClearDisplay();
            return;
        }
        if (_afPluginTabs.GetValueOrDefault(instance.InstanceId)?.PluginView is KiwiCwPluginView kiwiCw)
        {
            kiwiCw.ClearDisplay();
            return;
        }
        if (_afPluginTabs.GetValueOrDefault(instance.InstanceId)?.PluginView is KiwiCwSkimmerPluginView skimmer)
            skimmer.ClearDisplay();
    }

    private void ResetCwDecoder(string reason)
    {
        _afPluginHost.Reset("builtin.af.cw");
        _cwStatusLabel.Text = $"Reset · {reason}";
    }

    private void ResetCwDecoder(string instanceId, string reason)
    {
        _afPluginHost.Reset(instanceId);
        if (_afPluginTabs.GetValueOrDefault(instanceId)?.StatusLabel is { } status)
            status.Text = $"Reset · {reason}";
    }

    private void ClearCwDisplay()
    {
        _resizingCwColumns = true;
        _cwResultList.BeginUpdate();
        try
        {
            _cwResultList.Items.Clear();
            _cwRows.Clear();
        }
        finally
        {
            _cwResultList.EndUpdate();
            _resizingCwColumns = false;
        }
        _cwStatusLabel.Text = "Display cleared";
        if (_appSettings.CwShowOnAfWaterfall) _afDisplay.ClearCwMessages();
    }

    private void ClearCwDisplay(string instanceId)
    {
        var binding = _afPluginTabs.GetValueOrDefault(instanceId);
        if (binding?.ResultList is not { } list) return;
        var primary = ReferenceEquals(list, _cwResultList);
        if (primary) _resizingCwColumns = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            binding.CwRows?.Clear();
        }
        finally
        {
            list.EndUpdate();
            if (primary) _resizingCwColumns = false;
        }
        if (binding.StatusLabel is { } status) status.Text = "Display cleared";
        var vfoId = _appSettings.AfPluginInstances.FirstOrDefault(instance =>
            instance.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))?.VfoId ?? "main";
        if (_appSettings.CwShowOnAfWaterfall &&
            vfoId.Equals(SelectedAfVfoId(), StringComparison.OrdinalIgnoreCase))
            _afDisplay.ClearCwMessages();
    }

    private void ClearFtxDisplay()
    {
        _ftxResultList.Items.Clear();
        _ftxSlots.Clear();
        _display.ClearFtxOverlays();
        _afDisplay.ClearFtxMessages();
        _ftxStatusLabel.Text = "Display cleared";
    }

    private void ClearFtxDisplay(string instanceId)
    {
        var binding = _afPluginTabs.GetValueOrDefault(instanceId);
        binding?.ResultList?.Items.Clear();
        binding?.FtxSlots?.Clear();
        _afDisplay.ClearFtxMessages();
        if (binding?.StatusLabel is { } status) status.Text = "Display cleared";
    }

    private void ApplyFtxControls()
    {
        if (_synchronizingFtxControls) return;
        _appSettings.FtxTimeAdjustSeconds = (double)_ftxTimeAdjust.Value;
        _appSettings.FtxAutoTimeAdjust = _ftxAutoAdjust.Checked;
        SynchronizeFtxControls();
        ApplyAfPluginActivation();
    }

    private void ApplyFtxSharedControls(NumericUpDown timeAdjust, CheckBox autoAdjust, CheckBox qsoLines)
    {
        if (_synchronizingFtxControls) return;
        _appSettings.FtxTimeAdjustSeconds = (double)timeAdjust.Value;
        _appSettings.FtxAutoTimeAdjust = autoAdjust.Checked;
        _appSettings.FtxShowQsoLines = qsoLines.Checked;
        _afDisplay.ShowQsoLines = qsoLines.Checked;
        SynchronizeFtxControls();
        ApplyAfPluginActivation();
    }

    private void SynchronizeFtxControls()
    {
        _synchronizingFtxControls = true;
        foreach (var control in _afPluginTabs.Values
                     .Where(binding => binding.PluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase))
                     .SelectMany(binding => DescendantControls(binding.Page)))
        {
            if (control is NumericUpDown number && Equals(number.Tag, "ftx-time"))
                number.Value = Math.Clamp((decimal)_appSettings.FtxTimeAdjustSeconds, number.Minimum, number.Maximum);
            else if (control is CheckBox check && Equals(check.Tag, "ftx-auto"))
                check.Checked = _appSettings.FtxAutoTimeAdjust;
            else if (control is CheckBox qso && Equals(qso.Tag, "ftx-qso"))
                qso.Checked = _appSettings.FtxShowQsoLines;
        }
        _synchronizingFtxControls = false;
    }

    private void SynchronizeCwControls()
    {
        foreach (var control in _afPluginTabs.Values
                     .Where(binding => binding.PluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase))
                     .SelectMany(binding => DescendantControls(binding.Page)))
        {
            if (control is CheckBox check && Equals(check.Tag, "cw-afwf"))
                check.Checked = _appSettings.CwShowOnAfWaterfall;
        }
    }

    private static IEnumerable<Control> DescendantControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in DescendantControls(child)) yield return descendant;
        }
    }

    private void ApplySlowModePluginSettings()
    {
        ConfigureSstvPlugin();
        ConfigureRttyPlugin();
        ConfigureWeatherFaxPlugin();
        ConfigureKiwiNavtexPlugin();
        ConfigureKiwiWwvPlugin();
        ConfigureFlRttyPlugin();
        ConfigureFlCwPlugin();
        ConfigureFlFaxPlugin();
        ConfigureVisualAfPlugins();
        ConfigureKiwiTimecodePlugin();
        ConfigureAdsbPlugin();
        ConfigureLtePlugin();
    }

    private bool AfPluginRunning(string id) => _afPluginHost.IsEnabled(id);
    private bool IqPluginRunning(string id) => _iqPluginHost.IsEnabled(id);

    private AfPluginTabBinding? AfBindingForPage(TabPage page) =>
        _afPluginTabs.Values.FirstOrDefault(binding => ReferenceEquals(binding.Page, page));

    private AfPluginTabBinding? SelectedAfPluginBinding()
    {
        AfPluginTabBinding? RememberedIfVisible()
        {
            if (string.IsNullOrWhiteSpace(_lastSelectedAfInstanceId) ||
                !_afPluginTabs.TryGetValue(_lastSelectedAfInstanceId, out var remembered) ||
                remembered.Page is not { IsDisposed: false } page)
                return null;
            return page.Parent is TabControl host && !host.IsDisposed &&
                   ReferenceEquals(host.SelectedTab, page)
                ? remembered
                : null;
        }

        var remembered = RememberedIfVisible();
        if (remembered is not null) return remembered;

        if (_decoderTabs is { IsDisposed: false } && _decoderTabs.SelectedTab is TabPage selectedPage)
        {
            var fromTabs = AfBindingForPage(selectedPage);
            if (fromTabs is not null) return fromTabs;
        }

        return _afPluginTabs.Values.FirstOrDefault(binding =>
            binding.Page.Parent is TabControl host &&
            !host.IsDisposed &&
            ReferenceEquals(host.SelectedTab, binding.Page));
    }

    private string? SelectedAfPluginId() => SelectedAfPluginBinding()?.PluginId;

    private void RefreshAfPluginVfoChoices()
    {
        _synchronizingAfPluginVfo = true;
        foreach (var binding in _afPluginTabs.Values)
        {
            var route = _appSettings.AfPluginInstances.FirstOrDefault(instance =>
                instance.InstanceId.Equals(binding.InstanceId, StringComparison.OrdinalIgnoreCase))?.VfoId ?? "main";
            binding.VfoBox.Items.Clear();
            binding.VfoBox.Items.Add(new VfoChoice("main", "MAIN VFO"));
            if (_source is not IRemoteAudioSampleSource)
            {
                foreach (var sub in _appSettings.SubVfos) binding.VfoBox.Items.Add(new VfoChoice(sub.Id, sub.Name));
            }
            var selectedId = _source is IRemoteAudioSampleSource ? "main" : route;
            binding.VfoBox.SelectedItem = binding.VfoBox.Items.Cast<VfoChoice>().FirstOrDefault(item =>
                item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) ?? binding.VfoBox.Items[0];
            binding.VfoBox.Enabled = _source is not IRemoteAudioSampleSource;
            binding.VfoBox.Visible = true;
        }
        _synchronizingAfPluginVfo = false;
        RefreshAfPluginVfoSelection();
    }

    private void RefreshAfPluginVfoSelection()
    {
        InvalidateAfPluginChrome();
    }

    private void ApplyAfPluginActivation()
    {
        IEnumerable<string>? activeIds = null;
        if (_appSettings.SingleActiveAfPlugin)
        {
            var selected = SelectedAfPluginBinding();
            if (selected is not null) _lastSelectedAfInstanceId = selected.InstanceId;
            else if (_lastSelectedAfInstanceId is not null &&
                     _afPluginTabs.TryGetValue(_lastSelectedAfInstanceId, out var lastAf))
                selected = lastAf;
            activeIds = selected is null
                ? FirstEnabledAfPluginIds()
                : _appSettings.AfPluginInstances.Where(instance =>
                        instance.PluginId.Equals(selected.PluginId, StringComparison.OrdinalIgnoreCase))
                    .Select(instance => instance.InstanceId).ToArray();
            if (_eibiAiScanning && !string.IsNullOrWhiteSpace(_eibiAiScanCaptionId))
                activeIds = activeIds.Concat([_eibiAiScanCaptionId]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        _afPluginHost.Apply(_appSettings.AfPluginInstances, _appSettings.FtxMode,
            _appSettings.FtxTimeAdjustSeconds, _appSettings.FtxAutoTimeAdjust, activeIds);
        ApplyAfPluginRoutesForCurrentSource();
        EnsureAfDisplayFollowsMultiChannelCw();
        InvalidateAfPluginChrome();
        RefreshEibiWaterfall(force: true);
        RefreshEibiAiScanAvailability();
    }

    /// <summary>
    /// Multi-channel CW needs continuous AF audio on its routed VFO. The AF spectrum/waterfall
    /// only updates for the selected AF-display VFO, so lock that selection to the CW route
    /// while any Multi-channel CW Decoder instance is running.
    /// </summary>
    private string? MultiChannelCwForcedAfVfoId()
    {
        if (!_appSettings.EnabledAfPluginIds.Contains("builtin.af.cw", StringComparer.OrdinalIgnoreCase))
            return null;
        if (!_afPluginHost.IsEnabled("builtin.af.cw")) return null;

        var selected = SelectedAfPluginBinding();
        if (selected is not null &&
            selected.PluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase) &&
            _afPluginHost.IsEnabled(selected.InstanceId))
            return ResolveAfPluginVfoId(selected.InstanceId);

        foreach (var instance in _appSettings.AfPluginInstances)
        {
            if (!instance.PluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_afPluginHost.IsEnabled(instance.InstanceId)) continue;
            return ResolveAfPluginVfoId(instance.InstanceId);
        }
        return null;
    }

    private string ResolveAfPluginVfoId(string instanceId)
    {
        if (_source is IRemoteAudioSampleSource) return "main";
        if (_appSettings.AfPluginVfoRoutes.TryGetValue(instanceId, out var routed) &&
            !string.IsNullOrWhiteSpace(routed))
            return routed;
        var instance = _appSettings.AfPluginInstances.FirstOrDefault(item =>
            item.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(instance?.VfoId) ? "main" : instance!.VfoId;
    }

    private int _afPluginRouteApplyDepth;

    private void ApplyAfPluginRoutesForCurrentSource()
    {
        if (_afPluginRouteApplyDepth > 0) return;
        _afPluginRouteApplyDepth++;
        try
        {
            if (_source is IRemoteAudioSampleSource)
            {
                var forced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var instance in _appSettings.AfPluginInstances)
                    forced[instance.InstanceId] = "main";
                foreach (var pair in _appSettings.AfPluginVfoRoutes)
                    forced[pair.Key] = "main";
                _afPluginHost.SetRoutes(forced);
            }
            else
            {
                _afPluginHost.SetRoutes(_appSettings.AfPluginVfoRoutes);
            }
            RefreshAfPluginVfoChoices();
        }
        finally
        {
            _afPluginRouteApplyDepth--;
        }
    }

    private void EnsureAfDisplayFollowsMultiChannelCw()
    {
        var forced = MultiChannelCwForcedAfVfoId();
        if (forced is not null &&
            !SelectedAfVfoId().Equals(forced, StringComparison.OrdinalIgnoreCase))
            SelectAfDisplayVfo(forced, allowWhileCwLocked: true);
        else
            ApplyAfDisplayVfoLockChrome(forced);
    }

    private void ApplyAfDisplayVfoLockChrome(string? forced)
    {
        if (_afDisplayVfoPanel.IsDisposed) return;
        foreach (var button in _afDisplayVfoPanel.Controls.OfType<Button>())
        {
            var selected = string.Equals(button.Tag?.ToString(), SelectedAfVfoId(), StringComparison.OrdinalIgnoreCase);
            button.Enabled = forced is null || selected;
        }
    }

    private void ApplyIqPluginActivation()
    {
        _iqPluginHost.Apply(_appSettings.EnabledIqPluginIds);
        ConfigureKiwiTimecodePlugin();
        ConfigureAdsbPlugin();
        ConfigureLtePlugin();
    }

    private string[] FirstEnabledAfPluginIds()
    {
        foreach (var instance in _appSettings.AfPluginInstances)
        {
            if (AfPluginHasDisplay(instance.PluginId) ||
                instance.PluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase) ||
                instance.PluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase))
                return [instance.InstanceId];
        }
        foreach (var id in _appSettings.EnabledAfPluginIds)
        {
            if (AfPluginHasDisplay(id) ||
                id.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase))
                return [id];
        }
        return [];
    }

    private void ConfigureSstvPlugin(string? command = null)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = AfPluginRunning("builtin.af.sstv").ToString(),
            ["backend"] = _appSettings.SstvBackend,
            ["autoVis"] = _appSettings.SstvAutoVis.ToString(),
            ["manualMode"] = _appSettings.SstvManualMode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["frequencyShift"] = _appSettings.SstvFrequencyShiftHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["adaptive"] = _appSettings.SstvAdaptive.ToString(),
            ["weakSignal"] = _appSettings.SstvWeakSignal.ToString(),
            ["slant"] = _appSettings.SstvSlant.ToString(),
            ["median"] = _appSettings.SstvMedian.ToString(),
            ["fskId"] = _appSettings.SstvFskId.ToString()
        };
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _afPluginHost.Configure("builtin.af.sstv", options);
    }

    private void ConfigureRttyPlugin(string? command = null)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = AfPluginRunning("builtin.af.rtty").ToString(),
            ["baud"] = _appSettings.RttyBaud.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["center"] = _appSettings.RttyCenterHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deviation"] = _appSettings.RttyDeviationHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["inverse"] = _appSettings.RttyInverse.ToString(),
            ["autoPolarity"] = _appSettings.RttyAutoPolarity.ToString(),
            ["usos"] = _appSettings.RttyUsos.ToString(),
            ["unshiftOnError"] = _appSettings.RttyUnshiftOnError.ToString(),
            ["stopBits"] = _appSettings.RttyStopBits.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _afPluginHost.Configure("builtin.af.rtty", options);
    }

    private void ConfigureWeatherFaxPlugin(string? command = null)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = AfPluginRunning("builtin.af.weatherfax").ToString(),
            ["lps"] = (_appSettings.WeatherFaxLpm == 60 ? 1 : 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["calibration"] = _appSettings.WeatherFaxCalibration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["grayscale"] = _appSettings.WeatherFaxGrayscale.ToString(),
            ["videoFilter"] = _appSettings.WeatherFaxVideoFilter.ToString(),
            ["skipStartTone"] = _appSettings.WeatherFaxSkipStartTone.ToString(),
            ["width"] = _appSettings.WeatherFaxImageWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _afPluginHost.Configure("builtin.af.weatherfax", options);
    }

    private void ConfigureVisualAfPlugins()
    {
        foreach (var instance in _appSettings.AfPluginInstances)
        {
            if (_afPluginHost.Plugin(instance.InstanceId) is not IAfVisualPlugin) continue;
            var bag = _appSettings.AfPluginUiState.GetValueOrDefault(instance.PluginId) ??
                      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var options = new Dictionary<string, string>(bag, StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = AfPluginRunning(instance.PluginId).ToString()
            };
            _afPluginHost.Configure(instance.InstanceId, options);
        }
        RefreshEibiAiScanAvailability();
    }

    private string BindHostWaveOutForPlugin(int outputChannel1Based, string deviceNameContains, int volumePercent)
    {
        if (InvokeRequired)
        {
            try
            {
                string result = "UI unavailable";
                // Timed wait — never use bare Control.Invoke from the AF/RX thread.
                // Stopping RX on the UI while this Invoke is pending deadlocks the app.
                using var done = new ManualResetEventSlim(false);
                BeginInvoke(new Action(() =>
                {
                    try { result = BindHostWaveOutForPlugin(outputChannel1Based, deviceNameContains, volumePercent); }
                    finally { done.Set(); }
                }));
                return done.Wait(TimeSpan.FromMilliseconds(750)) ? result : "UI busy";
            }
            catch (InvalidOperationException)
            {
                return "UI unavailable";
            }
        }

        var channelIndex = outputChannel1Based == 2 ? 1 : 0;
        var settings = channelIndex == 0 ? _appSettings.Audio1 : _appSettings.Audio2;
        var hint = string.IsNullOrWhiteSpace(deviceNameContains) ? "CABLE Input" : deviceNameContains;
        var device = _audioDevices.FirstOrDefault(d =>
                         d.Id >= 0 && d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                     ?? _audioDevices.FirstOrDefault(d =>
                         d.Id >= 0 && d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        if (device is null)
            return $"no WaveOut matching '{hint}'";

        settings.Enabled = true;
        settings.DeviceId = device.Id;
        settings.DeviceName = device.Name;
        settings.Volume = Math.Clamp(volumePercent, 0, 100);
        // Clear digital-voice ownership so analog AF reaches OUT-VB for DSD+.
        DigitalVoicePlayback.OwnedOutputIndex = -1;
        DigitalVoicePlayback.Clear();

        if (channelIndex == 0)
        {
            _rxVolumeBar.Value = settings.Volume;
            _volumeSlider.Value = settings.Volume;
        }
        else
            _rxVolumeBar2.Value = settings.Volume;

        _audioCheck.Checked = true;
        _audioEnabled = true;
        try
        {
            OpenAudioOutputs();
        }
        catch (Exception exception)
        {
            return "WaveOut open failed: " + exception.GetBaseException().Message;
        }

        UpdateRxPanel();
        UpdateOutputOwnerCaptions();
        return $"OUT{channelIndex + 1} · #{device.Id} {device.Name} vol={settings.Volume}%";
    }

    private int? _dsdPlusSavedMainVolume;

    private string SetHostMainMutedForPlugin(bool muted)
    {
        if (InvokeRequired)
        {
            try
            {
                string result = "UI unavailable";
                using var done = new ManualResetEventSlim(false);
                BeginInvoke(new Action(() =>
                {
                    try { result = SetHostMainMutedForPlugin(muted); }
                    finally { done.Set(); }
                }));
                return done.Wait(TimeSpan.FromMilliseconds(750)) ? result : "UI busy";
            }
            catch (InvalidOperationException)
            {
                return "UI unavailable";
            }
        }

        if (muted)
        {
            _dsdPlusSavedMainVolume ??= _appSettings.Audio1.Volume;
            _appSettings.Audio1.Volume = 0;
            _rxVolumeBar.Value = 0;
            _volumeSlider.Value = 0;
            if (_audioOutputs[0] is { } main) main.VolumePercent = 0;
            return "MAIN muted (DSD+ speakers)";
        }

        if (_dsdPlusSavedMainVolume is int saved)
        {
            _appSettings.Audio1.Volume = saved;
            _rxVolumeBar.Value = Math.Clamp(saved, 0, 100);
            _volumeSlider.Value = Math.Clamp(saved, 0, 100);
            if (_audioOutputs[0] is { } main) main.VolumePercent = saved;
            _dsdPlusSavedMainVolume = null;
            return $"MAIN restored vol={saved}%";
        }

        return "MAIN already unmuted";
    }

    private Control? ResolveAfPluginView(AfPluginInstanceSettings instance, bool primary)
    {
        if (_afPluginHost.Plugin(instance.InstanceId) is IAfVisualPlugin visual)
            return visual.CreateView(new AfPluginUiHost(
                instance.InstanceId,
                instance.PluginId,
                () => AfPluginRunning(instance.PluginId),
                () => _tunedFrequency,
                () => _appSettings.AfPluginUiState.GetValueOrDefault(instance.PluginId) ??
                      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                options =>
                {
                    _appSettings.AfPluginUiState[instance.PluginId] =
                        new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);
                    if (NeuroCaption.IsCaptionPlugin(instance.PluginId))
                        RefreshEibiAiScanAvailability();
                },
                options => _afPluginHost.Configure(instance.InstanceId, options),
                BindHostWaveOutForPlugin,
                SetHostMainMutedForPlugin));
        return primary ? PrimaryAfPluginView(instance.PluginId) : CreateAfPluginView(instance.PluginId);
    }

    private void ConfigureKiwiNavtexPlugin(string? command = null)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = AfPluginRunning("builtin.af.kiwinavtex").ToString(),
            ["baud"] = "100",
            ["center"] = _appSettings.KiwiNavtexCenterHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deviation"] = _appSettings.KiwiNavtexDeviationHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["inverse"] = _appSettings.KiwiNavtexInverse.ToString(),
            ["pll"] = _appSettings.KiwiNavtexPll.ToString()
        };
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _afPluginHost.Configure("builtin.af.kiwinavtex", options);
    }

    private void ConfigureKiwiWwvPlugin(string? command = null)
    {
        var options = _kiwiWwvView.BuildOptions(AfPluginRunning("builtin.af.kiwiwwv"));
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _afPluginHost.Configure("builtin.af.kiwiwwv", options);
    }

    private void ConfigureFlRttyPlugin(string? command = null)
    {
        var options = _flRttyView.BuildOptions(AfPluginRunning("builtin.af.flrtty"));
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _afPluginHost.Configure("builtin.af.flrtty", options);
    }

    private void ConfigureFlCwPlugin(string? command = null)
    {
        var options = _flCwView.BuildOptions(AfPluginRunning("builtin.af.flcw"));
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _afPluginHost.Configure("builtin.af.flcw", options);
    }

    private void ConfigureFlFaxPlugin(string? command = null)
    {
        var options = _flFaxView.BuildOptions(AfPluginRunning("builtin.af.flfax"));
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _afPluginHost.Configure("builtin.af.flfax", options);
    }

    private void ConfigureKiwiTimecodePlugin(string? command = null)
    {
        var options = _kiwiTimecodeView.BuildOptions(IqPluginRunning("builtin.iq.kiwitimecode"));
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _iqPluginHost.Configure("builtin.iq.kiwitimecode", options);
    }

    private void ConfigureAdsbPlugin(string? command = null)
    {
        var options = _adsbView.BuildOptions(IqPluginRunning("builtin.iq.adsb"));
        if (!string.IsNullOrEmpty(command)) options["command"] = command;
        _iqPluginHost.Configure("builtin.iq.adsb", options);
    }

    private void ConfigureLtePlugin(string? command = null)
    {
        var options = _lteView.BuildOptions(IqPluginRunning("builtin.iq.lte"));
        if (!string.IsNullOrEmpty(command))
        {
            if (command.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
                options["mode"] = command["mode:".Length..].Trim();
            else
                options["command"] = command;
        }
        _iqPluginHost.Configure("builtin.iq.lte", options);
    }

    private Control WireEibiAiScan(Control bar, EibiBroadcastPluginView view)
    {
        view.AiScanRequested -= OnEibiAiScanRequested;
        view.AiScanRequested += OnEibiAiScanRequested;
        RefreshEibiAiScanAvailability();
        return bar;
    }

    private void OnEibiAiScanRequested(bool start)
    {
        if (start) StartEibiAiScan();
        else StopEibiAiScan();
    }

    private void RefreshEibiAiScanAvailability()
    {
        if (IsDisposed || Disposing) return;
        var live = CaptionScanReady();
        foreach (var binding in _afPluginTabs.Values)
        {
            if (binding.PluginView is EibiBroadcastPluginView eibi)
                eibi.SetAiScanAvailable(live);
        }
    }

    private bool CaptionScanReady()
    {
        var instance = CaptionInstance();
        if (instance is null) return false;
        var bag = _appSettings.AfPluginUiState.GetValueOrDefault(instance.PluginId);
        return NeuroCaption.CaptionOn(bag) && NeuroCaption.EngineReady(bag);
    }

    private AfPluginInstanceSettings? CaptionInstance() =>
        _appSettings.AfPluginInstances.FirstOrDefault(item => NeuroCaption.IsCaptionPlugin(item.PluginId));

    private NeuroCaptionAfPlugin? CaptionPlugin()
    {
        var instance = CaptionInstance();
        return instance is null ? null : _afPluginHost.Plugin(instance.InstanceId) as NeuroCaptionAfPlugin;
    }

    private void StartEibiAiScan()
    {
        var view = _afPluginTabs.Values.Select(item => item.PluginView).OfType<EibiBroadcastPluginView>().FirstOrDefault();
        var caption = CaptionInstance();
        if (view is null || caption is null) return;
        var bag = _appSettings.AfPluginUiState.GetValueOrDefault(caption.PluginId);
        if (!NeuroCaption.CaptionOn(bag) || !NeuroCaption.EngineReady(bag))
        {
            view.SetAiScanning(false);
            return;
        }
        StopEibiAiScan();
        if (!_source.IsRunning) ToggleReceiver();
        if (!_source.IsRunning) return;
        var start = view.SelectedRowIndex;
        var listedRows = view.RowsFrom(start);
        if (listedRows.Count == 0) return;
        var rows = listedRows
            .GroupBy(item => item.FrequencyHz)
            .Select(group => group.First())
            .ToArray();
        view.ClearScanFrom(start);
        _eibiAiScanCaptionId = caption.InstanceId;
        _eibiAiScanning = true;
        ApplyAfPluginActivation();
        TuneToShortwaveBroadcast(rows[0].FrequencyHz, rows[0].Station, centerWaterfall: false, refreshChrome: false);
        view.SetAiScanning(true);
        _eibiAiScanCts = new CancellationTokenSource();
        var token = _eibiAiScanCts.Token;
        _ = RunEibiAiScanAsync(view, rows, token);
    }

    private void StopEibiAiScan()
    {
        _eibiAiScanCts?.Cancel();
        _eibiAiScanCts?.Dispose();
        _eibiAiScanCts = null;
        CaptionPlugin()?.SetLanguageProbe(false);
        var was = _eibiAiScanning;
        _eibiAiScanning = false;
        _eibiAiScanCaptionId = null;
        foreach (var binding in _afPluginTabs.Values)
        {
            if (binding.PluginView is EibiBroadcastPluginView eibi)
                eibi.SetAiScanning(false);
        }
        if (was) ApplyAfPluginActivation();
        RefreshEibiAiScanAvailability();
    }

    private async Task RunEibiAiScanAsync(EibiBroadcastPluginView view, IReadOnlyList<EibiEntry> rows, CancellationToken token)
    {
        var seen = new Dictionary<long, (string Text, Color Color)>();
        try
        {
            CaptionPlugin()?.SetLanguageProbe(false);
            foreach (var entry in rows)
            {
                token.ThrowIfCancellationRequested();
                if (!_source.IsRunning) break;
                var key = EibiBroadcastPluginView.EntryKey(entry);
                if (view.IsDisposed) return;
                view.HighlightKey(key);
                if (seen.TryGetValue(entry.FrequencyHz, out var prior))
                {
                    view.WriteScan(key, prior.Text, prior.Color);
                    continue;
                }

                view.WriteScanForFrequency(entry.FrequencyHz, "...", Color.FromArgb(160, 176, 184));
                var frameBeforeTune = Volatile.Read(ref _rfSpectrumFrame);
                var hasReferenceBands = StationInAnalysisView(entry.FrequencyHz, 10_000);
                ChangeVfoFromDigitalDisplay(entry.FrequencyHz, userInitiated: true,
                    keepSpectrumView: hasReferenceBands);
                if (!hasReferenceBands) CenterViewOnVfo();
                _statusLabel.Text = $"Scan · {entry.Station} · {entry.FrequencyHz / 1_000d:0.#} kHz";
                var measured = await WaitScanOccupancyAsync(
                    entry.FrequencyHz, 10_000, frameBeforeTune, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (!_source.IsRunning) break;
                if (measured is null)
                {
                    CaptionPlugin()?.SetLanguageProbe(false);
                    RecordScan(view, seen, entry.FrequencyHz, "?", Color.FromArgb(150, 160, 168));
                    continue;
                }
                var occupancy = measured.Value;
                if (!occupancy.Occupied)
                {
                    CaptionPlugin()?.SetLanguageProbe(false);
                    RecordScan(view, seen, entry.FrequencyHz, "-", Color.FromArgb(96, 112, 120));
                    continue;
                }
                RecordScan(view, seen, entry.FrequencyHz, SignalGlyph(occupancy.SnrDb), SignalColor(occupancy.SnrDb));
                CaptionPlugin()?.SetLanguageProbe(true);
                await Task.Delay(5_200, token).ConfigureAwait(true);
                if (!_source.IsRunning) break;
                var until = DateTime.UtcNow.AddSeconds(10);
                var lang = "";
                while (DateTime.UtcNow < until)
                {
                    token.ThrowIfCancellationRequested();
                    if (!_source.IsRunning) break;
                    lang = CaptionPlugin()?.LastHeardLanguage ?? "";
                    var heardAt = CaptionPlugin()?.LastHeardUtc ?? DateTime.MinValue;
                    if (lang.Length > 0 && lang != "auto" && heardAt > DateTime.UtcNow.AddSeconds(-12))
                        break;
                    await Task.Delay(250, token).ConfigureAwait(true);
                }
                CaptionPlugin()?.SetLanguageProbe(false);
                occupancy = MeasureTunedOccupancy(entry.FrequencyHz, 10_000);
                if (lang.Length > 0 && lang != "auto")
                    RecordScan(view, seen, entry.FrequencyHz, lang.ToLowerInvariant(), Color.FromArgb(120, 220, 255));
                else
                    RecordScan(view, seen, entry.FrequencyHz, SignalGlyph(occupancy.SnrDb), SignalColor(occupancy.SnrDb));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            CaptionPlugin()?.SetLanguageProbe(false);
            if (!IsDisposed && !Disposing)
            {
                if (InvokeRequired) BeginInvoke(StopEibiAiScan);
                else StopEibiAiScan();
            }
        }
    }

    private static void RecordScan(EibiBroadcastPluginView view, Dictionary<long, (string Text, Color Color)> seen,
        long frequencyHz, string text, Color color)
    {
        seen[frequencyHz] = (text, color);
        view.WriteScanForFrequency(frequencyHz, text, color);
    }

    private bool StationInAnalysisView(long frequencyHz, int bandwidthHz)
    {
        var requiredMargin = bandwidthHz / 2L + Math.Max(1_000, (int)(bandwidthHz * .15)) +
                             Math.Max(20_000, bandwidthHz * 2);
        var half = Math.Max(5_000, _viewBandwidth) / 2L;
        var left = _viewCenterFrequency - half + requiredMargin;
        var right = _viewCenterFrequency + half - requiredMargin;
        return frequencyHz >= left && frequencyHz <= right;
    }

    private async Task<SpectrumOccupancy.Result?> WaitScanOccupancyAsync(
        long frequencyHz, int bandwidthHz, long frameBeforeTune, CancellationToken token)
    {
        var best = new SpectrumOccupancy.Result(-140f, -140f, 0f, false);
        var positiveFrames = 0;
        var validFrames = 0;
        var observedFrame = frameBeforeTune;
        var deadline = DateTime.UtcNow.AddMilliseconds(3_200);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (!_source.IsRunning) break;
            await Task.Delay(120, token).ConfigureAwait(true);
            var frame = Volatile.Read(ref _rfSpectrumFrame);
            if (frame <= observedFrame) continue;
            observedFrame = frame;
            var sample = MeasureTunedOccupancy(frequencyHz, bandwidthHz);
            if (sample.SignalDb <= -139f && sample.NoiseDb <= -139f) continue;
            validFrames++;
            if (sample.SnrDb > best.SnrDb) best = sample;
            if (sample.Occupied) positiveFrames++;
            if (sample.SnrDb >= 7f || positiveFrames >= 2)
                return sample with { Occupied = true };
        }
        if (validFrames == 0) return null;
        return best with { Occupied = positiveFrames >= 2 || best.SnrDb >= 7f };
    }

    private SpectrumOccupancy.Result MeasureTunedOccupancy(long frequencyHz, int bandwidthHz)
    {
        var spectrum = _scanSpectrum ?? _lastRfSpectrum;
        var center = _scanSpectrumSpan > 0 ? _scanSpectrumCenter :
            (_lastRfSpectrumCenter != 0 ? _lastRfSpectrumCenter : Interlocked.Read(ref _rfCenterFrequency));
        var span = _scanSpectrumSpan > 0 ? _scanSpectrumSpan : _lastRfSpectrumSampleRate;
        if (spectrum is null || spectrum.Length == 0 || span <= 0)
            return new(-140f, -140f, 0f, false);
        return SpectrumOccupancy.EvaluateBroadcast(
            spectrum, center, span, frequencyHz, bandwidthHz);
    }

    private static string SignalGlyph(float snrDb) =>
        snrDb >= 18 ? "●●" : snrDb >= 10 ? "●" : "·";

    private static Color SignalColor(float snrDb)
    {
        if (snrDb >= 18) return Color.FromArgb(90, 230, 140);
        if (snrDb >= 12) return Color.FromArgb(210, 210, 80);
        if (snrDb >= 8) return Color.FromArgb(230, 170, 70);
        return Color.FromArgb(200, 120, 70);
    }
}
