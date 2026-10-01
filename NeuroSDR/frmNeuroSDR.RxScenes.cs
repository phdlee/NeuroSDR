using NeuroSDR.Audio;
using NeuroSDR.Controls;
using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;
using NeuroSDR.Plugins;
using NeuroSDR.Plugins.DigitalVoice;
using NeuroSDR.Settings;
using System.Diagnostics;

namespace NeuroSDR;

public partial class frmNeuroSDR
{

    private void ApplySceneChannelStripLayout()
    {
        EnsureRxShiftBaselines();
        if (!_radioPanel.Controls.Contains(_sceneChannelStrip))
        {
            _radioPanel.Controls.Add(_sceneChannelStrip);
            _sceneChannelStrip.BringToFront();
        }
        var lines = Math.Clamp(_sceneChannelStrip.LineCount, 1, 2);
        var stripH = lines * SceneChannelStrip.LineHeight;
        // SCENE row — channel strip — separator — modes (one visual group above the line).
        const int stripTop = 118;
        _sceneChannelStrip.Location = new Point(10, stripTop);
        _sceneChannelStrip.Width = Math.Max(40, _radioPanel.ClientSize.Width - 20);
        _sceneChannelStrip.Height = stripH;
        _rxSceneModeSep.Location = new Point(10, stripTop + stripH + 2);
        _rxSceneModeSep.Width = Math.Max(40, _radioPanel.ClientSize.Width - 20);
        // Designer modes start at Y=121; new content top is sep+5.
        var offset = (_rxSceneModeSep.Top + 5) - 121;
        if (_rxShiftControls is not null && _rxShiftBaseY is not null)
        {
            for (var i = 0; i < _rxShiftControls.Length; i++)
                _rxShiftControls[i].Top = _rxShiftBaseY[i] + offset;
        }
        _subVfoArea.Top = _subVfoBaseTop + offset;
        var grow = SceneChannelStrip.LineHeight; // line 1 grows RX/AF panel; line 2+ steals SUB height
        if (grow != _channelAfGrow)
        {
            _channelAfGrow = grow;
            _afPanel.Height = Math.Clamp(
                _appSettings.AfPluginDisplayHeight + ReclaimedHeaderAfHeight + _channelAfGrow,
                AfPanelMinHeight, 720 + _channelAfGrow);
        }
    }


    private Panel BuildDecoderResultPanel()
    {
        ConfigureFtxList();
        ConfigureCwList();
        _decoderTabs.IsPageOutOfRange = IsAfPluginPageOutOfRange;
        _decoderTabs.IsPluginActive = IsAfPluginChromeActive;
        _kiwiTimecodeDecoderTab.Controls.Add(_kiwiTimecodeView);
        _adsbDecoderTab.Controls.Add(_adsbView);
        _lteDecoderTab.Controls.Add(_lteView);
        _sstvView.LoadSettings(_appSettings);
        _rttyView.LoadSettings(_appSettings);
        _weatherFaxView.LoadSettings(_appSettings);
        _kiwiNavtexView.LoadSettings(_appSettings);
        _kiwiWwvView.LoadSettings(_appSettings);
        _flRttyView.LoadSettings(_appSettings);
        _flCwView.LoadSettings(_appSettings);
        _flFaxView.LoadSettings(_appSettings);
        _kiwiTimecodeView.LoadSettings(_appSettings);
        _adsbView.LoadSettings(_appSettings);
        _lteView.LoadSettings(_appSettings);
        UpdateAfPluginDisplayLayout();
        RefreshAfPluginVfoChoices();
        return _decoderPanel;
    }

    private TableLayoutPanel BuildDecoderTabLayout(Control controlBar, Control content)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = _decoderPanel.BackColor,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(controlBar, 0, 0);
        layout.Controls.Add(content, 0, 1);
        return layout;
    }

    private void ApplyModeDefaults()
    {
        var mode = Enum.Parse<RadioMode>((string)_modeBox.SelectedItem!);
        _demodulator.Mode = mode;
        _bandwidthBox.Value = RadioModes.DefaultBandwidth(mode);
        ConfigureDisplay();
        EnsureViewFitsFilter();
        ConfigureAfDisplay();
    }

    private void RefreshRxSceneCombo(string? selectId = null)
    {
        AppSettingsStore.EnsureBuiltInRxScenes(_appSettings);
        _rxSceneUiBusy = true;
        try
        {
            var prefer = selectId ?? (_rxSceneBox.SelectedItem as RxScene)?.Id ?? _appSettings.SelectedRxSceneId;
            _rxSceneBox.Items.Clear();
            var ordered = _appSettings.RxScenes
                .OrderBy(scene => scene.Id.Equals(RxScene.DefaultId, StringComparison.OrdinalIgnoreCase) ? 0
                    : scene.Id.Equals(RxScene.SatelliteId, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(scene => scene.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var scene in ordered)
                _rxSceneBox.Items.Add(scene);
            var match = _appSettings.RxScenes.FirstOrDefault(s =>
                s.Id.Equals(prefer, StringComparison.OrdinalIgnoreCase));
            _rxSceneBox.SelectedItem = match ?? _rxSceneBox.Items[0];
            if (_rxSceneBox.SelectedItem is RxScene selected)
                _appSettings.SelectedRxSceneId = selected.Id;
        }
        finally
        {
            _rxSceneUiBusy = false;
        }
    }

    private RxScene CaptureRxScene(string name, string? id = null) => new()
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
        Name = name.Trim(),
        TunedFrequency = _tunedFrequency,
        RfCenterFrequency = _rfCenterFrequency,
        ViewCenterFrequency = _viewCenterFrequency,
        ViewBandwidth = _viewBandwidth,
        Mode = _demodulator.Mode,
        FilterBandwidth = _demodulator.Bandwidth,
        RfGain = _gainSlider.Value,
        CwLowerSide = _appSettings.CwLowerSide,
        AfFilterEnabled = _afFilterEnabledBeforeSatellite ?? _afFilterCheck.Checked,
        FtxMode = _appSettings.FtxMode,
        SingleActiveAfPlugin = _appSettings.SingleActiveAfPlugin,
        SelectedAfInstanceId = SelectedAfPluginBinding()?.InstanceId ?? _lastSelectedAfInstanceId,
        EnabledAfPluginIds = _appSettings.EnabledAfPluginIds.ToList(),
        AfPluginInstances = _appSettings.AfPluginInstances.Select(i => i.Clone()).ToList(),
        AfPluginVfoRoutes = new Dictionary<string, string>(_appSettings.AfPluginVfoRoutes, StringComparer.OrdinalIgnoreCase),
        SubVfos = _appSettings.SubVfos.Select(s => s.Clone()).ToList(),
        FrequencyChannels = CurrentSceneLocalChannels().Select(c => c.Clone()).ToList(),
        AfPluginUiState = RxScene.CloneUiState(_appSettings.AfPluginUiState),
        Audio1Volume = _volumeSlider.Value,
        Audio1SquelchEnabled = _squelchCheck.Checked,
        Audio1SquelchThreshold = (int)_squelchThreshold.Value,
        Audio2Volume = _rxVolumeBar2.Value,
        Audio2SquelchEnabled = _squelchCheck2.Checked,
        Audio2SquelchThreshold = _rxSquelchBar2.Value,
        FtxShowOnMainWaterfall = _appSettings.FtxShowOnMainWaterfall,
        CwShowOnAfWaterfall = _appSettings.CwShowOnAfWaterfall,
        SatelliteModeEnabled = IsSatelliteSceneActive,
        SatelliteAutoTrack = _appSettings.SatelliteAutoTrack,
        SatelliteAutoRefresh = _appSettings.SatelliteAutoRefresh,
        SatelliteShowFrequencyOnly = _appSettings.SatelliteShowFrequencyOnly,
        SatelliteLatitude = _appSettings.SatelliteLatitude,
        SatelliteLongitude = _appSettings.SatelliteLongitude,
        SatelliteAltitudeMeters = _appSettings.SatelliteAltitudeMeters,
        SatelliteLocationLabel = _appSettings.SatelliteLocationLabel,
        SatelliteTrackPreferences = _satellitePanel?.TrackPreferences.Select(item => item.Clone()).ToList()
            ?? _appSettings.SatelliteTrackPreferences.Select(item => item.Clone()).ToList(),
        MainAutoTune = (_appSettings.MainAutoTune ?? new AutoTuneSettings()).Clone(),
        SourceName = _source.Name,
        RemoteServerUrl = _source is IRemoteAudioSampleSource remote
            ? (string.IsNullOrWhiteSpace(remote.ServerUrl) ? _webUrlInput.Text.Trim() : remote.ServerUrl.Trim())
            : "",
        SpectrumPluginId = _pluginSelection.SpectrumId,
        WaterfallPluginId = _pluginSelection.WaterfallId,
        EnabledIqPluginIds = _appSettings.EnabledIqPluginIds.ToList(),
        AfPluginDisplayWidth = _appSettings.AfPluginDisplayWidth,
        AfPluginDisplayHeight = _appSettings.AfPluginDisplayHeight,
        Audio1Enabled = _appSettings.Audio1.Enabled,
        Audio1DeviceId = _appSettings.Audio1.DeviceId,
        Audio1DeviceName = _appSettings.Audio1.DeviceName ?? "",
        Audio2Enabled = _appSettings.Audio2.Enabled,
        Audio2DeviceId = _appSettings.Audio2.DeviceId,
        Audio2DeviceName = _appSettings.Audio2.DeviceName ?? "",
        RfDisplayDetached = _rfDisplayDetached,
        RfDisplayScreenDevice = _rfDetachForm is { IsDisposed: false } rfForm
            ? rfForm.CaptureScreenDeviceName()
            : (_rfPopOutBounds.Width > 0 ? Screen.FromRectangle(_rfPopOutBounds).DeviceName : ""),
        RfDisplayFullscreen = _rfDetachForm is { IsDisposed: false } rfFs
            ? rfFs.CaptureFullscreenState()
            : _rfPopOutFullscreen,
        RfDisplayX = (_rfDetachForm is { IsDisposed: false } rfB ? rfB.CaptureRestoreBounds() : _rfPopOutBounds).X,
        RfDisplayY = (_rfDetachForm is { IsDisposed: false } rfB2 ? rfB2.CaptureRestoreBounds() : _rfPopOutBounds).Y,
        RfDisplayWidth = (_rfDetachForm is { IsDisposed: false } rfB3 ? rfB3.CaptureRestoreBounds() : _rfPopOutBounds).Width,
        RfDisplayHeight = (_rfDetachForm is { IsDisposed: false } rfB4 ? rfB4.CaptureRestoreBounds() : _rfPopOutBounds).Height,
        AfDisplayDetached = _afDisplayDetached,
        AfDisplayScreenDevice = _afDetachForm is { IsDisposed: false } afForm
            ? afForm.CaptureScreenDeviceName()
            : (_afPopOutBounds.Width > 0 ? Screen.FromRectangle(_afPopOutBounds).DeviceName : ""),
        AfDisplayFullscreen = _afDetachForm is { IsDisposed: false } afFs
            ? afFs.CaptureFullscreenState()
            : _afPopOutFullscreen,
        AfDisplayX = (_afDetachForm is { IsDisposed: false } afB ? afB.CaptureRestoreBounds() : _afPopOutBounds).X,
        AfDisplayY = (_afDetachForm is { IsDisposed: false } afB2 ? afB2.CaptureRestoreBounds() : _afPopOutBounds).Y,
        AfDisplayWidth = (_afDetachForm is { IsDisposed: false } afB3 ? afB3.CaptureRestoreBounds() : _afPopOutBounds).Width,
        AfDisplayHeight = (_afDetachForm is { IsDisposed: false } afB4 ? afB4.CaptureRestoreBounds() : _afPopOutBounds).Height
    };

    private void SaveNewRxScene()
    {
        var name = RxSceneNameDialog.Prompt(this, "New RX scene", SuggestRxSceneName());
        if (string.IsNullOrWhiteSpace(name)) return;
        var scene = CaptureRxScene(name);
        _appSettings.RxScenes.Add(scene);
        _appSettings.SelectedRxSceneId = scene.Id;
        RefreshRxSceneCombo(scene.Id);
        SaveSettings();
        _statusLabel.Text = $"Scene saved · {scene.Name}";
    }

    private void SaveSelectedRxScene()
    {
        if (_rxSceneBox.SelectedItem is not RxScene selected)
        {
            SaveNewRxScene();
            return;
        }
        var updated = CaptureRxScene(selected.Name, selected.Id);
        if (RxScene.IsBuiltInScene(selected.Id))
            updated.Name = selected.Name;
        var idx = _appSettings.RxScenes.FindIndex(s => s.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _appSettings.RxScenes[idx] = updated;
        else _appSettings.RxScenes.Add(updated);
        _appSettings.SelectedRxSceneId = updated.Id;
        RefreshRxSceneCombo(updated.Id);
        SaveSettings();
        _statusLabel.Text = $"Scene updated · {updated.Name}";
    }

    private void DeleteSelectedRxScene()
    {
        if (_rxSceneBox.SelectedItem is not RxScene selected) return;
        if (RxScene.IsBuiltInScene(selected.Id))
        {
            MessageBox.Show(this,
                "Built-in scenes (DEFAULT and SATELLITE) cannot be removed.",
                "RX Scene", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Delete scene \"{selected.Name}\"?", "RX Scene",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _appSettings.RxScenes.RemoveAll(s => s.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
        AppSettingsStore.EnsureBuiltInRxScenes(_appSettings);
        RefreshRxSceneCombo(_appSettings.SelectedRxSceneId);
        if (_rxSceneBox.SelectedItem is RxScene next)
            ApplyRxScene(next);
        else
            SaveSettings();
        _statusLabel.Text = "Scene deleted";
    }

    private string SuggestRxSceneName()
    {
        var mode = _demodulator.Mode;
        var mhz = _tunedFrequency / 1_000_000d;
        return $"{mode} {mhz:0.###} · {DateTime.Now:HHmm}";
    }

    private void WireSceneChannelStrip()
    {
        if (_sceneChannelStrip.Tag as string == "wired") return;
        _sceneChannelStrip.Tag = "wired";
        _sceneChannelStrip.LineCountChanged += () => LayoutRxLowerArea();
        _sceneChannelStrip.AddRequested += AddSceneFrequencyChannel;
        _sceneChannelStrip.EditChannelRequested += EditSceneFrequencyChannel;
        _sceneChannelStrip.DeleteChannelRequested += DeleteSceneFrequencyChannel;
        _sceneChannelStrip.ChannelSelected += ApplySceneFrequencyChannel;
        _sceneChannelStrip.ClearAllRequested += ClearAllSceneFrequencyChannels;
        _sceneChannelStrip.CopyFromSceneRequested += CopySceneFrequencyChannelsFromOtherScene;
        UpdateMainAutoTuneButtonStyle();
    }

    private void EnsureMainAutoTuneButton()
    {
        if (_mainAutoTuneButton is not null) return;
        // Compact drop-down chrome: ~2/3 of prior width, chevron only, always visible.
        _mainAutoTuneButton = new Button
        {
            Text = "",
            Size = new Size(15, 50),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(200, 220, 230),
            BackColor = Color.FromArgb(45, 78, 98),
            TabStop = false,
            Visible = true,
            Cursor = Cursors.Hand
        };
        _mainAutoTuneButton.FlatAppearance.BorderSize = 1;
        _mainAutoTuneButton.FlatAppearance.BorderColor = Color.FromArgb(90, 130, 150);
        _mainAutoTuneButton.Click += (_, _) => EditMainAutoTune();
        _mainAutoTuneButton.Paint += PaintMainAutoTuneChevron;
        _radioPanel.Controls.Add(_mainAutoTuneButton);
        WireMainAutoTuneHotzone(_afFrequencyLabel);
        WireMainAutoTuneHotzone(_mainAutoTuneButton);
        PositionMainAutoTuneButton();
        _radioPanel.Resize += (_, _) => PositionMainAutoTuneButton();
        _afFrequencyLabel.Resize += (_, _) => PositionMainAutoTuneButton();
        UpdateMainAutoTuneButtonStyle();
        _mainAutoTuneTip = new ToolTip
        {
            AutoPopDelay = 6_000,
            InitialDelay = 200,
            ReshowDelay = 150,
            ShowAlways = true
        };
        ApplyMainAutoTuneHint();
        _mainAutoTuneButton.Visible = true;
        _mainAutoTuneButton.BringToFront();
    }

    private void WireMainAutoTuneHotzone(Control control)
    {
        control.MouseEnter += (_, _) => ShowMainAutoTuneButton(hot: true);
        control.MouseLeave += (_, _) => ScheduleMainAutoTuneButtonHide();
        control.MouseMove += (_, _) => ShowMainAutoTuneButton(hot: true);
    }

    private bool IsOverMainAutoTuneHotzone()
    {
        if (_mainAutoTuneButton is null) return false;
        var cursor = Cursor.Position;
        if (_afFrequencyLabel.ClientRectangle.Contains(_afFrequencyLabel.PointToClient(cursor)))
            return true;
        if (_mainAutoTuneButton.Visible &&
            _mainAutoTuneButton.ClientRectangle.Contains(_mainAutoTuneButton.PointToClient(cursor)))
            return true;
        var zone = _afFrequencyLabel.Bounds;
        zone.Inflate(8, 4);
        zone.Width += _mainAutoTuneButton.Width + 6;
        return zone.Contains(_radioPanel.PointToClient(cursor));
    }

    private void ShowMainAutoTuneButton(bool hot = false)
    {
        EnsureMainAutoTuneButton();
        if (_mainAutoTuneButton is null) return;
        _mainAutoHideTimer.Stop();
        PositionMainAutoTuneButton();
        UpdateMainAutoTuneButtonStyle(hot || IsOverMainAutoTuneHotzone());
        ApplyMainAutoTuneHint();
        _mainAutoTuneButton.Visible = true;
        _mainAutoTuneButton.BringToFront();
    }

    private void ScheduleMainAutoTuneButtonHide()
    {
        if (_mainAutoTuneButton is null) return;
        _mainAutoHideTimer.Stop();
        _mainAutoHideTimer.Start();
    }

    private void PositionMainAutoTuneButton()
    {
        if (_mainAutoTuneButton is null) return;
        // Source control at X=310. Width ~2/3 of previous 22px strip; height matches VFO digits.
        const int sourceLeft = 310;
        const int autoWidth = 15;
        const int gap = 2;
        var vfoLeft = _afFrequencyLabel.Left;
        var vfoWidth = Math.Max(250, sourceLeft - autoWidth - gap * 2 - vfoLeft);
        if (_afFrequencyLabel.Width != vfoWidth)
            _afFrequencyLabel.Width = vfoWidth;
        var autoHeight = Math.Max(1, _afFrequencyLabel.Height);
        _mainAutoTuneButton.Size = new Size(autoWidth, autoHeight);
        _mainAutoTuneButton.Location = new Point(
            _afFrequencyLabel.Right + gap,
            _afFrequencyLabel.Top);
        _mainAutoTuneButton.Visible = true;
        _mainAutoTuneButton.BringToFront();
    }

    private void PaintMainAutoTuneChevron(object? sender, PaintEventArgs e)
    {
        if (_mainAutoTuneButton is null) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var cx = _mainAutoTuneButton.Width / 2f;
        var cy = _mainAutoTuneButton.Height / 2f + 0.5f;
        const float halfW = 4.2f;
        const float halfH = 2.8f;
        PointF[] chevron =
        [
            new(cx - halfW, cy - halfH),
            new(cx + halfW, cy - halfH),
            new(cx, cy + halfH)
        ];
        using var brush = new SolidBrush(_mainAutoTuneButton.ForeColor);
        g.FillPolygon(brush, chevron);
    }

    private void UpdateMainAutoTuneButtonStyle(bool hot = false)
    {
        if (_mainAutoTuneButton is null) return;
        var on = _appSettings.MainAutoTune?.Enabled == true;
        if (hot)
        {
            // Amber / sky — no green.
            _mainAutoTuneButton.BackColor = on
                ? Color.FromArgb(70, 175, 230)
                : Color.FromArgb(255, 193, 69);
            _mainAutoTuneButton.FlatAppearance.BorderColor = on
                ? Color.FromArgb(170, 220, 250)
                : Color.FromArgb(255, 230, 150);
            _mainAutoTuneButton.ForeColor = on
                ? Color.FromArgb(8, 28, 42)
                : Color.FromArgb(40, 28, 5);
        }
        else if (on)
        {
            _mainAutoTuneButton.BackColor = Color.FromArgb(36, 92, 128);
            _mainAutoTuneButton.FlatAppearance.BorderColor = Color.FromArgb(90, 170, 210);
            _mainAutoTuneButton.ForeColor = Color.FromArgb(210, 235, 250);
        }
        else
        {
            _mainAutoTuneButton.BackColor = Color.FromArgb(45, 78, 98);
            _mainAutoTuneButton.FlatAppearance.BorderColor = Color.FromArgb(90, 130, 150);
            _mainAutoTuneButton.ForeColor = Color.FromArgb(200, 220, 230);
        }
        _mainAutoTuneButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 193, 69);
        _mainAutoTuneButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(230, 160, 50);
        _mainAutoTuneButton.Invalidate();
        ApplyMainAutoTuneHint();
    }

    private void ApplyMainAutoTuneHint()
    {
        if (_mainAutoTuneTip is null) return;
        var on = _appSettings.MainAutoTune?.Enabled == true;
        var tip = on
            ? "AUTO · ON · Main VFO auto frequency follow (click to edit)"
            : "AUTO · Main VFO auto frequency follow (click to edit)";
        _mainAutoTuneTip.SetToolTip(_afFrequencyLabel, tip);
        if (_mainAutoTuneButton is not null)
            _mainAutoTuneTip.SetToolTip(_mainAutoTuneButton, tip);
    }

    private void SchedulePersistCurrentSceneLayout()
    {
        if (_applyingRxScene || _rxSceneUiBusy) return;
        _sceneLayoutPersistTimer.Stop();
        _sceneLayoutPersistTimer.Start();
    }

    private void PersistCurrentSceneLayout()
    {
        if (_applyingRxScene || _rxSceneUiBusy || _persistSceneBusy || IsDisposed) return;
        AppSettingsStore.EnsureBuiltInRxScenes(_appSettings);
        var target = _rxSceneBox.SelectedItem as RxScene
            ?? _appSettings.RxScenes.FirstOrDefault(s =>
                s.Id.Equals(_appSettings.SelectedRxSceneId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;
        _persistSceneBusy = true;
        try
        {
            var updated = CaptureRxScene(target.Name, target.Id);
            var idx = _appSettings.RxScenes.FindIndex(s =>
                s.Id.Equals(updated.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _appSettings.RxScenes[idx] = updated;
            else _appSettings.RxScenes.Add(updated);
            _rxSceneUiBusy = true;
            try
            {
                var i = _rxSceneBox.SelectedIndex;
                if (i >= 0 && i < _rxSceneBox.Items.Count)
                {
                    _rxSceneBox.Items[i] = updated;
                    _rxSceneBox.SelectedIndex = i;
                }
            }
            finally
            {
                _rxSceneUiBusy = false;
            }
            _appSettings.SelectedRxSceneId = updated.Id;
            SaveSettingsAsync();
        }
        finally
        {
            _persistSceneBusy = false;
        }
    }

    private List<SceneFrequencyChannel> EnumerateVisibleSceneChannels()
    {
        var list = new List<SceneFrequencyChannel>();
        foreach (var ch in _appSettings.GlobalFrequencyChannels ?? [])
            list.Add(ch);
        if (_rxSceneBox.SelectedItem is RxScene scene)
        {
            var live = _appSettings.RxScenes.FirstOrDefault(s => s.Id.Equals(scene.Id, StringComparison.OrdinalIgnoreCase)) ?? scene;
            foreach (var ch in live.FrequencyChannels ?? [])
                if (!ch.Global) list.Add(ch);
        }
        return list;
    }

    private List<SceneFrequencyChannel> CurrentSceneLocalChannels()
    {
        if (_rxSceneBox.SelectedItem is not RxScene selected) return [];
        var live = _appSettings.RxScenes.FirstOrDefault(s => s.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
        return live?.FrequencyChannels?.Select(c => c.Clone()).ToList() ?? [];
    }

    private void RefreshSceneChannelStrip()
    {
        var channels = EnumerateVisibleSceneChannels();
        _sceneChannelStrip.SetChannels(channels, _appSettings.SelectedSceneChannelId);
        UpdateMainAutoTuneButtonStyle();
        LayoutRxLowerArea();
    }

    private void PersistSceneLocalChannels(List<SceneFrequencyChannel> locals)
    {
        if (_rxSceneBox.SelectedItem is not RxScene selected) return;
        var idx = _appSettings.RxScenes.FindIndex(s => s.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        _appSettings.RxScenes[idx].FrequencyChannels = locals;
    }

    private void AddSceneFrequencyChannel()
    {
        var draft = new SceneFrequencyChannel
        {
            Name = "",
            Frequency = _tunedFrequency,
            Mode = _demodulator.Mode,
            Bandwidth = _demodulator.Bandwidth,
            Global = false
        };
        using var dlg = new SceneFrequencyChannelEditorForm(draft);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var ch = dlg.Result;
        if (ch.Global)
        {
            _appSettings.GlobalFrequencyChannels.Add(ch);
        }
        else
        {
            if (_rxSceneBox.SelectedItem is not RxScene)
            {
                _statusLabel.Text = "Select or create a SCENE before adding a local channel.";
                return;
            }
            var locals = CurrentSceneLocalChannels();
            locals.Add(ch);
            PersistSceneLocalChannels(locals);
        }
        _appSettings.SelectedSceneChannelId = ch.Id;
        RefreshSceneChannelStrip();
        SaveSettings();
    }

    private void EditSceneFrequencyChannel(SceneFrequencyChannel channel)
    {
        var id = channel.Id;
        _appSettings.SelectedSceneChannelId = id;
        _sceneChannelStrip.SelectId(id);
        var global = _appSettings.GlobalFrequencyChannels.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        SceneFrequencyChannel? local = null;
        List<SceneFrequencyChannel>? locals = null;
        if (global is null)
        {
            locals = CurrentSceneLocalChannels();
            local = locals.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        var target = global ?? local;
        if (target is null) return;
        using var dlg = new SceneFrequencyChannelEditorForm(target);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var edited = dlg.Result;
        edited.Id = target.Id;
        _appSettings.GlobalFrequencyChannels.RemoveAll(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        locals ??= CurrentSceneLocalChannels();
        locals.RemoveAll(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (edited.Global) _appSettings.GlobalFrequencyChannels.Add(edited);
        else
        {
            if (_rxSceneBox.SelectedItem is null)
            {
                _statusLabel.Text = "Select a SCENE for a local channel.";
                return;
            }
            locals.Add(edited);
        }
        PersistSceneLocalChannels(locals);
        _appSettings.SelectedSceneChannelId = edited.Id;
        RefreshSceneChannelStrip();
        SaveSettings();
    }

    private void DeleteSceneFrequencyChannel(SceneFrequencyChannel channel)
    {
        var id = channel.Id;
        if (MessageBox.Show(this, $"Delete channel \"{channel}\"?", "SCENE channel",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _appSettings.GlobalFrequencyChannels.RemoveAll(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var locals = CurrentSceneLocalChannels();
        locals.RemoveAll(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        PersistSceneLocalChannels(locals);
        if (string.Equals(_appSettings.SelectedSceneChannelId, id, StringComparison.OrdinalIgnoreCase))
            _appSettings.SelectedSceneChannelId = "";
        RefreshSceneChannelStrip();
        SaveSettings();
    }

    private void ClearAllSceneFrequencyChannels()
    {
        if (_rxSceneBox.SelectedItem is not RxScene)
        {
            _statusLabel.Text = "Select a SCENE first.";
            return;
        }
        var locals = CurrentSceneLocalChannels();
        if (locals.Count == 0)
        {
            _statusLabel.Text = "No local SCENE channels to clear.";
            return;
        }
        if (MessageBox.Show(this,
                $"Clear all {locals.Count} local channel(s) in this SCENE?\n(GLOBAL · channels are kept.)",
                "Clear all",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        PersistSceneLocalChannels([]);
        _appSettings.SelectedSceneChannelId = "";
        RefreshSceneChannelStrip();
        SaveSettings();
        SchedulePersistCurrentSceneLayout();
    }

    private void CopySceneFrequencyChannelsFromOtherScene()
    {
        if (_rxSceneBox.SelectedItem is not RxScene current)
        {
            _statusLabel.Text = "Select or create a SCENE before copying channels.";
            return;
        }
        var scenes = _appSettings.RxScenes ?? [];
        if (scenes.Count <= 1)
        {
            MessageBox.Show(this, "No other SCENE is available to copy from.", "Copy SCENE channels",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SceneChannelCopyForm(scenes, current.Id);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var incoming = dlg.SelectedChannels;
        if (incoming.Count == 0) return;
        var locals = CurrentSceneLocalChannels();
        locals.AddRange(incoming);
        PersistSceneLocalChannels(locals);
        _appSettings.SelectedSceneChannelId = incoming[^1].Id;
        RefreshSceneChannelStrip();
        SaveSettings();
        SchedulePersistCurrentSceneLayout();
        _statusLabel.Text = $"Copied {incoming.Count} channel(s) into \"{current.Name}\".";
    }

    private void ApplySceneFrequencyChannel(SceneFrequencyChannel channel)
    {
        NoteManualTune();
        _appSettings.SelectedSceneChannelId = channel.Id;
        var previous = _tunedFrequency;
        _tunedFrequency = Math.Clamp(channel.Frequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        _suppressModeDefaults = true;
        try
        {
            if (_modeBox.Items.Contains(channel.Mode.ToString()))
                _modeBox.SelectedItem = channel.Mode.ToString();
            _demodulator.Mode = channel.Mode;
            _bandwidthBox.Value = Math.Clamp(channel.Bandwidth, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
            _demodulator.Bandwidth = (int)_bandwidthBox.Value;
        }
        finally { _suppressModeDefaults = false; }
        if (_source is IFixedCenterFrequencySampleSource)
            _tunedFrequency = Math.Clamp(_tunedFrequency, _rfCenterFrequency - _source.SampleRate * 45L / 100, _rfCenterFrequency + _source.SampleRate * 45L / 100);
        else if (Math.Abs(_tunedFrequency - _rfCenterFrequency) > _source.SampleRate * .45)
            RecenterOnVfo();
        ShiftMainAutoWindowWithVfo(_tunedFrequency - previous);
        ResetCwIfFrequencyChanged(previous);
        UpdateTuningDisplay();
        ConfigureAfDisplay();
        UpdateRxPanel();
        if (_tunedFrequency != previous) ScheduleVoiceFrequency(_tunedFrequency);
        CenterViewOnVfo();
        SaveSettings();
    }

    private void EditMainAutoTune()
    {
        var settings = _appSettings.MainAutoTune?.Clone() ?? new AutoTuneSettings();
        // Always seed around the current Main VFO so AUTO opens ready to edit.
        settings.StandbyFrequency = _tunedFrequency;
        settings.MinFrequency = Math.Max(RadioLimits.MinimumFrequency, _tunedFrequency - 25_000);
        settings.MaxFrequency = Math.Min(RadioLimits.MaximumFrequency, _tunedFrequency + 25_000);
        var suggested = SuggestAutoTuneTriggerDb(_tunedFrequency, settings.MinFrequency, settings.MaxFrequency);
        using var dlg = new AutoTuneEditorForm("Main VFO · auto frequency follow", settings, suggested);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _appSettings.MainAutoTune = dlg.Result;
        if (!_appSettings.MainAutoTune.Enabled)
        {
            _mainAutoFollowing = false;
            _mainAutoHoldUntil = 0;
        }
        UpdateMainAutoTuneButtonStyle(hot: IsOverMainAutoTuneHotzone());
        if (_mainAutoTuneButton is not null)
        {
            _mainAutoTuneButton.Visible = true;
            _mainAutoTuneButton.BringToFront();
        }
        UpdateAutoTuneOverlays();
        SaveSettings();
        SchedulePersistCurrentSceneLayout();
    }

    /// <summary>
    /// Trigger — level at current VFO + adjacent channels + 5 dB (not a fixed 0).
    /// </summary>
    private float? SuggestAutoTuneTriggerDb(long referenceHz, long minHz, long maxHz)
    {
        var spectrum = _lastRfSpectrum;
        if (spectrum is null || spectrum.Length < 8) return null;
        var center = _lastRfSpectrumCenter != 0 ? _lastRfSpectrumCenter : Interlocked.Read(ref _rfCenterFrequency);
        var sampleRate = _lastRfSpectrumSampleRate > 0 ? _lastRfSpectrumSampleRate : _source.SampleRate;
        if (sampleRate <= 0) return null;

        var step = Math.Max(6_250, _demodulator.Bandwidth / 2);
        var probes = new[]
        {
            referenceHz,
            referenceHz - step,
            referenceHz + step,
            referenceHz - 2 * step,
            referenceHz + 2 * step
        };
        var levels = new List<float>(probes.Length);
        foreach (var hz in probes)
        {
            if (hz < minHz || hz > maxHz) continue;
            levels.Add(SampleSpectrumDb(spectrum, center, sampleRate, hz));
        }
        if (levels.Count == 0) return null;
        levels.Sort();
        var baseline = levels[levels.Count / 2]; // median of current + adjacent
        var trigger = Math.Clamp(baseline + 5f, -120f, -15f);
        return trigger;
    }

    private static float SampleSpectrumDb(float[] spectrum, long center, int sampleRate, long hz)
    {
        var captureLeft = center - sampleRate / 2d;
        var index = (int)Math.Round((hz - captureLeft) * (spectrum.Length - 1) / sampleRate);
        index = Math.Clamp(index, 0, spectrum.Length - 1);
        // Average a few bins around the probe so a single spike doesn't dominate.
        var lo = Math.Max(0, index - 1);
        var hi = Math.Min(spectrum.Length - 1, index + 1);
        var sum = 0f;
        for (var i = lo; i <= hi; i++) sum += spectrum[i];
        return sum / (hi - lo + 1);
    }

    private void UpdateAutoTuneOverlays()
    {
        var list = new List<SpectrumWaterfallControl.AutoTuneOverlay>();
        var main = _appSettings.MainAutoTune;
        if (main is { Enabled: true } && main.MaxFrequency > main.MinFrequency)
            // AnchorFrequency 0: control uses live Main VFO for hover/hit.
            list.Add(new SpectrumWaterfallControl.AutoTuneOverlay(
                main.MinFrequency, main.MaxFrequency, main.TriggerLevelDb, true, "MAIN", 0));
        foreach (var sub in _appSettings.SubVfos)
        {
            var auto = sub.AutoTune;
            if (auto is not { Enabled: true } || auto.MaxFrequency <= auto.MinFrequency) continue;
            list.Add(new SpectrumWaterfallControl.AutoTuneOverlay(
                auto.MinFrequency, auto.MaxFrequency, auto.TriggerLevelDb, false, sub.Name, sub.Frequency));
        }
        _display.SetAutoTuneOverlays(list);
    }

    private void ProcessAutoTuneFollow()
    {
        var spectrum = _lastRfSpectrum;
        if (spectrum is null || spectrum.Length < 8) return;
        var center = _lastRfSpectrumCenter != 0 ? _lastRfSpectrumCenter : Interlocked.Read(ref _rfCenterFrequency);
        var sampleRate = _lastRfSpectrumSampleRate > 0 ? _lastRfSpectrumSampleRate : _source.SampleRate;
        var now = Environment.TickCount64;

        var mainMovedThisTick = false;
        var main = _appSettings.MainAutoTune;
        if (main is { Enabled: true } && main.MaxFrequency > main.MinFrequency && now >= _manualTuneHoldUntil)
        {
            FindPeakInRange(spectrum, center, sampleRate, main.MinFrequency, main.MaxFrequency,
                out var peakHz, out var peakDb);
            if (peakDb >= main.TriggerLevelDb && peakHz > 0)
            {
                peakHz = SnapAutoTuneFrequency(peakHz);
                if (!_mainAutoFollowing && main.StandbyFrequency <= 0)
                    main.StandbyFrequency = _tunedFrequency;
                _mainAutoFollowing = true;
                _mainAutoHoldUntil = now + Math.Max(200, main.HoldMilliseconds);
                var slop = AutoTuneFrequencySlop();
                if (Math.Abs(peakHz - _tunedFrequency) > slop)
                {
                    ChangeVfoFromDigitalDisplay(peakHz);
                    UpdateFrequencyReadout();
                    if (_digitalMode.IsActive) _digitalMode.NotifyCarrierRetune();
                    mainMovedThisTick = true;
                }
                else
                {
                    UpdateFrequencyReadout();
                }
            }
            else if (_mainAutoFollowing && now >= _mainAutoHoldUntil && peakDb <= main.ReleaseLevelDb)
            {
                _mainAutoFollowing = false;
                if (main.StandbyFrequency > 0)
                {
                    ChangeVfoFromDigitalDisplay(main.StandbyFrequency);
                    UpdateFrequencyReadout();
                    mainMovedThisTick = true;
                }
            }
        }

        // If MAIN is currently following (or moved on this tick), lock all SUB auto-tune so
        // overlapping VFOs never "compete" and move together. SUBs run only when MAIN is idle.
        var lockSubs = mainMovedThisTick || _mainAutoFollowing;
        if (lockSubs) return;

        // Priority: SUBVFO order in _appSettings.SubVfos (e.g. SUB1, SUB2, ...)
        var subControlTaken = false;
        foreach (var sub in _appSettings.SubVfos)
        {
            if (subControlTaken) break;
            var auto = sub.AutoTune;
            if (auto is not { Enabled: true } || auto.MaxFrequency <= auto.MinFrequency) continue;

            FindPeakInRange(spectrum, center, sampleRate, auto.MinFrequency, auto.MaxFrequency,
                out var peakHz, out var peakDb);
            _subAutoFollow.TryGetValue(sub.Id, out var state);

            if (peakDb >= auto.TriggerLevelDb && peakHz > 0)
            {
                peakHz = SnapAutoTuneFrequency(peakHz, sub.Mode);
                state = (true, now + Math.Max(200, auto.HoldMilliseconds));
                var slop = AutoTuneFrequencySlop(sub.Mode, sub.Bandwidth);
                if (Math.Abs(peakHz - sub.Frequency) > slop)
                {
                    sub.Frequency = peakHz;
                    var receiver = Volatile.Read(ref _subVfoReceivers)
                        .FirstOrDefault(r => r.Id.Equals(sub.Id, StringComparison.OrdinalIgnoreCase));
                    if (receiver is not null) receiver.Frequency = peakHz;
                    _lastSubVfoRangeSignature = "";
                    _display.SetSubVfoMarkers(_source is IRemoteAudioSampleSource
                        ? []
                        : _appSettings.SubVfos.Select(item => (item.Frequency, item.Name)));
                    UpdateAutoTuneOverlays();
                }

                _subAutoFollow[sub.Id] = state;
                subControlTaken = true;
                continue;
            }

            if (state.Following && now >= state.HoldUntil && peakDb <= auto.ReleaseLevelDb)
            {
                state = (false, 0);
                if (auto.StandbyFrequency > 0)
                {
                    sub.Frequency = auto.StandbyFrequency;
                    var receiver = Volatile.Read(ref _subVfoReceivers)
                        .FirstOrDefault(r => r.Id.Equals(sub.Id, StringComparison.OrdinalIgnoreCase));
                    if (receiver is not null) receiver.Frequency = auto.StandbyFrequency;
                    _lastSubVfoRangeSignature = "";
                    _display.SetSubVfoMarkers(_source is IRemoteAudioSampleSource
                        ? []
                        : _appSettings.SubVfos.Select(item => (item.Frequency, item.Name)));
                    UpdateAutoTuneOverlays();
                }
                _subAutoFollow[sub.Id] = state;
                subControlTaken = true;
                continue;
            }

            _subAutoFollow[sub.Id] = state;
        }
    }

    private static void FindPeakInRange(float[] spectrum, long center, int sampleRate, long minHz, long maxHz, out long peakHz, out float peakDb)
    {
        peakHz = 0;
        peakDb = -140f;
        if (maxHz <= minHz || sampleRate <= 0) return;
        var captureLeft = center - sampleRate / 2d;
        var first = Math.Clamp((int)Math.Floor((minHz - captureLeft) * spectrum.Length / sampleRate), 0, spectrum.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((maxHz - captureLeft) * spectrum.Length / sampleRate), first, spectrum.Length - 1);
        var best = first;
        for (var i = first; i <= last; i++)
        {
            if (spectrum[i] <= peakDb) continue;
            peakDb = spectrum[i];
            best = i;
        }
        peakHz = (long)Math.Round(captureLeft + best * (double)sampleRate / Math.Max(1, spectrum.Length - 1));
    }

    private long SnapAutoTuneFrequency(long hz) => SnapAutoTuneFrequency(hz, _demodulator.Mode);

    private static long SnapAutoTuneFrequency(long hz, RadioMode mode)
    {
        // DMR/NFM channels are almost always 12.5 kHz — bin-center peaks are often a few kHz off.
        var step = mode is RadioMode.DMR or RadioMode.DSTAR or RadioMode.C4FM or RadioMode.NFM
            ? 12_500
            : 0;
        if (step <= 1) return hz;
        return (long)Math.Round(hz / (double)step) * step;
    }

    private int AutoTuneFrequencySlop() => AutoTuneFrequencySlop(_demodulator.Mode, _demodulator.Bandwidth);

    private static int AutoTuneFrequencySlop(RadioMode mode, int bandwidth)
    {
        // Digital voice needs to stay near channel center; old BW/4 (~3 kHz) was too loose for DMR.
        if (mode is RadioMode.DMR or RadioMode.DSTAR or RadioMode.C4FM) return 80;
        return Math.Max(250, bandwidth / 4);
    }

    private void ApplyRxScene(RxScene scene, bool applyPopOut = true, bool persist = true) =>
        _ = ApplyRxSceneAsync(scene, applyPopOut, persist);

    private async Task ApplyRxSceneAsync(RxScene scene, bool applyPopOut = true, bool persist = true)
    {
        var snap = scene.DeepClone();
        var previousSceneId = _appSettings.SelectedRxSceneId;
        var wasRunning = _source.IsRunning;
        // Snapshot outgoing scene (incl. SDR device) before we switch selection.
        if (persist &&
            !string.IsNullOrWhiteSpace(previousSceneId) &&
            !previousSceneId.Equals(snap.Id, StringComparison.OrdinalIgnoreCase))
            PersistSceneSnapshot(previousSceneId);

        _applyingRxScene = true;
        try
        {
        _appSettings.SelectedRxSceneId = snap.Id;

        await ApplySceneSampleSourceAsync(snap).ConfigureAwait(true);

        _appSettings.CwLowerSide = snap.CwLowerSide;
        _cwSideBox.SelectedIndex = snap.CwLowerSide ? 1 : 0;
        _demodulator.CwPitchHz = snap.CwLowerSide ? -700 : 700;

        _appSettings.FtxMode = string.IsNullOrWhiteSpace(snap.FtxMode) ? "FT8" : snap.FtxMode;
        _appSettings.SingleActiveAfPlugin = snap.SingleActiveAfPlugin;
        _appSettings.EnabledAfPluginIds = snap.EnabledAfPluginIds.ToList();
        _appSettings.AfPluginInstances = snap.AfPluginInstances.Select(i => i.Clone()).ToList();
        _appSettings.AfPluginVfoRoutes = new Dictionary<string, string>(snap.AfPluginVfoRoutes, StringComparer.OrdinalIgnoreCase);
        _appSettings.AfPluginUiState = RxScene.CloneUiState(snap.AfPluginUiState);
        // Legacy scenes without SourceName omit IQ list — don't wipe the current set.
        if (!string.IsNullOrWhiteSpace(snap.SourceName) || (snap.EnabledIqPluginIds?.Count ?? 0) > 0)
            _appSettings.EnabledIqPluginIds = snap.EnabledIqPluginIds?.ToList() ?? [];
        if (snap.AfPluginDisplayWidth > 0) _appSettings.AfPluginDisplayWidth = snap.AfPluginDisplayWidth;
        if (snap.AfPluginDisplayHeight > 0) _appSettings.AfPluginDisplayHeight = snap.AfPluginDisplayHeight;
        if (!string.IsNullOrWhiteSpace(snap.SpectrumPluginId))
            _appSettings.SpectrumPluginId = snap.SpectrumPluginId;
        if (!string.IsNullOrWhiteSpace(snap.WaterfallPluginId))
            _appSettings.WaterfallPluginId = snap.WaterfallPluginId;
        _pluginSelection = new PluginSelection(_appSettings.SpectrumPluginId, _appSettings.WaterfallPluginId);
        _appSettings.SubVfos = snap.SubVfos.Select(s => s.Clone()).ToList();
        _lastSelectedAfInstanceId = snap.SelectedAfInstanceId;
        _appSettings.FtxShowOnMainWaterfall = snap.FtxShowOnMainWaterfall;
        _display.ShowFtxOnMain = snap.FtxShowOnMainWaterfall;
        _appSettings.CwShowOnAfWaterfall = snap.CwShowOnAfWaterfall;
        if (!snap.CwShowOnAfWaterfall) _afDisplay.ClearCwMessages();
        SynchronizeCwControls();
        _appSettings.SatelliteAutoTrack = snap.SatelliteAutoTrack;
        _appSettings.SatelliteAutoRefresh = snap.SatelliteAutoRefresh;
        _appSettings.SatelliteShowFrequencyOnly = snap.SatelliteShowFrequencyOnly;
        _appSettings.SatelliteLatitude = snap.SatelliteLatitude;
        _appSettings.SatelliteLongitude = snap.SatelliteLongitude;
        _appSettings.SatelliteAltitudeMeters = snap.SatelliteAltitudeMeters;
        _appSettings.SatelliteLocationLabel = snap.SatelliteLocationLabel;
        _appSettings.SatelliteTrackPreferences = snap.SatelliteTrackPreferences.Select(item => item.Clone()).ToList();
        ApplySatelliteObserverSummary();
        ApplySatelliteSceneUi(RxScene.IsSatelliteScene(snap.Id), persist: false);
        if (!RxScene.IsSatelliteScene(snap.Id))
        {
            _appSettings.AfFilterEnabled = snap.AfFilterEnabled;
            _suppressAfFilterEvents = true;
            try { _afFilterCheck.Checked = snap.AfFilterEnabled; }
            finally { _suppressAfFilterEvents = false; }
            SetAfFilterRuntimeEnabled(snap.AfFilterEnabled);
        }
        _appSettings.MainAutoTune = snap.MainAutoTune?.Clone() ?? new AutoTuneSettings();
        _mainAutoFollowing = false;
        _mainAutoHoldUntil = 0;
        _subAutoFollow.Clear();
        UpdateMainAutoTuneButtonStyle(hot: IsOverMainAutoTuneHotzone());
        if (_mainAutoTuneButton is not null)
        {
            _mainAutoTuneButton.Visible = true;
            _mainAutoTuneButton.BringToFront();
        }

        _volumeSlider.Value = Math.Clamp(snap.Audio1Volume, 0, 100);
        _appSettings.Audio1.Volume = _volumeSlider.Value;
        _appSettings.Audio1.Enabled = snap.Audio1Enabled;
        _appSettings.Audio1.DeviceId = snap.Audio1DeviceId;
        _appSettings.Audio1.DeviceName = snap.Audio1DeviceName ?? "";
        if (_audioOutputs[0] is not null) _audioOutputs[0]!.VolumePercent = _volumeSlider.Value;
        _squelchCheck.Checked = snap.Audio1SquelchEnabled;
        _squelchThreshold.Value = Math.Clamp(snap.Audio1SquelchThreshold, -140, 0);
        _rxSquelchBar.Value = (int)_squelchThreshold.Value;
        _appSettings.Audio1.SquelchEnabled = snap.Audio1SquelchEnabled;
        _appSettings.Audio1.SquelchThreshold = (int)_squelchThreshold.Value;
        _rxVolumeBar2.Value = Math.Clamp(snap.Audio2Volume, 0, 100);
        _appSettings.Audio2.Volume = _rxVolumeBar2.Value;
        _appSettings.Audio2.Enabled = snap.Audio2Enabled;
        _appSettings.Audio2.DeviceId = snap.Audio2DeviceId;
        _appSettings.Audio2.DeviceName = snap.Audio2DeviceName ?? "";
        if (_audioOutputs[1] is not null) _audioOutputs[1]!.VolumePercent = _rxVolumeBar2.Value;
        _squelchCheck2.Checked = snap.Audio2SquelchEnabled;
        _rxSquelchBar2.Value = Math.Clamp(snap.Audio2SquelchThreshold, -140, 0);
        _appSettings.Audio2.SquelchEnabled = snap.Audio2SquelchEnabled;
        _appSettings.Audio2.SquelchThreshold = _rxSquelchBar2.Value;
        ApplyAudioDspSettings();
        _audioCheck.Checked = _appSettings.Audio1.Enabled || _appSettings.Audio2.Enabled;

        _rfPopOutFullscreen = snap.RfDisplayFullscreen;
        _rfPopOutBounds = snap.RfDisplayWidth > 0
            ? new Rectangle(snap.RfDisplayX, snap.RfDisplayY, snap.RfDisplayWidth, snap.RfDisplayHeight)
            : _rfPopOutBounds;
        _afPopOutFullscreen = snap.AfDisplayFullscreen;
        _afPopOutBounds = snap.AfDisplayWidth > 0
            ? new Rectangle(snap.AfDisplayX, snap.AfDisplayY, snap.AfDisplayWidth, snap.AfDisplayHeight)
            : _afPopOutBounds;

        _gainSlider.Value = Math.Clamp(snap.RfGain, _gainSlider.Minimum, _gainSlider.Maximum);
        _appSettings.RfGain = _gainSlider.Value;
        ApplyUserRfGain();

        _suppressModeDefaults = true;
        try
        {
            if (_modeBox.Items.Contains(snap.Mode.ToString()))
                _modeBox.SelectedItem = snap.Mode.ToString();
            _demodulator.Mode = snap.Mode;
            _bandwidthBox.Value = Math.Clamp(snap.FilterBandwidth, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
            _demodulator.Bandwidth = (int)_bandwidthBox.Value;
        }
        finally
        {
            _suppressModeDefaults = false;
        }

        var previousFrequency = _tunedFrequency;
        _tunedFrequency = Math.Clamp(snap.TunedFrequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        _rfCenterFrequency = Math.Clamp(snap.RfCenterFrequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        var maxViewBw = _source is IRemoteAudioSampleSource remoteView
            ? Math.Max(remoteView.MaximumSpectrumSpan, Math.Max(5_000, snap.ViewBandwidth))
            : Math.Max(5_000, _source.SampleRate);
        _viewBandwidth = Math.Clamp(snap.ViewBandwidth, 5_000, maxViewBw);
        _viewCenterFrequency = Math.Clamp(snap.ViewCenterFrequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        // Scene owns the waterfall viewport — don't let the first remote FFT row
        // replace center/span with whatever the server had open last session.
        if (_source is IRemoteAudioSampleSource)
            _remoteSpectrumInitialized = true;
        if (_source is IFixedCenterFrequencySampleSource)
        {
            _tunedFrequency = Math.Clamp(_tunedFrequency,
                _rfCenterFrequency - _source.SampleRate * 45L / 100,
                _rfCenterFrequency + _source.SampleRate * 45L / 100);
        }
        else
        {
            Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
            if (!_uiTimer.Enabled) ApplyLogicalCenterToSource();
            _spectrumPipeline.Reset();
        }
        ResetCwIfFrequencyChanged(previousFrequency);
        ApplyDemodFrequencyOffset();
        SyncDigitalModeEngine();
        RebuildSubVfoReceivers();
        RefreshSceneChannelStrip();
        LayoutRxLowerArea();
        // SET / AF Plugins are scene-owned — rebuild runners so instance IDs match this scene.
        _afPluginHost.Rebuild(_appSettings.AfPluginInstances);
        _afPluginHost.SetRoutes(_appSettings.AfPluginVfoRoutes);
        UpdateAfPluginDisplayLayout();
        ApplyAfPluginActivation();
        ApplyIqPluginActivation();
        ApplySlowModePluginSettings();
        ApplyDisplayPlugins(_pluginSelection, persist: false);
        UpdateTuningDisplay();
        ConfigureDisplay();
        ConfigureAfDisplay();
        UpdateRxPanel();
        if (wasRunning && _audioCheck.Checked)
        {
            try
            {
                DisposeAudioOutputs();
                OpenAudioOutputs();
            }
            catch { /* device may be unavailable */ }
        }
        if (applyPopOut)
            ApplyRfDisplayDetachFromScene(snap);
        if (persist)
            SaveSettings();
        if (wasRunning && !_source.IsRunning)
            await RestartReceiverAfterSceneAsync().ConfigureAwait(true);
        _statusLabel.Text =
            $"Scene · {snap.Name} · {_source.Name} · AF {(snap.SingleActiveAfPlugin ? "ONLY ACTIVE" : "ALL ENABLED")} · SUB {snap.SubVfos.Count}";
        }
        finally
        {
            _applyingRxScene = false;
        }
    }

    private void PersistSceneSnapshot(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId) || _persistSceneBusy) return;
        var target = _appSettings.RxScenes.FirstOrDefault(s =>
            s.Id.Equals(sceneId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;
        FlushPluginViewSettingsIntoApp();
        var updated = CaptureRxScene(target.Name, target.Id);
        if (RxScene.IsBuiltInScene(updated.Id))
            updated.Name = target.Name;
        var idx = _appSettings.RxScenes.FindIndex(s =>
            s.Id.Equals(updated.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _appSettings.RxScenes[idx] = updated;
        else _appSettings.RxScenes.Add(updated);
    }

    private void FlushPluginViewSettingsIntoApp()
    {
        try
        {
            _sstvView.SaveSettings(_appSettings);
            _rttyView.SaveSettings(_appSettings);
            _weatherFaxView.SaveSettings(_appSettings);
            _kiwiNavtexView.SaveSettings(_appSettings);
            _kiwiWwvView.SaveSettings(_appSettings);
            _flRttyView.SaveSettings(_appSettings);
            _flCwView.SaveSettings(_appSettings);
            _flFaxView.SaveSettings(_appSettings);
            _kiwiTimecodeView.SaveSettings(_appSettings);
            _adsbView.SaveSettings(_appSettings);
            _lteView.SaveSettings(_appSettings);
        }
        catch { /* views may not be ready during early startup */ }
    }

    private async Task ApplySceneSampleSourceAsync(RxScene snap)
    {
        if (string.IsNullOrWhiteSpace(snap.SourceName)) return;
        var index = _sources.FindIndex(source =>
            source.Name.Equals(snap.SourceName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _statusLabel.Text = $"Scene · source unavailable · {snap.SourceName}";
            return;
        }

        var urlOverride = string.IsNullOrWhiteSpace(snap.RemoteServerUrl) ? null : snap.RemoteServerUrl.Trim();
        if (_rxSourceBox.SelectedIndex != index)
        {
            _suppressSourceChange = true;
            try { _rxSourceBox.SelectedIndex = index; }
            finally { _suppressSourceChange = false; }
        }

        await SelectSourceCoreAsync(_sources[index], urlOverride).ConfigureAwait(true);
        _appSettings.SourceName = _source.Name;
        UpdateRxPanel();
    }

    private async Task RestartReceiverAfterSceneAsync()
    {
        try
        {
            if (_source is IRemoteAudioSampleSource remote)
                await ConnectRemoteAsync(remote).ConfigureAwait(true);
            else if (!_source.IsRunning)
                ToggleReceiver();
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"Scene RX restart failed · {exception.GetBaseException().Message}";
        }
    }
}
