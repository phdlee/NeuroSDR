using NeuroSDR.Audio;
using NeuroSDR.Controls;
using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;
using NeuroSDR.Recording;
using NeuroSDR.Memory;
using NeuroSDR.Plugins;
using NeuroSDR.Plugins.Caption;
using NeuroSDR.Plugins.DigitalVoice;
using NeuroSDR.Plugins.Broadcast;
using NeuroSDR.Settings;
using NeuroSDR.Diagnostics;
using System.Diagnostics;
using System.Runtime;

namespace NeuroSDR;

public partial class frmNeuroSDR : Form
{
    private const int ReclaimedHeaderAfHeight = 70;
    private const int AfPanelMinHeight = 270;
    private const int AfPluginMinWidth = 560;
    private readonly List<ISampleSource> _sources = [];
    private IReadOnlyList<RemoteSdrEntry> _remoteDirectory = [];
    private bool _suppressSourceChange;
    private bool _suppressSiteChange;
    private bool _suppressModeDefaults;
    private bool _rxSceneUiBusy;
    private bool _applyingRxScene;
    private bool _persistSceneBusy;
    private bool _rfDisplayDetached;
    private PopOutHostForm? _rfDetachForm;
    private bool _afDisplayDetached;
    private PopOutHostForm? _afDetachForm;
    private Panel? _centerPluginHost;
    private Panel? _afPopOutChrome;
    private Button? _afPopOutButton;
    private Button? _mainAutoTuneButton;
    private ToolTip? _mainAutoTuneTip;
    private long _nextStatusLabelTick;
    private Rectangle _rfPopOutBounds;
    private Rectangle _afPopOutBounds;
    private bool _rfPopOutFullscreen;
    private bool _afPopOutFullscreen;
    private readonly System.Windows.Forms.Timer _sceneLayoutPersistTimer = new() { Interval = 1_500 };
    private readonly System.Windows.Forms.Timer _mainAutoHideTimer = new() { Interval = 120 };
    private string? _listedSiteProtocol;
    private readonly string _hardwareStatus = string.Empty;
    private ISampleSource _source = null!;
    private readonly SpectrumPipeline _spectrumPipeline = new();
    private readonly AudioDemodulator _demodulator = new();
    private readonly AudioPostProcessor[] _audioProcessors = [new(), new()];
    private readonly AudioSpectrumPipeline _audioSpectrumPipeline = new();
    private readonly CtcssToneDecoder _ctcssDecoder = new();
    private readonly DcsToneDecoder _dcsDecoder = new();
    private readonly DtmfDecoder _dtmfDecoder = new();
    private readonly Mdc1200Decoder _mdcDecoder = new();
    private readonly SceneChannelStrip _sceneChannelStrip = new();
    private Control[]? _rxShiftControls;
    private int[]? _rxShiftBaseY;
    private int _subVfoBaseTop = 271;
    private int _channelAfGrow;
    private bool _mainAutoFollowing;
    private long _mainAutoHoldUntil;
    /// <summary>After a manual VFO change, suppress Main AUTO follow so it doesn't yank the dial back.</summary>
    private long _manualTuneHoldUntil;
    private readonly Dictionary<string, (bool Following, long HoldUntil)> _subAutoFollow = new(StringComparer.OrdinalIgnoreCase);
    private IVoiceAnnouncer _voiceAnnouncer = new NullVoiceAnnouncer();
    private readonly System.Windows.Forms.Timer _voiceDebounceTimer = new() { Interval = 450 };
    private long _pendingVoiceFrequency;
    private float _lastUiCtcssHz = -1f;
    private long _nextCtcssUiTick;
    private readonly WaveOutPlayer?[] _audioOutputs = new WaveOutPlayer?[2];
    private readonly IReadOnlyList<WaveOutDeviceInfo> _audioDevices = WaveOutPlayer.EnumerateDevices();
    private IqWaveRecorder? _iqRecorder;
    private AfStreamWavRecorder? _afRecorder;
    private long _afRecordStartedTick;
    private bool _afRecordBlinkOn;
    private long _nextAfRecordBlinkTick;
    private SubVfoReceiver[] _subVfoReceivers = [];
    private readonly Dictionary<RadioMode, Button> _rxModeButtons = [];
    private readonly Dictionary<int, Button> _rxBandwidthButtons = [];
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _smartRecordTimer = new() { Interval = 1_000 };
    private readonly System.Windows.Forms.Timer _scanTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _remoteViewportTimer = new() { Interval = 140 };
    private readonly System.Windows.Forms.Timer _afVfoHideTimer = new() { Interval = 10_000 };
    private readonly System.Windows.Forms.Timer _rfOverlayHideTimer = new() { Interval = 10_000 };
    private readonly System.Windows.Forms.Timer _subVfoCenterHoldTimer = new() { Interval = 2_000 };
    private string? _pendingSubVfoCenterId;
    private readonly SmartRecordScheduler _smartRecord = new();
    private readonly MemoryChannelStore _memoryStore = new();
    private readonly MemoryScanScheduler _memoryScanner = new();
    private readonly RangeScanScheduler _rangeScanner = new();
    private readonly DisplayPluginCatalog _displayPlugins = new();
    private readonly AfPluginCatalog _afPlugins = new();
    private readonly IqPluginCatalog _iqPlugins = new();
    private readonly AfPluginHost _afPluginHost = null!;
    private readonly IqPluginHost _iqPluginHost = null!;
    private readonly AppSettings _appSettings = AppSettingsStore.Load();
    private PluginSelection _pluginSelection = PluginSelection.Default;
    private MemoryChannelsForm? _memoryForm;
    private MemoryChannel? _scanChannel;
    private long? _rangeScanFrequency;
    private float _signalLevelDb = -140;
    private float _channelSnrDb = 0;
    private int _channelOccupiedFlag;
    private CancellationTokenSource? _eibiAiScanCts;
    private bool _eibiAiScanning;
    private string? _eibiAiScanCaptionId;
    private float _audioLevelDb = -140;
    private float[]? _pendingSpectrum;
    private int _pendingSpectrumGeneration;
    private RemoteSpectrumFrame? _pendingRemoteSpectrum;
    private int _spectrumGeneration;
    private int _pendingRemoteSpectrumGeneration;
    private bool _remoteSpectrumInitialized;
    /// <summary>
    /// While reconnecting, freeze the pre-Start waterfall start/end so early FFT rows
    /// (server default zoom) cannot overwrite the view the user already had on screen.
    /// </summary>
    private bool _restoreRemoteViewOnConnect;
    private long _restoreRemoteViewStart;
    private long _restoreRemoteViewEnd;
    private bool _awaitingRemoteViewport;
    private long _awaitingRemoteViewportCenter;
    private int _awaitingRemoteViewportSpan;
    private CancellationTokenSource? _remoteViewportCts;
    private float[]? _pendingAfSpectrum;
    private float[]? _pendingAfSpectrumLeft;
    private float[]? _pendingAfSpectrumRight;
    private GCLatencyMode _previousGcLatency = GCLatencyMode.Interactive;
    private volatile bool _audioEnabled = true;
    private long _tunedFrequency = 28_000_000;
    private long _rfCenterFrequency = 28_000_000;
    private long _viewCenterFrequency = 28_000_000;
    private int _viewBandwidth = 1_000_000;
    private readonly ListView _ftxResultList = new();
    private readonly ListView _cwResultList = new();
    private readonly Label _ftxStatusLabel = new();
    private readonly Label _cwStatusLabel = new();
    private readonly Queue<long> _ftxSlots = new();
    private readonly Dictionary<string, ListViewItem> _cwRows = [];
    private readonly Label _ftxResultTitle = new();
    private readonly Label _cwResultTitle = new();
    private readonly Dictionary<string, AfPluginTabBinding> _afPluginTabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TabPage> _ownedAfPluginTabs = [];
    private bool _synchronizingAfPluginVfo;
    // Written only by UI selection handlers; RF/DSP workers must never inspect
    // WinForms controls directly (the debugger correctly rejects that access).
    private string _selectedAfVfoId = "main";
    private string _lastSubVfoRangeSignature = string.Empty;
    private string? _lastSelectedAfInstanceId;
    private int _afPluginLayoutDepth;
    private volatile bool _pluginUiSuspended;
    private readonly SstvPluginView _sstvView = new();
    private readonly RttyPluginView _rttyView = new();
    private readonly WeatherFaxPluginView _weatherFaxView = new();
    private readonly KiwiNavtexPluginView _kiwiNavtexView = new();
    private readonly KiwiWwvPluginView _kiwiWwvView = new();
    private readonly FlRttyPluginView _flRttyView = new();
    private readonly FlCwPluginView _flCwView = new();
    private readonly FlFaxPluginView _flFaxView = new();
    private readonly KiwiTimecodePluginView _kiwiTimecodeView = new();
    private readonly AdsbPluginView _adsbView = new();
    private readonly LtePluginView _lteView = new();
    private readonly DigitalModeEngine _digitalMode = new();
    private readonly DigitalModeOptionsPanel _digitalModePanel = new();
    private WaveInCapture? _digitalVoiceWaveIn;
    private AfPcmWavPlayer? _digitalVoiceWavPlayer;
    private DigitalVoiceFeedSource _digitalVoiceFeedSource = DigitalVoiceFeedSource.Rf;
    private readonly AnalogModeOptionsPanel _analogModePanel = new();
    private readonly WfmModeOptionsPanel _wfmModePanel = new();
    private readonly CwModeOptionsPanel _cwModePanel = new();
    private readonly EnFilterVoicePanel _enFilterVoicePanel = new();
    private readonly EnFilterCwPanel _enFilterCwPanel = new();
    private readonly EnFilterProcessor _enFilter = new();
    private readonly GraphicEqualizer _wfmEq = new();
    private float[]? _lastAfSpectrum;
    private float[]? _lastRfSpectrum;
    private int _lastRfSpectrumSampleRate;
    private long _lastRfSpectrumCenter;
    private long _rfSpectrumFrame;
    private float[]? _scanSpectrum;
    private long _scanSpectrumCenter;
    private int _scanSpectrumSpan;
    private long _afcOffsetHz;
    private float _stereoLeftDb = -140;
    private float _stereoRightDb = -140;
    private string _lastDigitalOverlay = "";
    private float[]? _pendingDigitalVoiceSpectrum;
    private readonly ComboBox _ftxModeBox = new();
    private readonly NumericUpDown _ftxTimeAdjust = new();
    private readonly CheckBox _ftxAutoAdjust = new();
    private readonly CheckBox _ftxQsoLines = new();
    private bool _synchronizingFtxControls;
    private bool _resizingCwColumns;
    private long _pendingCenterFrequency = 28_000_000;
    private long _renderedSpectrumFrames;
    private long _renderedAfSpectrumFrames;
    private long _nextRfDisplaySubmissionTick;
    private long _nextAutoTuneTick;
    private long _nextSubVfoVisualTick;
    private System.Windows.Forms.Timer? _gcLatencyTimer;
    private bool _centerDragActive;
    private long _centerDragOriginalCenter, _centerDragOriginalTuned, _centerDragOriginalViewCenter;
    private bool _automatedCenterDragHealthy = true;
    private bool _automatedMemoryScanHealthy = true;
    private bool _automatedRangeScanHealthy = true;
    private bool _automatedAfDragHealthy = true;
    private bool _automatedDisplayPluginHealthy = true;
    private bool _automatedVfoLevelHealthy = true;
    private bool _automatedRestartStateHealthy = true;
    private bool _automatedAfPluginViewHealthy = true;
    private bool _automatedAfLayoutHealthy = true;
    private bool _automatedSoak;
    private bool _passiveDiagnostic;
    private long _passiveUiLastTick, _passiveUiMaximumGapTicks, _passivePcmSamples;
    private int _passiveAudioPeakBits = BitConverter.SingleToInt32Bits(-140f);
    private bool _settingsResetRequested;
    private bool _windowGeometryReady;
    private StreamingFloatResampler? _remoteAudioResampler;
    private int _remoteAudioInputRate;
    private UiPipelineTrace? _pipelineTrace;
    private long _nextPipelineTraceSnapshot;
    private long _nextSubVfoMeterUpdateTick;
    private bool _sitePanelHostedInSubVfo;
    private const string SubVfoHeaderCaption = "SUB VFO  ·  INDEPENDENT CHANNEL DECODING";
    private readonly bool _forcePipelineDiagnostics;
    internal int AutomatedExitCode { get; private set; } = 69;

    public frmNeuroSDR() : this(false, null)
    {
    }

    public frmNeuroSDR(bool forcePipelineDiagnostics, int? forcedSampleRate = null)
    {
        _forcePipelineDiagnostics = forcePipelineDiagnostics;
        InitializeComponent();
        AttachAppChrome();
        if (System.ComponentModel.LicenseManager.UsageMode == System.ComponentModel.LicenseUsageMode.Designtime)
            return;
        var discovered = HardwareSourceCatalog.Discover();
        _sources.AddRange(discovered.Where(result => result.Source is not null).Select(result => result.Source!));
        _remoteDirectory = ListedRemoteDirectory();
        ApplyConfiguredSampleRates();
        _hardwareStatus = string.Join(" · ", discovered.Select(result => result.Status));
        if (_sources.Count == 0) throw new InvalidOperationException("No sample sources are available.");
        _source = _sources.FirstOrDefault(source => source.Name.Equals(_appSettings.SourceName, StringComparison.OrdinalIgnoreCase)) ?? _sources[0];
        if (forcedSampleRate is { } rate && _source is IConfigurableSampleRateSource forcedRateSource &&
            forcedRateSource.SupportedSampleRates.Contains(rate))
            forcedRateSource.ConfiguredSampleRate = rate;
        if (_source is IRemoteAudioSampleSource restoredRemote) restoredRemote.ServerUrl = RemoteUrl(restoredRemote);
        var restoredRadio = RestoredRadioState.From(_appSettings, _source.SampleRate);
        _tunedFrequency = restoredRadio.TunedFrequency;
        _rfCenterFrequency = restoredRadio.RfCenterFrequency;
        _viewCenterFrequency = restoredRadio.ViewCenterFrequency;
        _viewBandwidth = restoredRadio.ViewBandwidth;
        Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
        restoredRadio.ApplyTo(_demodulator);
        PurgeAbsorbedDigitalAfPlugins();
        _afPluginHost = new AfPluginHost(_afPlugins, _appSettings.AfPluginInstances);
        _afPluginHost.SetRoutes(_appSettings.AfPluginVfoRoutes);
        _afPluginHost.ResultAvailable += OnAfPluginResult;
        _iqPluginHost = new IqPluginHost(_iqPlugins, _appSettings.EnabledIqPluginIds);
        _iqPluginHost.ResultAvailable += OnIqPluginResult;
        _pluginSelection = new PluginSelection(_appSettings.SpectrumPluginId, _appSettings.WaterfallPluginId);
        _spectrumPipeline.SpectrumAvailable += OnSpectrumAvailable;
        _audioSpectrumPipeline.SpectrumAvailable += spectrum => Interlocked.Exchange(ref _pendingAfSpectrum, spectrum);
        _audioSpectrumPipeline.StereoSpectrumAvailable += (left, right) =>
        {
            Interlocked.Exchange(ref _pendingAfSpectrumLeft, left);
            Interlocked.Exchange(ref _pendingAfSpectrumRight, right);
        };
        RegisterRxPanelButtons();
        ConfigureDigitalModeUi();
        ConfigureSatelliteUi();
        ConfigureRuntimeInterface();
        RebuildSubVfoReceivers();
        ConfigureSmartRecord();
        _smartRecord.StatusChanged += text =>
        {
            if (IsDisposed || Disposing) return;
            try { BeginInvoke(() => { if (!IsDisposed) _statusLabel.Text = text; }); } catch { }
        };
        _smartRecordTimer.Tick += (_, _) =>
        {
            if (IsDisposed || Disposing) return;
            TickSmartRecord();
            RefreshEibiWaterfall(force: false);
        };
        _smartRecordTimer.Start();
        ApplySavedSettings();
        ApplyDisplayPlugins(_pluginSelection, false);
        WireEvents();
        _voiceAnnouncer = new CompositeVoiceAnnouncer();
        _voiceDebounceTimer.Tick += (_, _) =>
        {
            _voiceDebounceTimer.Stop();
            if (!_appSettings.VoiceGuidanceEnabled || IsDisposed || Disposing) return;
            _voiceAnnouncer.SpeakFrequencyHz(Interlocked.Read(ref _pendingVoiceFrequency));
        };
        RefreshRxSceneCombo(_appSettings.SelectedRxSceneId);
        // Combo selection is suppressed while busy — apply selected SCENE so channels/state load.
        if (_rxSceneBox.SelectedItem is RxScene startupScene)
            ApplyRxScene(startupScene, applyPopOut: false, persist: false);
        // Pop-out restore needs a created handle + screen metrics; defer until Shown,
        // then raise pop-outs after the main form finishes activating.
        Shown += (_, _) =>
        {
            if (IsDisposed) return;
            if (_rxSceneBox.SelectedItem is RxScene scene)
                ApplyRfDisplayDetachFromScene(scene);
            BeginInvoke(RaiseDetachedPopOutsToFront);
        };
        ApplyAfPluginActivation();
        ApplyIqPluginActivation();
        ApplySlowModePluginSettings();
        ApplySourceHardwareSettings();
        ApplyAudioDspSettings();
        ApplyRtlTcpEndpointFromSettings();
        AttachSource(_source);
        if (_source is not IFixedCenterFrequencySampleSource)
        {
            ApplyLogicalCenterToSource();
        }
        else
        {
            _rfCenterFrequency = _source.CenterFrequency;
        }
        Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
        ApplyDemodFrequencyOffset();
        UpdateTuningDisplay();
        RestoreWindowState();
        ConfigurePipelineDiagnostics();
        StartRemoteWebIfEnabled();
    }

    private void RegisterRxPanelButtons()
    {
        _rxModeButtons.Clear();
        _rxModeButtons.Add(RadioMode.AM, _modeAmButton);
        _rxModeButtons.Add(RadioMode.SAM, _modeSamButton);
        _rxModeButtons.Add(RadioMode.DMR, _modeDmrButton);
        _rxModeButtons.Add(RadioMode.DSTAR, _modeDstarButton);
        _rxModeButtons.Add(RadioMode.C4FM, _modeC4fmButton);
        _rxModeButtons.Add(RadioMode.FREEDV, _modeFreedvButton);
        _rxModeButtons.Add(RadioMode.NFM, _modeNfmButton);
        _rxModeButtons.Add(RadioMode.WFM, _modeWfmButton);
        _rxModeButtons.Add(RadioMode.USB, _modeUsbButton);
        _rxModeButtons.Add(RadioMode.LSB, _modeLsbButton);
        _rxModeButtons.Add(RadioMode.CW, _modeCwButton);
        _rxModeButtons.Add(RadioMode.RAW, _modeRawButton);

        _rxBandwidthButtons.Clear();
        _rxBandwidthButtons.Add(500, _bandwidth500Button);
        _rxBandwidthButtons.Add(2_700, _bandwidth2700Button);
        _rxBandwidthButtons.Add(4_000, _bandwidth4000Button);
        _rxBandwidthButtons.Add(7_000, _bandwidth7000Button);
        _rxBandwidthButtons.Add(10_000, _bandwidth10000Button);
        _rxBandwidthButtons.Add(12_500, _bandwidth12500Button);
        _rxBandwidthButtons.Add(180_000, _bandwidth180000Button);
    }

    private void ConfigureRuntimeInterface()
    {
        _afPanel.Height = Math.Clamp(_appSettings.AfPluginDisplayHeight + ReclaimedHeaderAfHeight + _channelAfGrow, AfPanelMinHeight, 720 + _channelAfGrow);
        _decoderPanel.Width = Math.Clamp(_appSettings.AfPluginDisplayWidth, AfPluginMinWidth, 900);
        _sampleRateValueLabel.Text = $"{_source.SampleRate / 1_000_000d:0.000} MS/s";
        FillRxSourceBox();
        DarkNativeTheme.Apply(_subVfoList);
        BuildDecoderResultPanel();
        WireSceneChannelStrip();
        EnsureMainAutoTuneButton();
        RefreshSceneChannelStrip();
        LayoutRxLowerArea();
        RefreshAfDisplayVfoChoices();
        PositionAfDisplayOverlay();
        PositionRfDisplayOverlay();
        ConfigureAfDisplay();
        UpdateRxPanel();
    }

    private void LayoutRxLowerArea()
    {
        ApplySceneChannelStripLayout();
        _subVfoArea.Width = Math.Max(40, _radioPanel.ClientSize.Width - 20);
        var available = Math.Max(0, _radioPanel.ClientSize.Height - _subVfoArea.Top - 8);
        var remote = _source is IRemoteAudioSampleSource;
        var contentNeeded = remote
            ? 130
            : 37 + (_appSettings.SubVfos.Count == 0 ? 55 : _appSettings.SubVfos.Count * 33);
        _statusLabel.Visible = available >= contentNeeded + _statusLabel.Height;
        _subVfoArea.Height = Math.Max(0, available - (_statusLabel.Visible ? _statusLabel.Height + 2 : 0));
        _subVfoArea.Visible = _subVfoArea.Height >= 30;
        ApplyWebSdrSubVfoChrome();
        if (!remote)
        {
            ResizeSubVfoRows();
            ResetSubVfoListScroll();
        }
        else StretchSitePanelToSubVfo();
    }

    /// <summary>
    /// WebSDR / Kiwi / OpenWebRX have no local IQ sub-VFOs. Reuse the SUB VFO slot
    /// for SITE / favorites / URL instead of parking those controls in the sidebar.
    /// </summary>
    private void ApplyWebSdrSubVfoChrome()
    {
        var remote = _source as IRemoteAudioSampleSource;
        if (remote is not null)
        {
            if (!_sitePanelHostedInSubVfo)
            {
                _sidebarControls.Controls.Remove(_sitePanel);
                _subVfoArea.Controls.Add(_sitePanel);
                _sitePanelHostedInSubVfo = true;
            }
            _subVfoList.Visible = false;
            _subVfoList.Dock = DockStyle.None;
            _addSubVfoButton.Visible = false;
            _sitePanel.Visible = true;
            _sitePanel.AutoSize = false;
            _sitePanel.Dock = DockStyle.Fill;
            _sitePanel.Padding = new Padding(4, 2, 4, 4);
            _subVfoHeader.Text = remote.Name switch
            {
                "Virtual WebSDR" => "WEBSDR  ·  SITE / FAVORITES / URL",
                "Virtual KiwiSDR" => "KIWISDR  ·  SITE / FAVORITES / URL",
                _ => "OPENWEBRX  ·  SITE / FAVORITES / URL"
            };
            // Keep header on top of the fill-docked site panel.
            _subVfoHeader.BringToFront();
            _sitePanel.BringToFront();
            _siteFindButton.Visible = remote.Name is "Virtual WebSDR" or "Virtual KiwiSDR";
            StretchSitePanelToSubVfo();
        }
        else
        {
            if (_sitePanelHostedInSubVfo)
            {
                _subVfoArea.Controls.Remove(_sitePanel);
                _sidebarControls.Controls.Add(_sitePanel);
                _sidebarControls.Controls.SetChildIndex(_sitePanel, 0);
                _sitePanelHostedInSubVfo = false;
            }
            _sitePanel.Visible = false;
            _sitePanel.AutoSize = true;
            _sitePanel.Dock = DockStyle.None;
            _sitePanel.Padding = Padding.Empty;
            _sitePanel.Width = 240;
            RestoreSitePanelDefaultWidths();
            _addSubVfoButton.Visible = true;
            _subVfoHeader.Text = SubVfoHeaderCaption;
            // Dock order: list at index 0, header last so Top docks first and Fill gets remainder.
            _subVfoList.Dock = DockStyle.Fill;
            _subVfoList.Visible = true;
            if (_subVfoArea.Controls.Contains(_subVfoList))
                _subVfoArea.Controls.SetChildIndex(_subVfoList, 0);
            if (_subVfoArea.Controls.Contains(_subVfoHeader))
                _subVfoArea.Controls.SetChildIndex(_subVfoHeader, _subVfoArea.Controls.Count - 1);
            ResetSubVfoListScroll();
            _subVfoArea.PerformLayout();
        }
    }

    private void ResetSubVfoListScroll()
    {
        if (_subVfoList.IsDisposed) return;
        try
        {
            // FlowLayoutPanel keeps AutoScrollPosition after remote chrome — first row clips to ~3px.
            _subVfoList.AutoScrollPosition = new Point(0, 0);
            if (_subVfoList.VerticalScroll.Maximum > 0)
                _subVfoList.VerticalScroll.Value = 0;
            _subVfoList.PerformLayout();
        }
        catch { /* ignore */ }
    }

    private void StretchSitePanelToSubVfo()
    {
        if (!_sitePanelHostedInSubVfo || _sitePanel.IsDisposed) return;
        var width = Math.Max(200, _subVfoArea.ClientSize.Width - _sitePanel.Padding.Horizontal - 8);
        _sitePanel.Width = width;
        _siteLabel.Width = width;
        _favoriteBox.Width = width;
        _favoriteButtonsPanel.Width = width;
        _siteBox.Width = Math.Max(120, width - 72);
        _siteBox.DropDownWidth = Math.Max(420, width);
        _sitePickRow.Width = width;
        _webUrlInput.Width = width;
        // Prefer a compact favorites row: ★ Add / Edit share the full width.
        var buttonGap = 8;
        var half = Math.Max(72, (width - buttonGap) / 2);
        _favoriteButton.Width = half;
        _favoriteEditButton.Width = half;
        _favoriteButton.Margin = new Padding(0, 0, buttonGap, 0);
        _favoriteEditButton.Margin = Padding.Empty;
    }

    private void RestoreSitePanelDefaultWidths()
    {
        _siteLabel.Width = 212;
        _favoriteBox.Width = 212;
        _favoriteButtonsPanel.Width = 212;
        _sitePickRow.Width = 212;
        _siteBox.Width = 140;
        _siteBox.DropDownWidth = 420;
        _webUrlInput.Width = 212;
        _favoriteButton.Width = 100;
        _favoriteEditButton.Width = 100;
        _favoriteButton.Margin = new Padding(0, 0, 8, 0);
        _favoriteEditButton.Margin = Padding.Empty;
    }

    private void EnsureRxShiftBaselines()
    {
        if (_rxShiftControls is not null) return;
        _rxShiftControls =
        [
            _modeAmButton, _modeSamButton, _modeDmrButton, _modeDstarButton, _modeC4fmButton, _modeFreedvButton,
            _modeNfmButton, _modeWfmButton, _modeUsbButton, _modeLsbButton, _modeCwButton, _modeRawButton,
            _bandMwButton, _bandSwButton, _band160Button, _band80Button, _band40Button, _band30Button,
            _band20Button, _band17Button, _band15Button, _band12Button, _band10Button, _band6Button,
            _bandFmButton, _bandAirButton,
            _rxBandwidthLabel, _bandwidth500Button, _bandwidth2700Button, _bandwidth4000Button,
            _bandwidth7000Button, _bandwidth10000Button, _bandwidth12500Button, _bandwidth180000Button,
            _bandwidthDownButton, _bandwidthUpButton,
            _rxVolumeBar, _rxSquelchBar, _rxVolumeBar2, _rxSquelchBar2,
            _squelchCheck, _squelchCheck2, _sqlLed1, _sqlLed2, _stereoLed
        ];
        _rxShiftBaseY = _rxShiftControls.Select(c => c.Top).ToArray();
        _subVfoBaseTop = _subVfoArea.Top;
    }
    private string AfPluginBaseCaption(string pluginId)
    {
        var name = _afPlugins.Plugins.FirstOrDefault(plugin =>
            plugin.Info.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase))?.Info.Name;
        return string.IsNullOrWhiteSpace(name) ? "AF PLUGIN" : name;
    }

    private string AfPluginTabCaption(AfPluginInstanceSettings instance, int ordinal)
    {
        if (instance.PluginId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase))
            return ordinal > 1 ? $"CEC CW {ordinal}" : "CEC CW";
        if (NeuroCaption.IsCaptionPlugin(instance.PluginId))
        {
            var shortName = AfPluginBaseCaption(instance.PluginId);
            return ordinal > 1 ? $"{shortName} {ordinal}" : shortName;
        }
        var caption = instance.PluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase)
            ? NormalizeFtxVariant(instance.Variant) : AfPluginBaseCaption(instance.PluginId);
        return $"{caption} {ordinal}";
    }

    private static string AfPluginOrdinalKey(AfPluginInstanceSettings instance) =>
        instance.PluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase)
            ? $"{instance.PluginId}:{NormalizeFtxVariant(instance.Variant)}"
            : instance.PluginId;

    private static string NormalizeFtxVariant(string? variant) =>
        variant != null && variant.Equals("FT4", StringComparison.OrdinalIgnoreCase) ? "FT4" : "FT8";

    private static Button MakeRxButton(string text, int x, int y, int width)
    {
        var button = new Button();
        ConfigureRxButton(button, text, x, y, width);
        return button;
    }

    private static int RxRowX(int index, int count, int left, int right, int width)
    {
        if (count <= 1) return left;
        return left + index * (right - left - width) / (count - 1);
    }

    private static void ConfigureRxButton(Button button, string text, int x, int y, int width)
    {
        button.Text = text;
        button.Location = new Point(x, y);
        button.Size = new Size(width, 24);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(35, 66, 83);
        button.ForeColor = Color.FromArgb(222, 233, 240);
        button.Font = new Font("Segoe UI Semibold", 8f);
        button.Margin = Padding.Empty;
        button.TabStop = false;
        button.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        button.FlatAppearance.BorderSize = 1;
    }

    private static void StylePrimaryRxButton(Button button, Color color)
    {
        button.Size = new Size(81, 31);
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
        button.FlatAppearance.BorderSize = 2;
        button.FlatAppearance.BorderColor = Color.FromArgb(8, 18, 25);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(
            Math.Min(255, color.R + 18), Math.Min(255, color.G + 18), Math.Min(255, color.B + 18));
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(
            Math.Max(0, color.R - 25), Math.Max(0, color.G - 25), Math.Max(0, color.B - 25));
        button.Padding = new Padding(0, 1, 0, 0);
    }

    private void FillRxSourceBox()
    {
        _suppressSourceChange = true;
        _rxSourceBox.BeginUpdate();
        _rxSourceBox.Items.Clear();
        foreach (var source in _sources)
            _rxSourceBox.Items.Add(RemoteSdrCatalog.DisplayName(source.Name));
        var index = Math.Max(0, _sources.FindIndex(source => ReferenceEquals(source, _source)));
        if (_rxSourceBox.Items.Count > 0) _rxSourceBox.SelectedIndex = index;
        _rxSourceBox.EndUpdate();
        _suppressSourceChange = false;
    }

    private void SyncRxSourceBox()
    {
        var index = _sources.FindIndex(source => ReferenceEquals(source, _source));
        if (index < 0 || index == _rxSourceBox.SelectedIndex) return;
        _suppressSourceChange = true;
        _rxSourceBox.SelectedIndex = index;
        _suppressSourceChange = false;
    }

    private static IReadOnlyList<RemoteSdrEntry> ListedRemoteDirectory() =>
        RemoteSdrCatalog.LoadDirectory();

    private void ResetRfSpectrum()
    {
        Interlocked.Increment(ref _spectrumGeneration);
        _spectrumPipeline.Reset();
        Interlocked.Exchange(ref _pendingSpectrum, null);
        Interlocked.Exchange(ref _pendingRemoteSpectrum, null);
        _remoteSpectrumInitialized = false;
        _display.ClearSpectrum();
    }

    private void RefreshSiteLists(bool force = false)
    {
        var protocol = RemoteSdrCatalog.ProtocolForSourceName(_source.Name);
        if (protocol is null) return;
        if (!force && _listedSiteProtocol == protocol && _siteBox.Items.Count > 0)
        {
            _suppressSiteChange = true;
            _favoriteBox.BeginUpdate();
            _favoriteBox.Items.Clear();
            foreach (var favorite in _appSettings.RemoteSdrFavorites.Where(item =>
                         RemoteSdrCatalog.NormalizeProtocol(item.Protocol) == protocol))
                _favoriteBox.Items.Add(favorite);
            _favoriteBox.EndUpdate();
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => { _suppressSiteChange = false; });
            else
                _suppressSiteChange = false;
            return;
        }
        _listedSiteProtocol = protocol;
        _suppressSiteChange = true;
        _favoriteBox.BeginUpdate();
        _favoriteBox.Items.Clear();
        foreach (var favorite in _appSettings.RemoteSdrFavorites.Where(item =>
                     RemoteSdrCatalog.NormalizeProtocol(item.Protocol) == protocol))
            _favoriteBox.Items.Add(favorite);
        _favoriteBox.EndUpdate();
        _siteBox.BeginUpdate();
        _siteBox.Items.Clear();
        IEnumerable<RemoteSdrEntry> sites = _remoteDirectory.Where(item => item.Protocol == protocol);
        if (_appSettings.RemoteSdrReachableOnly)
            sites = sites.Where(item => RemoteSdrReachability.IsReachable(item.Url));
        if (_appSettings.RemoteSdrPreferReception)
            sites = sites.OrderByDescending(item => RemoteSdrReachability.Find(item.Url)?.ReceiveScore ?? 0)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sites)
            _siteBox.Items.Add(entry);
        _siteBox.EndUpdate();
        SelectSiteMatchingCurrentUrl();
        // ComboBox may raise SelectedIndexChanged after EndUpdate returns. Keep suppress
        // until the next UI turn so directory refresh does not auto-reconnect.
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(() => { _suppressSiteChange = false; });
        else
            _suppressSiteChange = false;
    }

    internal void PrepareShortwaveMapReceiver()
    {
        if (_source is not IRemoteAudioSampleSource)
            RestoreLocalSatelliteObserver();
    }

    internal (double Lat, double Lon, string Label)? TryGetMapReceiverLocation()
    {
        if (_source is IRemoteAudioSampleSource remote)
        {
            var url = string.IsNullOrWhiteSpace(remote.ServerUrl) ? _webUrlInput.Text : remote.ServerUrl;
            RemoteSdrEntry? entry = _siteBox.SelectedItem as RemoteSdrEntry;
            if (entry is null || !RemoteSdrCatalog.SameSite(entry.Url, url))
                entry = _remoteDirectory.FirstOrDefault(item => RemoteSdrCatalog.SameSite(item.Url, url));
            if (entry is { HasCoordinates: true })
            {
                var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Protocol : entry.Name;
                return (entry.Latitude!.Value, entry.Longitude!.Value, name);
            }
            return TryGetLocalMapReceiverLocation();
        }

        return TryGetLocalMapReceiverLocation();
    }

    private (double Lat, double Lon, string Label)? TryGetLocalMapReceiverLocation()
    {
        if (_satellitePanel?.TryGetLocalHome() is { } live &&
            !IsRemoteObserverLabel(live.Name) &&
            live.Latitude is >= -90 and <= 90 &&
            live.Longitude is >= -180 and <= 180)
            return (live.Latitude, live.Longitude, LocalHomeLabel(live.Name));

        var homes = _appSettings.SatelliteHomes ?? [];
        var selectedId = _appSettings.SatelliteSelectedHomeId;
        var home = homes.FirstOrDefault(item =>
                       IsUsableLocalHome(item) &&
                       !string.IsNullOrWhiteSpace(selectedId) &&
                       !selectedId.StartsWith("remotesdr:", StringComparison.OrdinalIgnoreCase) &&
                       item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                   ?? homes.FirstOrDefault(IsUsableLocalHome);
        if (home is not null)
            return (home.Latitude, home.Longitude, LocalHomeLabel(home.Name));

        if (!IsRemoteObserverLabel(_appSettings.SatelliteLocationLabel) &&
            !(_appSettings.SatelliteSelectedHomeId ?? "").StartsWith("remotesdr:", StringComparison.OrdinalIgnoreCase))
        {
            var lat = _appSettings.SatelliteLatitude;
            var lon = _appSettings.SatelliteLongitude;
            if (!double.IsNaN(lat) && !double.IsNaN(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
                return (lat, lon, LocalHomeLabel(_appSettings.SatelliteLocationLabel));
        }

        var fallback = DefaultLocalObserver();
        return (fallback.Lat, fallback.Lon, fallback.Label);
    }

    private (double Lat, double Lon, double Alt, string Label) DefaultLocalObserver()
    {
        foreach (var scene in _appSettings.RxScenes ?? [])
        {
            if (RxScene.IsSatelliteScene(scene.Id)) continue;
            if (IsRemoteObserverLabel(scene.SatelliteLocationLabel)) continue;
            if (scene.SatelliteLatitude is < -90 or > 90 || scene.SatelliteLongitude is < -180 or > 180)
                continue;
            return (scene.SatelliteLatitude, scene.SatelliteLongitude, scene.SatelliteAltitudeMeters,
                LocalHomeLabel(scene.SatelliteLocationLabel));
        }

        return (0, 0, 0, "Local RX");
    }

    private static bool IsUsableLocalHome(SatelliteHomeLocation item) =>
        !item.IsRemoteSdr &&
        !IsRemoteObserverLabel(item.Name) &&
        item.Latitude is >= -90 and <= 90 &&
        item.Longitude is >= -180 and <= 180;

    private static string LocalHomeLabel(string? name)
    {
        var label = (name ?? "").Replace(" (default)", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (label.Length == 0 || IsRemoteObserverLabel(label)) return "Local RX";
        return label;
    }

    private static bool IsRemoteObserverLabel(string? label)
    {
        var text = (label ?? "").Trim();
        if (text.Length == 0) return false;
        return text.StartsWith("Kiwi", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Web ·", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("WebSDR", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("OWRX", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("OpenWebRX", StringComparison.OrdinalIgnoreCase);
    }

    private void OpenRemoteSdrFind()
    {
        var protocol = RemoteSdrCatalog.ProtocolForSourceName(_source.Name);
        if (protocol is not ("WebSDR" or "KiwiSDR")) return;
        var lat = _appSettings.SatelliteLatitude;
        var lon = _appSettings.SatelliteLongitude;
        var haveGps = Math.Abs(lat) > 0.01 || Math.Abs(lon) > 0.01;
        using var find = new RemoteSdrFindForm(
            _remoteDirectory, protocol, _webUrlInput.Text, lat, lon, haveGps);
        var result = find.ShowDialog(this);
        if (find.DirectoryChanged)
        {
            _remoteDirectory = ListedRemoteDirectory();
            RefreshSiteLists(force: true);
        }
        if (result != DialogResult.OK || find.Selected is null) return;
        _ = ApplyRemoteEntryAsync(find.Selected);
    }

    private void SelectSiteMatchingCurrentUrl()
    {
        var url = _source is IRemoteAudioSampleSource remote && remote.ServerUrl.Trim().Length > 0
            ? remote.ServerUrl
            : _webUrlInput.Text;
        SelectSiteInCombo(url);
    }

    private void SelectSiteInCombo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var match = _siteBox.Items.Cast<object>().OfType<RemoteSdrEntry>()
            .FirstOrDefault(entry => RemoteSdrCatalog.SameSite(entry.Url, url));
        if (match is null) return;
        if (ReferenceEquals(_siteBox.SelectedItem, match)) return;
        _suppressSiteChange = true;
        _siteBox.SelectedItem = match;
        _siteBox.Text = match.ToString();
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(() => { _suppressSiteChange = false; });
        else
            _suppressSiteChange = false;
    }

    private void SelectSiteInCombo(RemoteSdrEntry entry)
    {
        var match = _siteBox.Items.Cast<object>().OfType<RemoteSdrEntry>()
            .FirstOrDefault(item => RemoteSdrCatalog.SameSite(item.Url, entry.Url));
        if (match is null)
        {
            _siteBox.Items.Add(entry);
            match = entry;
        }
        if (ReferenceEquals(_siteBox.SelectedItem, match)) return;
        _suppressSiteChange = true;
        _siteBox.SelectedItem = match;
        _siteBox.Text = match.ToString();
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(() => { _suppressSiteChange = false; });
        else
            _suppressSiteChange = false;
    }

    private bool _remoteDirectoryRefreshBusy;

    private void BeginRefreshRemoteDirectory()
    {
        if (_remoteDirectoryRefreshBusy || IsDisposed || Disposing) return;
        _remoteDirectoryRefreshBusy = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await RemoteSdrCatalog.RefreshOfficialDirectoriesAsync().ConfigureAwait(false);
                if (!ok || IsDisposed || Disposing) return;
                void Apply()
                {
                    if (IsDisposed || Disposing) return;
                    _remoteDirectory = ListedRemoteDirectory();
                    _listedSiteProtocol = null;
                    if (_source is IRemoteAudioSampleSource)
                        RefreshSiteLists(force: true);
                    if (IsSatelliteSceneActive)
                        RefreshSatelliteRemoteHomes();
                }
                if (InvokeRequired) BeginInvoke(Apply);
                else Apply();
            }
            catch
            {
                // keep CSV / previous cache
            }
            finally
            {
                _remoteDirectoryRefreshBusy = false;
            }
        });
    }

    private async Task ApplyRemoteEntryAsync(RemoteSdrEntry entry)
    {
        SelectSiteInCombo(entry);
        await ApplyRemoteUrlAsync(entry.Url, entry.Bands);
        if (entry.HasCoordinates)
            TrySyncSatelliteObserverFromCurrentRemote();
    }

    private async Task ApplyRemoteUrlAsync(string url, IReadOnlyList<RemoteSdrBandSpan>? bands = null)
    {
        if (_source is not IRemoteAudioSampleSource remote) return;
        url = url.Trim();
        if (url.Length == 0) return;
        if (!RemoteSdrCatalog.SameSite(remote.ServerUrl, url))
            ResetRfSpectrum();
        RememberDisplayLevels();
        _webUrlInput.Text = url;
        remote.ServerUrl = url;
        SaveRemoteUrl(remote);
        SelectSiteMatchingCurrentUrl();
        if (bands is { Count: > 0 })
        {
            var next = RemoteSdrBands.ClampHz(_tunedFrequency, bands);
            if (next != _tunedFrequency)
                _restoreRemoteViewOnConnect = false;
            ChangeVfoFromDigitalDisplay(next, bands: bands);
        }
        await ConnectRemoteAsync(remote);
        ApplyDisplayLevelsForCurrentSource();
        TrySyncSatelliteObserverFromCurrentRemote();
    }

    private async Task ConnectRemoteAsync(IRemoteAudioSampleSource remote)
    {
        _startButton.Enabled = false;
        // Capture waterfall edges before Connect mutates SampleRate / default zoom.
        _restoreRemoteViewStart = _viewCenterFrequency - _viewBandwidth / 2L;
        _restoreRemoteViewEnd = _viewCenterFrequency + _viewBandwidth / 2L;
        _restoreRemoteViewOnConnect = _restoreRemoteViewEnd > _restoreRemoteViewStart;
        var constrained = ConstrainFrequencyForCurrentSource(_tunedFrequency);
        if (constrained != _tunedFrequency)
        {
            _restoreRemoteViewOnConnect = false;
            ChangeVfoFromDigitalDisplay(constrained);
        }
        try
        {
            DisposeAudioOutputs();
            if (_audioCheck.Checked)
            {
                try
                {
                    OpenAudioOutputs();
                    _demodulator.Reset();
                    foreach (var processor in _audioProcessors) processor.Reset();
                }
                catch (Exception audioException)
                {
                    DisposeAudioOutputs();
                    _statusLabel.Text = $"Audio disabled: {audioException.Message}";
                }
            }

            // Connect first — ApplyReceiver needs an open SND/session.
            // If the dial is outside every band, connect moves the VFO onto one the
            // server actually has. Adopt that before ApplyLogicalCenter, or the
            // display snaps back to the old frequency while audio stays on the
            // server VFO (often a digital channel).
            await remote.ConnectAsync();
            if (remote.CenterFrequency > 0 && remote.CenterFrequency != _tunedFrequency)
            {
                _tunedFrequency = remote.CenterFrequency;
                UpdateFrequencyReadout();
            }
            await remote.ApplyReceiverAsync(RadioModes.DemodMode(_demodulator.Mode, _demodulator.SsbLower), _demodulator.Bandwidth);
            ApplyLogicalCenterToSource();

            // Match ToggleReceiver(Start): without the UI timer, pending spectrum/audio meters never flush.
            RuntimeWarmup.StartIfNeeded();
            EnterRealtimeGarbageCollection();
            _uiTimer.Start();
            AppIcons.ApplyRxButton(_startButton, running: true);
            _statusLabel.Text = remote.ConnectionStatus;

            await ApplyRemoteSpectrumViewOnConnectAsync(remote).ConfigureAwait(true);
            TrySyncSatelliteObserverFromCurrentRemote();
            if (IsSatelliteSceneActive)
                _ = ApplySatelliteTrackingAsync();
        }
        catch (Exception exception)
        {
            DisposeAudioOutputs();
            AppIcons.ApplyRxButton(_startButton, running: false);
            _statusLabel.Text = $"Web SDR connection failed · {exception.GetBaseException().Message}";
        }
        finally
        {
            _restoreRemoteViewOnConnect = false;
            _startButton.Enabled = true;
        }
    }

    /// <summary>
    /// After remote connect: push the pre-Start waterfall start/end to the server,
    /// then re-center on the discrete zoom the server actually opened so the same
    /// RF window is restored as closely as possible.
    /// </summary>
    private async Task ApplyRemoteSpectrumViewOnConnectAsync(IRemoteAudioSampleSource remote)
    {
        var maxSpan = Math.Max(MinimumViewBandwidth(), remote.MaximumSpectrumSpan);
        var minSpan = MinimumViewBandwidth();
        var defaultSpan = DefaultRemoteViewSpanHz(remote, maxSpan);

        long desiredStart;
        long desiredEnd;
        if (_restoreRemoteViewOnConnect && _restoreRemoteViewEnd > _restoreRemoteViewStart)
        {
            desiredStart = _restoreRemoteViewStart;
            desiredEnd = _restoreRemoteViewEnd;
        }
        else
        {
            desiredStart = _viewCenterFrequency - _viewBandwidth / 2L;
            desiredEnd = _viewCenterFrequency + _viewBandwidth / 2L;
        }

        var desiredSpan = (int)Math.Clamp(desiredEnd - desiredStart, minSpan, maxSpan);
        var desiredMid = desiredStart + (desiredEnd - desiredStart) / 2;
        var hasSavedView = desiredSpan >= minSpan &&
                           desiredMid >= RadioLimits.MinimumFrequency &&
                           desiredMid <= RadioLimits.MaximumFrequency;
        if (!hasSavedView)
        {
            desiredSpan = defaultSpan;
            desiredMid = ClampViewCenter(_tunedFrequency, desiredSpan);
            desiredStart = desiredMid - desiredSpan / 2L;
            desiredEnd = desiredMid + desiredSpan / 2L;
        }

        _viewBandwidth = desiredSpan;
        _viewCenterFrequency = ClampViewCenter(desiredMid, _viewBandwidth);
        desiredMid = _viewCenterFrequency;
        desiredStart = desiredMid - _viewBandwidth / 2L;
        desiredEnd = desiredMid + _viewBandwidth / 2L;
        _remoteSpectrumInitialized = true;
        ConfigureDisplay();
        UpdateZoomControls();

        _remoteViewportCts?.Cancel();
        _remoteViewportCts?.Dispose();
        _remoteViewportCts = new CancellationTokenSource();
        try
        {
            var viewport = await remote.SetSpectrumViewportAsync(
                desiredMid, desiredSpan, _remoteViewportCts.Token).ConfigureAwait(true);
            if (viewport.ServerApplied)
            {
                // Display must match the live capture 1:1. Never touch the audio VFO.
                _viewBandwidth = Math.Max(MinimumViewBandwidth(), viewport.SpanHz);
                _viewCenterFrequency = viewport.CenterFrequency;
                if (!_centerDragActive)
                    _rfCenterFrequency = viewport.CenterFrequency;
            }
            else if (!hasSavedView)
            {
                _viewBandwidth = DefaultRemoteViewSpanHz(remote, Math.Max(MinimumViewBandwidth(), remote.SampleRate));
                _viewCenterFrequency = ClampViewCenter(_tunedFrequency, _viewBandwidth);
                if (!_centerDragActive && remote.CenterFrequency > 0)
                    _rfCenterFrequency = remote.CenterFrequency;
            }
            else
            {
                // OpenWebRX / local-only viewport: keep the pre-Start edges.
                _viewBandwidth = desiredSpan;
                _viewCenterFrequency = ClampViewCenter(desiredMid, _viewBandwidth);
                if (!_centerDragActive && remote.CenterFrequency > 0)
                    _rfCenterFrequency = remote.CenterFrequency;
            }
            ConfigureDisplay();
            UpdateZoomControls();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _statusLabel.Text = $"Viewport warning · {exception.GetBaseException().Message}";
        }
        finally
        {
            _restoreRemoteViewOnConnect = false;
        }
    }

    /// <summary>Sensible initial / "full" span for remote sources (avoids sub-0 Hz Kiwi labels).</summary>
    private static int DefaultRemoteViewSpanHz(IRemoteAudioSampleSource remote, int maximumSpanHz)
    {
        maximumSpanHz = Math.Max(5_000, maximumSpanHz);
        if (remote.Name.Contains("Kiwi", StringComparison.OrdinalIgnoreCase))
            return Math.Min(maximumSpanHz, 2_000_000);
        return maximumSpanHz;
    }

    private void AddRemoteFavorite()
    {
        if (_source is not IRemoteAudioSampleSource remote) return;
        var url = _webUrlInput.Text.Trim();
        if (url.Length == 0) url = remote.ServerUrl.Trim();
        if (url.Length == 0) return;
        var protocol = RemoteSdrCatalog.ProtocolForSourceName(remote.Name) ?? "WebSDR";
        var name = PromptText("Favorites", "Name", SuggestFavoriteName(url));
        if (string.IsNullOrWhiteSpace(name)) return;
        _appSettings.RemoteSdrFavorites.RemoveAll(item =>
            item.Url.Equals(url, StringComparison.OrdinalIgnoreCase) &&
            RemoteSdrCatalog.NormalizeProtocol(item.Protocol) == protocol);
        _appSettings.RemoteSdrFavorites.Add(new RemoteSdrFavorite
        {
            Name = name.Trim(),
            Url = url,
            Protocol = protocol
        });
        AppSettingsStore.Save(_appSettings);
        RefreshSiteLists(true);
        _statusLabel.Text = $"Favorite added · {name.Trim()}";
    }

    private void EditRemoteFavorite()
    {
        if (_favoriteBox.SelectedItem is not RemoteSdrFavorite selected)
        {
            MessageBox.Show(this, "Select a favorite to edit from the list.", "Favorites",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new Form
        {
            Text = "Edit Favorite", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(380, 168),
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            BackColor = Color.FromArgb(20, 27, 37)
        };
        var nameCaption = new Label { Text = "Name", AutoSize = true, ForeColor = Color.Gainsboro, Location = new Point(12, 12) };
        var nameBox = new TextBox
        {
            Text = selected.Name, Location = new Point(12, 32), Width = 356,
            BackColor = Color.FromArgb(8, 21, 30), ForeColor = Color.White
        };
        var urlCaption = new Label { Text = "URL", AutoSize = true, ForeColor = Color.Gainsboro, Location = new Point(12, 64) };
        var urlBox = new TextBox
        {
            Text = selected.Url, Location = new Point(12, 84), Width = 356,
            BackColor = Color.FromArgb(8, 21, 30), ForeColor = Color.White
        };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(132, 126), Width = 72 };
        var delete = new Button { Text = "Delete", Location = new Point(212, 126), Width = 72 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(292, 126), Width = 72 };
        StyleButton(save, Color.FromArgb(31, 112, 153));
        StyleButton(delete, Color.FromArgb(140, 55, 60));
        StyleButton(cancel, Color.FromArgb(48, 67, 84));
        var deleted = false;
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(dialog, $"Delete '{selected.Name}' from Favorites?", "Delete Favorite",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            deleted = true;
            dialog.DialogResult = DialogResult.Abort;
            dialog.Close();
        };
        dialog.Controls.AddRange([nameCaption, nameBox, urlCaption, urlBox, save, delete, cancel]);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        var result = dialog.ShowDialog(this);
        if (result == DialogResult.Abort || deleted)
        {
            _appSettings.RemoteSdrFavorites.Remove(selected);
            AppSettingsStore.Save(_appSettings);
            RefreshSiteLists(true);
            _statusLabel.Text = $"Favorite deleted · {selected.Name}";
            return;
        }
        if (result != DialogResult.OK) return;
        var name = nameBox.Text.Trim();
        var url = urlBox.Text.Trim();
        if (name.Length == 0 || url.Length == 0)
        {
            MessageBox.Show(this, "Enter both a name and URL.", "Favorites",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        selected.Name = name;
        selected.Url = url;
        AppSettingsStore.Save(_appSettings);
        RefreshSiteLists(true);
        _favoriteBox.SelectedItem = selected;
        _statusLabel.Text = $"Favorite updated · {name}";
    }

    private static string SuggestFavoriteName(string url)
    {
        try
        {
            var uri = new Uri(url.Contains("://", StringComparison.Ordinal) ? url : "http://" + url);
            return string.IsNullOrEmpty(uri.Host) ? url : uri.Host;
        }
        catch { return url; }
    }

    private string? PromptText(string title, string label, string initial)
    {
        using var dialog = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 120), MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            BackColor = Color.FromArgb(20, 27, 37)
        };
        var caption = new Label { Text = label, AutoSize = true, ForeColor = Color.Gainsboro, Location = new Point(12, 12) };
        var box = new TextBox
        {
            Text = initial, Location = new Point(12, 36), Width = 336,
            BackColor = Color.FromArgb(8, 21, 30), ForeColor = Color.White
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(192, 76), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(273, 76), Width = 75 };
        dialog.Controls.AddRange([caption, box, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    private void CycleSource(int direction)
    {
        if (_sources.Count < 2) return;
        var next = (_rxSourceBox.SelectedIndex + direction + _sources.Count) % _sources.Count;
        _rxSourceBox.SelectedIndex = next;
    }

    private void SelectBand(long frequency, RadioMode mode)
    {
        _modeBox.SelectedItem = mode.ToString();
        RecallMemoryChannel(MemoryChannel.Create("BAND", frequency, mode, (int)_bandwidthBox.Value));
    }

    private void AdjustBandwidth(int direction)
    {
        var current = (int)_bandwidthBox.Value;
        var step = current < 5_000 ? 100 : current < 50_000 ? 500 : 5_000;
        _bandwidthBox.Value = Math.Clamp(current + direction * step, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
    }

    private void UpdateRxPanel()
    {
        SyncRxSourceBox();
        _rxBandwidthLabel.Text = $"BW {_demodulator.Bandwidth / 1_000d:0.###}k";
        foreach (var (mode, button) in _rxModeButtons)
            button.BackColor = mode == _demodulator.Mode ? Color.FromArgb(184, 118, 40) : Color.FromArgb(35, 66, 83);
        foreach (var (bandwidth, button) in _rxBandwidthButtons)
            button.BackColor = bandwidth == _demodulator.Bandwidth ? Color.FromArgb(86, 72, 156) : Color.FromArgb(35, 66, 83);
        var secondOutput = _appSettings.Audio2.Enabled;
        _rxVolumeBar2.Visible = secondOutput;
        _rxSquelchBar2.Visible = secondOutput;
        _squelchCheck2.Visible = secondOutput;
        _sqlLed2.Visible = secondOutput;
        UpdateOutputOwnerCaptions();
        UpdateSourceCapabilities();
    }

    private sealed record VfoChoice(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    private void RefreshAfDisplayVfoChoices()
    {
        if (_afDisplayVfoPanel.IsDisposed) return;
        if (_source is IRemoteAudioSampleSource)
        {
            _afDisplayVfoPanel.Visible = false;
            if (!SelectedAfVfoId().Equals("main", StringComparison.OrdinalIgnoreCase))
                SelectAfDisplayVfo("main", allowWhileCwLocked: true);
            PositionAfDisplayOverlay();
            return;
        }
        // Multi-channel CW is sensitive to AF spectrum VFO matching.
        // When the sub-VFO list changes (scene/sub-vfo add/remove), the previously selected VFO
        // can temporarily become invalid; in that case we must re-lock to the CW route immediately.
        var forced = MultiChannelCwForcedAfVfoId();
        if (forced is not null)
        {
            if (!SelectedAfVfoId().Equals(forced, StringComparison.OrdinalIgnoreCase))
                SelectAfDisplayVfo(forced, allowWhileCwLocked: true);
        }
        else
        {
            if (!_selectedAfVfoId.Equals("main", StringComparison.OrdinalIgnoreCase) &&
                !_appSettings.SubVfos.Any(item => item.Id.Equals(_selectedAfVfoId, StringComparison.OrdinalIgnoreCase)))
                SelectAfDisplayVfo("main");
        }
        _afDisplayVfoPanel.SuspendLayout();
        foreach (var control in _afDisplayVfoPanel.Controls.Cast<Control>().ToArray())
            if (!ReferenceEquals(control, _afDisplayVfoInfo)) control.Dispose();
        _afDisplayVfoPanel.Controls.Clear();
        var x = 2;
        x = AddAfDisplayVfoButton("main", "MAIN", x);
        foreach (var sub in _appSettings.SubVfos) x = AddAfDisplayVfoButton(sub.Id, sub.Name, x);
        _afDisplayVfoPanel.Size = new Size(Math.Max(110, x + 2), 48);
        _afDisplayVfoInfo.Location = new Point(4, 27);
        _afDisplayVfoInfo.Size = new Size(_afDisplayVfoPanel.Width - 8, 18);
        _afDisplayVfoInfo.TextAlign = ContentAlignment.MiddleRight;
        _afDisplayVfoInfo.ForeColor = Color.FromArgb(171, 202, 216);
        _afDisplayVfoInfo.BackColor = Color.Transparent;
        _afDisplayVfoInfo.Font = new Font("Segoe UI Semibold", 7.2f);
        _afDisplayVfoPanel.Controls.Add(_afDisplayVfoInfo);
        UpdateAfDisplayVfoInfo();
        _afDisplayVfoPanel.ResumeLayout();
        PositionAfDisplayOverlay();
        EnsureAfDisplayFollowsMultiChannelCw();
    }

    private int AddAfDisplayVfoButton(string id, string text, int x)
    {
        var selected = id.Equals(_selectedAfVfoId, StringComparison.OrdinalIgnoreCase);
        using var measureFont = new Font("Segoe UI Semibold", 7.2f);
        var width = Math.Clamp(TextRenderer.MeasureText(text, measureFont).Width + 14, 48, 82);
        var button = new Button
        {
            Text = text, AutoSize = false, Location = new Point(x, 2), Size = new Size(width, 23), FlatStyle = FlatStyle.Flat,
            BackColor = selected ? Color.FromArgb(184, 118, 40) : Color.FromArgb(31, 61, 76),
            ForeColor = selected ? Color.White : Color.FromArgb(207, 225, 234),
            Font = new Font("Segoe UI Semibold", 7.2f), Tag = id
        };
        button.FlatAppearance.BorderColor = selected ? Color.FromArgb(238, 151, 48) : Color.FromArgb(62, 94, 109);
        button.Click += (_, _) => SelectAfDisplayVfo(id);
        _afDisplayVfoPanel.Controls.Add(button);
        return x + width + 2;
    }

    private void SelectAfDisplayVfo(string id, bool allowWhileCwLocked = false)
    {
        var forced = MultiChannelCwForcedAfVfoId();
        if (forced is not null && !allowWhileCwLocked &&
            !id.Equals(forced, StringComparison.OrdinalIgnoreCase))
        {
            ApplyAfDisplayVfoLockChrome(forced);
            return;
        }
        if (forced is not null && !allowWhileCwLocked)
            id = forced;
        var sub = _appSettings.SubVfos.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (!id.Equals("main", StringComparison.OrdinalIgnoreCase) && sub is null) id = "main";
        if (SelectedAfVfoId().Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            ApplyAfDisplayVfoLockChrome(forced ?? MultiChannelCwForcedAfVfoId());
            UpdateAfDisplayVfoInfo();
            SyncCaptionTickerToSelectedAfVfo();
            return;
        }
        Volatile.Write(ref _selectedAfVfoId, id);
        _audioSpectrumPipeline.Reset();
        Interlocked.Exchange(ref _pendingAfSpectrum, null);
        Interlocked.Exchange(ref _pendingAfSpectrumLeft, null);
        Interlocked.Exchange(ref _pendingAfSpectrumRight, null);
        var caption = sub?.Name ?? "MAIN VFO";
        if (forced is not null && id.Equals(forced, StringComparison.OrdinalIgnoreCase))
            caption += " · CW";
        _afDisplay.ResetDisplay(caption);
        UpdateAfDisplayVfoInfo();
        foreach (var button in _afDisplayVfoPanel.Controls.OfType<Button>())
        {
            var selected = string.Equals(button.Tag?.ToString(), id, StringComparison.OrdinalIgnoreCase);
            button.BackColor = selected ? Color.FromArgb(184, 118, 40) : Color.FromArgb(31, 61, 76);
            button.ForeColor = selected ? Color.White : Color.FromArgb(207, 225, 234);
            button.FlatAppearance.BorderColor = selected ? Color.FromArgb(238, 151, 48) : Color.FromArgb(62, 94, 109);
        }
        ApplyAfDisplayVfoLockChrome(forced ?? MultiChannelCwForcedAfVfoId());
        SyncCaptionTickerToSelectedAfVfo();
    }

    private void SyncCaptionTickerToSelectedAfVfo()
    {
        if (_afDisplay.IsDisposed) return;
        var afVfo = SelectedAfVfoId();
        var show = false;
        var tickerConfigured = false;
        foreach (var instance in _appSettings.AfPluginInstances)
        {
            if (!NeuroCaption.IsCaptionPlugin(instance.PluginId)) continue;
            var bag = _appSettings.AfPluginUiState.GetValueOrDefault(instance.PluginId);
            var tickerOn = bag is not null &&
                            bag.TryGetValue("afTicker", out var flag) &&
                            bool.TryParse(flag, out var on) && on;
            if (!tickerOn) continue;
            tickerConfigured = true;
            if (ResolveAfPluginVfoId(instance.InstanceId).Equals(afVfo, StringComparison.OrdinalIgnoreCase))
            {
                show = true;
                break;
            }
        }
        _afDisplay.SetCaptionTickerEnabled(show, clearWhenOff: !tickerConfigured);
    }

    private void UpdateAfDisplayVfoInfo()
    {
        if (_afDisplayVfoInfo.IsDisposed) return;
        var selected = SelectedAfVfoId();
        var sub = _appSettings.SubVfos.FirstOrDefault(item => item.Id.Equals(selected, StringComparison.OrdinalIgnoreCase));
        var frequency = sub?.Frequency ?? _tunedFrequency;
        var mode = sub?.Mode ?? _demodulator.Mode;
        _afDisplayVfoInfo.Text = $"{frequency / 1_000_000d:0.000000} MHz  ·  {mode}";
    }

    private void PositionAfDisplayOverlay()
    {
        if (_afDisplayVfoPanel.Parent is not Control graphPanel) return;
        _afDisplayVfoPanel.Left = Math.Max(4, graphPanel.ClientSize.Width - _afDisplayVfoPanel.Width - 4);
        _afDisplayVfoPanel.Top = 3;
        _afFilterCheck.Left = 5;
        _afFilterCheck.Top = 15;
        _afFilterCheck.BringToFront();
        if (_source is not IRemoteAudioSampleSource)
            _afDisplayVfoPanel.BringToFront();
    }

    private void ShowAfVfoPanel()
    {
        if (IsSatelliteSceneActive) return;
        if (_source is IRemoteAudioSampleSource) return;
        _afVfoHideTimer.Stop();
        _afDisplayVfoPanel.Visible = true;
        _afFilterCheck.BringToFront();
        _afDisplayVfoPanel.BringToFront();
    }

    private void ScheduleAfVfoPanelHide()
    {
        if (_graphPanel.ClientRectangle.Contains(_graphPanel.PointToClient(Cursor.Position))) return;
        _afVfoHideTimer.Stop();
        _afVfoHideTimer.Start();
    }

    private void PositionRfDisplayOverlay()
    {
        _rfOverlayPanel.Left = 8;
        var top = 26;
        if (_demodulator.Mode == RadioMode.WFM &&
            !_wfmStationPanel.IsDisposed &&
            _wfmStationPanel.GetMarkerStations().Count > 0)
            top = 56;
        _rfOverlayPanel.Top = top;
        FitRfOverlayPanelWidth();
        SyncRfOverlayChromeReserve();
        _rfOverlayPanel.BringToFront();
    }

    private void FitRfOverlayPanelWidth()
    {
        var right = 3;
        foreach (Control child in _rfOverlayPanel.Controls)
        {
            if (!child.Visible) continue;
            right = Math.Max(right, child.Right);
        }
        _rfOverlayPanel.Width = right + 3;
        _rfOverlayPanel.Height = 28;
    }

    private void SyncRfOverlayChromeReserve()
    {
        if (_display.IsDisposed || _rfOverlayPanel.Parent is null) return;
        try
        {
            var screen = _rfOverlayPanel.Parent.PointToScreen(_rfOverlayPanel.Location);
            var loc = _display.PointToClient(screen);
            _display.SetOverlayChromeReserve(new Rectangle(loc, _rfOverlayPanel.Size));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void ShowRfDisplayOverlay()
    {
        _rfOverlayHideTimer.Stop();
        _rfOverlayPanel.Visible = true;
        _rfOverlayPanel.BringToFront();
        UpdateRfPopOutButtonCaption();
    }

    private void ScheduleRfDisplayOverlayHide()
    {
        var hoverHost = _rfDisplayDetached && _rfDetachForm is { IsDisposed: false } form
            ? (Control)form
            : _centerPanel;
        if (hoverHost.ClientRectangle.Contains(hoverHost.PointToClient(Cursor.Position))) return;
        _rfOverlayHideTimer.Stop();
        _rfOverlayHideTimer.Start();
    }

    private void UpdateRfPopOutButtonCaption()
    {
        _rfPopOutOverlayButton.Text = _rfDisplayDetached ? "↩ NeuroSDR" : "Pop Out";
        _rfFullscreenOverlayButton.Visible = _rfDisplayDetached;
        if (_rfDisplayDetached)
        {
            var full = _rfDetachForm?.IsFullscreen == true || _rfPopOutFullscreen;
            _rfFullscreenOverlayButton.Text = full ? "Exit Fullscreen (F12)" : "Fullscreen (F12)";
        }
        FitRfOverlayPanelWidth();
        SyncRfOverlayChromeReserve();
    }

    private void ToggleRfPopOutFullscreen()
    {
        if (!_rfDisplayDetached || _rfDetachForm is not { IsDisposed: false } form) return;
        form.ToggleFullscreen();
        _rfPopOutFullscreen = form.IsFullscreen;
        if (!form.IsFullscreen) _rfPopOutBounds = form.CaptureRestoreBounds();
        UpdateRfPopOutButtonCaption();
        ShowRfDisplayOverlay();
        PersistCurrentSceneLayout();
    }

    private void ToggleRfDisplayDetach()
    {
        if (_rfDisplayDetached) DockRfDisplay();
        else DetachRfDisplay();
    }

    private void DetachRfDisplay(
        string? preferredScreenDevice = null,
        bool? fullscreen = null,
        Rectangle? bounds = null)
    {
        var wantFullscreen = fullscreen ?? _rfPopOutFullscreen;
        var useBounds = bounds ?? (_rfPopOutBounds.Width > 0 ? _rfPopOutBounds : Rectangle.Empty);

        if (_rfDisplayDetached && _rfDetachForm is { IsDisposed: false })
        {
            ApplyPopOutPlacement(_rfDetachForm, preferredScreenDevice, wantFullscreen, useBounds, isRf: true);
            ShowRfDisplayOverlay();
            ApplyDetachedMainLayout();
            SchedulePersistCurrentSceneLayout();
            return;
        }

        _rfDetachForm?.Dispose();
        _rfDetachForm = new PopOutHostForm("NeuroSDR · RF Spectrum", this) { ExternalChrome = true };
        _rfDetachForm.DockRequested += () => DockRfDisplay();
        _rfDetachForm.FullscreenChanged += () =>
        {
            _rfPopOutFullscreen = _rfDetachForm?.IsFullscreen == true;
            if (_rfDetachForm is { IsDisposed: false } form && !form.IsFullscreen)
                _rfPopOutBounds = form.CaptureRestoreBounds();
            UpdateRfPopOutButtonCaption();
            PersistCurrentSceneLayout();
        };
        _rfDetachForm.LayoutStateChanged += SchedulePersistCurrentSceneLayout;
        _rfDetachForm.HostPanel.MouseEnter += (_, _) => ShowRfDisplayOverlay();
        _rfDetachForm.HostPanel.MouseLeave += (_, _) => ScheduleRfDisplayOverlayHide();
        _rfDetachForm.MouseEnter += (_, _) => ShowRfDisplayOverlay();
        _rfDetachForm.MouseLeave += (_, _) => ScheduleRfDisplayOverlayHide();

        _display.Parent = _rfDetachForm.HostPanel;
        _display.Dock = DockStyle.Fill;
        _rfOverlayPanel.Parent = _rfDetachForm;
        _rfOverlayPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _rfDisplayDetached = true;
        UpdateRfPopOutButtonCaption();
        PositionRfDisplayOverlay();
        ApplyPopOutPlacement(_rfDetachForm, preferredScreenDevice, wantFullscreen, useBounds, isRf: true);
        ShowRfDisplayOverlay();
        ApplyDetachedMainLayout();
        _rfDetachForm.RaiseToFront();
        _statusLabel.Text = "RF Pop Out · F12 fullscreen · hover for controls · ↩ NeuroSDR to dock";
        PersistCurrentSceneLayout();
    }

    private void DockRfDisplay()
    {
        if (!_rfDisplayDetached && _rfDetachForm is null) return;
        var form = _rfDetachForm;
        if (form is { IsDisposed: false })
        {
            _rfPopOutFullscreen = form.IsFullscreen;
            _rfPopOutBounds = form.CaptureRestoreBounds();
            form.StopChrome();
        }
        _rfDetachForm = null;
        _rfDisplayDetached = false;

        if (_display.Parent != _centerPanel)
        {
            _display.Parent = _centerPanel;
            _display.Dock = DockStyle.Fill;
            _display.BringToFront();
        }
        if (_rfOverlayPanel.Parent != _centerPanel)
        {
            _rfOverlayPanel.Parent = _centerPanel;
            _rfOverlayPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        }
        UpdateRfPopOutButtonCaption();
        PositionRfDisplayOverlay();
        _rfOverlayPanel.BringToFront();
        form?.Hide();
        form?.Dispose();
        ConfigureDisplay();
        ApplyDetachedMainLayout();
        ShowRfDisplayOverlay();
        _statusLabel.Text = "RF spectrum docked";
        PersistCurrentSceneLayout();
    }

    private void ToggleAfDisplayDetach()
    {
        if (_afDisplayDetached) DockAfDisplay();
        else DetachAfDisplay();
    }

    private void DetachAfDisplay(
        string? preferredScreenDevice = null,
        bool? fullscreen = null,
        Rectangle? bounds = null)
    {
        var wantFullscreen = fullscreen ?? _afPopOutFullscreen;
        var useBounds = bounds ?? (_afPopOutBounds.Width > 0 ? _afPopOutBounds : Rectangle.Empty);

        if (_afDisplayDetached && _afDetachForm is { IsDisposed: false })
        {
            ApplyPopOutPlacement(_afDetachForm, preferredScreenDevice, wantFullscreen, useBounds, isRf: false);
            ApplyDetachedMainLayout();
            SchedulePersistCurrentSceneLayout();
            return;
        }

        EnsureAfPopOutChrome();
        _afDetachForm?.Dispose();
        _afDetachForm = new PopOutHostForm("NeuroSDR · AF Plugins", this);
        _afDetachForm.DockRequested += () => DockAfDisplay();
        _afDetachForm.FullscreenChanged += () =>
        {
            _afPopOutFullscreen = _afDetachForm?.IsFullscreen == true;
            if (_afDetachForm is { IsDisposed: false } form && !form.IsFullscreen)
                _afPopOutBounds = form.CaptureRestoreBounds();
            PersistCurrentSceneLayout();
        };
        _afDetachForm.LayoutStateChanged += SchedulePersistCurrentSceneLayout;

        _afDisplayDetached = true;
        if (_afPopOutButton is not null) _afPopOutButton.Text = "↩ NeuroSDR";
        ApplyPopOutPlacement(_afDetachForm, preferredScreenDevice, wantFullscreen, useBounds, isRf: false);
        ApplyDetachedMainLayout();
        _afDetachForm.AttachHoverTargets(_decoderPanel);
        _afDetachForm.ShowChrome();
        _afDetachForm.RaiseToFront();
        // Layout/reparent on the main form often steals activation; raise again after the pump settles.
        BeginInvoke(() =>
        {
            if (_afDetachForm is { IsDisposed: false })
                _afDetachForm.RaiseToFront();
        });
        ShowAfPopOutChrome();
        _statusLabel.Text = "AF Plugin Pop Out · hover for ↩ NeuroSDR / F12 · close window to dock";
        PersistCurrentSceneLayout();
    }

    private void DockAfDisplay()
    {
        if (!_afDisplayDetached && _afDetachForm is null) return;
        var form = _afDetachForm;
        if (form is { IsDisposed: false })
        {
            _afPopOutFullscreen = form.CaptureFullscreenState();
            _afPopOutBounds = form.CaptureRestoreBounds();
            form.StopChrome();
        }
        _afDetachForm = null;
        _afDisplayDetached = false;
        if (_afPopOutButton is not null) _afPopOutButton.Text = "Pop Out";
        // Reparent decoder before disposing the host form.
        ApplyDetachedMainLayout();
        try { form?.Hide(); form?.Dispose(); }
        catch (ObjectDisposedException) { }
        PersistCurrentSceneLayout();
    }

    private void ApplyPopOutPlacement(
        PopOutHostForm form,
        string? preferredScreenDevice,
        bool fullscreen,
        Rectangle bounds,
        bool isRf)
    {
        if (fullscreen)
        {
            var screen = ResolvePopOutScreen(preferredScreenDevice, preferOther: false);
            // If saved bounds exist, seed restore size before going fullscreen.
            if (bounds.Width >= 400 && bounds.Height >= 280)
            {
                form.ShowWindowed(bounds);
                form.SetFullscreen(true, screen);
            }
            else
                form.ShowFullscreen(screen);
            form.RaiseToFront();
        }
        else
        {
            var rect = bounds.Width >= 400 && bounds.Height >= 280
                ? bounds
                : DefaultPopOutBounds(isRf);
            form.ShowWindowed(rect);
            form.RaiseToFront();
        }
        if (isRf)
        {
            _rfPopOutFullscreen = form.CaptureFullscreenState();
            if (!form.IsFullscreen) _rfPopOutBounds = form.CaptureRestoreBounds();
            UpdateRfPopOutButtonCaption();
        }
        else
        {
            _afPopOutFullscreen = form.CaptureFullscreenState();
            if (!form.IsFullscreen) _afPopOutBounds = form.CaptureRestoreBounds();
        }
    }

    private Rectangle DefaultPopOutBounds(bool isRf)
    {
        var screen = Screen.FromControl(this);
        var wa = screen.WorkingArea;
        var w = Math.Min(isRf ? 1100 : 1200, wa.Width - 60);
        var h = Math.Min(isRf ? 700 : 800, wa.Height - 60);
        // Offset so the pop-out is not fully buried under a maximized main window.
        var left = wa.Left + Math.Min(120, Math.Max(40, wa.Width / 8));
        var top = wa.Top + Math.Min(80, Math.Max(40, wa.Height / 10));
        return new Rectangle(left, top, w, h);
    }

    private Screen ResolvePopOutScreen(string? preferredScreenDevice, bool preferOther)
    {
        if (!string.IsNullOrWhiteSpace(preferredScreenDevice))
        {
            var match = Screen.AllScreens.FirstOrDefault(s =>
                s.DeviceName.Equals(preferredScreenDevice, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        if (preferOther)
        {
            var other = Screen.AllScreens.FirstOrDefault(s => !s.Primary && s.DeviceName != Screen.FromControl(this).DeviceName);
            if (other is not null) return other;
        }
        return Screen.FromControl(this);
    }

    private void ApplyRfDisplayDetachFromScene(RxScene scene)
    {
        if (scene.RfDisplayDetached)
        {
            var bounds = scene.RfDisplayWidth > 0
                ? new Rectangle(scene.RfDisplayX, scene.RfDisplayY, scene.RfDisplayWidth, scene.RfDisplayHeight)
                : Rectangle.Empty;
            _rfPopOutFullscreen = scene.RfDisplayFullscreen;
            DetachRfDisplay(scene.RfDisplayScreenDevice, scene.RfDisplayFullscreen, bounds);
        }
        else DockRfDisplay();

        if (scene.AfDisplayDetached)
        {
            var bounds = scene.AfDisplayWidth > 0
                ? new Rectangle(scene.AfDisplayX, scene.AfDisplayY, scene.AfDisplayWidth, scene.AfDisplayHeight)
                : Rectangle.Empty;
            _afPopOutFullscreen = scene.AfDisplayFullscreen;
            DetachAfDisplay(scene.AfDisplayScreenDevice, scene.AfDisplayFullscreen, bounds);
        }
        else DockAfDisplay();
    }

    private void RaiseDetachedPopOutsToFront()
    {
        if (IsDisposed) return;
        try
        {
            if (_rfDisplayDetached && _rfDetachForm is { IsDisposed: false } rf)
                rf.RaiseToFront();
            if (_afDisplayDetached && _afDetachForm is { IsDisposed: false } af)
                af.RaiseToFront();
        }
        catch (ObjectDisposedException) { }
    }

    private void EnsureAfPopOutChrome()
    {
        if (_afPopOutChrome is not null) return;
        _afPopOutChrome = new Panel
        {
            Size = new Size(108, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.FromArgb(210, 10, 28, 38),
            Visible = false
        };
        _afPopOutButton = new Button
        {
            Text = "Pop Out",
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 7.2f),
            ForeColor = Color.FromArgb(255, 193, 69),
            BackColor = Color.FromArgb(35, 66, 83),
            Size = new Size(100, 22),
            Location = new Point(4, 3),
            TabStop = false
        };
        _afPopOutButton.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        _afPopOutButton.Click += (_, _) => ToggleAfDisplayDetach();
        _afPopOutChrome.Controls.Add(_afPopOutButton);
        _decoderPanel.Controls.Add(_afPopOutChrome);
        _decoderPanel.MouseEnter += (_, _) => ShowAfPopOutChrome();
        _decoderPanel.MouseLeave += (_, _) => ScheduleAfPopOutChromeHide();
        _decoderTabs.MouseEnter += (_, _) => ShowAfPopOutChrome();
        _afPopOutChrome.MouseEnter += (_, _) => ShowAfPopOutChrome();
        PositionAfPopOutChrome();
        _decoderPanel.Resize += (_, _) => PositionAfPopOutChrome();
    }

    private void ShowAfPopOutChrome()
    {
        EnsureAfPopOutChrome();
        if (_afPopOutChrome is null) return;
        if (_afPopOutButton is not null)
            _afPopOutButton.Text = _afDisplayDetached ? "↩ NeuroSDR" : "Pop Out";
        PositionAfPopOutChrome();
        _afPopOutChrome.Visible = true;
        _afPopOutChrome.BringToFront();
    }

    private void ScheduleAfPopOutChromeHide()
    {
        if (_afPopOutChrome is null) return;
        // While popped out, keep the return button available (form chrome also has it).
        if (_afDisplayDetached) return;
        if (_decoderPanel.ClientRectangle.Contains(_decoderPanel.PointToClient(Cursor.Position))) return;
        _afPopOutChrome.Visible = false;
    }

    private void PositionAfPopOutChrome()
    {
        if (_afPopOutChrome is null) return;
        _afPopOutChrome.Left = Math.Max(4, _decoderPanel.ClientSize.Width - _afPopOutChrome.Width - 6);
        _afPopOutChrome.Top = 4;
    }

    private void ApplyDetachedMainLayout()
    {
        EnsureCenterPluginHost();
        EnsureAfPopOutChrome();

        // Restore decoder into AF strip when neither fill-host needs it.
        if (!_rfDisplayDetached && !_afDisplayDetached)
        {
            // Reparent BEFORE clearing the center host so mosaic panes stay intact for Relayout.
            if (_decoderPanel.Parent != _afPanel)
            {
                _decoderPanel.Parent = _afPanel;
                _decoderPanel.Dock = DockStyle.Left;
                _decoderPanel.Width = Math.Clamp(_appSettings.AfPluginDisplayWidth, AfPluginMinWidth, 900);
            }
            // Dock.Left: highest z-order is closest to the left edge.
            // Keep RX CONTROL leftmost, AF plugins immediately to its right.
            EnsureAfPanelDockOrder();
            if (_centerPluginHost is not null)
            {
                _centerPluginHost.Visible = false;
                foreach (Control child in _centerPluginHost.Controls.Cast<Control>().ToArray())
                {
                    if (ReferenceEquals(child, _decoderPanel)) continue;
                    _centerPluginHost.Controls.Remove(child);
                    child.Dispose();
                }
            }
            RelayoutAfPluginPanes(null);
            EnsureAfPanelDockOrder();
            _decoderPanel.Visible = _decoderTabs.TabPages.Count > 0;
            _decoderTabs.Visible = true;
            _decoderTabs.Dock = DockStyle.Fill;
            if (_display.Parent == _centerPanel)
            {
                _display.Visible = true;
                _display.BringToFront();
            }
            _rfOverlayPanel.BringToFront();
            return;
        }

        if (_rfDisplayDetached && _display.Parent == _centerPanel)
            _display.Visible = false;

        if (_afDisplayDetached && _afDetachForm is { IsDisposed: false } afForm)
        {
            // All plugins go to AF pop-out (fullscreen-friendly mosaic).
            if (_decoderPanel.Parent != afForm.HostPanel)
            {
                _decoderPanel.Parent = afForm.HostPanel;
                _decoderPanel.Dock = DockStyle.Fill;
            }
            _decoderPanel.Visible = true;
            RelayoutAfPluginPanes(afForm.HostPanel.ClientSize.Width);
            // Keep ↩ NeuroSDR visible on the popped-out AF content.
            if (_afPopOutButton is not null) _afPopOutButton.Text = "↩ NeuroSDR";
            ShowAfPopOutChrome();
            afForm.AttachHoverTargets(_decoderPanel);
            afForm.ShowChrome();

            if (_rfDisplayDetached && _centerPluginHost is not null)
            {
                _centerPluginHost.Controls.Clear();
                _centerPluginHost.Visible = true;
                _centerPluginHost.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.FromArgb(140, 170, 190),
                    Font = new Font("Segoe UI Semibold", 12f),
                    Text = "RF + AF Pop Out active\r\nMain deck free for future tools"
                });
            }
            else if (_centerPluginHost is not null)
            {
                _centerPluginHost.Visible = false;
                _centerPluginHost.Controls.Clear();
            }
        }
        else if (_rfDisplayDetached)
        {
            // RF gone: AF plugins fill the former waterfall area; AF spectrum widens.
            if (_decoderPanel.Parent != _centerPluginHost)
            {
                _decoderPanel.Parent = _centerPluginHost;
                _decoderPanel.Dock = DockStyle.Fill;
            }
            _centerPluginHost!.Visible = true;
            _centerPluginHost.BringToFront();
            _decoderPanel.Visible = true;
            RelayoutAfPluginPanes(_centerPluginHost.ClientSize.Width);
        }
    }

    private void EnsureAfPanelDockOrder()
    {
        if (_afPanel.IsDisposed) return;
        // Only enforce when the AF plugin strip is docked beside RX CONTROL.
        if (!ReferenceEquals(_decoderPanel.Parent, _afPanel)) return;

        // WinForms DockStyle.Left: highest z-order (last in Controls) docks first → leftmost.
        // Desired left → right: RX CONTROL | AF Plugin | AF spectrum (Fill).
        // Raising each child to the end in this sequence leaves radio highest / leftmost.
        _afPanel.SuspendLayout();
        try
        {
            foreach (var child in new Control[] { _graphPanel, _decoderPanel, _radioPanel })
            {
                if (!_afPanel.Controls.Contains(child)) continue;
                _afPanel.Controls.SetChildIndex(child, _afPanel.Controls.Count - 1);
            }
        }
        finally
        {
            _afPanel.ResumeLayout(true);
        }
    }

    private void EnsureCenterPluginHost()
    {
        if (_centerPluginHost is not null) return;
        _centerPluginHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(8, 18, 26),
            Visible = false,
            Name = "_centerPluginHost"
        };
        _centerPluginHost.Resize += (_, _) =>
        {
            if (_rfDisplayDetached && !_afDisplayDetached)
                RelayoutAfPluginPanes(_centerPluginHost.ClientSize.Width);
        };
        _centerPanel.Controls.Add(_centerPluginHost);
    }

    private void RelayoutAfPluginPanes(int? hostWidth)
    {
        var keepId = _lastSelectedAfInstanceId ?? SelectedAfPluginBinding()?.InstanceId;
        _afPluginLayoutDepth++;
        try
        {
            RelayoutAfPluginPanesCore(hostWidth);
            RestoreAfPluginTab(keepId);
        }
        finally
        {
            _afPluginLayoutDepth--;
        }
        ApplyAfPluginActivation();
        InvalidateAfPluginChrome();
    }

    private void RestoreAfPluginTab(string? instanceId)
    {
        AfPluginTabBinding? binding = null;
        if (!string.IsNullOrWhiteSpace(instanceId))
            _afPluginTabs.TryGetValue(instanceId, out binding);
        binding ??= _afPluginTabs.Values.FirstOrDefault();
        if (binding is null || binding.Page.IsDisposed) return;
        _lastSelectedAfInstanceId = binding.InstanceId;
        ForceShowAfPluginPage(binding.Page);
    }

    private static void ForceShowAfPluginPage(TabPage page)
    {
        if (page.Parent is not TabControl host || host.IsDisposed || !host.TabPages.Contains(page))
            return;
        try
        {
            var index = host.TabPages.IndexOf(page);
            if (index < 0) return;
            // Reparent/Clear leaves the header on tab 0 while the previous page stays
            // visible. Switching away and back is what makes header, body, and
            // Single-Active stay on the same plugin.
            if (host.TabCount > 1)
                host.SelectedIndex = index == 0 ? 1 : 0;
            host.SelectedIndex = index;
            page.Visible = true;
            page.BringToFront();
        }
        catch (ObjectDisposedException) { }
        catch (ArgumentException) { }
    }

    private void RelayoutAfPluginPanesCore(int? hostWidth)
    {
        // Gather every plugin page (may currently live in mosaic mini-tabs).
        var pages = new List<TabPage>();
        foreach (Control child in _decoderPanel.Controls.Cast<Control>().ToArray())
        {
            if (child is not Panel { Tag: "af-pane" } pane) continue;
            foreach (var mini in pane.Controls.OfType<TabControl>())
            {
                foreach (TabPage page in mini.TabPages.Cast<TabPage>().ToArray())
                {
                    mini.TabPages.Remove(page);
                    pages.Add(page);
                }
            }
            _decoderPanel.Controls.Remove(pane);
            pane.Dispose();
        }
        foreach (TabPage page in _decoderTabs.TabPages.Cast<TabPage>().ToArray())
            pages.Add(page);
        _decoderTabs.TabPages.Clear();
        foreach (var page in pages)
            _decoderTabs.TabPages.Add(page);

        var width = hostWidth ?? _decoderPanel.ClientSize.Width;
        if (width < 80 || _decoderTabs.TabPages.Count == 0)
        {
            _decoderTabs.Dock = DockStyle.Fill;
            _decoderTabs.Visible = true;
            ConfigureAfPluginTabChrome(_decoderTabs);
            return;
        }

        var mosaic = _afDisplayDetached || (_rfDisplayDetached && _decoderPanel.Parent == _centerPluginHost);
        if (!mosaic)
        {
            _decoderTabs.Dock = DockStyle.Fill;
            _decoderTabs.Visible = true;
            ConfigureAfPluginTabChrome(_decoderTabs);
            return;
        }

        const int paneMin = 360;
        pages = _decoderTabs.TabPages.Cast<TabPage>().ToList();
        var maxSide = Math.Max(1, width / paneMin);
        var sideCount = Math.Min(pages.Count, maxSide);
        var overflow = pages.Count > sideCount;
        var dedicated = overflow ? Math.Max(1, sideCount - 1) : sideCount;

        _decoderTabs.TabPages.Clear();
        var x = 0;
        var paneW = overflow
            ? Math.Max(paneMin, (width - paneMin) / Math.Max(1, dedicated))
            : Math.Max(paneMin, width / Math.Max(1, dedicated));

        for (var i = 0; i < dedicated; i++)
        {
            var page = pages[i];
            var pane = new Panel
            {
                Tag = "af-pane",
                Bounds = new Rectangle(x, 0, i == dedicated - 1 && !overflow ? width - x : paneW, Math.Max(1, _decoderPanel.ClientSize.Height)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
                BackColor = Color.FromArgb(5, 17, 24)
            };
            var mini = new DarkTabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", _afDisplayDetached ? 10f : 8.5f),
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(_afDisplayDetached ? 168 : 96, _afDisplayDetached ? 26 : 22),
                Padding = new Point(8, 3)
            };
            ConfigureAfPluginTabChrome(mini);
            mini.TabPages.Add(page);
            mini.SelectedIndexChanged += (_, _) =>
            {
                if (_afPluginLayoutDepth > 0 || mini.SelectedTab is not TabPage selected) return;
                ActivateAfPluginPage(selected);
            };
            pane.MouseDown += (_, _) => ActivateAfPluginPage(page);
            mini.MouseDown += (_, _) => ActivateAfPluginPage(page);
            page.MouseDown += (_, _) => ActivateAfPluginPage(page);

            var banner = new Label
            {
                Tag = "af-active-banner",
                Height = 18,
                Dock = DockStyle.Top,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 8f),
                Cursor = Cursors.Hand
            };
            banner.Click += (_, _) => ActivateAfPluginPage(page);
            pane.Controls.Add(mini);
            pane.Controls.Add(banner);
            banner.BringToFront();
            _decoderPanel.Controls.Add(pane);
            pane.BringToFront();
            x += pane.Width;
        }

        if (overflow)
        {
            for (var i = dedicated; i < pages.Count; i++)
                _decoderTabs.TabPages.Add(pages[i]);
            _decoderTabs.Dock = DockStyle.None;
            _decoderTabs.Bounds = new Rectangle(x, 0, Math.Max(paneMin, width - x), Math.Max(1, _decoderPanel.ClientSize.Height));
            _decoderTabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _decoderTabs.Visible = true;
            ConfigureAfPluginTabChrome(_decoderTabs);
            _decoderTabs.BringToFront();
        }
        else
        {
            _decoderTabs.Visible = false;
        }

        if (_afPopOutChrome is not null) _afPopOutChrome.BringToFront();
    }

    private void ConfigureAfPluginTabChrome(DarkTabControl tabs)
    {
        tabs.IsPageOutOfRange = IsAfPluginPageOutOfRange;
        tabs.IsPluginActive = IsAfPluginChromeActive;
    }

    private bool IsAfPluginChromeActive(TabPage page)
    {
        if (page.Parent is not TabControl host || host.IsDisposed)
            return false;
        if (!ReferenceEquals(host.SelectedTab, page))
            return false;
        if (!_appSettings.SingleActiveAfPlugin || AfBindingForPage(page) is null)
            return true;
        return SelectedAfPluginBinding() is { } selected &&
               AfBindingForPage(page) is { } binding &&
               selected.InstanceId.Equals(binding.InstanceId, StringComparison.OrdinalIgnoreCase);
    }

    private void ActivateAfPluginPage(TabPage page)
    {
        if (_afPluginLayoutDepth > 0 || page.IsDisposed) return;
        var binding = AfBindingForPage(page);
        if (binding is null) return;
        var already = _lastSelectedAfInstanceId is not null &&
                      binding.InstanceId.Equals(_lastSelectedAfInstanceId, StringComparison.OrdinalIgnoreCase);
        _lastSelectedAfInstanceId = binding.InstanceId;
        if (page.Parent is TabControl host && host.TabPages.Contains(page) &&
            !ReferenceEquals(host.SelectedTab, page))
        {
            try { host.SelectedTab = page; }
            catch (ObjectDisposedException) { }
            catch (ArgumentException) { }
        }
        if (already) return;
        ApplyAfPluginActivation();
        ApplySlowModePluginSettings();
        UpdateAfFskMarkers();
        InvalidateAfPluginChrome();
    }

    private void InvalidateAfPluginChrome()
    {
        if (_decoderTabs is { IsDisposed: false }) _decoderTabs.Invalidate();
        if (_decoderPanel is null || _decoderPanel.IsDisposed) return;
        foreach (Control child in _decoderPanel.Controls)
        {
            if (child is not Panel { Tag: "af-pane" } pane || pane.IsDisposed) continue;
            foreach (var mini in pane.Controls.OfType<DarkTabControl>())
            {
                if (!mini.IsDisposed) mini.Invalidate();
            }
            var banner = pane.Controls.OfType<Label>().FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), "af-active-banner", StringComparison.Ordinal));
            if (banner is null) continue;
            var page = pane.Controls.OfType<TabControl>().SelectMany(tabs => tabs.TabPages.Cast<TabPage>()).FirstOrDefault();
            var show = _appSettings.SingleActiveAfPlugin && page is not null && AfBindingForPage(page) is not null;
            banner.Visible = show;
            if (!show) continue;
            var active = page is not null && IsAfPluginChromeActive(page);
            banner.Text = active ? "● ACTIVE" : "Click to activate";
            banner.ForeColor = active ? Color.FromArgb(255, 220, 140) : Color.FromArgb(160, 186, 198);
            banner.BackColor = active ? Color.FromArgb(120, 78, 22) : Color.FromArgb(22, 48, 60);
        }
    }

    private void RebuildSubVfoReceivers()
    {
        var receivers = _appSettings.SubVfos.Select(settings => new SubVfoReceiver(settings)).ToArray();
        foreach (var receiver in receivers) ConfigureSubVfoProcessor(receiver);
        Interlocked.Exchange(ref _subVfoReceivers, receivers);
        // Startup restores receivers before the form handle exists. The RX panel
        // controls already exist at this point, so populate their rows immediately;
        // otherwise persisted SUB VFOs run invisibly until the first edit/rebuild.
        if (!IsDisposed && !_subVfoList.IsDisposed)
        {
            RefreshSubVfoRows();
            RefreshAfPluginVfoChoices();
            RefreshAfDisplayVfoChoices();
            UpdateOutputOwnerCaptions();
            ConfigureDisplay();
        }
    }

    private void ApplySubVfoFrequencyInPlace(SubVfoSettings settings)
    {
        var receiver = Volatile.Read(ref _subVfoReceivers)
            .FirstOrDefault(item => item.Id.Equals(settings.Id, StringComparison.OrdinalIgnoreCase));
        if (receiver is not null)
            receiver.Frequency = settings.Frequency;
        _lastSubVfoRangeSignature = string.Empty;
        UpdateSubVfoRangeVisuals();
    }

    private void ConfigureSubVfoProcessor(SubVfoReceiver receiver)
    {
        receiver.Processor.AgcEnabled = _agcCheck.Checked;
        receiver.Processor.ExtendedAgcRange = _appSettings.ExtendedAfAgcRangeEnabled;
        receiver.Processor.NoiseReductionEnabled = _noiseReductionCheck.Checked;
        receiver.Processor.NoiseReductionStrength = _noiseReductionSlider.Value;
        receiver.Processor.NotchEnabled = _notchCheck.Checked;
        receiver.Processor.NotchFrequency = (int)_notchFrequency.Value;
        receiver.Processor.AfFilterEnabled = CwEnFilterActive ? false : _afFilterCheck.Checked;
        receiver.Processor.AfLowCutHz = _appSettings.AfLowCutHz;
        receiver.Processor.AfHighCutHz = _appSettings.AfHighCutHz;
    }

    private void RefreshSubVfoRows()
    {
        CancelSubVfoCenterHold();
        if (_subVfoList.IsDisposed) return;
        _subVfoList.SuspendLayout();
        foreach (var oldControl in _subVfoList.Controls.Cast<Control>().ToArray()) oldControl.Dispose();
        _subVfoList.Controls.Clear();
        if (_appSettings.SubVfos.Count == 0)
        {
            _subVfoList.Controls.Add(new Label
            {
                AutoSize = false, Height = 55, Margin = new Padding(4, 5, 4, 2),
                Text = "No Sub VFO. Click + ADD or right-click the spectrum / waterfall.",
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(116, 151, 169)
            });
        }
        foreach (var sub in _appSettings.SubVfos)
        {
            var receiver = Volatile.Read(ref _subVfoReceivers).FirstOrDefault(item => item.Id == sub.Id);
            var inside = receiver?.IsInside(_rfCenterFrequency, _source.SampleRate) == true && _source is not IRemoteAudioSampleSource;
            var row = new BufferedPanel { Height = 31, Margin = new Padding(2, 2, 2, 0), BackColor = Color.FromArgb(15, 37, 48), Tag = sub.Id };
            var output = sub.OutputChannel == 0 ? "DEC" : $"CH{sub.OutputChannel}";
            var rowLabel = new Label
            {
                Location = new Point(6, 1), Size = new Size(385, 28), TextAlign = ContentAlignment.MiddleLeft,
                Text = $"{sub.Name}  {sub.Frequency / 1_000_000d:0.000000} MHz  {sub.Mode}  {sub.Bandwidth / 1_000d:0.###}k  {output}" + (inside ? "" : "  · OUT OF RANGE"),
                ForeColor = inside ? Color.FromArgb(211, 226, 235) : Color.FromArgb(211, 121, 104), Font = new Font("Segoe UI", 7.9f)
            };
            row.Controls.Add(rowLabel);
            var meter = new SignalMeterControl
            {
                Size = new Size(72, 25),
                Location = new Point(0, 3),
                Caption = "RF",
                Tag = "sub-rf-meter",
                Enabled = inside,
                Value = inside && receiver is not null ? Volatile.Read(ref receiver.SignalLevelDb) : -140
            };
            var edit = MakeRxButton("EDIT", 0, 3, 48); edit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var delete = MakeRxButton("DEL", 0, 3, 42); delete.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            edit.Click += (_, _) => EditSubVfo(sub.Id);
            delete.Click += (_, _) => DeleteSubVfo(sub.Id);
            row.Controls.Add(meter); row.Controls.Add(edit); row.Controls.Add(delete);
            row.Resize += (_, _) =>
            {
                delete.Left = row.ClientSize.Width - delete.Width - 4;
                edit.Left = delete.Left - edit.Width - 4;
                meter.Left = edit.Left - meter.Width - 5;
                rowLabel.Width = Math.Max(80, meter.Left - rowLabel.Left - 4);
            };
            AttachSubVfoCenterGesture(row, sub.Id);
            AttachSubVfoCenterGesture(rowLabel, sub.Id);
            AttachSubVfoCenterGesture(meter, sub.Id);
            _subVfoList.Controls.Add(row);
        }
        ResizeSubVfoRows();
        _subVfoList.ResumeLayout();
        // AutoScroll decides whether the vertical bar is needed after layout.
        // Re-apply the reserved-width calculation once that decision is final.
        if (_subVfoList.IsHandleCreated)
            _subVfoList.BeginInvoke(ResizeSubVfoRows);
        _lastSubVfoRangeSignature = string.Empty;
        UpdateSubVfoRangeVisuals();
    }

    private void UpdateSubVfoRangeVisuals()
    {
        if (_subVfoList.IsDisposed) return;
        var now = Environment.TickCount64;
        if (now < _nextSubVfoVisualTick) return;
        _nextSubVfoVisualTick = now + 200;
        var center = Interlocked.Read(ref _rfCenterFrequency);
        var receivers = Volatile.Read(ref _subVfoReceivers);
        var states = _appSettings.SubVfos.Select(sub =>
        {
            var receiver = receivers.FirstOrDefault(item => item.Id.Equals(sub.Id, StringComparison.OrdinalIgnoreCase));
            var inside = _source is not IRemoteAudioSampleSource && receiver?.IsInside(center, _source.SampleRate) == true;
            return (Sub: sub, Inside: inside);
        }).ToArray();
        var signature = string.Join("|", states.Select(item => $"{item.Sub.Id}:{item.Inside}"));
        if (signature == _lastSubVfoRangeSignature) return;
        _lastSubVfoRangeSignature = signature;
        foreach (var state in states)
        {
            var row = _subVfoList.Controls.Cast<Control>().FirstOrDefault(control =>
                string.Equals(control.Tag?.ToString(), state.Sub.Id, StringComparison.OrdinalIgnoreCase));
            var label = row?.Controls.OfType<Label>().FirstOrDefault();
            var meter = row?.Controls.OfType<SignalMeterControl>().FirstOrDefault();
            if (label is null) continue;
            var output = state.Sub.OutputChannel == 0 ? "DEC" : $"CH{state.Sub.OutputChannel}";
            label.Text = $"{state.Sub.Name}  {state.Sub.Frequency / 1_000_000d:0.000000} MHz  {state.Sub.Mode}  " +
                         $"{state.Sub.Bandwidth / 1_000d:0.###}k  {output}" + (state.Inside ? "" : "  · OUT OF RANGE");
            label.ForeColor = state.Inside ? Color.FromArgb(211, 226, 235) : Color.FromArgb(238, 151, 48);
            if (row is not null) row.BackColor = state.Inside ? Color.FromArgb(15, 37, 48) : Color.FromArgb(55, 38, 30);
            if (meter is not null)
            {
                meter.Enabled = state.Inside;
                if (!state.Inside) meter.Value = -140;
            }
        }
        InvalidateAfPluginChrome();
    }

    private void UpdateSubVfoMeters()
    {
        var now = Environment.TickCount64;
        if (now < _nextSubVfoMeterUpdateTick) return;
        _nextSubVfoMeterUpdateTick = now + 750;
        var center = Interlocked.Read(ref _rfCenterFrequency);
        var receivers = Volatile.Read(ref _subVfoReceivers);
        foreach (var receiver in receivers)
        {
            var row = _subVfoList.Controls.Cast<Control>().FirstOrDefault(control =>
                string.Equals(control.Tag?.ToString(), receiver.Id, StringComparison.OrdinalIgnoreCase));
            var meter = row?.Controls.OfType<SignalMeterControl>().FirstOrDefault();
            if (meter is null) continue;
            var inside = _source is not IRemoteAudioSampleSource && receiver.IsInside(center, _source.SampleRate);
            meter.Enabled = inside;
            meter.Value = inside ? Volatile.Read(ref receiver.SignalLevelDb) : -140;
        }
    }

    private bool IsAfPluginPageOutOfRange(TabPage page)
    {
        var binding = _afPluginTabs.Values.FirstOrDefault(item => ReferenceEquals(item.Page, page));
        if (binding is null) return false;
        var instance = _appSettings.AfPluginInstances.FirstOrDefault(item =>
            item.InstanceId.Equals(binding.InstanceId, StringComparison.OrdinalIgnoreCase));
        if (instance is null || instance.VfoId.Equals("main", StringComparison.OrdinalIgnoreCase)) return false;
        var receiver = Volatile.Read(ref _subVfoReceivers).FirstOrDefault(item =>
            item.Id.Equals(instance.VfoId, StringComparison.OrdinalIgnoreCase));
        return _source is IRemoteAudioSampleSource ||
               receiver?.IsInside(Interlocked.Read(ref _rfCenterFrequency), _source.SampleRate) != true;
    }

    private void ResizeSubVfoRows()
    {
        if (_subVfoList.IsDisposed) return;
        // Always reserve a vertical scrollbar gutter. If the rows are initially
        // sized to the full viewport, adding the vertical bar makes them overflow
        // and causes FlowLayoutPanel to unnecessarily show a horizontal bar too.
        var usableWidth = _subVfoList.ClientSize.Width
                          - SystemInformation.VerticalScrollBarWidth
                          - _subVfoList.Padding.Horizontal
                          - 8;
        foreach (Control control in _subVfoList.Controls)
        {
            var width = Math.Max(80, usableWidth - control.Margin.Horizontal);
            if (control.Width != width) control.Width = width;
        }
    }

    private void AttachSubVfoCenterGesture(Control control, string id)
    {
        control.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left) return;
            _pendingSubVfoCenterId = id;
            _subVfoCenterHoldTimer.Stop();
            _subVfoCenterHoldTimer.Start();
        };
        control.MouseUp += (_, _) => CancelSubVfoCenterHold();
        control.MouseLeave += (_, _) => CancelSubVfoCenterHold();
        control.MouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Left) return;
            CancelSubVfoCenterHold();
            CenterSpectrumOnSubVfo(id);
        };
    }

    private void CancelSubVfoCenterHold()
    {
        _subVfoCenterHoldTimer.Stop();
        _pendingSubVfoCenterId = null;
    }

    private void CenterSpectrumOnSubVfo(string id)
    {
        var sub = _appSettings.SubVfos.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (sub is null) return;
        CenterSpectrumOnFrequency(sub.Frequency);
    }

    private void AddSubVfo(long frequency)
    {
        if (_source is IRemoteAudioSampleSource)
        {
            _statusLabel.Text = "Sub VFO requires a local IQ source.";
            return;
        }
        var used = _appSettings.SubVfos.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = Enumerable.Range(1, int.MaxValue).First(index => !used.Contains($"SUB {index}"));
        var settings = new SubVfoSettings
        {
            Name = $"SUB {number}", Frequency = Math.Clamp(frequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency),
            Mode = _demodulator.Mode, Bandwidth = _demodulator.Bandwidth
        };
        using var dialog = new SubVfoEditorForm(settings, SuggestAutoTuneTriggerFromSettings, CurrentMainVfo());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        CommitSubVfo(dialog.Result);
    }

    private void EditSubVfo(string id)
    {
        var settings = _appSettings.SubVfos.FirstOrDefault(item => item.Id == id);
        if (settings is null) return;
        using var dialog = new SubVfoEditorForm(settings, SuggestAutoTuneTriggerFromSettings, CurrentMainVfo());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        CommitSubVfo(dialog.Result);
    }

    private MainVfoCopy CurrentMainVfo() =>
        new(Interlocked.Read(ref _tunedFrequency), _demodulator.Mode, _demodulator.Bandwidth);

    private float? SuggestAutoTuneTriggerFromSettings(AutoTuneSettings auto)
    {
        var reference = auto.StandbyFrequency > 0 ? auto.StandbyFrequency : _tunedFrequency;
        var min = auto.MinFrequency > 0 ? auto.MinFrequency : reference - 25_000;
        var max = auto.MaxFrequency > 0 ? auto.MaxFrequency : reference + 25_000;
        return SuggestAutoTuneTriggerDb(reference, min, max);
    }

    private void CommitSubVfo(SubVfoSettings result)
    {
        if (result.OutputChannel != 0)
            foreach (var other in _appSettings.SubVfos.Where(item => item.Id != result.Id && item.OutputChannel == result.OutputChannel))
                other.OutputChannel = 0;
        var index = _appSettings.SubVfos.FindIndex(item => item.Id == result.Id);
        if (index < 0) _appSettings.SubVfos.Add(result); else _appSettings.SubVfos[index] = result;
        RebuildSubVfoReceivers();
        SchedulePersistCurrentSceneLayout();
    }

    private void DeleteSubVfo(string id)
    {
        _appSettings.SubVfos.RemoveAll(item => item.Id == id);
        foreach (var pluginId in _appSettings.AfPluginVfoRoutes.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
            _appSettings.AfPluginVfoRoutes[pluginId] = "main";
        foreach (var instance in _appSettings.AfPluginInstances.Where(instance => instance.VfoId == id))
            instance.VfoId = "main";
        RebuildSubVfoReceivers();
        _afPluginHost.SetRoutes(_appSettings.AfPluginVfoRoutes);
        SchedulePersistCurrentSceneLayout();
    }

    private float[] _digitalVoicePcm = [];

    private bool WriteDigitalVoiceOutput(int outputIndex, int samples, UiPipelineTrace? trace)
    {
        // Decoded voice is paced on its own 20 ms thread (DigitalVoicePacer).
        // Claiming the channel here only suppresses MAIN analog on that OUT.
        if (!DigitalVoicePlayback.Owns(outputIndex) || samples <= 0) return false;
        return true;
    }

    private SubVfoReceiver? OutputOwner(int outputIndex) =>
        Volatile.Read(ref _subVfoReceivers).FirstOrDefault(receiver =>
            receiver.OutputChannel == outputIndex + 1 &&
            _source is not IRemoteAudioSampleSource &&
            receiver.IsInside(Interlocked.Read(ref _rfCenterFrequency), _source.SampleRate));

    private void UpdateOutputOwnerCaptions()
    {
        var dv1 = DigitalVoicePlayback.Owns(0);
        var dv2 = DigitalVoicePlayback.Owns(1);
        var digitalName = RadioModes.IsDigitalVoice(_demodulator.Mode) ? _demodulator.Mode.ToString() : "Digital Voice";
        _rxVolumeBar.Caption = $"{(dv1 ? digitalName : OutputOwner(0)?.Name ?? "MAIN")} · OUT 1 VOL";
        _rxVolumeBar2.Caption = $"{(dv2 ? digitalName : OutputOwner(1)?.Name ?? "MAIN")} · OUT 2 VOL";
    }

    private void UpdateSourceCapabilities()
    {
        var remote = _source as IRemoteAudioSampleSource;
        if (remote is not null)
        {
            if (!_webUrlInput.Focused) _webUrlInput.Text = RemoteUrl(remote);
            RefreshSiteLists();
        }
        LayoutRxLowerArea();
        _gainSlider.Enabled = remote is null && _source is IGainControlledSampleSource;
        _recordButton.Enabled = remote is null;
        _openIqButton.Enabled = remote is null;
        _centerOnVfoOverlayButton.Enabled = true;
        _fullRfRangeOverlayButton.Enabled = true;
        _rfDisplayModeOverlayButton.Enabled = true;
        _rfPopOutOverlayButton.Enabled = true;
        _cwSideBox.Enabled = remote is null;
        _addSubVfoButton.Enabled = remote is null;
        RefreshAfPluginVfoSelection();
        _centerLabel.Text = remote is null
            ? $"RF center {_rfCenterFrequency / 1_000_000d:0.000000} MHz"
            : $"REMOTE AF · {remote.AudioSampleRate / 1_000d:0.#} kHz";
    }

    private string RemoteUrl(IRemoteAudioSampleSource remote) => remote.Name switch
    {
        "Virtual WebSDR" => _appSettings.WebSdrUrl,
        "Virtual KiwiSDR" => _appSettings.KiwiSdrUrl,
        _ => _appSettings.OpenWebRxUrl
    };

    private void SaveRemoteUrl(IRemoteAudioSampleSource remote)
    {
        if (remote.Name == "Virtual WebSDR") _appSettings.WebSdrUrl = remote.ServerUrl;
        else if (remote.Name == "Virtual KiwiSDR") _appSettings.KiwiSdrUrl = remote.ServerUrl;
        else _appSettings.OpenWebRxUrl = remote.ServerUrl;
    }

    private void WireEvents()
    {
        _uiTimer.Tick += (_, _) =>
        {
            if (IsDisposed || Disposing || _pluginUiSuspended) return;
            if (_passiveDiagnostic)
            {
                var nowTick = Stopwatch.GetTimestamp();
                var previousTick = Interlocked.Exchange(ref _passiveUiLastTick, nowTick);
                if (previousTick != 0)
                {
                    var gap = nowTick - previousTick;
                    long current;
                    while (gap > (current = Interlocked.Read(ref _passiveUiMaximumGapTicks)) &&
                           Interlocked.CompareExchange(ref _passiveUiMaximumGapTicks, gap, current) != current) { }
                }
            }
            RenderPendingSpectrum();
            RenderPendingAfSpectrum();
            ApplyAfcTick();
            var requested = Interlocked.Read(ref _pendingCenterFrequency);
            var requestedDeviceFrequency = DeviceCenterFrequency(requested);
            // A remote receiver has two independent frequencies: the demodulated
            // audio VFO and the server waterfall viewport. Never feed the latter
            // through the local-hardware center-follow loop; ApplyRemoteTune owns
            // the audio VFO and SetSpectrumViewportAsync owns the waterfall.
            if (_source is not IFixedCenterFrequencySampleSource and not IRemoteAudioSampleSource &&
                _source.CenterFrequency != requestedDeviceFrequency)
            {
                try { _source.CenterFrequency = requestedDeviceFrequency; }
                catch (Exception exception) { _statusLabel.Text = exception.Message; }
                _rfCenterFrequency = LogicalCenterFrequency(_source.CenterFrequency);
                Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
                ConfigureDisplay();
            }
            if (_source.IsRunning && Environment.TickCount64 >= _nextStatusLabelTick)
            {
                _nextStatusLabelTick = Environment.TickCount64 + 500;
                if (_source is ISampleSourceMetrics metrics)
                {
                    var dropped = metrics.DroppedSamples / 1_000_000d;
                    var audioDropped = _audioOutputs.Sum(output => output?.DroppedBuffers ?? 0);
                    var audioUnderruns = _audioOutputs.Sum(output => output?.StarvationEvents ?? 0);
                    var recording = _iqRecorder is null ? string.Empty : $" · REC {_iqRecorder.WrittenSamples / 1_000_000d:0.0}M";
                    var afRecording = _afRecorder is null ? string.Empty : " · AF REC";
                    _statusLabel.Text = $"Receiving · ADC {metrics.TotalSamples / 1_000_000d:0.0}M · DSP {metrics.DeliveredSamples / 1_000_000d:0.0}M · drop {dropped:0.00}M · audio drop {audioDropped} · underrun {audioUnderruns}{recording}{afRecording}";
                    _statusLabel.ForeColor = metrics.LastDeliveryAgeMilliseconds > 1_000
                        ? Color.FromArgb(255, 105, 105)
                        : Color.FromArgb(148, 165, 184);
                }
                else
                {
                    _statusLabel.Text = $"Receiving · {_source.Name}";
                }
            }
            UpdateSubVfoRangeVisuals();
            UpdateSubVfoMeters();
            _rfMeter.Value = Volatile.Read(ref _signalLevelDb);
            _afMeter.Value = Volatile.Read(ref _audioLevelDb);
            UpdateWfmAudioChrome();
            if (_demodulator.Mode == RadioMode.NFM && Environment.TickCount64 >= _nextCtcssUiTick)
            {
                _nextCtcssUiTick = Environment.TickCount64 + 120;
                UpdateCtcssUi();
            }
            if (Environment.TickCount64 >= _nextAutoTuneTick)
            {
                _nextAutoTuneTick = Environment.TickCount64 + 120;
                ProcessAutoTuneFollow();
            }
            TickAfRecordUi();
            var owner1 = OutputOwner(0);
            var owner2 = OutputOwner(1);
            UpdateOutputOwnerCaptions();
            _sqlLed1.IsOn = owner1 is null
                ? _squelchCheck.Checked && _audioProcessors[0].SquelchOpen
                : _appSettings.SubVfos.FirstOrDefault(item => item.Id == owner1.Id)?.SquelchEnabled == true && owner1.Processor.SquelchOpen;
            _sqlLed2.IsOn = owner2 is null
                ? _squelchCheck2.Checked && _audioProcessors[1].SquelchOpen
                : _appSettings.SubVfos.FirstOrDefault(item => item.Id == owner2.Id)?.SquelchEnabled == true && owner2.Processor.SquelchOpen;
            if (_pipelineTrace is not null && Environment.TickCount64 >= _nextPipelineTraceSnapshot)
            {
                _nextPipelineTraceSnapshot = Environment.TickCount64 + 2_500;
                var traceEvent = "TICK";
                ThreadPool.QueueUserWorkItem(_ => SnapshotPipelineTrace(traceEvent));
            }
        };
        _display.TunedFrequencyChanged += frequency =>
        {
            NoteManualTune();
            var previousFrequency = _tunedFrequency;
            _tunedFrequency = ConstrainFrequencyForCurrentSource(frequency);
            ShiftMainAutoWindowWithVfo(_tunedFrequency - previousFrequency);
            ResetAfcOffset();
            ResetCwIfFrequencyChanged(previousFrequency);
            ApplyRemoteTune();
            UpdateFrequencyReadout();
            ApplyDemodFrequencyOffset();
            SchedulePersistCurrentSceneLayout();
        };
        _display.WfmStationClicked += (hz, _) => _wfmStationPanel.RecallByFrequency(hz);
        _display.BroadcastStationClicked += (hz, name) =>
        {
            TuneToShortwaveBroadcast(hz, name, centerWaterfall: false);
            SelectEibiListStation(hz, name);
        };
        _display.CenterFrequencyChanged += MoveRfCenterFromDisplay;
        _display.CenterDragStarted += BeginRfCenterDrag;
        _display.CenterDragCompleted += CompleteRfCenterDrag;
        _display.ViewChanged += (center, bandwidth) =>
        {
            _viewCenterFrequency = center;
            _viewBandwidth = bandwidth;
            Interlocked.Exchange(ref _pendingSpectrum, null);
            QueueRemoteViewport(immediate: false);
            UpdateZoomControls();
            RefreshEibiWaterfall(force: true);
            SchedulePersistCurrentSceneLayout();
        };
        _remoteViewportTimer.Tick += async (_, _) =>
        {
            _remoteViewportTimer.Stop();
            await ApplyQueuedRemoteViewportAsync().ConfigureAwait(true);
        };
        _display.DisplayLevelsChanged += (spectrum, waterfall) =>
        {
            _appSettings.SpectrumLevelOffsetDb = spectrum;
            _appSettings.WaterfallLevelOffsetDb = waterfall;
            RememberDisplayLevels();
            SchedulePersistCurrentSceneLayout();
        };
        _display.AutoLevelsChanged += (_, _) =>
        {
            RememberDisplayLevels();
            SchedulePersistCurrentSceneLayout();
        };
        _afFrequencyLabel.FrequencyChanged += hz => ChangeVfoFromDigitalDisplay(hz, userInitiated: true);
        _afDisplay.FilterRangeChanged += ApplyAfFilterRange;
        _afDisplay.CwCenterChanged += hz => _enFilterCwPanel.SetCenterHz(hz);
        _afDisplay.FskMarkersChanged += ApplyAfFskMarkersFromDisplay;
        _sourcePreviousButton.Click += (_, _) => CycleSource(-1);
        _sourceNextButton.Click += (_, _) => CycleSource(1);
        _modeAmButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.AM.ToString();
        _modeSamButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.SAM.ToString();
        _modeDmrButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.DMR.ToString();
        _modeDstarButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.DSTAR.ToString();
        _modeC4fmButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.C4FM.ToString();
        _modeFreedvButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.FREEDV.ToString();
        _modeNfmButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.NFM.ToString();
        _modeWfmButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.WFM.ToString();
        _modeUsbButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.USB.ToString();
        _modeLsbButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.LSB.ToString();
        _modeCwButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.CW.ToString();
        _modeRawButton.Click += (_, _) => _modeBox.SelectedItem = RadioMode.RAW.ToString();
        _bandMwButton.Click += (_, _) => SelectBand(1_000_000, RadioMode.AM);
        _bandSwButton.Click += (_, _) => SelectBand(6_000_000, RadioMode.AM);
        _band160Button.Click += (_, _) => SelectBand(1_900_000, RadioMode.LSB);
        _band80Button.Click += (_, _) => SelectBand(3_650_000, RadioMode.LSB);
        _band40Button.Click += (_, _) => SelectBand(7_100_000, RadioMode.LSB);
        _band30Button.Click += (_, _) => SelectBand(10_120_000, RadioMode.USB);
        _band20Button.Click += (_, _) => SelectBand(14_200_000, RadioMode.USB);
        _band17Button.Click += (_, _) => SelectBand(18_100_000, RadioMode.USB);
        _band15Button.Click += (_, _) => SelectBand(21_200_000, RadioMode.USB);
        _band12Button.Click += (_, _) => SelectBand(24_950_000, RadioMode.USB);
        _band10Button.Click += (_, _) => SelectBand(28_400_000, RadioMode.USB);
        _band6Button.Click += (_, _) => SelectBand(50_150_000, RadioMode.USB);
        _bandFmButton.Click += (_, _) => SelectBand(89_100_000, RadioMode.WFM);
        _bandAirButton.Click += (_, _) => SelectBand(118_300_000, RadioMode.AM);
        _bandwidth500Button.Click += (_, _) => _bandwidthBox.Value = 500;
        _bandwidth2700Button.Click += (_, _) => _bandwidthBox.Value = 2_700;
        _bandwidth4000Button.Click += (_, _) => _bandwidthBox.Value = 4_000;
        _bandwidth7000Button.Click += (_, _) => _bandwidthBox.Value = 7_000;
        _bandwidth10000Button.Click += (_, _) => _bandwidthBox.Value = 10_000;
        _bandwidth12500Button.Click += (_, _) => _bandwidthBox.Value = 12_500;
        _bandwidth180000Button.Click += (_, _) => _bandwidthBox.Value = 180_000;
        _bandwidthDownButton.Click += (_, _) => AdjustBandwidth(-1);
        _bandwidthUpButton.Click += (_, _) => AdjustBandwidth(1);
        _radioPanel.Resize += (_, _) => LayoutRxLowerArea();
        _subVfoList.ControlAdded += (_, _) => LayoutRxLowerArea();
        _subVfoList.ControlRemoved += (_, _) => LayoutRxLowerArea();
        _afVfoHideTimer.Tick += (_, _) =>
        {
            _afVfoHideTimer.Stop();
            _afDisplayVfoPanel.Visible = false;
        };
        _afDisplay.MouseEnter += (_, _) => ShowAfVfoPanel();
        _afDisplayVfoPanel.MouseEnter += (_, _) => ShowAfVfoPanel();
        _graphPanel.MouseEnter += (_, _) => ShowAfVfoPanel();
        _afDisplay.MouseLeave += (_, _) => ScheduleAfVfoPanelHide();
        _afDisplayVfoPanel.MouseLeave += (_, _) => ScheduleAfVfoPanelHide();
        _graphPanel.MouseLeave += (_, _) => ScheduleAfVfoPanelHide();
        _graphPanel.Resize += (_, _) =>
        {
            PositionAfDisplayOverlay();
            if (_demodulator.Mode == RadioMode.WFM)
                ApplyWfmAfChrome();
        };
        _rfOverlayHideTimer.Tick += (_, _) =>
        {
            _rfOverlayHideTimer.Stop();
            _rfOverlayPanel.Visible = false;
        };
        _display.MouseEnter += (_, _) => ShowRfDisplayOverlay();
        _rfOverlayPanel.MouseEnter += (_, _) => ShowRfDisplayOverlay();
        _centerPanel.MouseEnter += (_, _) => ShowRfDisplayOverlay();
        _display.MouseLeave += (_, _) => ScheduleRfDisplayOverlayHide();
        _rfOverlayPanel.MouseLeave += (_, _) => ScheduleRfDisplayOverlayHide();
        _centerPanel.MouseLeave += (_, _) => ScheduleRfDisplayOverlayHide();
        _centerPanel.Resize += (_, _) => PositionRfDisplayOverlay();
        _centerOnVfoOverlayButton.Click += (_, _) => CenterSpectrumOnFrequency(_tunedFrequency);
        _fullRfRangeOverlayButton.Click += (_, _) => ResetFullView();
        _rfDisplayModeOverlayButton.Click += (_, _) => ToggleRfDisplayMode();
        _rfPopOutOverlayButton.Click += (_, _) => ToggleRfDisplayDetach();
        _rfFullscreenOverlayButton.Click += (_, _) => ToggleRfPopOutFullscreen();
        EnsureAfPopOutChrome();
        _sceneLayoutPersistTimer.Tick += (_, _) =>
        {
            _sceneLayoutPersistTimer.Stop();
            PersistCurrentSceneLayout();
        };
        _mainAutoHideTimer.Tick += (_, _) =>
        {
            _mainAutoHideTimer.Stop();
            if (_mainAutoTuneButton is null) return;
            // Always keep the strip visible; only the hover color changes.
            UpdateMainAutoTuneButtonStyle(hot: IsOverMainAutoTuneHotzone());
            _mainAutoTuneButton.Visible = true;
        };
        ApplyRfDisplayMode();
        _subVfoCenterHoldTimer.Tick += (_, _) =>
        {
            _subVfoCenterHoldTimer.Stop();
            var id = _pendingSubVfoCenterId;
            _pendingSubVfoCenterId = null;
            if (!string.IsNullOrWhiteSpace(id)) CenterSpectrumOnSubVfo(id);
        };
        _decoderTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_afPluginLayoutDepth > 0) return;
            if (_decoderTabs.SelectedTab is TabPage selectedPage)
            {
                var binding = AfBindingForPage(selectedPage);
                if (binding is not null) _lastSelectedAfInstanceId = binding.InstanceId;
            }
            ApplyAfPluginActivation();
            ApplySlowModePluginSettings();
            UpdateAfFskMarkers();
            RefreshAfPluginVfoSelection();
        };
        _addSubVfoButton.Click += (_, _) => AddSubVfo(_tunedFrequency);
        _display.SubVfoRequested += AddSubVfo;
        _ftxModeBox.SelectedIndexChanged += (_, _) => ApplyFtxControls();
        _ftxTimeAdjust.ValueChanged += (_, _) => ApplyFtxControls();
        _ftxAutoAdjust.CheckedChanged += (_, _) => ApplyFtxControls();
        _ftxQsoLines.CheckedChanged += (_, _) =>
        {
            if (_synchronizingFtxControls) return;
            _appSettings.FtxShowQsoLines = _ftxQsoLines.Checked;
            _afDisplay.ShowQsoLines = _ftxQsoLines.Checked;
            SynchronizeFtxControls();
        };
        _sstvView.OptionsChanged += () =>
        {
            _sstvView.SaveSettings(_appSettings);
            ConfigureSstvPlugin();
        };
        _sstvView.CommandRequested += command =>
        {
            _sstvView.SaveSettings(_appSettings);
            ConfigureSstvPlugin(command);
        };
        _rttyView.OptionsChanged += () =>
        {
            _rttyView.SaveSettings(_appSettings);
            ConfigureRttyPlugin();
            UpdateAfFskMarkers();
        };
        _rttyView.CommandRequested += command =>
        {
            _rttyView.SaveSettings(_appSettings);
            ConfigureRttyPlugin(command);
        };
        _weatherFaxView.OptionsChanged += () =>
        {
            _weatherFaxView.SaveSettings(_appSettings);
            ConfigureWeatherFaxPlugin();
        };
        _weatherFaxView.CommandRequested += command =>
        {
            _weatherFaxView.SaveSettings(_appSettings);
            ConfigureWeatherFaxPlugin(command);
        };
        _kiwiNavtexView.OptionsChanged += () =>
        {
            _kiwiNavtexView.SaveSettings(_appSettings);
            ConfigureKiwiNavtexPlugin();
            UpdateAfFskMarkers();
        };
        _kiwiNavtexView.CommandRequested += command =>
        {
            _kiwiNavtexView.SaveSettings(_appSettings);
            ConfigureKiwiNavtexPlugin(command);
        };
        _kiwiWwvView.OptionsChanged += () =>
        {
            _kiwiWwvView.SaveSettings(_appSettings);
            ConfigureKiwiWwvPlugin();
        };
        _kiwiWwvView.CommandRequested += command =>
        {
            _kiwiWwvView.SaveSettings(_appSettings);
            ConfigureKiwiWwvPlugin(command);
        };
        _flRttyView.OptionsChanged += () =>
        {
            _flRttyView.SaveSettings(_appSettings);
            ConfigureFlRttyPlugin();
            UpdateAfFskMarkers();
        };
        _flRttyView.CommandRequested += command =>
        {
            _flRttyView.SaveSettings(_appSettings);
            ConfigureFlRttyPlugin(command);
        };
        _flCwView.OptionsChanged += () =>
        {
            _flCwView.SaveSettings(_appSettings);
            ConfigureFlCwPlugin();
        };
        _flCwView.CommandRequested += command =>
        {
            _flCwView.SaveSettings(_appSettings);
            ConfigureFlCwPlugin(command);
        };
        _flFaxView.OptionsChanged += () =>
        {
            _flFaxView.SaveSettings(_appSettings);
            ConfigureFlFaxPlugin();
        };
        _flFaxView.CommandRequested += command =>
        {
            _flFaxView.SaveSettings(_appSettings);
            ConfigureFlFaxPlugin(command);
        };
        _kiwiTimecodeView.OptionsChanged += () =>
        {
            _kiwiTimecodeView.SaveSettings(_appSettings);
            ConfigureKiwiTimecodePlugin();
        };
        _kiwiTimecodeView.CommandRequested += command =>
        {
            _kiwiTimecodeView.SaveSettings(_appSettings);
            ConfigureKiwiTimecodePlugin(command);
        };
        _adsbView.CommandRequested += command => ConfigureAdsbPlugin(command);
        _adsbView.PresetSelected += preset => ApplyAdsbFrequencyPreset(preset);
        _lteView.CommandRequested += command => ConfigureLtePlugin(command);
        _lteView.PresetSelected += preset => ApplyAdsbFrequencyPreset(preset);
        _rxSourceBox.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressSourceChange) return;
            SelectSource();
            UpdateRxPanel();
        };
        _siteBox.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressSiteChange) return;
            if (_siteBox.SelectedItem is RemoteSdrEntry entry)
                _ = ApplyRemoteEntryAsync(entry);
        };
        _siteFindButton.Click += (_, _) => OpenRemoteSdrFind();
        _favoriteBox.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressSiteChange) return;
            if (_favoriteBox.SelectedItem is RemoteSdrFavorite favorite)
                _ = ApplyRemoteUrlAsync(favorite.Url);
        };
        _favoriteButton.Click += (_, _) => AddRemoteFavorite();
        _favoriteEditButton.Click += (_, _) => EditRemoteFavorite();
        _webUrlInput.KeyDown += async (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter || _source is not IRemoteAudioSampleSource remote) return;
            eventArgs.SuppressKeyPress = true;
            remote.ServerUrl = _webUrlInput.Text.Trim();
            SaveRemoteUrl(remote);
            await ConnectRemoteAsync(remote);
        };
        _openIqButton.Click += (_, _) => OpenIqWave();
        _recordButton.Click += (_, _) => ToggleIqRecording();
        EnsureAfRecordButton();
        EnsureSmartRecordButton();
        _memoryButton.Click += (_, _) => ShowMemoryPanel();
        _rxSceneBox.SelectedIndexChanged += (_, _) =>
        {
            if (_rxSceneUiBusy || _applyingRxScene) return;
            if (_rxSceneBox.SelectedItem is RxScene scene)
                ApplyRxScene(scene);
        };
        _rxSceneSaveButton.Click += (_, _) => SaveSelectedRxScene();
        _rxSceneNewButton.Click += (_, _) => SaveNewRxScene();
        _rxSceneDelButton.Click += (_, _) => DeleteSelectedRxScene();
        _setupButton.Click += (_, _) => ShowSetup();
        _scanTimer.Tick += (_, _) => ScanMemoryTick();
        _bandwidthBox.ValueChanged += (_, _) =>
        {
            _demodulator.Bandwidth = (int)_bandwidthBox.Value;
            if (_source is IRemoteAudioSampleSource remote)
                _ = remote.ApplyReceiverAsync(RadioModes.DemodMode(_demodulator.Mode, _demodulator.SsbLower), _demodulator.Bandwidth);
            EnsureViewFitsFilter();
            ConfigureDisplay();
            ConfigureAfDisplay();
            UpdateRxPanel();
        };
        _modeBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressModeDefaults)
                ApplyModeDefaults();
            else
            {
                _demodulator.Mode = Enum.Parse<RadioMode>((string)_modeBox.SelectedItem!);
                ConfigureDisplay();
                ConfigureAfDisplay();
            }
            SyncDigitalModeEngine();
            if (_source is IRemoteAudioSampleSource remote)
                _ = remote.ApplyReceiverAsync(RadioModes.DemodMode(_demodulator.Mode, _demodulator.SsbLower), _demodulator.Bandwidth);
            UpdateRxPanel();
            AnnounceModeIfEnabled(_demodulator.Mode.ToString());
            if (_demodulator.Mode != RadioMode.NFM)
            {
                _ctcssDecoder.Reset();
                _dcsDecoder.Reset();
                _dtmfDecoder.Reset();
                _mdcDecoder.Reset();
                UpdateCtcssUi(force: true);
            }
        };
        _cwSideBox.SelectedIndexChanged += (_, _) =>
        {
            _demodulator.CwPitchHz = _cwSideBox.SelectedIndex == 1 ? -700 : 700;
            ApplyCwShiftToFilterCenter();
            ConfigureDisplay();
            ConfigureAfDisplay();
            UpdateRxPanel();
        };
        _gainSlider.ValueChanged += (_, _) =>
        {
            NoteDigitalAgcCeiling(_gainSlider.Value);
            SchedulePersistCurrentSceneLayout();
        };
        _volumeSlider.ValueChanged += (_, _) =>
        {
            if (_audioOutputs[0] is not null) _audioOutputs[0]!.VolumePercent = _volumeSlider.Value;
            _appSettings.Audio1.Volume = _volumeSlider.Value;
            if (_rxVolumeBar.Value != _volumeSlider.Value) _rxVolumeBar.Value = _volumeSlider.Value;
            SchedulePersistCurrentSceneLayout();
        };
        _rxVolumeBar.ValueChanged += (_, _) => { if (_volumeSlider.Value != _rxVolumeBar.Value) _volumeSlider.Value = _rxVolumeBar.Value; };
        _rxVolumeBar2.ValueChanged += (_, _) =>
        {
            _appSettings.Audio2.Volume = _rxVolumeBar2.Value;
            if (_audioOutputs[1] is not null) _audioOutputs[1]!.VolumePercent = _rxVolumeBar2.Value;
            SchedulePersistCurrentSceneLayout();
        };
        _audioCheck.CheckedChanged += (_, _) => _audioEnabled = _audioCheck.Checked;
        _squelchCheck.CheckedChanged += (_, _) =>
        {
            _audioProcessors[0].SquelchEnabled = _squelchCheck.Checked;
            _appSettings.Audio1.SquelchEnabled = _squelchCheck.Checked;
            SchedulePersistCurrentSceneLayout();
        };
        _squelchCheck2.CheckedChanged += (_, _) =>
        {
            _audioProcessors[1].SquelchEnabled = _squelchCheck2.Checked;
            _appSettings.Audio2.SquelchEnabled = _squelchCheck2.Checked;
            SchedulePersistCurrentSceneLayout();
        };
        _squelchThreshold.ValueChanged += (_, _) =>
        {
            _audioProcessors[0].SquelchThresholdDb = (float)_squelchThreshold.Value;
            _appSettings.Audio1.SquelchThreshold = (int)_squelchThreshold.Value;
            var sql = (int)_squelchThreshold.Value;
            if (_rxSquelchBar.Value != sql) _rxSquelchBar.Value = sql;
            SchedulePersistCurrentSceneLayout();
        };
        _rxSquelchBar.ValueChanged += (_, _) =>
        {
            var sql = Math.Clamp(_rxSquelchBar.Value, (int)_squelchThreshold.Minimum, (int)_squelchThreshold.Maximum);
            if (_rxSquelchBar.Value != sql) _rxSquelchBar.Value = sql;
            if ((int)_squelchThreshold.Value != sql) _squelchThreshold.Value = sql;
            SchedulePersistCurrentSceneLayout();
        };
        _rxSquelchBar2.ValueChanged += (_, _) =>
        {
            var sql = Math.Clamp(_rxSquelchBar2.Value, -140, 0);
            if (_rxSquelchBar2.Value != sql) _rxSquelchBar2.Value = sql;
            _appSettings.Audio2.SquelchThreshold = sql;
            _audioProcessors[1].SquelchThresholdDb = sql;
            SchedulePersistCurrentSceneLayout();
        };
        _agcCheck.CheckedChanged += (_, _) => { foreach (var processor in _audioProcessors) processor.AgcEnabled = _agcCheck.Checked; };
        _noiseReductionCheck.CheckedChanged += (_, _) => { foreach (var processor in _audioProcessors) processor.NoiseReductionEnabled = _noiseReductionCheck.Checked; };
        _noiseReductionSlider.ValueChanged += (_, _) => { foreach (var processor in _audioProcessors) processor.NoiseReductionStrength = _noiseReductionSlider.Value; };
        _notchCheck.CheckedChanged += (_, _) => { foreach (var processor in _audioProcessors) processor.NotchEnabled = _notchCheck.Checked; };
        _notchFrequency.ValueChanged += (_, _) => { foreach (var processor in _audioProcessors) processor.NotchFrequency = (int)_notchFrequency.Value; };
        _afFilterCheck.CheckedChanged += (_, _) =>
        {
            if (_suppressAfFilterEvents || IsSatelliteSceneActive) return;
            _appSettings.AfFilterEnabled = _afFilterCheck.Checked;
            var cwFilter = CwEnFilterActive;
            foreach (var processor in _audioProcessors)
                processor.AfFilterEnabled = cwFilter ? false : _afFilterCheck.Checked;
            ConfigureAfDisplay();
            SchedulePersistCurrentSceneLayout();
        };
        _frequencyInput.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Keys.Enter) return;
            ApplyTypedFrequency();
            eventArgs.SuppressKeyPress = true;
        };
        _frequencyInput.Leave += (_, _) => UpdateTuningDisplay();
        _startButton.Click += (_, _) => ToggleReceiver();
        FormClosing += (_, _) => Shutdown();
    }

    private void ApplyAudioDspSettings()
    {
        for (var index = 0; index < _audioProcessors.Length; index++)
        {
            var processor = _audioProcessors[index];
            processor.SquelchEnabled = index == 0 ? _squelchCheck.Checked : _squelchCheck2.Checked;
            processor.SquelchThresholdDb = index == 0 ? (float)_squelchThreshold.Value : _rxSquelchBar2.Value;
            processor.AgcEnabled = _agcCheck.Checked;
            processor.ExtendedAgcRange = _appSettings.ExtendedAfAgcRangeEnabled;
            processor.NoiseReductionEnabled = _noiseReductionCheck.Checked;
            processor.NoiseReductionStrength = _noiseReductionSlider.Value;
            processor.NotchEnabled = _notchCheck.Checked;
            processor.NotchFrequency = (int)_notchFrequency.Value;
            processor.AfFilterEnabled = CwEnFilterActive ? false : _afFilterCheck.Checked;
            processor.AfLowCutHz = _appSettings.AfLowCutHz;
            processor.AfHighCutHz = _appSettings.AfHighCutHz;
        }
        foreach (var receiver in Volatile.Read(ref _subVfoReceivers)) ConfigureSubVfoProcessor(receiver);
    }

    private void AttachSource(ISampleSource source)
    {
        source.SamplesAvailable += OnSamplesAvailable;
        if (source is not IRemoteAudioSampleSource remote) return;
        remote.AudioSamplesAvailable += OnRemoteAudioSamples;
        remote.RemoteSpectrumAvailable += OnRemoteSpectrum;
        remote.ConnectionStatusChanged += OnRemoteStatus;
    }

    private void DetachSource(ISampleSource source)
    {
        source.SamplesAvailable -= OnSamplesAvailable;
        if (source is not IRemoteAudioSampleSource remote) return;
        remote.AudioSamplesAvailable -= OnRemoteAudioSamples;
        remote.RemoteSpectrumAvailable -= OnRemoteSpectrum;
        remote.ConnectionStatusChanged -= OnRemoteStatus;
    }

    private void OnSamplesAvailable(Complex32[] samples)
    {
        var trace = _pipelineTrace;
        trace?.RecordRfArrival(samples.Length);
        var callbackStart = Stopwatch.GetTimestamp();
        try
        {
            _iqRecorder?.Write(samples);
            if (Volatile.Read(ref _digitalAgcObserve) != 0)
                NoteDigitalAgcPeak(samples);
            var stageStart = Stopwatch.GetTimestamp();
            samples = _iqPluginHost.Process(samples, _source.SampleRate, _rfCenterFrequency, DateTime.UtcNow);
            trace?.RecordStage(UiPipelineStage.IqPlugins, stageStart);
            var now = Environment.TickCount64;
            if (now >= Interlocked.Read(ref _nextRfDisplaySubmissionTick))
            {
                var fps = Math.Clamp(_appSettings.RfDisplayFramesPerSecond, 5, 30);
                Interlocked.Exchange(ref _nextRfDisplaySubmissionTick, now + Math.Max(1, 1_000 / fps));
                stageStart = Stopwatch.GetTimestamp();
                _spectrumPipeline.Submit(samples, _source.SampleRate, Volatile.Read(ref _viewBandwidth),
                    _appSettings.RfFftQuality);
                trace?.RecordStage(UiPipelineStage.RfDisplaySubmit, stageStart);
            }
            var hasAudio = false;
            for (var index = 0; index < _audioOutputs.Length; index++)
            {
                if (_audioOutputs[index] is not null) { hasAudio = true; break; }
            }
            const bool showAf = true;
            if ((!hasAudio || !_audioEnabled) && !showAf && Volatile.Read(ref _subVfoReceivers).Length == 0) return;
            ApplyDemodFrequencyOffset();
            stageStart = Stopwatch.GetTimestamp();
            var demodulated = _demodulator.Process(samples, _source.SampleRate);
            trace?.RecordStage(UiPipelineStage.Demodulator, stageStart);
            trace?.RecordPcm(demodulated.Length);
            // Write MAIN audio first, then run SUBs off the RF callback (shared IQ copy).
            // Sequential SUB+FT8 on this thread was starving waveOut in Debug (~0.2s audio/s).
            ProcessDemodulatedAudio(demodulated, showAf, trace);
            ScheduleSubVfoProcessing(samples, showAf, trace);
        }
        catch (Exception exception)
        {
            trace?.RecordError(exception);
            throw;
        }
        finally { trace?.RecordStage(UiPipelineStage.WholeCallback, callbackStart); }
    }

    private void OnRemoteAudioSamples(float[] samples, int sampleRate)
    {
        if (_source is not IRemoteAudioSampleSource || samples.Length == 0) return;
        if (_remoteAudioResampler is null || _remoteAudioInputRate != sampleRate)
        {
            _remoteAudioInputRate = sampleRate;
            _remoteAudioResampler = new StreamingFloatResampler(sampleRate, AudioDemodulator.AudioSampleRate);
        }
        var audio = _remoteAudioResampler.Process(samples);
        if (audio.Length > 0) ProcessDemodulatedAudio(audio, true, _pipelineTrace);
    }

    private void ProcessDemodulatedAudio(float[] demodulated, bool showAf, UiPipelineTrace? trace)
    {
        if (_demodulator.Mode == RadioMode.NFM && demodulated.Length > 0)
        {
            _dcsDecoder.Process(demodulated, AudioDemodulator.AudioSampleRate);
            if (_dcsDecoder.HasActivity)
                _ctcssDecoder.Reset();
            else
                _ctcssDecoder.Process(demodulated, AudioDemodulator.AudioSampleRate);
            _dtmfDecoder.Process(demodulated, AudioDemodulator.AudioSampleRate);
            _mdcDecoder.Process(demodulated, AudioDemodulator.AudioSampleRate);
        }

        if (demodulated.Length > 0)
        {
            double sumSquares = 0;
            foreach (var value in demodulated) sumSquares += value * value;
            var meanSquare = sumSquares / demodulated.Length;
            Volatile.Write(ref _audioLevelDb, (float)Math.Clamp(20 * Math.Log10(Math.Sqrt(meanSquare) + 1e-9), -140, 0));
            if (_passiveDiagnostic)
            {
                Interlocked.Add(ref _passivePcmSamples, demodulated.Length);
                var level = Volatile.Read(ref _audioLevelDb);
                var current = BitConverter.Int32BitsToSingle(Volatile.Read(ref _passiveAudioPeakBits));
                while (level > current)
                {
                    var observed = Interlocked.CompareExchange(ref _passiveAudioPeakBits,
                        BitConverter.SingleToInt32Bits(level), BitConverter.SingleToInt32Bits(current));
                    if (observed == BitConverter.SingleToInt32Bits(current)) break;
                    current = BitConverter.Int32BitsToSingle(observed);
                }
            }
        }
        var stageStart = Stopwatch.GetTimestamp();
        float[]? digitalFeed = null;
        var digitalMode = _demodulator.Mode;
        if (_digitalMode.IsActive &&
            _digitalVoiceFeedSource == DigitalVoiceFeedSource.Rf &&
            demodulated.Length > 0 &&
            RadioModes.IsDigitalVoice(digitalMode))
            digitalFeed = CopyAudio(demodulated);
        // Push the discriminator before AF plugins. Caption/EIBI must not delay
        // the symbol clock; the copy stays free of RNNoise and squelch.
        if (_digitalMode.IsActive && _digitalVoiceFeedSource == DigitalVoiceFeedSource.Rf && digitalFeed is not null)
        {
            _digitalMode.ProcessAf(digitalFeed, softwareDiscriminator: RadioModes.IsFmDigitalVoice(digitalMode));
            PublishDigitalOverlayIfChanged();
        }
        demodulated = _afPluginHost.Process(demodulated, AudioDemodulator.AudioSampleRate, DateTime.UtcNow, "main");
        var afDisplayAudio = demodulated;
        trace?.RecordStage(UiPipelineStage.AfPlugins, stageStart);
        _smartRecord.WriteAudio("main", demodulated);
        _afRecorder?.WriteFloatMono(demodulated);

        if (_digitalMode.IsActive && _digitalVoiceFeedSource == DigitalVoiceFeedSource.Rf && digitalFeed is null)
        {
            _digitalMode.ProcessAf(demodulated);
            PublishDigitalOverlayIfChanged();
        }

        // WFM stereo is taken once and shared by AF display + MAIN OUT.
        var stereo = _demodulator.Mode == RadioMode.WFM && _appSettings.WfmStereoEnabled
            ? _demodulator.TakeStereoAudio()
            : [];
        if (stereo.Length >= 2)
        {
            _wfmEq.ProcessStereoInterleaved(stereo);
            UpdateStereoLevelMeters(stereo);
        }
        else if (_demodulator.Mode == RadioMode.WFM)
        {
            _wfmEq.ProcessMono(demodulated);
            var monoDb = Volatile.Read(ref _audioLevelDb);
            Volatile.Write(ref _stereoLeftDb, monoDb);
            Volatile.Write(ref _stereoRightDb, monoDb);
        }

        if (showAf && SelectedAfVfoId().Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            stageStart = Stopwatch.GetTimestamp();
            if (_digitalMode.IsActive)
            {
                var voice = Interlocked.Exchange(ref _pendingDigitalVoiceSpectrum, null);
                if (voice is { Length: > 0 })
                    _audioSpectrumPipeline.Submit(voice);
            }
            else if (_demodulator.Mode == RadioMode.WFM && stereo.Length >= 2)
            {
                _audioSpectrumPipeline.SubmitStereo(CopyAudio(stereo));
            }
            else
            {
                var spectrumAudio = _demodulator.Mode == RadioMode.CW ? afDisplayAudio : demodulated;
                _audioSpectrumPipeline.Submit(CopyAudio(spectrumAudio));
            }
            trace?.RecordStage(UiPipelineStage.AfDisplaySubmit, stageStart);
        }
        // Web remote AF is pre-waveOut volume so phone/browser gain stays independent of desktop OUT1/OUT2.
        if (_remoteBridge is not null)
            PublishRemoteAudio(stereo.Length >= 2 ? StereoToMono(stereo) : demodulated);

        if (_audioEnabled)
        {
            for (var index = 0; index < _audioOutputs.Length; index++)
            {
                var output = _audioOutputs[index];
                if (output is null) continue;
                if (WriteDigitalVoiceOutput(index, demodulated.Length, trace)) continue;
                if (OutputOwner(index) is not null) continue;
                stageStart = Stopwatch.GetTimestamp();
                if (index == 0 && output.Channels == 2 && stereo.Length >= 2)
                {
                    output.Write(stereo);
                    trace?.RecordStage(UiPipelineStage.Audio1Write, stageStart);
                    continue;
                }
                // RNNoise follows only the VFO actually playing on OUT 1.
                var playback = index == 0 ? ApplyEnFilter(demodulated, _demodulator.Mode) : demodulated;
                var processed = _audioProcessors[index].Process(playback, Volatile.Read(ref _signalLevelDb));
                trace?.RecordStage(index == 0 ? UiPipelineStage.Audio1Dsp : UiPipelineStage.Audio2Dsp, stageStart);
                stageStart = Stopwatch.GetTimestamp();
                output.Write(processed);
                trace?.RecordStage(index == 0 ? UiPipelineStage.Audio1Write : UiPipelineStage.Audio2Write, stageStart);
            }
        }
    }

    private static float[] StereoToMono(float[] interleaved)
    {
        var mono = new float[interleaved.Length / 2];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = 0.5f * (interleaved[i * 2] + interleaved[i * 2 + 1]);
        return mono;
    }

    private void ScheduleSubVfoProcessing(Complex32[] samples, bool showAf, UiPipelineTrace? trace)
    {
        if (_source is IRemoteAudioSampleSource) return;
        var receivers = Volatile.Read(ref _subVfoReceivers);
        if (receivers.Length == 0) return;
        var center = Interlocked.Read(ref _rfCenterFrequency);
        var sampleRate = _source.SampleRate;
        var hasWork = false;
        for (var index = 0; index < receivers.Length; index++)
        {
            var receiver = receivers[index];
            if (!receiver.IsInside(center, sampleRate)) continue;
            if (!SubVfoHasDownstream(receiver)) continue;
            hasWork = true;
            break;
        }
        if (!hasWork) return;

        var copy = new Complex32[samples.Length];
        Array.Copy(samples, copy, samples.Length);
        ThreadPool.UnsafeQueueUserWorkItem(ProcessSubVfoBlock,
            (copy, center, sampleRate, showAf, trace));
    }

    private void ProcessSubVfoBlock(object? state)
    {
        var work = ((Complex32[] Iq, long Center, int Rate, bool ShowAf, UiPipelineTrace? Trace))state!;
        var active = Volatile.Read(ref _subVfoReceivers);
        for (var index = 0; index < active.Length; index++)
        {
            var receiver = active[index];
            if (!receiver.IsInside(work.Center, work.Rate)) continue;
            if (!SubVfoHasDownstream(receiver)) continue;
            ProcessSubVfo(receiver, work.Iq, work.Center, work.Rate, work.ShowAf, work.Trace);
        }
    }

    private void ProcessSubVfosSequential(Complex32[] samples, bool showAf, UiPipelineTrace? trace)
    {
        // Kept for diagnostics / soak tests that need deterministic ordering.
        if (_source is IRemoteAudioSampleSource) return;
        var receivers = Volatile.Read(ref _subVfoReceivers);
        if (receivers.Length == 0) return;
        var center = Interlocked.Read(ref _rfCenterFrequency);
        var sampleRate = _source.SampleRate;
        foreach (var receiver in receivers)
        {
            if (!receiver.IsInside(center, sampleRate)) continue;
            if (!SubVfoHasDownstream(receiver)) continue;
            ProcessSubVfo(receiver, samples, center, sampleRate, showAf, trace);
        }
    }

    private Task[] StartSubVfoProcessing(Complex32[] samples, bool showAf, UiPipelineTrace? trace)
    {
        if (_source is IRemoteAudioSampleSource) return [];
        var receivers = Volatile.Read(ref _subVfoReceivers);
        if (receivers.Length == 0) return [];
        var center = Interlocked.Read(ref _rfCenterFrequency);
        var sampleRate = _source.SampleRate;
        return receivers
            .Where(receiver => receiver.IsInside(center, sampleRate) && SubVfoHasDownstream(receiver))
            .Select(receiver => Task.Run(() => ProcessSubVfo(receiver, samples, center, sampleRate, showAf, trace)))
            .ToArray();
    }

    private void ProcessSubVfo(SubVfoReceiver receiver, Complex32[] samples, long center, int sampleRate,
        bool showAf, UiPipelineTrace? trace)
    {
        try
        {
            receiver.Demodulator.FrequencyOffset = receiver.Frequency - center;
            var stageStart = Stopwatch.GetTimestamp();
            var audio = receiver.Demodulator.Process(samples, sampleRate);
            trace?.RecordStage(UiPipelineStage.Demodulator, stageStart);
            if (audio.Length == 0) return;
            Interlocked.Exchange(ref receiver.LastAudioTick, Environment.TickCount64);
            audio = _afPluginHost.Process(audio, AudioDemodulator.AudioSampleRate, DateTime.UtcNow, receiver.Id);
            var afDisplayAudio = audio;
            _smartRecord.WriteAudio(receiver.Id, audio);
            if (receiver.Id.Equals("main", StringComparison.OrdinalIgnoreCase))
                _afRecorder?.WriteFloatMono(audio);
            if (showAf && SelectedAfVfoId().Equals(receiver.Id, StringComparison.OrdinalIgnoreCase))
            {
                var spectrumAudio = receiver.Demodulator.Mode == RadioMode.CW ? afDisplayAudio : audio;
                _audioSpectrumPipeline.Submit(CopyAudio(spectrumAudio));
            }
            if (!_audioEnabled || receiver.OutputChannel is < 1 or > 2) return;
            if (DigitalVoicePlayback.Owns(receiver.OutputChannel - 1)) return;
            var output = _audioOutputs[receiver.OutputChannel - 1];
            if (output is null) return;
            var playback = receiver.OutputChannel == 1
                ? ApplyEnFilter(audio, receiver.Demodulator.Mode)
                : audio;
            var processed = receiver.Processor.Process(playback, Volatile.Read(ref receiver.SignalLevelDb));
            output.Write(processed);
        }
        catch (Exception exception) { trace?.RecordError(exception); }
    }

    private bool SubVfoHasDownstream(SubVfoReceiver receiver) =>
        receiver.OutputChannel != 0 ||
        _afPluginHost.IsVfoInUse(receiver.Id) ||
        _smartRecord.IsRecordingVfo(receiver.Id) ||
        _appSettings.AfPluginInstances.Any(instance =>
            instance.VfoId.Equals(receiver.Id, StringComparison.OrdinalIgnoreCase) &&
            _afPluginHost.IsEnabled(instance.InstanceId));

    private string SelectedAfVfoId() => Volatile.Read(ref _selectedAfVfoId);

    private static float[] CopyAudio(float[] source)
    {
        var copy = new float[source.Length];
        Buffer.BlockCopy(source, 0, copy, 0, source.Length * sizeof(float));
        return copy;
    }

    private void OnRemoteSpectrum(RemoteSpectrumFrame frame)
    {
        Interlocked.Exchange(ref _pendingRemoteSpectrumGeneration, Volatile.Read(ref _spectrumGeneration));
        Interlocked.Exchange(ref _pendingRemoteSpectrum, frame);
    }

    private void OnRemoteStatus(string status)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            BeginInvoke(() =>
            {
                _statusLabel.Text = status;
                if (_source is not IRemoteAudioSampleSource remote) return;
                if (status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
                {
                    _tunedFrequency = remote.CenterFrequency;
                    UpdateFrequencyReadout();
                    AppIcons.ApplyRxButton(_startButton, running: true);
                }
                else if (status.StartsWith("Connection failed", StringComparison.OrdinalIgnoreCase))
                {
                    AppIcons.ApplyRxButton(_startButton, running: false);
                }
            });
        }
        catch { }
    }

    private void OnSpectrumAvailable(float[] spectrum)
    {
        Interlocked.Exchange(ref _pendingSpectrumGeneration, Volatile.Read(ref _spectrumGeneration));
        Interlocked.Exchange(ref _pendingSpectrum, spectrum);
        _lastRfSpectrum = spectrum;
        _lastRfSpectrumSampleRate = _source.SampleRate;
        _lastRfSpectrumCenter = Interlocked.Read(ref _rfCenterFrequency);
        Interlocked.Increment(ref _rfSpectrumFrame);
        _scanSpectrum = spectrum;
        _scanSpectrumCenter = _lastRfSpectrumCenter;
        _scanSpectrumSpan = _lastRfSpectrumSampleRate;
        var source = _source;
        var sampleRate = source.SampleRate;
        var center = Interlocked.Read(ref _rfCenterFrequency);
        var tuned = Interlocked.Read(ref _tunedFrequency) + Interlocked.Read(ref _afcOffsetHz);
        var bandwidth = _demodulator.Bandwidth;
        var (low, high) = RadioModes.FilterRange(_demodulator.Mode, tuned, bandwidth, _demodulator.SsbLower);
        var captureLeft = center - sampleRate / 2d;
        var first = Math.Clamp((int)Math.Floor((low - captureLeft) * spectrum.Length / sampleRate), 0, spectrum.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((high - captureLeft) * spectrum.Length / sampleRate), first, spectrum.Length - 1);
        var peak = -140f;
        for (var index = first; index <= last; index++) peak = Math.Max(peak, spectrum[index]);
        Volatile.Write(ref _signalLevelDb, peak);
        UpdateChannelOccupancy(spectrum, center, sampleRate);
        if (_demodulator.Mode == RadioMode.WFM)
            _demodulator.StereoCarrierDb = peak;
        foreach (var receiver in Volatile.Read(ref _subVfoReceivers))
            Volatile.Write(ref receiver.SignalLevelDb, SignalPeak(spectrum, center, sampleRate,
                receiver.Frequency, receiver.Mode, receiver.Bandwidth));
        _afPluginHost.PublishRfSpectrum(spectrum, center, sampleRate, tuned, bandwidth);
        PublishRemoteSpectrum(spectrum);
    }

    private void UpdateChannelOccupancy(float[] spectrum, long centerHz, int spanHz)
    {
        if (spectrum.Length == 0 || spanHz <= 0) return;
        var tuned = Interlocked.Read(ref _tunedFrequency);
        var bandwidth = Math.Max(50, _demodulator.Bandwidth);
        var (low, high) = RadioModes.FilterRange(_demodulator.Mode, tuned, bandwidth, _demodulator.SsbLower);
        var captureLeft = centerHz - spanHz / 2d;
        var first = Math.Clamp((int)Math.Floor((low - captureLeft) * spectrum.Length / spanHz), 0, spectrum.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((high - captureLeft) * spectrum.Length / spanHz), first, spectrum.Length - 1);
        var occupancy = SpectrumOccupancy.Evaluate(spectrum, first, last, _appSettings.SatelliteHandoffSnrDb);
        Volatile.Write(ref _channelSnrDb, occupancy.SnrDb);
        Volatile.Write(ref _channelOccupiedFlag, occupancy.Occupied ? 1 : 0);
    }

    private static float SignalPeak(float[] spectrum, long center, int sampleRate, long frequency, RadioMode mode, int bandwidth)
    {
        var (low, high) = RadioModes.FilterRange(mode, frequency, bandwidth);
        var captureLeft = center - sampleRate / 2d;
        var first = Math.Clamp((int)Math.Floor((low - captureLeft) * spectrum.Length / sampleRate), 0, spectrum.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((high - captureLeft) * spectrum.Length / sampleRate), first, spectrum.Length - 1);
        var peak = -140f;
        for (var index = first; index <= last; index++) peak = Math.Max(peak, spectrum[index]);
        return peak;
    }

    private void ApplyAdsbFrequencyPreset(NeuroSDRPreset preset)
    {
        var tuneHz = Math.Clamp(preset.DialFrequencyHz, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        if (_modeBox.Items.Contains(preset.Mode.ToString())) _modeBox.SelectedItem = preset.Mode.ToString();
        _bandwidthBox.Value = Math.Clamp(preset.BandwidthHz, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
        ChangeVfoFromDigitalDisplay(tuneHz);
        CenterSpectrumOnFrequency(tuneHz);
    }

    private async void SelectSource()
    {
        if (_rxSourceBox.SelectedIndex < 0 || _rxSourceBox.SelectedIndex >= _sources.Count) return;
        await SelectSourceCoreAsync(_sources[_rxSourceBox.SelectedIndex]).ConfigureAwait(true);
    }

    private async Task SelectSourceCoreAsync(ISampleSource selected, string? remoteUrlOverride = null)
    {
        if (selected is IRemoteAudioSampleSource sameRemote && ReferenceEquals(selected, _source))
        {
            if (!string.IsNullOrWhiteSpace(remoteUrlOverride) &&
                !sameRemote.ServerUrl.Trim().TrimEnd('/').Equals(remoteUrlOverride.Trim().TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                RememberDisplayLevels();
                sameRemote.ServerUrl = remoteUrlOverride.Trim();
                _webUrlInput.Text = sameRemote.ServerUrl;
                SaveRemoteUrl(sameRemote);
                ApplyDisplayLevelsForCurrentSource();
                UpdateSourceCapabilities();
                ResetRfSpectrum();
            }
            return;
        }
        if (ReferenceEquals(selected, _source)) return;
        RememberDisplayLevels();
        var previousFrequency = _tunedFrequency;
        StopMemoryScan();
        StopIqRecording();
        if (_source.IsRunning) await StopReceiverCoreAsync().ConfigureAwait(true);
        DetachSource(_source);
        _source = selected;
        _listedSiteProtocol = null;
        if (selected is IRemoteAudioSampleSource remoteSource)
        {
            remoteSource.ServerUrl = !string.IsNullOrWhiteSpace(remoteUrlOverride)
                ? remoteUrlOverride.Trim()
                : RemoteUrl(remoteSource);
            _webUrlInput.Text = remoteSource.ServerUrl;
            SaveRemoteUrl(remoteSource);
            _remoteAudioResampler = null;
            _remoteAudioInputRate = 0;
            RefreshSatelliteRemoteHomes();
        }
        ApplyDisplayLevelsForCurrentSource();
        ResetRfSpectrum();
        if (selected is IFixedCenterFrequencySampleSource && selected.CenterFrequency > 0)
            _tunedFrequency = selected.CenterFrequency;
        if (selected is IRemoteAudioSampleSource)
        {
            selected.CenterFrequency = _tunedFrequency;
            // Scene apply restores Tuned/View from the snapshot after SelectSource —
            // do not wipe the saved waterfall viewport here.
            if (!_applyingRxScene)
            {
                _rfCenterFrequency = _tunedFrequency;
                _viewCenterFrequency = _tunedFrequency;
                _viewBandwidth = Math.Clamp(100_000, MinimumViewBandwidth(), selected.SampleRate);
            }
        }
        else if (!_applyingRxScene)
        {
            _rfCenterFrequency = selected is IFixedCenterFrequencySampleSource ? selected.CenterFrequency : _tunedFrequency;
            _viewCenterFrequency = _rfCenterFrequency;
            _viewBandwidth = selected.SampleRate;
        }
        Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
        if (selected is not IFixedCenterFrequencySampleSource && selected is not IRemoteAudioSampleSource) ApplyLogicalCenterToSource();
        ApplyRtlTcpEndpointFromSettings();
        AttachSource(_source);
        DiscardAfCaptions();
        ResetCwIfFrequencyChanged(previousFrequency);
        _appSettings.SourceName = _source.Name;
        _statusLabel.Text = $"Ready · {_source.Name}";
        if (selected is not IRemoteAudioSampleSource)
            RestoreLocalSatelliteObserver();
        ConfigureDisplay();
        UpdateSourceCapabilities();
        ApplyAfPluginRoutesForCurrentSource();
        if (!_applyingRxScene)
            SchedulePersistCurrentSceneLayout();
    }

    /// <summary>
    /// Stop RX without blocking the UI message pump on remote WebSocket teardown.
    /// Sync <see cref="ISampleSource.Stop"/> + mid-flight <c>Control.Invoke</c> from AF
    /// plugins deadlocks when switching WebSDR → Kiwi (debugger lands on GetResult).
    /// </summary>
    private async Task StopReceiverCoreAsync()
    {
        StopEibiAiScan();
        StopMemoryScan();
        StopIqRecording();
        StopAfRecording(promptSave: !_automatedSoak);
        LeaveRealtimeGarbageCollection();
        if (_source is IRemoteAudioSampleSource remote)
            await remote.StopConnectionAsync().ConfigureAwait(true);
        else
            _source.Stop();
        DisposeAudioOutputs();
        _mainWaterfallLive = false;
        AppIcons.ApplyRxButton(_startButton, running: false);
        _statusLabel.Text = $"Stopped · {_source.Name}";
    }

    private async void ToggleReceiver()
    {
        if (_source.IsRunning)
        {
            await StopReceiverCoreAsync().ConfigureAwait(true);
            return;
        }

        await StartReceiverAsync().ConfigureAwait(true);
    }

    private async Task StartReceiverAsync()
    {
        if (_source.IsRunning) return;
        try
        {
            if (_source is IRemoteAudioSampleSource remote)
            {
                await ConnectRemoteAsync(remote).ConfigureAwait(true);
                return;
            }

            if (_source is not IFixedCenterFrequencySampleSource) ApplyLogicalCenterToSource();
            ApplyUserRfGain();
            ApplySourceHardwareSettings();
            if (_audioCheck.Checked)
            {
                try
                {
                    OpenAudioOutputs();
                    _demodulator.Reset();
                    foreach (var processor in _audioProcessors) processor.Reset();
                    foreach (var receiver in Volatile.Read(ref _subVfoReceivers))
                    {
                        receiver.Demodulator.Reset();
                        receiver.Processor.Reset();
                    }
                }
                catch (Exception audioException)
                {
                    DisposeAudioOutputs();
                    _pipelineTrace?.RecordError(audioException);
                    _statusLabel.Text = $"Audio disabled: {audioException.Message}";
                }
            }
            _source.Start();
            RuntimeWarmup.StartIfNeeded();
            EnterRealtimeGarbageCollection();
            SnapshotPipelineTrace("START");
            _uiTimer.Start();
            AppIcons.ApplyRxButton(_startButton, running: true);
            if (IsSatelliteSceneActive)
                _ = ApplySatelliteTrackingAsync();
        }
        catch (Exception exception)
        {
            DisposeAudioOutputs();
            _statusLabel.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "Failed to Start RX", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EnterRealtimeGarbageCollection()
    {
        _previousGcLatency = GCSettings.LatencyMode;
        _gcLatencyTimer?.Stop();
        _gcLatencyTimer?.Dispose();
        _gcLatencyTimer = new System.Windows.Forms.Timer { Interval = 45_000 };
        _gcLatencyTimer.Tick += (_, _) =>
        {
            _gcLatencyTimer?.Stop();
            try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; }
            catch { /* Server GC does not support SustainedLowLatency. */ }
        };
        _gcLatencyTimer.Start();
    }

    private void LeaveRealtimeGarbageCollection()
    {
        _gcLatencyTimer?.Stop();
        _gcLatencyTimer?.Dispose();
        _gcLatencyTimer = null;
        try { GCSettings.LatencyMode = _previousGcLatency; } catch { }
    }

    private void OpenAudioOutputs()
    {
        DisposeAudioOutputs();
        var channels = new[] { _appSettings.Audio1, _appSettings.Audio2 };
        Exception? lastError = null;
        for (var index = 0; index < channels.Length; index++)
        {
            var channel = channels[index];
            if (!channel.Enabled) continue;
            var namedDevice = string.IsNullOrWhiteSpace(channel.DeviceName) ? null :
                _audioDevices.FirstOrDefault(device => device.Id >= 0 &&
                    device.Name.Equals(channel.DeviceName, StringComparison.OrdinalIgnoreCase));
            var deviceId = namedDevice?.Id ??
                (_audioDevices.Any(device => device.Id == channel.DeviceId) ? channel.DeviceId : -1);
            try
            {
                // Multiple demodulators make delivery slightly burstier even though
                // their long-term PCM rate remains 48 kHz. Give waveOut enough room
                // to absorb that burst instead of dropping whole 20 ms audio blocks.
                var remote = _source is IRemoteAudioSampleSource;
                var jitterBuffers = remote ? 96
                    : _source.SampleRate >= 15_000_000 ? 128
                    : Volatile.Read(ref _subVfoReceivers).Length > 0 ? 64 : 40;
                var preroll = remote ? 30 : jitterBuffers >= 64 ? 14 : 10;
                var waveChannels = index == 0 &&
                    _demodulator.Mode == RadioMode.WFM &&
                    _appSettings.WfmStereoEnabled ? 2 : 1;
                try
                {
                    _audioOutputs[index] = new WaveOutPlayer(AudioDemodulator.AudioSampleRate, deviceId, jitterBuffers,
                        prerollBuffers: preroll, restartOnStarvation: !remote, channels: waveChannels)
                    {
                        VolumePercent = channel.Volume
                    };
                }
                catch when (deviceId != -1)
                {
                    _audioOutputs[index] = new WaveOutPlayer(AudioDemodulator.AudioSampleRate, -1, jitterBuffers,
                        prerollBuffers: preroll, channels: waveChannels)
                    {
                        VolumePercent = channel.Volume
                    };
                }
            }
            catch (Exception exception) { lastError = exception; }
        }
        if (_audioOutputs.All(output => output is null) && lastError is not null) throw lastError;
    }

    private void DisposeAudioOutputs()
    {
        for (var index = 0; index < _audioOutputs.Length; index++)
        {
            var output = Interlocked.Exchange(ref _audioOutputs[index], null);
            output?.Dispose();
        }
    }

    private void ToggleIqRecording()
    {
        if (_iqRecorder is not null)
        {
            StopIqRecording();
            return;
        }
        if (!_source.IsRunning)
        {
            MessageBox.Show(this, "Start receiving before starting IQ recording.", "IQ Recording", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "NeuroSDR IQ WAV (*.wav)|*.wav",
            DefaultExt = "wav",
            AddExtension = true,
            FileName = $"NeuroSDR_{_rfCenterFrequency}_{DateTime.Now:yyyyMMdd_HHmmss}.wav"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _iqRecorder = new IqWaveRecorder(dialog.FileName, _source.SampleRate, _rfCenterFrequency, EffectiveDemodFrequency);
            AppIcons.ApplyRecordButton(_recordButton, recording: true);
            _recordButton.BackColor = Color.FromArgb(190, 61, 70);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Failed to Start IQ Recording", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopIqRecording()
    {
        var recorder = Interlocked.Exchange(ref _iqRecorder, null);
        if (recorder is null) return;
        recorder.Dispose();
        AppIcons.ApplyRecordButton(_recordButton, recording: false);
        _recordButton.BackColor = Color.FromArgb(118, 55, 64);
        _statusLabel.Text = recorder.Error is null
            ? $"IQ recording complete · {recorder.WrittenSamples:N0} samples · {Path.GetFileName(recorder.Path)}"
            : $"IQ recording error · {recorder.Error}";
    }

    private void OpenIqWave()
    {
        using var dialog = new OpenFileDialog { Filter = "NeuroSDR IQ WAV (*.wav)|*.wav|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!IqWaveFileSampleSource.TryOpen(dialog.FileName, out var source, out var status) || source is null)
        {
            MessageBox.Show(this, status, "Failed to Open IQ WAV", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (_source.IsRunning) ToggleReceiver();
        _sources.Add(source);
        FillRxSourceBox();
        _rxSourceBox.SelectedIndex = _sources.Count - 1;
        _statusLabel.Text = status;
    }

    private FlowLayoutPanel? _afRecordRow;
    private Button? _afRecordButton;
    private Label? _afRecordTimeLabel;
    private Button? _smartRecordButton;

    private static readonly Color AfRecordIdleColor = Color.FromArgb(31, 112, 153);
    private static readonly Color AfRecordBlinkOnColor = Color.FromArgb(220, 48, 58);
    private static readonly Color AfRecordBlinkOffColor = Color.FromArgb(88, 32, 40);

    private void EnsureAfRecordButton()
    {
        if (_afRecordButton is not null && !_afRecordButton.IsDisposed) return;
        _afRecordRow = new FlowLayoutPanel
        {
            Width = _recordButton.Width,
            Height = 30,
            Margin = AppIcons.SidebarMargin,
            Padding = Padding.Empty,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.FromArgb(20, 27, 37)
        };
        _afRecordButton = new Button
        {
            Text = "Record",
            Width = _recordButton.Width,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = AfRecordIdleColor,
            ForeColor = Color.FromArgb(225, 233, 241),
            Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _afRecordButton.FlatAppearance.BorderColor = Color.FromArgb(75, 92, 111);
        AppIcons.ApplyAfRecordButton(_afRecordButton, recording: false);
        _afRecordButton.Click += (_, _) => ToggleAfRecording();
        _afRecordTimeLabel = new Label
        {
            AutoSize = false,
            Width = 70,
            Height = 30,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(255, 120, 128),
            Font = new Font("Consolas", 9f, FontStyle.Bold),
            Margin = new Padding(4, 0, 0, 0),
            Visible = false
        };
        _afRecordRow.Controls.Add(_afRecordButton);
        _afRecordRow.Controls.Add(_afRecordTimeLabel);
        var parent = _recordButton.Parent;
        if (parent is null) return;
        parent.Controls.Add(_afRecordRow);
        parent.Controls.SetChildIndex(_afRecordRow, parent.Controls.GetChildIndex(_recordButton) + 1);
    }

    private void EnsureSmartRecordButton()
    {
        if (_smartRecordButton is not null && !_smartRecordButton.IsDisposed) return;
        _smartRecordButton = new Button
        {
            Text = "Smart Record",
            Width = _recordButton.Width,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 66, 83),
            ForeColor = Color.FromArgb(225, 233, 241),
            Margin = AppIcons.SidebarMargin,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _smartRecordButton.FlatAppearance.BorderColor = Color.FromArgb(75, 92, 111);
        AppIcons.ApplySmartRecordButton(_smartRecordButton, active: false);
        _smartRecordButton.Click += (_, _) => ShowSmartRecord();
        var parent = _recordButton.Parent;
        if (parent is null) return;
        parent.Controls.Add(_smartRecordButton);
        var after = _afRecordRow is not null && !_afRecordRow.IsDisposed
            ? (Control)_afRecordRow
            : _recordButton;
        parent.Controls.SetChildIndex(_smartRecordButton, parent.Controls.GetChildIndex(after) + 1);
    }

    private void ToggleAfRecording()
    {
        if (_afRecorder is not null)
        {
            StopAfRecording(promptSave: true);
            return;
        }

        if (!_source.IsRunning)
        {
            MessageBox.Show(this, "Start receiving before starting recording.", "Record",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "NeuroSDR");
            Directory.CreateDirectory(folder);
            var tempPath = Path.Combine(folder, $"af-{Guid.NewGuid():N}.wav");
            _afRecorder = new AfStreamWavRecorder(tempPath, AudioDemodulator.AudioSampleRate, "main");
            _afRecordStartedTick = Environment.TickCount64;
            _afRecordBlinkOn = true;
            _nextAfRecordBlinkTick = _afRecordStartedTick + 400;
            ApplyAfRecordButton(recording: true);
            TickAfRecordUi();
            _statusLabel.Text = "Recording AF";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Failed to Start Recording", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ApplyAfRecordButton(bool recording)
    {
        if (_afRecordButton is null || _afRecordButton.IsDisposed) return;
        AppIcons.ApplyAfRecordButton(_afRecordButton, recording);
        _afRecordButton.Width = recording ? _recordButton.Width - 74 : _recordButton.Width;
        _afRecordButton.BackColor = recording ? AfRecordBlinkOnColor : AfRecordIdleColor;
        _afRecordButton.ForeColor = Color.FromArgb(225, 233, 241);
        _afRecordButton.FlatAppearance.BorderColor = recording
            ? Color.FromArgb(255, 90, 98)
            : Color.FromArgb(75, 92, 111);
        if (_afRecordTimeLabel is not null && !_afRecordTimeLabel.IsDisposed)
        {
            _afRecordTimeLabel.Visible = recording;
            if (!recording) _afRecordTimeLabel.Text = string.Empty;
        }
    }

    private void TickAfRecordUi()
    {
        if (_afRecorder is null || _afRecordButton is null || _afRecordButton.IsDisposed) return;
        var now = Environment.TickCount64;
        if (now >= _nextAfRecordBlinkTick)
        {
            _nextAfRecordBlinkTick = now + 400;
            _afRecordBlinkOn = !_afRecordBlinkOn;
            _afRecordButton.BackColor = _afRecordBlinkOn ? AfRecordBlinkOnColor : AfRecordBlinkOffColor;
            _afRecordButton.ForeColor = _afRecordBlinkOn
                ? Color.FromArgb(255, 240, 242)
                : Color.FromArgb(180, 150, 154);
        }

        if (_afRecordTimeLabel is null || _afRecordTimeLabel.IsDisposed) return;
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, now - _afRecordStartedTick));
        _afRecordTimeLabel.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void StopAfRecording(bool promptSave)
    {
        var recorder = Interlocked.Exchange(ref _afRecorder, null);
        if (recorder is null) return;
        var path = recorder.Path;
        recorder.Dispose();
        ApplyAfRecordButton(recording: false);

        var size = File.Exists(path) ? new FileInfo(path).Length : 0L;
        if (!promptSave || size <= 44)
        {
            TryDeleteFile(path);
            if (promptSave) _statusLabel.Text = "No audio to save";
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Save Recording",
            Filter = "WAV (*.wav)|*.wav",
            DefaultExt = "wav",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"neurosdr-af-{DateTime.Now:yyyyMMdd-HHmmss}.wav",
            InitialDirectory = DefaultAfRecordFolder()
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            TryDeleteFile(path);
            _statusLabel.Text = "Save canceled";
            return;
        }

        try
        {
            SaveAfRecording(path, dialog.FileName);
            _statusLabel.Text = $"Recording saved · {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            TryDeleteFile(path);
            MessageBox.Show(this, exception.Message, "Failed to Save Recording", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void SaveAfRecording(string tempPath, string destination)
    {
        var destDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destDir)) Directory.CreateDirectory(destDir);
        if (string.Equals(Path.GetFullPath(tempPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return;
        if (File.Exists(destination)) File.Delete(destination);
        try
        {
            File.Move(tempPath, destination);
        }
        catch (IOException)
        {
            File.Copy(tempPath, destination, overwrite: true);
            TryDeleteFile(tempPath);
        }
    }

    private static string DefaultAfRecordFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeuroSDR");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private void ConfigureSmartRecord()
    {
        _appSettings.SmartRecordJobs ??= [];
        if (string.IsNullOrWhiteSpace(_appSettings.SmartRecordOutputFolder))
            _appSettings.SmartRecordOutputFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeuroSDR", "SmartRecord");
        _smartRecord.Configure(_appSettings.SmartRecordJobs, _appSettings.SmartRecordOutputFolder);
    }

    private void ShowSmartRecord()
    {
        var originalJobs = _appSettings.SmartRecordJobs.Select(job => job.Clone()).ToList();
        var originalFolder = _appSettings.SmartRecordOutputFolder;
        using var form = new SmartRecordForm(_appSettings, () =>
        {
            var list = new List<(string Id, string Name)> { ("main", "MAIN VFO") };
            foreach (var sub in _appSettings.SubVfos)
                list.Add((sub.Id, sub.Name));
            return list;
        });
        form.JobsChanged += () =>
        {
            _appSettings.SmartRecordJobs = form.ResultJobs;
            _appSettings.SmartRecordOutputFolder = form.ResultOutputFolder;
            ConfigureSmartRecord();
            UpdateSmartRecordButton();
        };
        var result = form.ShowDialog(this);
        if (result == DialogResult.OK || form.Committed)
        {
            _appSettings.SmartRecordJobs = form.ResultJobs;
            _appSettings.SmartRecordOutputFolder = form.ResultOutputFolder;
            ConfigureSmartRecord();
            SaveSettings();
            _statusLabel.Text = $"Smart Record · {_appSettings.SmartRecordJobs.Count(j => j.Enabled)} enabled job(s)";
        }
        else
        {
            _appSettings.SmartRecordJobs = originalJobs;
            _appSettings.SmartRecordOutputFolder = originalFolder;
            ConfigureSmartRecord();
        }
        UpdateSmartRecordButton();
    }

    private void UpdateSmartRecordButton()
    {
        if (_smartRecordButton is null || _smartRecordButton.IsDisposed) return;
        var active = _smartRecord.ActiveAudioCount + _smartRecord.ActiveReportCount;
        var enabled = _appSettings.SmartRecordJobs.Count(j => j.Enabled);
        _smartRecordButton.Text = active > 0
            ? $"Smart Record · REC {active}"
            : enabled > 0 ? $"Smart Record · {enabled}" : "Smart Record";
        _smartRecordButton.BackColor = active > 0
            ? Color.FromArgb(190, 61, 70)
            : Color.FromArgb(35, 66, 83);
        AppIcons.ApplySmartRecordButton(_smartRecordButton, active > 0);
    }

    private void TickSmartRecord()
    {
        try
        {
            _smartRecord.Tick(DateTime.Now, EnsureSmartRecordVfo, ApplySmartRecordTune);
            ProcessSmartRecordPileup();
            UpdateSmartRecordButton();
            if (!_source.IsRunning &&
                _appSettings.SmartRecordJobs.Any(job => job.IsScheduledNow(DateTime.Now)))
                _statusLabel.Text = "Smart Record · start RX to record";
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "Smart Record · " + exception.Message;
        }
    }

    private bool EnsureSmartRecordVfo(string vfoId)
    {
        if (vfoId.Equals("main", StringComparison.OrdinalIgnoreCase))
            return _source.IsRunning;
        return _source.IsRunning &&
               _appSettings.SubVfos.Any(s => s.Id.Equals(vfoId, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplySmartRecordTune(SmartRecordJob job)
    {
        if (job.TuneFrequencyHz <= 0 && job.TuneMode is null && job.TuneBandwidthHz <= 0) return;
        if (job.VfoId.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            if (job.TuneFrequencyHz > 0)
                ChangeVfoFromDigitalDisplay(job.TuneFrequencyHz);
            if (job.TuneMode is RadioMode mode)
            {
                _suppressModeDefaults = true;
                try
                {
                    if (_modeBox.Items.Contains(mode.ToString()))
                        _modeBox.SelectedItem = mode.ToString();
                    _demodulator.Mode = mode;
                }
                finally { _suppressModeDefaults = false; }
            }
            if (job.TuneBandwidthHz > 0)
            {
                _bandwidthBox.Value = Math.Clamp(job.TuneBandwidthHz, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
                _demodulator.Bandwidth = (int)_bandwidthBox.Value;
            }
            ConfigureDisplay();
            ConfigureAfDisplay();
            return;
        }

        var sub = _appSettings.SubVfos.FirstOrDefault(s => s.Id.Equals(job.VfoId, StringComparison.OrdinalIgnoreCase));
        if (sub is null) return;
        if (job.TuneFrequencyHz > 0) sub.Frequency = job.TuneFrequencyHz;
        if (job.TuneMode is RadioMode subMode) sub.Mode = subMode;
        if (job.TuneBandwidthHz > 0) sub.Bandwidth = job.TuneBandwidthHz;
        var receiver = Volatile.Read(ref _subVfoReceivers)
            .FirstOrDefault(r => r.Id.Equals(sub.Id, StringComparison.OrdinalIgnoreCase));
        if (receiver is not null)
        {
            receiver.Frequency = sub.Frequency;
            // Mode/BW need rebuild for demodulator fields that are init-only style.
        }
        RebuildSubVfoReceivers();
    }

    private void ProcessSmartRecordPileup()
    {
        var spectrum = _lastRfSpectrum;
        if (spectrum is null || spectrum.Length < 8) return;
        var center = _lastRfSpectrumCenter != 0 ? _lastRfSpectrumCenter : Interlocked.Read(ref _rfCenterFrequency);
        var sampleRate = _lastRfSpectrumSampleRate > 0 ? _lastRfSpectrumSampleRate : _source.SampleRate;
        var now = Environment.TickCount64;

        foreach (var job in _appSettings.SmartRecordJobs.Where(j =>
                     j.Kind == SmartRecordKind.Audio && j.PileupFollowEnabled && _smartRecord.IsRecordingVfo(j.VfoId)))
        {
            var standby = job.TuneFrequencyHz > 0
                ? job.TuneFrequencyHz
                : (job.VfoId.Equals("main", StringComparison.OrdinalIgnoreCase)
                    ? _tunedFrequency
                    : _appSettings.SubVfos.FirstOrDefault(s => s.Id.Equals(job.VfoId, StringComparison.OrdinalIgnoreCase))?.Frequency ?? 0);
            if (standby <= 0) continue;
            var minHz = standby - Math.Max(1_000, job.PileupRangeHz);
            var maxHz = standby + Math.Max(1_000, job.PileupRangeHz);
            FindPeakInRange(spectrum, center, sampleRate, minHz, maxHz, out var peakHz, out var peakDb);
            var state = _smartRecord.GetPileupState(job.Id);
            if (state.Standby <= 0) state.Standby = standby;

            if (peakDb >= job.PileupTriggerLevelDb && peakHz > 0)
            {
                state.Following = true;
                state.HoldUntil = now + 1_500;
                ApplySmartRecordPileupFrequency(job.VfoId, peakHz);
            }
            else if (state.Following && now >= state.HoldUntil && peakDb <= job.PileupReleaseLevelDb)
            {
                state.Following = false;
                ApplySmartRecordPileupFrequency(job.VfoId, state.Standby);
            }
            _smartRecord.NotePileupState(job.Id, state.Following, state.HoldUntil, state.Standby);
        }
    }

    private void ApplySmartRecordPileupFrequency(string vfoId, long frequencyHz)
    {
        if (vfoId.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            if (Math.Abs(frequencyHz - _tunedFrequency) > 250)
                ChangeVfoFromDigitalDisplay(frequencyHz);
            return;
        }
        var sub = _appSettings.SubVfos.FirstOrDefault(s => s.Id.Equals(vfoId, StringComparison.OrdinalIgnoreCase));
        if (sub is null) return;
        if (Math.Abs(sub.Frequency - frequencyHz) <= 250) return;
        sub.Frequency = frequencyHz;
        var receiver = Volatile.Read(ref _subVfoReceivers)
            .FirstOrDefault(r => r.Id.Equals(sub.Id, StringComparison.OrdinalIgnoreCase));
        if (receiver is not null) receiver.Frequency = frequencyHz;
        _lastSubVfoRangeSignature = "";
        _display.SetSubVfoMarkers(_source is IRemoteAudioSampleSource
            ? []
            : _appSettings.SubVfos.Select(item => (item.Frequency, item.Name)));
    }

    private long _nextEibiWaterfallTick;
    private bool _eibiCacheTried;
    private bool _applyingDisplayLevels;
    private bool _mainWaterfallLive;

    private void TickEibiWaterfall() => RefreshEibiWaterfall(force: false);

    private void NoteMainWaterfallFrame()
    {
        if (_mainWaterfallLive || !_source.IsRunning) return;
        _mainWaterfallLive = true;
        RefreshEibiWaterfall(force: true);
    }

    private bool ShortwaveScheduleTabPresent() =>
        _afPluginTabs.Values.Any(binding =>
            binding.PluginId.Equals(EibiBroadcastAfPlugin.PluginId, StringComparison.OrdinalIgnoreCase)) ||
        _appSettings.AfPluginInstances.Any(instance =>
            instance.PluginId.Equals(EibiBroadcastAfPlugin.PluginId, StringComparison.OrdinalIgnoreCase));

    private bool EibiWantsMainWaterfall()
    {
        // Flags follow the Shortwave tab existing in the AF pane — not which tab is
        // selected, and not whether that plugin is the active AF processor.
        if (!ShortwaveScheduleTabPresent()) return false;
        var bag = _appSettings.AfPluginUiState.GetValueOrDefault(EibiBroadcastAfPlugin.PluginId);
        if (bag is not null &&
            bag.TryGetValue("showOnMainWaterfall", out var flag) &&
            bool.TryParse(flag, out var on))
            return on;
        return _appSettings.EibiShowOnMainWaterfall;
    }

    private void RefreshEibiWaterfall(bool force)
    {
        var show = EibiWantsMainWaterfall();
        if (!show)
        {
            _display.SetBroadcastMarkers([]);
            return;
        }
        // Keep last labels when RX/waterfall is idle. Only paint while the waterfall is live.
        if (!_source.IsRunning || !_mainWaterfallLive)
            return;
        var now = Environment.TickCount64;
        if (!force && now < _nextEibiWaterfallTick) return;
        _nextEibiWaterfallTick = now + (force ? 50 : 5_000);
        if (!_eibiCacheTried)
        {
            _eibiCacheTried = true;
            EibiScheduleStore.LoadCacheIfPresent();
        }
        var bag = LiveShortwaveOptions();
        var language = bag.GetValueOrDefault("language") ?? "All";
        var country = bag.GetValueOrDefault("country") ?? "All";
        var search = bag.GetValueOrDefault("search") ?? "";
        var onAirOnly = !bag.TryGetValue("onAirOnly", out var air) ||
                        !bool.TryParse(air, out var onAir) ||
                        onAir;
        EibiBroadcastPluginView.BandRange(bag.GetValueOrDefault("band") ?? "Shortwave", out var minHz, out var maxHz);
        var viewLeft = _viewCenterFrequency - _viewBandwidth / 2L;
        var viewRight = _viewCenterFrequency + _viewBandwidth / 2L;
        if (minHz <= 0 || viewLeft > minHz) minHz = viewLeft;
        if (maxHz <= 0 || viewRight < maxHz) maxHz = viewRight;
        if (viewRight <= viewLeft)
        {
            _display.SetBroadcastMarkers([]);
            return;
        }
        var utc = DateTime.UtcNow;
        var overlay = EibiScheduleStore.Filter(language, country, search, onAirOnly, utc, minHz, maxHz)
            .GroupBy(entry => entry.FrequencyHz)
            .OrderBy(group => group.Key)
            .Take(120)
            .Select(group => (group.Key, group.First().Station));
        _display.SetBroadcastMarkers(overlay);
    }

    private Dictionary<string, string> LiveShortwaveOptions()
    {
        var live = _afPluginTabs.Values
            .Select(item => item.PluginView)
            .OfType<EibiBroadcastPluginView>()
            .FirstOrDefault();
        if (live is not null) return live.BuildOptions();
        return _appSettings.AfPluginUiState.GetValueOrDefault(EibiBroadcastAfPlugin.PluginId)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void TuneToShortwaveBroadcast(long frequencyHz, string? station = null, bool centerWaterfall = true,
        bool refreshChrome = true)
    {
        if (frequencyHz <= 0) return;
        ChangeVfoFromDigitalDisplay(frequencyHz, userInitiated: true, keepSpectrumView: !centerWaterfall);
        _suppressModeDefaults = true;
        try
        {
            if (_modeBox.Items.Contains(RadioMode.AM.ToString()))
                _modeBox.SelectedItem = RadioMode.AM.ToString();
            _demodulator.Mode = RadioMode.AM;
            _bandwidthBox.Value = Math.Clamp(10_000, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
            _demodulator.Bandwidth = (int)_bandwidthBox.Value;
        }
        finally { _suppressModeDefaults = false; }
        if (centerWaterfall)
            CenterViewOnVfo();
        if (refreshChrome)
        {
            ConfigureDisplay();
            ConfigureAfDisplay();
            UpdateRxPanel();
            RefreshEibiWaterfall(force: true);
        }
        _statusLabel.Text = string.IsNullOrWhiteSpace(station)
            ? $"Shortwave · {frequencyHz / 1_000d:0.#} kHz AM"
            : $"Shortwave · {station} · {frequencyHz / 1_000d:0.#} kHz AM";
    }

    private void SelectEibiListStation(long frequencyHz, string? station)
    {
        foreach (var binding in _afPluginTabs.Values)
        {
            if (binding.PluginView is EibiBroadcastPluginView eibi)
                eibi.SelectStation(frequencyHz, station);
        }
    }

    private void ShowMemoryPanel()
    {
        if (_memoryForm is null || _memoryForm.IsDisposed)
        {
            _memoryForm = new MemoryChannelsForm(_memoryStore,
                () => (_tunedFrequency, _demodulator.Mode, _demodulator.Bandwidth), RecallMemoryChannel);
            _memoryForm.ScanRequested += StartMemoryScan;
            _memoryForm.RangeScanRequested += StartRangeScan;
            _memoryForm.ScanStopRequested += StopMemoryScan;
        }
        if (!_memoryForm.Visible) _memoryForm.Show(this);
        _memoryForm.BringToFront();
    }

    private void StartMemoryScan(IReadOnlyList<MemoryChannel> channels, int dwellMilliseconds, bool holdOnSignal, float thresholdDb)
    {
        if (!_source.IsRunning) ToggleReceiver();
        if (!_source.IsRunning) return;
        _rangeScanner.Stop();
        _memoryForm?.SetRangeScanning(false);
        _memoryScanner.DwellMilliseconds = dwellMilliseconds;
        _memoryScanner.HoldOnSignal = holdOnSignal;
        _memoryScanner.SignalThresholdDb = thresholdDb;
        _memoryScanner.Start(channels, Environment.TickCount64);
        _scanChannel = null;
        _scanTimer.Start();
        ScanMemoryTick();
    }

    private void StartRangeScan(long startFrequency, long endFrequency, long stepFrequency, int dwellMilliseconds, bool holdOnSignal, float thresholdDb)
    {
        if (!_source.IsRunning) ToggleReceiver();
        if (!_source.IsRunning) return;
        _memoryScanner.Stop();
        _memoryForm?.SetScanning(false);
        _rangeScanner.DwellMilliseconds = dwellMilliseconds;
        _rangeScanner.HoldOnSignal = holdOnSignal;
        _rangeScanner.SignalThresholdDb = thresholdDb;
        _rangeScanner.Start(startFrequency, endFrequency, stepFrequency, Environment.TickCount64);
        _rangeScanFrequency = null;
        _scanTimer.Start();
        ScanMemoryTick();
    }

    private void StopMemoryScan()
    {
        _scanTimer.Stop();
        _memoryScanner.Stop();
        _rangeScanner.Stop();
        _scanChannel = null;
        _rangeScanFrequency = null;
        _memoryForm?.SetScanning(false);
        _memoryForm?.SetRangeScanning(false);
    }

    private void ScanMemoryTick()
    {
        if (!_memoryScanner.IsRunning && !_rangeScanner.IsRunning)
        {
            StopMemoryScan();
            return;
        }
        var signal = Volatile.Read(ref _signalLevelDb);
        if (_memoryScanner.IsRunning)
        {
            var channel = _memoryScanner.Poll(Environment.TickCount64, signal);
            if (channel is not null)
            {
                _scanChannel = channel;
                RecallMemoryChannel(channel);
            }
            _memoryForm?.UpdateScanStatus(_scanChannel, signal, _memoryScanner.IsHolding);
            return;
        }
        var frequency = _rangeScanner.Poll(Environment.TickCount64, signal);
        if (frequency is not null)
        {
            _rangeScanFrequency = frequency;
            RecallMemoryChannel(MemoryChannel.Create("RANGE", frequency.Value, _demodulator.Mode, _demodulator.Bandwidth));
        }
        _memoryForm?.UpdateRangeScanStatus(_rangeScanFrequency, signal, _rangeScanner.IsHolding);
    }

    private void RecallMemoryChannel(MemoryChannel channel)
    {
        var previousFrequency = _tunedFrequency;
        if (_modeBox.Items.Contains(channel.Mode.ToString())) _modeBox.SelectedItem = channel.Mode.ToString();
        _bandwidthBox.Value = Math.Clamp(channel.Bandwidth, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
        _tunedFrequency = ConstrainFrequencyForCurrentSource(channel.Frequency);
        if (_source is IFixedCenterFrequencySampleSource)
        {
            _tunedFrequency = Math.Clamp(_tunedFrequency, _rfCenterFrequency - _source.SampleRate * 45L / 100, _rfCenterFrequency + _source.SampleRate * 45L / 100);
        }
        else if (Math.Abs(_tunedFrequency - _rfCenterFrequency) > _source.SampleRate * .45)
        {
            _rfCenterFrequency = _tunedFrequency;
            _viewCenterFrequency = _tunedFrequency;
            Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
            if (!_uiTimer.Enabled) ApplyLogicalCenterToSource();
            _spectrumPipeline.Reset();
        }
        else
        {
            _viewCenterFrequency = ClampViewCenter(_tunedFrequency, _viewBandwidth);
        }
        ResetCwIfFrequencyChanged(previousFrequency);
        UpdateTuningDisplay();
    }

    private void UpdateTuningDisplay()
    {
        ApplyDemodFrequencyOffset();
        ApplyRemoteTune();
        UpdateFrequencyReadout();
        UpdateZoomControls();
        _statusLabel.Text = _source is IRemoteAudioSampleSource remote
            ? remote.ConnectionStatus : $"{_hardwareStatus} · Current input: {_source.Name}";
        ConfigureDisplay();
        SchedulePersistCurrentSceneLayout();
    }

    private void ApplyRemoteTune()
    {
        if (_source is not IRemoteAudioSampleSource remote) return;
        remote.CenterFrequency = _tunedFrequency;
                _ = remote.ApplyReceiverAsync(RadioModes.DemodMode(_demodulator.Mode, _demodulator.SsbLower), _demodulator.Bandwidth);
    }

    private void UpdateFrequencyReadout()
    {
        if (!_frequencyInput.Focused) _frequencyInput.Text = $"{FormatFrequencyMHz(_tunedFrequency)} MHz";
        _centerLabel.Text = _source is IRemoteAudioSampleSource remote
            ? $"REMOTE AF · {remote.AudioSampleRate / 1_000d:0.#} kHz"
            : $"RF center  {FormatFrequencyMHz(_rfCenterFrequency)} MHz";
        var main = _appSettings.MainAutoTune;
        var showAdjust = _mainAutoFollowing && main is { Enabled: true };
        if (showAdjust)
        {
            var home = main!.StandbyFrequency > 0 ? main.StandbyFrequency : _tunedFrequency;
            _afFrequencyLabel.Frequency = home;
            _afFrequencyLabel.SetAdjustFrequency(_tunedFrequency);
        }
        else
        {
            _afFrequencyLabel.ClearAdjustFrequency();
            _afFrequencyLabel.Frequency = _tunedFrequency;
        }
        UpdateAfDisplayVfoInfo();
        _afModeLabel.Text = $"{_demodulator.Mode}  ·  BW {_demodulator.Bandwidth:N0} Hz";
    }

    private static string FormatFrequencyMHz(long frequency) => frequency >= 1_000_000_000
        ? (frequency / 1_000_000d).ToString("0000.00000")
        : (frequency / 1_000_000d).ToString("000.000000");

    private long DeviceCenterFrequency(long logicalFrequency) =>
        RadioLimits.ToDeviceFrequency(logicalFrequency, _appSettings.TunerFrequencyOffsetHz);

    private long LogicalCenterFrequency(long deviceFrequency) =>
        RadioLimits.ToLogicalFrequency(deviceFrequency, _appSettings.TunerFrequencyOffsetHz);

    private void ApplyLogicalCenterToSource()
    {
        if (_source is IFixedCenterFrequencySampleSource) return;
        if (_source is IRemoteAudioSampleSource)
        {
            _source.CenterFrequency = _tunedFrequency;
            _rfCenterFrequency = _source.CenterFrequency;
        }
        else
        {
            _source.CenterFrequency = DeviceCenterFrequency(_rfCenterFrequency);
            _rfCenterFrequency = LogicalCenterFrequency(_source.CenterFrequency);
        }
        Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
        ApplyDemodFrequencyOffset();
    }

    private void RenderPendingSpectrum()
    {
        var remote = Interlocked.Exchange(ref _pendingRemoteSpectrum, null);
        if (remote is not null &&
            Volatile.Read(ref _pendingRemoteSpectrumGeneration) != Volatile.Read(ref _spectrumGeneration))
            remote = null;
        if (remote is not null && _source is IRemoteAudioSampleSource)
        {
            // Rows already in flight still describe the previous server viewport.
            // During a center drag, keeping the interaction's requested center is
            // essential: adopting an old row here changes the delta origin and
            // produces the accelerating/reversing frequency symptom.
            if (_centerDragActive)
                return;
            if (_restoreRemoteViewOnConnect)
            {
                // Keep pre-Start start/end until ApplyRemoteSpectrumViewOnConnect finishes.
            }
            else if (_awaitingRemoteViewport &&
                     RemoteViewportGate.IsStaleFrame(
                         remote.CenterFrequency, _awaitingRemoteViewportCenter, remote.SpanHz, _awaitingRemoteViewportSpan))
            {
                // Drop stale rows so a zoom/pan cannot snap back to the previous window.
                return;
            }
            else if (!_remoteSpectrumInitialized)
            {
                _remoteSpectrumInitialized = true;
                _viewBandwidth = Math.Max(MinimumViewBandwidth(), remote.SpanHz);
                _viewCenterFrequency = ClampViewCenter(remote.CenterFrequency, _viewBandwidth);
                _rfCenterFrequency = remote.CenterFrequency;
            }
            else if (!_awaitingRemoteViewport)
            {
                _rfCenterFrequency = remote.CenterFrequency;
                _viewBandwidth = Math.Max(MinimumViewBandwidth(), remote.SpanHz);
                _viewCenterFrequency = remote.CenterFrequency;
            }
            var remoteSpectrumDb = new float[remote.Intensities.Length];
            for (var index = 0; index < remoteSpectrumDb.Length; index++)
                remoteSpectrumDb[index] = -130f + remote.Intensities[index] * (130f / 255f);
            if (remoteSpectrumDb.Length > 0) Volatile.Write(ref _signalLevelDb, remoteSpectrumDb.Max());
            UpdateChannelOccupancy(remoteSpectrumDb, remote.CenterFrequency, remote.SpanHz);
            _scanSpectrum = remoteSpectrumDb;
            _scanSpectrumCenter = remote.CenterFrequency;
            _scanSpectrumSpan = remote.SpanHz;
            Interlocked.Increment(ref _rfSpectrumFrame);
            ConfigureDisplay();
            _display.PushSpectrum(remoteSpectrumDb);
            Interlocked.Increment(ref _renderedSpectrumFrames);
            NoteMainWaterfallFrame();
            return;
        }
        var spectrum = Interlocked.Exchange(ref _pendingSpectrum, null);
        if (spectrum is not null &&
            Volatile.Read(ref _pendingSpectrumGeneration) != Volatile.Read(ref _spectrumGeneration))
            spectrum = null;
        if (spectrum is null) return;
        _display.PushSpectrum(spectrum);
        Interlocked.Increment(ref _renderedSpectrumFrames);
        NoteMainWaterfallFrame();
    }

    private void RenderPendingAfSpectrum()
    {
        var left = Interlocked.Exchange(ref _pendingAfSpectrumLeft, null);
        var right = Interlocked.Exchange(ref _pendingAfSpectrumRight, null);
        var spectrum = Interlocked.Exchange(ref _pendingAfSpectrum, null);
        if (left is null && right is null && spectrum is null) return;
        PublishRemoteAfSpectrum(spectrum ?? left ?? right ?? []);

        if (_demodulator.Mode == RadioMode.WFM)
        {
            if (left is not null && right is not null)
                _wfmAudioVisual.PushStereoSpectrum(left, right);
            else if (spectrum is not null)
                _wfmAudioVisual.PushSpectrum(spectrum);
            _lastAfSpectrum = spectrum ?? left;
        }
        else if (spectrum is not null)
        {
            _lastAfSpectrum = spectrum;
            _afDisplay.PushSpectrum(spectrum);
        }
        else return;

        Interlocked.Increment(ref _renderedAfSpectrumFrames);
    }

    private void UpdateStereoLevelMeters(float[] interleaved)
    {
        double sumL = 0, sumR = 0;
        var frames = interleaved.Length / 2;
        if (frames <= 0) return;
        for (var i = 0; i + 1 < interleaved.Length; i += 2)
        {
            sumL += interleaved[i] * interleaved[i];
            sumR += interleaved[i + 1] * interleaved[i + 1];
        }
        Volatile.Write(ref _stereoLeftDb,
            (float)Math.Clamp(20 * Math.Log10(Math.Sqrt(sumL / frames) + 1e-9), -140, 0));
        Volatile.Write(ref _stereoRightDb,
            (float)Math.Clamp(20 * Math.Log10(Math.Sqrt(sumR / frames) + 1e-9), -140, 0));
    }

    private void ConfigureDisplay()
    {
        SyncFreedvSideband();
        _display.Configure(_rfCenterFrequency, _tunedFrequency, _source.SampleRate,
            (int)_bandwidthBox.Value, _demodulator.Mode, _demodulator.CwPitchHz, _viewCenterFrequency, _viewBandwidth,
            _source is IRemoteAudioSampleSource remote ? remote.MaximumSpectrumSpan : _source.SampleRate);
        _display.SsbLower = _demodulator.SsbLower;
        _display.SetSubVfoMarkers(_source is IRemoteAudioSampleSource
            ? []
            : _appSettings.SubVfos.Select(item => (item.Frequency, item.Name)));
        UpdateWfmStationMarkers();
        UpdateAutoTuneOverlays();
    }

    private void ConfigureAfDisplay()
    {
        var bandwidth = _demodulator.Bandwidth;
        // WFM: show music-friendly AF span; other modes keep existing rules.
        var afSpan = _demodulator.Mode == RadioMode.WFM
            ? 4_000
            : Math.Clamp(bandwidth <= 4_000 ? 3_500 : bandwidth, 3_500, 12_000);
        var cwFilter = CwEnFilterActive;
        _afDisplay.Configure(afSpan, _audioProcessors[0].AfLowCutHz, _audioProcessors[0].AfHighCutHz,
            cwFilter ? false : _afFilterCheck.Checked);
        _afDisplay.SetCwPassband(cwFilter, (int)_enFilterCwPanel.CenterHz, _enFilterCwPanel.BandwidthHz);
        if (!_wfmAudioVisual.IsDisposed)
            _wfmAudioVisual.Configure(afSpan);
        _afModeLabel.Text = $"{_demodulator.Mode}  ·  BW {bandwidth:N0} Hz";
        UpdateAfFskMarkers();
        ApplyWfmAfChrome();
    }

    private void ToggleRfDisplayMode()
    {
        _appSettings.RfDisplayMode = _appSettings.RfDisplayMode == RfDisplayMode.SpectrumWaterfall
            ? RfDisplayMode.Perspective3D
            : RfDisplayMode.SpectrumWaterfall;
        ApplyRfDisplayMode();
    }

    private void ApplyRfDisplayMode()
    {
        _display.DisplayMode = _appSettings.RfDisplayMode;
        // Button shows the mode you will switch TO (not the current view).
        _rfDisplayModeOverlayButton.Text = _appSettings.RfDisplayMode == RfDisplayMode.Perspective3D
            ? "Classic WF"
            : "3D WF";
    }

    private void UpdateAfFskMarkers()
    {
        var pluginId = SelectedAfPluginId();
        if (pluginId?.Equals("builtin.af.rtty", StringComparison.OrdinalIgnoreCase) == true &&
            _appSettings.EnabledAfPluginIds.Contains("builtin.af.rtty", StringComparer.OrdinalIgnoreCase))
        {
            _afDisplay.SetFskMarkers(true, _appSettings.RttyCenterHz, _appSettings.RttyDeviationHz);
            return;
        }
        if (SelectedAfPluginBinding()?.PluginView is IAfFskTuningView fsk &&
            fsk.TryGetFskMarkers(out var visualCenter, out var visualDeviation))
        {
            _afDisplay.SetFskMarkers(true, visualCenter, visualDeviation);
            return;
        }
        if (pluginId?.Equals("builtin.af.kiwinavtex", StringComparison.OrdinalIgnoreCase) == true &&
            _appSettings.EnabledAfPluginIds.Contains("builtin.af.kiwinavtex", StringComparer.OrdinalIgnoreCase))
        {
            _afDisplay.SetFskMarkers(true, _appSettings.KiwiNavtexCenterHz, _appSettings.KiwiNavtexDeviationHz);
            return;
        }
        if (pluginId?.Equals("builtin.af.flrtty", StringComparison.OrdinalIgnoreCase) == true &&
            _appSettings.EnabledAfPluginIds.Contains("builtin.af.flrtty", StringComparer.OrdinalIgnoreCase))
        {
            _afDisplay.SetFskMarkers(true, _appSettings.FlRttyCenterHz, _appSettings.FlRttyShiftHz / 2);
            return;
        }
        _afDisplay.SetFskMarkers(false, 0, 0);
    }

    private void ApplyAfFskMarkersFromDisplay(int centerHz, int deviationHz)
    {
        var binding = SelectedAfPluginBinding();
        if (binding?.PluginId.Equals("builtin.af.rtty", StringComparison.OrdinalIgnoreCase) == true)
        {
            _appSettings.RttyCenterHz = centerHz;
            _appSettings.RttyDeviationHz = deviationHz;
            (binding.PluginView as RttyPluginView)?.SetFskTuning(centerHz, deviationHz);
            ConfigureRttyPlugin();
            return;
        }
        if (binding?.PluginView is IAfFskTuningView fsk)
        {
            fsk.SetFskTuning(centerHz, deviationHz);
            if (_appSettings.AfPluginUiState.TryGetValue(binding.PluginId, out var bag))
            {
                bag["center"] = centerHz.ToString(System.Globalization.CultureInfo.InvariantCulture);
                bag["deviation"] = deviationHz.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            var options = new Dictionary<string, string>(
                _appSettings.AfPluginUiState.GetValueOrDefault(binding.PluginId) ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = AfPluginRunning(binding.PluginId).ToString(),
                ["center"] = centerHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["deviation"] = deviationHz.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            _afPluginHost.Configure(binding.InstanceId, options);
            return;
        }
        if (binding?.PluginId.Equals("builtin.af.kiwinavtex", StringComparison.OrdinalIgnoreCase) == true)
        {
            _appSettings.KiwiNavtexCenterHz = centerHz;
            _appSettings.KiwiNavtexDeviationHz = deviationHz;
            (binding.PluginView as KiwiNavtexPluginView)?.SetFskTuning(centerHz, deviationHz);
            ConfigureKiwiNavtexPlugin();
            return;
        }
        if (binding?.PluginId.Equals("builtin.af.flrtty", StringComparison.OrdinalIgnoreCase) == true)
        {
            _appSettings.FlRttyCenterHz = centerHz;
            _appSettings.FlRttyShiftHz = Math.Max(20, deviationHz * 2);
            ConfigureFlRttyPlugin();
        }
    }

    private void ApplyAfFilterRange(int lowFrequency, int highFrequency)
    {
        _appSettings.AfLowCutHz = lowFrequency;
        _appSettings.AfHighCutHz = highFrequency;
        if (_demodulator.Mode == RadioMode.CW)
            _appSettings.CwAfFilterCenterHz = Math.Clamp((lowFrequency + highFrequency) / 2, 200, 1_500);
        foreach (var processor in _audioProcessors)
        {
            processor.AfLowCutHz = lowFrequency;
            processor.AfHighCutHz = highFrequency;
        }
    }

    private void ApplyCwAfFilterPreset(int widthHz)
    {
        _appSettings.CwAfFilterWidthHz = widthHz;
        _afFilterCheck.Checked = true;
        foreach (var processor in _audioProcessors)
            processor.AfFilterEnabled = true;

        var center = _appSettings.CwAfFilterCenterHz > 0
            ? _appSettings.CwAfFilterCenterHz
            : 500;
        if (_cwModePanel.AutoPeakEnabled)
        {
            var peak = FindCwPeakHz(_lastAfSpectrum);
            if (peak > 0) center = peak;
        }

        center = Math.Clamp(center, 150, 1_800);
        _appSettings.CwAfFilterCenterHz = center;
        var half = Math.Max(40, widthHz / 2);
        var low = Math.Max(0, center - half);
        var high = Math.Min(20_000, center + half);
        ApplyAfFilterRange(low, high);
        ConfigureAfDisplay();
    }

    private static int FindCwPeakHz(float[]? spectrum)
    {
        if (spectrum is null || spectrum.Length < 16) return 0;
        // Positive-frequency bins map 0 .. Nyquist (24 kHz).
        var nyquist = AudioDemodulator.AudioSampleRate / 2d;
        var minBin = Math.Max(1, (int)(200 * spectrum.Length / nyquist));
        var maxBin = Math.Min(spectrum.Length - 1, (int)(1_400 * spectrum.Length / nyquist));
        var bestBin = -1;
        var best = float.NegativeInfinity;
        for (var bin = minBin; bin <= maxBin; bin++)
        {
            if (spectrum[bin] <= best) continue;
            best = spectrum[bin];
            bestBin = bin;
        }
        if (bestBin < 0) return 0;
        return (int)Math.Round(bestBin * nyquist / spectrum.Length);
    }

    private void ApplyAfcTick()
    {
        var mode = _demodulator.Mode;
        if (mode is not (RadioMode.AM or RadioMode.SAM or RadioMode.NFM))
        {
            ResetAfcOffset();
            return;
        }

        var rangeHz = _appSettings.AfcRangeHz is 300 or 500 or 1_000 or 2_000 or 3_000 or 5_000
            ? _appSettings.AfcRangeHz : 1_000;
        var offset = Interlocked.Read(ref _afcOffsetHz);
        var actual = _tunedFrequency + offset;
        _analogModePanel.SetActualFrequency(actual, offset);

        if (!_appSettings.AfcEnabled)
        {
            ResetAfcOffset();
            _analogModePanel.SetStatus("AFC off");
            return;
        }

        if (_source is IRemoteAudioSampleSource)
        {
            _analogModePanel.SetStatus("AFC n/a (remote)");
            return;
        }

        // SQL closed — do not chase noise.
        if (_squelchCheck.Checked && !_audioProcessors[0].SquelchOpen)
        {
            _analogModePanel.SetStatus("AFC hold · SQL closed");
            return;
        }

        if (!TryFindAfcPeak(rangeHz, out var peakFrequency, out var peakDb, out var prominenceDb, out var failReason))
        {
            _analogModePanel.SetStatus(failReason);
            return;
        }

        var targetOffset = peakFrequency - _tunedFrequency;
        targetOffset = Math.Clamp(targetOffset, -rangeHz, rangeHz);
        var error = targetOffset - offset;
        var gain = _appSettings.AfcSpeedIndex switch
        {
            0 => 0.12,
            2 => 0.40,
            _ => 0.22
        };
        if (Math.Abs(error) < 5)
        {
            _analogModePanel.SetStatus($"AFC lock · peak {peakDb:0.0} dB (+{prominenceDb:0.0})");
            return;
        }

        var step = Math.Clamp(error * gain, -35.0, 35.0);
        if (Math.Abs(step) < 1) return;
        var next = (long)Math.Round(offset + step);
        next = Math.Clamp(next, -rangeHz, rangeHz);
        if (next == offset) return;
        Interlocked.Exchange(ref _afcOffsetHz, next);
        ApplyDemodFrequencyOffset();
        _analogModePanel.SetActualFrequency(_tunedFrequency + next, next);
        _analogModePanel.SetStatus($"AFC · peak {peakFrequency / 1_000d:0.000} kHz");
    }

    private bool TryFindAfcPeak(int rangeHz, out long peakFrequency, out float peakDb, out float prominenceDb,
        out string failReason)
    {
        peakFrequency = _tunedFrequency;
        peakDb = -140;
        prominenceDb = 0;
        failReason = "AFC wait · no peak";
        var spectrum = _lastRfSpectrum;
        if (spectrum is null || spectrum.Length < 16)
        {
            failReason = "AFC wait · no spectrum";
            return false;
        }
        var sampleRate = _lastRfSpectrumSampleRate;
        var center = _lastRfSpectrumCenter;
        if (sampleRate <= 0)
        {
            failReason = "AFC wait · no spectrum";
            return false;
        }

        // Wide RF views use coarse FFT bins (~1 kHz). Always search enough
        // bins to see a real hill, then clamp the resulting offset to RANGE.
        var binHz = sampleRate / (double)spectrum.Length;
        var searchHalfHz = Math.Max(rangeHz, (int)Math.Ceiling(binHz * 8));
        var captureLeft = center - sampleRate / 2d;
        var searchLow = _tunedFrequency - searchHalfHz;
        var searchHigh = _tunedFrequency + searchHalfHz;
        var first = Math.Clamp((int)Math.Floor((searchLow - captureLeft) * spectrum.Length / sampleRate), 0, spectrum.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((searchHigh - captureLeft) * spectrum.Length / sampleRate), first, spectrum.Length - 1);
        if (last <= first)
        {
            failReason = "AFC wait · bin too coarse";
            return false;
        }

        var bestBin = first;
        var best = float.NegativeInfinity;
        for (var bin = first; bin <= last; bin++)
        {
            if (spectrum[bin] <= best) continue;
            best = spectrum[bin];
            bestBin = bin;
        }

        var exclude = Math.Max(1, (int)Math.Ceiling(2_500 / binHz));
        double noiseSum = 0;
        var noiseCount = 0;
        for (var bin = first; bin <= last; bin++)
        {
            if (Math.Abs(bin - bestBin) <= exclude) continue;
            noiseSum += spectrum[bin];
            noiseCount++;
        }
        float noiseFloor;
        if (noiseCount >= 3)
        {
            noiseFloor = (float)(noiseSum / noiseCount);
        }
        else
        {
            var sorted = new float[last - first + 1];
            for (var i = 0; i < sorted.Length; i++) sorted[i] = spectrum[first + i];
            Array.Sort(sorted);
            noiseFloor = sorted[Math.Max(0, sorted.Length / 5)];
        }

        prominenceDb = best - noiseFloor;
        if (prominenceDb < 3.5f)
        {
            failReason = $"AFC wait · weak +{prominenceDb:0.0} dB";
            return false;
        }
        if (best < -110f)
        {
            failReason = "AFC wait · peak too low";
            return false;
        }

        peakDb = best;
        peakFrequency = (long)Math.Round(captureLeft + bestBin * binHz);
        var offset = peakFrequency - _tunedFrequency;
        if (Math.Abs(offset) > rangeHz + binHz * 1.5)
        {
            failReason = $"AFC · peak {offset:+0;-0} Hz > RANGE";
            return false;
        }
        peakFrequency = _tunedFrequency + Math.Clamp(offset, -rangeHz, rangeHz);
        return true;
    }

    private long EffectiveDemodFrequency => _tunedFrequency + Interlocked.Read(ref _afcOffsetHz);

    private void ApplyDemodFrequencyOffset()
    {
        _demodulator.FrequencyOffset = EffectiveDemodFrequency - _rfCenterFrequency;
    }

    private void ResetAfcOffset()
    {
        if (Interlocked.Exchange(ref _afcOffsetHz, 0) == 0) return;
        ApplyDemodFrequencyOffset();
    }

    private void UpdateWfmAudioChrome()
    {
        var wfm = _demodulator.Mode == RadioMode.WFM;
        _stereoLed.Visible = wfm;
        if (!wfm)
        {
            _stereoLed.IsOn = false;
            return;
        }

        var stereoOn = _appSettings.WfmStereoEnabled;
        var locked = stereoOn && _demodulator.StereoLocked;
        var left = Volatile.Read(ref _stereoLeftDb);
        var right = Volatile.Read(ref _stereoRightDb);
        if (!stereoOn)
        {
            var mono = Volatile.Read(ref _audioLevelDb);
            left = right = mono;
        }
        _wfmModePanel.UpdateMonitor(locked, left, right);
        _stereoLed.IsOn = _wfmModePanel.StereoLampOn;
        if (!stereoOn) _wfmModePanel.SetStatus("Mono · drag EQ ±12 dB");
        else if (locked) _wfmModePanel.SetStatus($"STEREO · lock {_demodulator.StereoLockPercent:0}%");
        else _wfmModePanel.SetStatus($"Pilot search {_demodulator.StereoLockPercent:0}%");
    }

    private void UpdateCtcssUi(bool force = false)
    {
        var nfm = _demodulator.Mode == RadioMode.NFM;
        // DCS bitstream looks like wandering CTCSS peaks — prefer DCS and blank CTCSS while active.
        var dcsLabel = nfm ? _dcsDecoder.DetectedLabel : "";
        var dcsBusy = nfm && _dcsDecoder.HasActivity;
        var toneHz = nfm && !dcsBusy && dcsLabel.Length == 0 ? _ctcssDecoder.DetectedToneHz : 0f;
        var mdc = nfm ? _mdcDecoder.DetectedLabel : "";
        var dtmf = nfm ? _dtmfDecoder.DetectedLabel : "";
        var ani = mdc.Length > 0 ? mdc : dtmf;
        float key;
        if (dcsLabel.Length > 0)
            key = -2000f - HashCode.Combine(dcsLabel);
        else if (dcsBusy)
            key = -1500f - (_dcsDecoder.DebugVotes % 100);
        else if (toneHz > 0) key = toneHz;
        else if (ani.Length > 0) key = -3000f - HashCode.Combine(ani);
        else key = 0f;
        if (!force && Math.Abs(key - _lastUiCtcssHz) < 0.05f) return;
        _lastUiCtcssHz = key;
        try
        {
            if (dcsLabel.Length > 0)
            {
                _afDisplay.SetDigitalOverlay($"DCS {dcsLabel}");
                _analogModePanel.SetCtcss(0, dcsLabel);
            }
            else if (dcsBusy)
            {
                var hint = _dcsDecoder.DebugBestDist < 23
                    ? $"DCS … d{_dcsDecoder.DebugBestDist}"
                    : "DCS …";
                _afDisplay.SetDigitalOverlay(hint);
                _analogModePanel.SetCtcss(0, null, hint);
            }
            else if (toneHz > 0)
            {
                var overlay = ani.Length > 0 ? $"CTCSS {toneHz:0.0} · {ani}" : $"CTCSS {toneHz:0.0}";
                _afDisplay.SetDigitalOverlay(overlay);
                _analogModePanel.SetCtcss(toneHz, null, null, ani.Length > 0 ? ani : null);
            }
            else if (ani.Length > 0)
            {
                _afDisplay.SetDigitalOverlay(ani);
                _analogModePanel.SetCtcss(0, null, null, ani);
            }
            else if (nfm)
            {
                _afDisplay.SetDigitalOverlay("CTCSS —");
                _analogModePanel.SetCtcss(0);
            }
            else
            {
                _afDisplay.SetDigitalOverlay("");
                _analogModePanel.SetCtcss(0);
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void ScheduleVoiceFrequency(long frequencyHz)
    {
        if (!_appSettings.VoiceGuidanceEnabled) return;
        Interlocked.Exchange(ref _pendingVoiceFrequency, frequencyHz);
        _voiceDebounceTimer.Stop();
        _voiceDebounceTimer.Start();
    }

    private void AnnounceModeIfEnabled(string mode)
    {
        if (!_appSettings.VoiceGuidanceEnabled) return;
        _voiceDebounceTimer.Stop();
        _voiceAnnouncer.SpeakMode(mode);
    }

    private async void ShowSetup()
    {
        var wasRunning = _source.IsRunning;
        var oldSampleRate = _source.SampleRate;
        var oldSampleRateGainCompensation = _appSettings.SdrplaySampleRateGainCompensationEnabled;
        var oldAfPluginInstances = AfPluginInstanceSignature();
        var oldIqPlugins = IqPluginSignature();
        var oldAfDisplayWidth = _appSettings.AfPluginDisplayWidth;
        var oldAfDisplayHeight = _appSettings.AfPluginDisplayHeight;
        var webWasEnabled = _appSettings.WebRemoteEnabled;
        var webPort = _appSettings.WebRemotePort;
        var webBind = _appSettings.WebRemoteBindAllInterfaces;
        var webToken = _appSettings.WebRemoteAccessToken ?? "";
        var voiceWasEnabled = _appSettings.VoiceGuidanceEnabled;
        using var setup = new PluginSetupForm(_displayPlugins, _afPlugins, _iqPlugins, _pluginSelection,
            _appSettings, _audioDevices, _source);
        var setupAccepted = setup.ShowDialog(this) == DialogResult.OK;
        if (setup.WebSdrDirectoryChanged)
        {
            _remoteDirectory = ListedRemoteDirectory();
            _listedSiteProtocol = null;
            RefreshSiteLists(force: true);
            if (IsSatelliteSceneActive)
                RefreshSatelliteRemoteHomes();
        }
        if (!setupAccepted) return;
        if (setup.ResetRequested)
        {
            AppSettingsStore.Reset();
            AppSettingsStore.Save(AppSettingsStore.FactoryDefaults());
            _settingsResetRequested = true;
            MessageBox.Show(this, "Settings have been reset. Defaults will apply the next time the app starts.", "NeuroSDR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var sampleRateGainCompensationChanged =
            oldSampleRateGainCompensation != _appSettings.SdrplaySampleRateGainCompensationEnabled;
        var afPluginInstancesChanged = oldAfPluginInstances != AfPluginInstanceSignature();
        var iqPluginsChanged = oldIqPlugins != IqPluginSignature();
        var afDisplayLayoutChanged = oldAfDisplayWidth != _appSettings.AfPluginDisplayWidth ||
            oldAfDisplayHeight != _appSettings.AfPluginDisplayHeight;
        var sampleRateChanged = false;
        var uiTimerWasRunning = _uiTimer.Enabled;
        _uiTimer.Stop();
        _pluginUiSuspended = true;
        PauseDecoderPlugins();
        try
        {
        if (_source is IConfigurableSampleRateSource configurable &&
            _appSettings.HardwareSampleRates.TryGetValue(_source.Name, out var requestedRate) &&
            configurable.SupportedSampleRates.Contains(requestedRate) && requestedRate != oldSampleRate)
        {
            if (wasRunning) await StopReceiverCoreAsync().ConfigureAwait(true);
            configurable.ConfiguredSampleRate = requestedRate;
            sampleRateChanged = true;
            _sampleRateValueLabel.Text = $"{_source.SampleRate / 1_000_000d:0.000} MS/s";
            if (_viewBandwidth >= oldSampleRate)
            {
                _viewBandwidth = requestedRate;
                _viewCenterFrequency = _rfCenterFrequency;
            }
            else
            {
                _viewBandwidth = Math.Clamp(_viewBandwidth, MinimumViewBandwidth(), requestedRate);
                _viewCenterFrequency = ClampViewCenter(_viewCenterFrequency, _viewBandwidth);
            }
            if (Math.Abs(_tunedFrequency - _rfCenterFrequency) > requestedRate * .45)
            {
                _rfCenterFrequency = _tunedFrequency;
                _viewCenterFrequency = _tunedFrequency;
            }
            Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
            Interlocked.Exchange(ref _nextRfDisplaySubmissionTick, 0);
            _spectrumPipeline.Reset();
            Interlocked.Exchange(ref _pendingSpectrum, null);
        }
        if (afPluginInstancesChanged)
        {
            if (wasRunning && !sampleRateChanged)
                await StopReceiverCoreAsync().ConfigureAwait(true);
            _afPluginHost.Rebuild(_appSettings.AfPluginInstances);
            ApplyAfPluginRoutesForCurrentSource();
        }
        _cwSideBox.SelectedIndex = _appSettings.CwLowerSide ? 1 : 0;
        _rxVolumeBar2.Value = _appSettings.Audio2.Volume;
        _rxSquelchBar2.Value = _appSettings.Audio2.SquelchThreshold;
        _squelchCheck2.Checked = _appSettings.Audio2.SquelchEnabled;
        if (afPluginInstancesChanged || iqPluginsChanged || afDisplayLayoutChanged)
            UpdateAfPluginDisplayLayout();
        ApplyAfPluginActivation();
        ApplyIqPluginActivation();
        ApplySlowModePluginSettings();
        ApplySourceHardwareSettings();
        ApplyAudioDspSettings();
        ConfigurePipelineDiagnostics();
        ApplyRtlTcpEndpointFromSettings();
        var webChanged = webWasEnabled != _appSettings.WebRemoteEnabled ||
            webPort != _appSettings.WebRemotePort ||
            webBind != _appSettings.WebRemoteBindAllInterfaces ||
            !string.Equals(webToken, _appSettings.WebRemoteAccessToken ?? "", StringComparison.Ordinal);
        if (webChanged) ApplyWebRemoteHost(forceRestart: _appSettings.WebRemoteEnabled);
        if (_appSettings.VoiceGuidanceEnabled && !voiceWasEnabled)
        {
            if (_voiceAnnouncer.IsAvailable) _voiceAnnouncer.Speak("Voice guidance on");
            else _statusLabel.Text = "Voice guidance · no speech engine";
        }
        if (_source is not IFixedCenterFrequencySampleSource)
        {
            ApplyLogicalCenterToSource();
            _spectrumPipeline.Reset();
            Interlocked.Exchange(ref _pendingSpectrum, null);
            ConfigureDisplay();
        }
        ApplyDisplayPlugins(setup.Selection, true);
        UpdateRxPanel();
        // Plugin enable/disable + routes + volumes are captured by the active Rx SCENE.
        PersistSceneSnapshot(_appSettings.SelectedRxSceneId);
        SaveSettings();
        if (sampleRateGainCompensationChanged && wasRunning && !sampleRateChanged && !afPluginInstancesChanged)
            await StopReceiverCoreAsync().ConfigureAwait(true);
        if ((sampleRateChanged || sampleRateGainCompensationChanged || afPluginInstancesChanged) && wasRunning)
            await StartReceiverAsync().ConfigureAwait(true);
        else if (wasRunning && _source is not IRemoteAudioSampleSource)
        {
            DisposeAudioOutputs();
            if (_audioCheck.Checked) OpenAudioOutputs();
        }
        }
        finally
        {
            _pluginUiSuspended = false;
            if (uiTimerWasRunning || _source.IsRunning) _uiTimer.Start();
        }
    }

    private void PauseDecoderPlugins()
    {
        _iqPluginHost.Apply([]);
        _afPluginHost.Apply(_appSettings.AfPluginInstances, _appSettings.FtxMode,
            _appSettings.FtxTimeAdjustSeconds, _appSettings.FtxAutoTimeAdjust, []);
    }

    private string AfPluginInstanceSignature() => string.Join("|", _appSettings.AfPluginInstances.Select(instance =>
        $"{instance.InstanceId}:{instance.PluginId}:{instance.Variant}:{instance.VfoId}"));

    private string IqPluginSignature() => string.Join("|",
        _appSettings.EnabledIqPluginIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

    private void ApplyConfiguredSampleRates()
    {
        foreach (var source in _sources)
        {
            if (source is not IConfigurableSampleRateSource configurable) continue;
            if (!_appSettings.HardwareSampleRates.TryGetValue(source.Name, out var requestedRate) ||
                !configurable.SupportedSampleRates.Contains(requestedRate)) continue;
            configurable.ConfiguredSampleRate = requestedRate;
        }
    }

    private void ApplyDisplayPlugins(PluginSelection selection, bool persist)
    {
        var spectrum = _displayPlugins.SpectrumPlugins.FirstOrDefault(plugin =>
            plugin.Info.Id.Equals(selection.SpectrumId, StringComparison.OrdinalIgnoreCase)) ?? _displayPlugins.SpectrumPlugins[0];
        var waterfall = _displayPlugins.WaterfallPlugins.FirstOrDefault(plugin =>
            plugin.Info.Id.Equals(selection.WaterfallId, StringComparison.OrdinalIgnoreCase)) ?? _displayPlugins.WaterfallPlugins[0];
        _display.SetRenderPlugins(spectrum.Create(), waterfall.Create());
        _pluginSelection = new PluginSelection(spectrum.Info.Id, waterfall.Info.Id);
        _appSettings.SpectrumPluginId = _pluginSelection.SpectrumId;
        _appSettings.WaterfallPluginId = _pluginSelection.WaterfallId;
        if (persist) SaveSettings();
        _statusLabel.Text = $"Display plugins · {spectrum.Info.Name} / {waterfall.Info.Name}";
    }

    private void Shutdown()
    {
        DockAfDisplay();
        DockRfDisplay();
        _uiTimer.Stop();
        _smartRecordTimer.Stop();
        _afVfoHideTimer.Stop();
        _remoteViewportTimer.Stop();
        _voiceDebounceTimer.Stop();
        _remoteViewportCts?.Cancel();
        _remoteViewportCts?.Dispose();
        try { StopRemoteWebBlocking(); } catch { }
        try { _voiceAnnouncer.Dispose(); } catch { }
        _voiceAnnouncer = new NullVoiceAnnouncer();
        if (!_automatedSoak && !_settingsResetRequested)
        {
            // Flush Last Scene (Main/SUB VFOs, waterfall center+span, …) before disk write.
            PersistSceneSnapshot(_appSettings.SelectedRxSceneId);
            SaveSettings();
        }
        StopMemoryScan();
        _memoryForm?.Dispose();
        _memoryForm = null;
        StopIqRecording();
        StopAfRecording(promptSave: !_automatedSoak);
        DisposeAudioOutputs();
        LeaveRealtimeGarbageCollection();
        DetachSource(_source);
        _afPluginHost.ResultAvailable -= OnAfPluginResult;
        _afPluginHost.Dispose();
        DigitalVoicePacer.Stop();
        StopDigitalVoiceExternalFeeds();
        _digitalMode.Dispose();
        _enFilter.Dispose();
        _iqPluginHost.ResultAvailable -= OnIqPluginResult;
        _iqPluginHost.Dispose();
        foreach (var source in _sources) source.Dispose();
        _spectrumPipeline.Dispose();
        _audioSpectrumPipeline.Dispose();
        _afVfoHideTimer.Dispose();
        _voiceDebounceTimer.Dispose();
        _pipelineTrace?.Dispose();
        _pipelineTrace = null;
    }

    private void ConfigurePipelineDiagnostics()
    {
        if (!_appSettings.PipelineDiagnosticsEnabled && !_forcePipelineDiagnostics)
        {
            _pipelineTrace?.Dispose();
            _pipelineTrace = null;
            return;
        }
        if (_pipelineTrace is not null) return;
        try
        {
            _pipelineTrace = new UiPipelineTrace();
            _nextPipelineTraceSnapshot = 0;
            _statusLabel.Text = $"PIPELINE LOG · {_pipelineTrace.FilePath}";
        }
        catch (Exception exception)
        {
            _appSettings.PipelineDiagnosticsEnabled = false;
            _statusLabel.Text = $"Pipeline log failed: {exception.Message}";
        }
    }

    private void ApplyRtlTcpEndpointFromSettings()
    {
        var endpoint = string.IsNullOrWhiteSpace(_appSettings.RtlTcpEndpoint)
            ? "127.0.0.1:1234" : _appSettings.RtlTcpEndpoint.Trim();
        foreach (var source in _sources)
        {
            if (source is not Hardware.RtlTcpSampleSource rtlTcp) continue;
            if (rtlTcp.IsRunning) continue;
            try { rtlTcp.Endpoint = endpoint; }
            catch { /* ignore invalid while idle */ }
        }
        FillRxSourceBox();
    }

    private void ApplySourceHardwareSettings()
    {
        if (_source is IRfAmplifierSampleSource amplifierSource)
            amplifierSource.RfAmplifierEnabled = _appSettings.HackRfAmplifierEnabled;
        if (_source is IHardwareAgcSampleSource hardwareAgcSource)
            hardwareAgcSource.HardwareAgcEnabled = _appSettings.SdrplayHardwareAgcEnabled;
        if (_source is ISampleRateGainCompensationSource compensationSource)
            compensationSource.SampleRateGainCompensationEnabled = _appSettings.SdrplaySampleRateGainCompensationEnabled;
    }

    private void SnapshotPipelineTrace(string eventName = "TICK")
    {
        _pipelineTrace?.Snapshot(_source, _demodulator.Mode, _tunedFrequency, _demodulator.Bandwidth,
            Volatile.Read(ref _viewBandwidth), _appSettings.RfDisplayFramesPerSecond, _appSettings.RfFftQuality, _audioOutputs,
            _afPluginHost.Plugins.Where(plugin => _afPluginHost.IsEnabled(plugin.Id)).Select(plugin => plugin.Id), eventName);
    }

    private void RestoreWindowState()
    {
        var bounds = new Rectangle(_appSettings.WindowX, _appSettings.WindowY, _appSettings.WindowWidth, _appSettings.WindowHeight);
        if (_appSettings.WindowX >= 0 && Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds)))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
        }
        if (_appSettings.WindowMaximized) WindowState = FormWindowState.Maximized;
        _windowGeometryReady = true;
    }

    private void ApplySavedSettings()
    {
        _modeBox.SelectedItem = _appSettings.Mode.ToString();
        _bandwidthBox.Value = Math.Clamp(_appSettings.FilterBandwidth, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
        _demodulator.Mode = _appSettings.Mode;
        _demodulator.Bandwidth = (int)_bandwidthBox.Value;
        _demodulator.CwPitchHz = _appSettings.CwLowerSide ? -700 : 700;
        ApplyDemodFrequencyOffset();
        SyncDigitalModeEngine();
        _gainSlider.Value = Math.Clamp(_appSettings.RfGain, _gainSlider.Minimum, _gainSlider.Maximum);
        _audioCheck.Checked = _appSettings.AudioEnabled;
        _volumeSlider.Value = Math.Clamp(_appSettings.Audio1.Volume, 0, 100);
        _squelchCheck.Checked = _appSettings.Audio1.SquelchEnabled;
        _squelchThreshold.Value = Math.Clamp(_appSettings.Audio1.SquelchThreshold, -140, 0);
        _rxVolumeBar2.Value = Math.Clamp(_appSettings.Audio2.Volume, 0, 100);
        _rxSquelchBar2.Value = Math.Clamp(_appSettings.Audio2.SquelchThreshold, -140, 0);
        _squelchCheck2.Checked = _appSettings.Audio2.SquelchEnabled;
        _cwSideBox.SelectedIndex = _appSettings.CwLowerSide ? 1 : 0;
        _afFilterCheck.Checked = _appSettings.AfFilterEnabled;
        _display.ShowFtxOnMain = _appSettings.FtxShowOnMainWaterfall;
        _agcCheck.Checked = _appSettings.AgcEnabled;
        _noiseReductionCheck.Checked = _appSettings.NoiseReductionEnabled;
        _noiseReductionSlider.Value = Math.Clamp(_appSettings.NoiseReductionStrength, 0, 100);
        _notchCheck.Checked = _appSettings.NotchEnabled;
        _notchFrequency.Value = Math.Clamp(_appSettings.NotchFrequency, 50, 20_000);
        _analogModePanel.LoadOptions(_appSettings.AfcEnabled, _appSettings.AfcSpeedIndex, _appSettings.AfcRangeHz);
        _demodulator.AfcEnabled = false;
        _wfmModePanel.LoadOptions(
            _appSettings.WfmStereoEnabled,
            _appSettings.WfmHfSoftEnabled,
            _appSettings.WfmHideAfPlugins,
            _appSettings.WfmEqGainsDb,
            _appSettings.WfmEqSelectedPreset,
            _appSettings.WfmEqPresets);
        ApplyWfmEqualizer();
        _wfmStationPanel.LoadStations(
            _appSettings.WfmStations,
            _appSettings.WfmEqPresets.Select(p => p.Name));
        _demodulator.StereoEnabled = _appSettings.WfmStereoEnabled;
        _cwModePanel.LoadOptions(
            _appSettings.CwAfFilterWidthHz is 100 or 200 or 300 or 400
                ? _appSettings.CwAfFilterWidthHz : 200,
            _appSettings.CwAfFilterAutoPeak);
        LoadEnFilterFromSettings();
        ApplyDisplayLevelsForCurrentSource();
        ApplyRfDisplayMode();
        ApplyWfmAfChrome();
    }

    private string CurrentDisplayLevelKey()
    {
        if (_source is IRemoteAudioSampleSource remote)
        {
            var url = string.IsNullOrWhiteSpace(remote.ServerUrl) ? _webUrlInput.Text : remote.ServerUrl;
            return remote.Name + "\n" + url.Trim().TrimEnd('/').ToLowerInvariant();
        }
        return _source.Name;
    }

    private void RememberDisplayLevels()
    {
        if (_applyingDisplayLevels) return;
        _appSettings.DisplayLevelProfiles ??= new Dictionary<string, DisplayLevelProfile>(StringComparer.OrdinalIgnoreCase);
        _display.ReadDisplayLevels(out var spectrum, out var waterfall, out var spectrumAuto, out var waterfallAuto);
        _appSettings.SpectrumLevelOffsetDb = spectrum;
        _appSettings.WaterfallLevelOffsetDb = waterfall;
        _appSettings.DisplayLevelProfiles[CurrentDisplayLevelKey()] = new DisplayLevelProfile
        {
            SpectrumOffsetDb = spectrum,
            WaterfallOffsetDb = waterfall,
            SpectrumAuto = spectrumAuto,
            WaterfallAuto = waterfallAuto
        };
    }

    private void ApplyDisplayLevelsForCurrentSource()
    {
        _appSettings.DisplayLevelProfiles ??= new Dictionary<string, DisplayLevelProfile>(StringComparer.OrdinalIgnoreCase);
        if (!_appSettings.DisplayLevelProfiles.TryGetValue(CurrentDisplayLevelKey(), out var profile) || profile is null)
            profile = new DisplayLevelProfile();
        _applyingDisplayLevels = true;
        try
        {
            _appSettings.SpectrumLevelOffsetDb = profile.SpectrumOffsetDb;
            _appSettings.WaterfallLevelOffsetDb = profile.WaterfallOffsetDb;
            _display.SetDisplayLevels(profile.SpectrumOffsetDb, profile.WaterfallOffsetDb);
            _display.SetAutoLevels(profile.SpectrumAuto, profile.WaterfallAuto);
        }
        finally
        {
            _applyingDisplayLevels = false;
        }
    }

    private void SaveSettings()
    {
        CaptureSettingsFromUi();
        AppSettingsStore.Save(_appSettings);
    }

    /// <summary>
    /// Scene auto-save must not stall the UI/audio pump with synchronous disk I/O.
    /// Serialize on the UI thread (stable snapshot), write the bytes off-thread.
    /// </summary>
    private void SaveSettingsAsync()
    {
        string json;
        try
        {
            CaptureSettingsFromUi();
            json = AppSettingsStore.Serialize(_appSettings);
        }
        catch
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try { AppSettingsStore.WriteSerialized(json); }
            catch { /* best-effort background persist */ }
        });
    }

    private void CaptureSettingsFromUi()
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
        if (_windowGeometryReady)
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _appSettings.WindowX = bounds.X;
            _appSettings.WindowY = bounds.Y;
            _appSettings.WindowWidth = bounds.Width;
            _appSettings.WindowHeight = bounds.Height;
            if (WindowState == FormWindowState.Maximized)
                _appSettings.WindowMaximized = true;
            else if (WindowState == FormWindowState.Normal)
                _appSettings.WindowMaximized = false;
        }
        _appSettings.SourceName = _source.Name;
        _appSettings.TunedFrequency = _tunedFrequency;
        _appSettings.RfCenterFrequency = _rfCenterFrequency;
        _appSettings.ViewCenterFrequency = _viewCenterFrequency;
        _appSettings.ViewBandwidth = _viewBandwidth;
        // Keep SelectedRxScene (Last Scene) in RxScenes[] aligned with live radio so
        // scene switch / restart restores Main, SUB VFOs, and waterfall view.
        if (!_applyingRxScene && !_rxSceneUiBusy)
            PersistSceneSnapshot(_appSettings.SelectedRxSceneId);
        _appSettings.Mode = _demodulator.Mode;
        _appSettings.DigitalVoiceOutputChannel = Math.Clamp(_digitalMode.OutputChannel, 1, 2);
        _appSettings.DigitalVoiceFeedAgc = _digitalMode.FeedAgcEnabled;
        _appSettings.DigitalModeAgc = _digitalModePanel.DigitalModeAgc;
        _appSettings.DigitalVoiceFeedVolume = _digitalMode.FeedVolumePercent;
        _appSettings.DigitalVoiceFeedSource = DigitalVoiceFeedSourceToSettings(_digitalVoiceFeedSource);
        _appSettings.DigitalVoiceLineInDeviceId = _digitalModePanel.LineInDeviceId;
        _appSettings.DigitalVoiceWavPath = _digitalModePanel.SelectedWavPath;
        _appSettings.FilterBandwidth = _demodulator.Bandwidth;
        _appSettings.RfGain = _gainSlider.Value;
        _appSettings.AudioEnabled = _audioCheck.Checked;
        _appSettings.CwLowerSide = _cwSideBox.SelectedIndex == 1;
        _appSettings.AfFilterEnabled = _afFilterEnabledBeforeSatellite ?? _afFilterCheck.Checked;
        _appSettings.AfcEnabled = _analogModePanel.AfcEnabled;
        _appSettings.AfcSpeedIndex = _analogModePanel.AfcSpeedIndex;
        _appSettings.AfcRangeHz = _analogModePanel.AfcRangeHz;
        _appSettings.WfmStereoEnabled = _wfmModePanel.StereoEnabled;
        _appSettings.WfmHfSoftEnabled = _wfmModePanel.HfSoftEnabled;
        _appSettings.WfmHideAfPlugins = _wfmModePanel.HideAfPlugins;
        _appSettings.WfmEqGainsDb = _wfmModePanel.EqGainsDb;
        _appSettings.WfmEqSelectedPreset = _wfmModePanel.SelectedEqPreset;
        _appSettings.WfmEqPresets = _wfmModePanel.ExportPresets();
        _appSettings.WfmStations = _wfmStationPanel.ExportStations();
        _appSettings.RfDisplayMode = _display.DisplayMode;
        _appSettings.CwAfFilterWidthHz = _cwModePanel.SelectedWidthHz;
        _appSettings.CwAfFilterAutoPeak = _cwModePanel.AutoPeakEnabled;
        CaptureEnFilterSettings();
        _appSettings.AgcEnabled = _agcCheck.Checked;
        _appSettings.NoiseReductionEnabled = _noiseReductionCheck.Checked;
        _appSettings.NoiseReductionStrength = _noiseReductionSlider.Value;
        _appSettings.NotchEnabled = _notchCheck.Checked;
        _appSettings.NotchFrequency = (int)_notchFrequency.Value;
        _appSettings.MainAutoTune ??= new AutoTuneSettings();
        _appSettings.GlobalFrequencyChannels ??= [];
        _appSettings.SmartRecordJobs ??= [];
    }

    private void NoteManualTune()
    {
        _manualTuneHoldUntil = Environment.TickCount64 + 4_000;
        _mainAutoFollowing = false;
    }

    private long ConstrainFrequencyForCurrentSource(long frequency, IReadOnlyList<RemoteSdrBandSpan>? bands = null)
    {
        frequency = Math.Clamp(frequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        if (bands is { Count: > 0 })
            return RemoteSdrBands.ClampHz(frequency, bands);
        if (_source is not IRemoteAudioSampleSource remote) return frequency;
        var entry = RemoteSdrCatalog.FindByUrl(remote.ServerUrl);
        if (entry is null || entry.Bands.Count == 0) return frequency;
        return RemoteSdrBands.ClampHz(frequency, entry.Bands);
    }

    private void ChangeVfoFromDigitalDisplay(long frequency, bool userInitiated = false,
        IReadOnlyList<RemoteSdrBandSpan>? bands = null, bool keepSpectrumView = false)
    {
        if (userInitiated) NoteManualTune();
        var previousFrequency = _tunedFrequency;
        _tunedFrequency = ConstrainFrequencyForCurrentSource(frequency, bands);
        if (_source is IFixedCenterFrequencySampleSource)
            _tunedFrequency = Math.Clamp(_tunedFrequency, _rfCenterFrequency - _source.SampleRate * 45L / 100, _rfCenterFrequency + _source.SampleRate * 45L / 100);
        else if (!keepSpectrumView && Math.Abs(_tunedFrequency - _rfCenterFrequency) > _source.SampleRate * .45)
            RecenterOnVfo();
        if (userInitiated)
            ShiftMainAutoWindowWithVfo(_tunedFrequency - previousFrequency);
        ResetCwIfFrequencyChanged(previousFrequency);
        UpdateTuningDisplay();
        if (_tunedFrequency != previousFrequency) ScheduleVoiceFrequency(_tunedFrequency);
    }

    /// <summary>
    /// When "Window follows VFO" is on, keep AUTO Min/Max/Standby locked to the dial offset.
    /// </summary>
    private void ShiftMainAutoWindowWithVfo(long deltaHz)
    {
        if (deltaHz == 0) return;
        var main = _appSettings.MainAutoTune;
        if (main is not { WindowFollowsVfo: true }) return;
        if (main.MaxFrequency <= main.MinFrequency && main.StandbyFrequency <= 0) return;

        long Shift(long hz)
        {
            if (hz <= 0) return hz;
            return Math.Clamp(hz + deltaHz, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        }

        main.StandbyFrequency = Shift(main.StandbyFrequency);
        main.MinFrequency = Shift(main.MinFrequency);
        main.MaxFrequency = Shift(main.MaxFrequency);
        if (main.MaxFrequency < main.MinFrequency)
            (main.MinFrequency, main.MaxFrequency) = (main.MaxFrequency, main.MinFrequency);
        UpdateAutoTuneOverlays();
    }

    private void ApplyTypedFrequency()
    {
        NoteManualTune();
        var text = _frequencyInput.Text.Replace("MHz", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(",", string.Empty).Trim();
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) &&
            !double.TryParse(text, out value))
        {
            System.Media.SystemSounds.Beep.Play();
            UpdateTuningDisplay();
            return;
        }
        var hz = value < 10_000 ? (long)Math.Round(value * 1_000_000) : (long)Math.Round(value);
        var previousFrequency = _tunedFrequency;
        _tunedFrequency = ConstrainFrequencyForCurrentSource(hz);
        if (_source is IFixedCenterFrequencySampleSource)
            _tunedFrequency = Math.Clamp(_tunedFrequency, _rfCenterFrequency - _source.SampleRate * 45L / 100, _rfCenterFrequency + _source.SampleRate * 45L / 100);
        else if (Math.Abs(_tunedFrequency - _rfCenterFrequency) > _source.SampleRate * .45) RecenterOnVfo();
        ShiftMainAutoWindowWithVfo(_tunedFrequency - previousFrequency);
        ResetCwIfFrequencyChanged(previousFrequency);
        UpdateTuningDisplay();
        if (_tunedFrequency != previousFrequency) ScheduleVoiceFrequency(_tunedFrequency);
        _display.Focus();
    }

    private void RecenterOnVfo()
    {
        if (_source is IFixedCenterFrequencySampleSource)
        {
            CenterViewOnVfo();
            return;
        }
        if (_iqRecorder is not null)
        {
            _tunedFrequency = Math.Clamp(_tunedFrequency, _rfCenterFrequency - _source.SampleRate * 45L / 100, _rfCenterFrequency + _source.SampleRate * 45L / 100);
            _statusLabel.Text = "The RF center frequency cannot be changed while IQ recording is active.";
            return;
        }
        _rfCenterFrequency = _tunedFrequency;
        _viewCenterFrequency = _tunedFrequency;
        Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
        if (!_uiTimer.Enabled) ApplyLogicalCenterToSource();
        UpdateFrequencyReadout();
        RenderPendingSpectrum();
    }

    private void CenterViewOnVfo() => CenterSpectrumOnFrequency(_tunedFrequency);

    private void CenterSpectrumOnFrequency(long frequency)
    {
        frequency = Math.Clamp(frequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        if (_source is IRemoteAudioSampleSource remote)
        {
            var previous = _tunedFrequency;
            _tunedFrequency = frequency;
            _viewCenterFrequency = ClampViewCenter(frequency, _viewBandwidth);
            if (!_centerDragActive) _rfCenterFrequency = _viewCenterFrequency;
            ResetCwIfFrequencyChanged(previous);
            ApplyRemoteTune();
            ConfigureDisplay();
            UpdateZoomControls();
            UpdateFrequencyReadout();
            if (remote.IsRunning)
                QueueRemoteViewport(immediate: false);
            return;
        }
        if (_source is IFixedCenterFrequencySampleSource || _iqRecorder is not null)
        {
            _viewCenterFrequency = ClampViewCenter(frequency, _viewBandwidth);
            ConfigureDisplay();
            UpdateZoomControls();
            if (_iqRecorder is not null && ClampViewCenter(frequency, _viewBandwidth) != frequency)
                _statusLabel.Text = "The RF center frequency cannot be changed while IQ recording is active.";
            return;
        }

        if (ClampViewCenter(frequency, _viewBandwidth) == frequency)
        {
            _viewCenterFrequency = frequency;
            ConfigureDisplay();
            UpdateZoomControls();
            return;
        }

        _rfCenterFrequency = frequency;
        _viewCenterFrequency = frequency;
        Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
        if (!_uiTimer.Enabled) ApplyLogicalCenterToSource();
        UpdateFrequencyReadout();
        ConfigureDisplay();
        RenderPendingSpectrum();
    }

    private void ResetFullView()
    {
        if (_source is IRemoteAudioSampleSource remote)
        {
            var fullSpan = Math.Max(MinimumViewBandwidth(), remote.MaximumSpectrumSpan);
            // "Full RF" on Kiwi still avoids a 30 MHz window that starts below 0 Hz.
            _viewBandwidth = DefaultRemoteViewSpanHz(remote, fullSpan);
            _viewCenterFrequency = ClampViewCenter(_tunedFrequency, _viewBandwidth);
            UpdateZoomControls();
            ConfigureDisplay();
            if (remote.IsRunning)
                QueueRemoteViewport(immediate: false);
            SchedulePersistCurrentSceneLayout();
            return;
        }

        _viewBandwidth = _source.SampleRate;
        _viewCenterFrequency = _rfCenterFrequency;
        UpdateZoomControls();
        ConfigureDisplay();
        SchedulePersistCurrentSceneLayout();
    }

    private void EnsureViewFitsFilter()
    {
        var minimum = MinimumViewBandwidth();
        if (_viewBandwidth >= minimum) return;
        _viewBandwidth = minimum;
        _viewCenterFrequency = ClampViewCenter(_tunedFrequency, _viewBandwidth);
        UpdateZoomControls();
    }

    private int MinimumViewBandwidth() => Math.Min(_source.SampleRate, 5_000);

    private void MoveRfCenterFromDisplay(long requestedCenter)
    {
        if (_source is IFixedCenterFrequencySampleSource)
        {
            _statusLabel.Text = "The RF center for an IQ playback source is fixed by the recording file.";
            _display.CancelCenterDrag();
            ConfigureDisplay();
            return;
        }
        if (_iqRecorder is not null)
        {
            _statusLabel.Text = "The RF center frequency cannot be changed while IQ recording is active.";
            _display.CancelCenterDrag();
            ConfigureDisplay();
            return;
        }

        requestedCenter = Math.Clamp(requestedCenter, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        if (_source is IRemoteAudioSampleSource)
        {
            // Remote: waterfall viewport is independent of the audio VFO.
            // Dragging the main waterfall must NOT retune (_tunedFrequency / ApplyRemoteTune).
            var deltaFromOrigin = requestedCenter - _centerDragOriginalCenter;
            _rfCenterFrequency = requestedCenter;
            _viewCenterFrequency = Math.Clamp(_centerDragOriginalViewCenter + deltaFromOrigin,
                RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
            // Coalesce server viewport until mouse-up — mid-drag apply fights the deltas.
            _remoteViewportTimer.Stop();
            return;
        }
        var delta = requestedCenter - _rfCenterFrequency;
        if (delta == 0) return;
        _rfCenterFrequency = requestedCenter;
        _viewCenterFrequency = ClampViewCenter(_viewCenterFrequency + delta, _viewBandwidth);
        ApplyDemodFrequencyOffset();
        ApplyRemoteTune();
        // Keep hardware tuning following the drag at the UI timer cadence. MouseMove
        // only publishes the newest requested center, so rapid movement is coalesced
        // instead of blocking once per mouse event.
        Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
        if (!_uiTimer.Enabled) ApplyLogicalCenterToSource();
        UpdateFrequencyReadout();
        RenderPendingSpectrum();
    }

    private void BeginRfCenterDrag()
    {
        if (_source is IFixedCenterFrequencySampleSource || _iqRecorder is not null)
        {
            _centerDragActive = false;
            return;
        }
        _centerDragOriginalCenter = _rfCenterFrequency;
        _centerDragOriginalTuned = _tunedFrequency;
        _centerDragOriginalViewCenter = _viewCenterFrequency;
        _centerDragActive = true;
        _remoteViewportTimer.Stop();
        Interlocked.Exchange(ref _pendingRemoteSpectrum, null);
    }

    private void CompleteRfCenterDrag(long _)
    {
        if (!_centerDragActive)
        {
            _display.CancelCenterDrag();
            ConfigureDisplay();
            return;
        }
        try
        {
            if (_source is IRemoteAudioSampleSource)
            {
                // Mark the new window before leaving the drag freeze, then apply immediately.
                _awaitingRemoteViewport = true;
                _awaitingRemoteViewportCenter = _viewCenterFrequency;
                _awaitingRemoteViewportSpan = _viewBandwidth;
                _centerDragActive = false;
                Interlocked.Exchange(ref _pendingRemoteSpectrum, null);
                _display.CommitCenterDrag();
                QueueRemoteViewport(immediate: true);
                RefreshEibiWaterfall(force: true);
                SchedulePersistCurrentSceneLayout();
                return;
            }
            _centerDragActive = false;
            ApplyLogicalCenterToSource();
            Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
            _spectrumPipeline.Reset();
            Interlocked.Exchange(ref _pendingSpectrum, null);
            _display.CommitCenterDrag();
            UpdateFrequencyReadout();
            ConfigureDisplay();
            RefreshEibiWaterfall(force: true);
            SchedulePersistCurrentSceneLayout();
        }
        catch (Exception exception)
        {
            _centerDragActive = false;
            _rfCenterFrequency = _centerDragOriginalCenter;
            _tunedFrequency = _centerDragOriginalTuned;
            _viewCenterFrequency = _centerDragOriginalViewCenter;
            Interlocked.Exchange(ref _pendingCenterFrequency, _rfCenterFrequency);
            _display.CancelCenterDrag();
            ConfigureDisplay();
            RefreshEibiWaterfall(force: true);
            _statusLabel.Text = $"Failed to change RF center: {exception.Message}";
        }
    }

    private void QueueRemoteViewport(bool immediate)
    {
        if (_source is not IRemoteAudioSampleSource || !_source.IsRunning || _centerDragActive) return;
        _awaitingRemoteViewport = true;
        _awaitingRemoteViewportCenter = _viewCenterFrequency;
        _awaitingRemoteViewportSpan = _viewBandwidth;
        _remoteViewportTimer.Stop();
        if (immediate)
            _ = ApplyQueuedRemoteViewportAsync();
        else
            _remoteViewportTimer.Start();
    }

    private async Task ApplyQueuedRemoteViewportAsync()
    {
        if (_source is not IRemoteAudioSampleSource remote || !_source.IsRunning || _centerDragActive) return;
        _remoteViewportCts?.Cancel();
        _remoteViewportCts?.Dispose();
        // No auto-timeout: cancelling ClientWebSocket.SendAsync aborts the socket.
        _remoteViewportCts = new CancellationTokenSource();
        var source = _source;
        var requestedCenter = _viewCenterFrequency;
        var requestedSpan = _viewBandwidth;
        _awaitingRemoteViewportCenter = requestedCenter;
        _awaitingRemoteViewportSpan = requestedSpan;
        try
        {
            var viewport = await remote.SetSpectrumViewportAsync(requestedCenter, requestedSpan, _remoteViewportCts.Token)
                .ConfigureAwait(true);
            if (!ReferenceEquals(source, _source) || _centerDragActive) return;
            if (viewport.ServerApplied)
            {
                _viewBandwidth = Math.Max(MinimumViewBandwidth(), viewport.SpanHz);
                _viewCenterFrequency = viewport.CenterFrequency;
                _rfCenterFrequency = viewport.CenterFrequency;
                _awaitingRemoteViewportCenter = viewport.CenterFrequency;
                _awaitingRemoteViewportSpan = _viewBandwidth;
                ConfigureDisplay();
            }
            _awaitingRemoteViewport = false;
            UpdateZoomControls();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(source, _source))
                _statusLabel.Text = $"Viewport warning · {exception.GetBaseException().Message}";
        }
    }

    private long ClampViewCenter(long center, int bandwidth)
    {
        var half = Math.Max(1L, bandwidth / 2L);
        if (_source is IRemoteAudioSampleSource)
        {
            // Remote capture is a movable server window. Do not clamp the view to the
            // current FFT span or a drag cannot leave the previous waterfall.
            var minCenter = half;
            var maxCenter = RadioLimits.MaximumFrequency - half;
            return Math.Clamp(center, minCenter, Math.Max(minCenter, maxCenter));
        }

        var minimum = _rfCenterFrequency - _source.SampleRate / 2L + bandwidth / 2L;
        var maximum = _rfCenterFrequency + _source.SampleRate / 2L - bandwidth / 2L;
        return minimum > maximum ? _rfCenterFrequency : Math.Clamp(center, minimum, maximum);
    }

    private void UpdateZoomControls()
    {
        if (_source.SampleRate <= 0 || _viewBandwidth <= 0) return;
        var span = _viewBandwidth >= 1_000_000 ? $"{_viewBandwidth / 1_000_000d:0.###} MHz" : $"{_viewBandwidth / 1_000d:0.#} kHz";
        var maximumSpan = _source is IRemoteAudioSampleSource remote ? remote.MaximumSpectrumSpan : _source.SampleRate;
        var viewportKind = _source is IRemoteAudioSampleSource network
            ? network.SupportsServerSpectrumViewport ? " · SERVER" : " · LOCAL"
            : string.Empty;
        _viewLabel.Text = $"Span {span}  ·  x{(double)maximumSpan / _viewBandwidth:0.#}{viewportKind}";
    }

    internal void BeginKiwiConnectDiagnostic(string url, int seconds)
    {
        _automatedSoak = true;
        _passiveDiagnostic = true;
        Shown += (_, _) =>
        {
            var log = Path.Combine(Path.GetTempPath(), "neurosdr-kiwi-ui-start.log");
            async void OnShown()
            {
                var lines = new List<string>();
                try
                {
                    var kiwi = _sources.FirstOrDefault(source => source.Name == "Virtual KiwiSDR");
                    if (kiwi is null) throw new InvalidOperationException("Virtual KiwiSDR source missing");
                    await SelectSourceCoreAsync(kiwi, url).ConfigureAwait(true);
                    ApplyAfPluginRoutesForCurrentSource();
                    var vfoPresent = _afPluginTabs.Values.All(binding => binding.VfoBox.Parent is not null);
                    var vfoLocked = _afPluginTabs.Values.All(binding => !binding.VfoBox.Enabled);
                    var vfoMain = _afPluginTabs.Values.All(binding =>
                        binding.VfoBox.SelectedItem is VfoChoice choice &&
                        choice.Id.Equals("main", StringComparison.OrdinalIgnoreCase));
                    var selectedBox = SelectedAfPluginBinding()?.VfoBox;
                    lines.Add($"source={_source.Name}");
                    lines.Add($"url={url}");
                    lines.Add($"plugins={_afPluginTabs.Count}");
                    lines.Add($"vfoPresent={vfoPresent}");
                    lines.Add($"vfoLocked={vfoLocked}");
                    lines.Add($"vfoMain={vfoMain}");
                    lines.Add($"selectedVfoVisible={selectedBox?.Visible}");
                    lines.Add($"selectedVfoEnabled={selectedBox?.Enabled}");
                    foreach (var binding in _afPluginTabs.Values)
                        lines.Add($"plugin {binding.PluginId} enabled={binding.VfoBox.Enabled} parent={binding.VfoBox.Parent?.GetType().Name} text={binding.VfoBox.SelectedItem}");
                    lines.Add($"afDisplayVfoHidden={!_afDisplayVfoPanel.Visible}");
                    lines.Add($"selectedAfVfo={SelectedAfVfoId()}");
                    ToggleReceiver();
                    var deadline = DateTime.UtcNow.AddSeconds(Math.Max(8, seconds - 2));
                    while (DateTime.UtcNow < deadline && !_source.IsRunning)
                        await Task.Delay(250).ConfigureAwait(true);
                    if (_source.IsRunning)
                    {
                        var audioUntil = DateTime.UtcNow.AddSeconds(5);
                        while (DateTime.UtcNow < audioUntil && Interlocked.Read(ref _passivePcmSamples) < 8_000)
                            await Task.Delay(250).ConfigureAwait(true);
                    }
                    lines.Add($"running={_source.IsRunning}");
                    lines.Add($"connected={(_source as IRemoteAudioSampleSource)?.IsConnected}");
                    lines.Add($"status={(_source as IRemoteAudioSampleSource)?.ConnectionStatus}");
                    lines.Add($"pcm={Interlocked.Read(ref _passivePcmSamples)}");
                    lines.Add($"mode={_demodulator.Mode}");
                    lines.Add($"startEnabled={_startButton.Enabled}");
                    lines.Add($"startText={_startButton.Text}");
                    if (_source.IsRunning)
                    {
                        if (_modeBox.Items.Contains(RadioMode.FREEDV.ToString()))
                            _modeBox.SelectedItem = RadioMode.FREEDV.ToString();
                        await Task.Delay(1500).ConfigureAwait(true);
                        lines.Add($"afterFdvMode={_demodulator.Mode}");
                        lines.Add($"ssbLower={_demodulator.SsbLower}");
                        lines.Add($"dvOwned={DigitalVoicePlayback.OwnedOutputIndex}");
                        lines.Add($"dvActive={_digitalMode.IsActive}");
                    }
                    var ok = _source.IsRunning && vfoPresent && vfoLocked && vfoMain &&
                             SelectedAfVfoId().Equals("main", StringComparison.OrdinalIgnoreCase) &&
                             !_afDisplayVfoPanel.Visible &&
                             Interlocked.Read(ref _passivePcmSamples) > 8_000;
                    AutomatedExitCode = ok ? 0 : 105;
                }
                catch (Exception exception)
                {
                    lines.Add($"exception={exception}");
                    AutomatedExitCode = 105;
                }
                try { File.WriteAllText(log, string.Join("\r\n", lines)); } catch { }
                if (!IsDisposed) Close();
            }
            OnShown();
        };
    }

    internal void BeginPassiveReceiveDiagnostic(int seconds)
    {
        _automatedSoak = true; // Never alter the user's persisted settings.
        _passiveDiagnostic = true;
        var spectrumStart = Interlocked.Read(ref _renderedSpectrumFrames);
        var afStart = Interlocked.Read(ref _renderedAfSpectrumFrames);
        Shown += (_, _) =>
        {
            ToggleReceiver();
            var finish = new System.Windows.Forms.Timer { Interval = seconds * 1_000 };
            finish.Tick += (_, _) =>
            {
                finish.Stop();
                finish.Dispose();
                var uiMaxGapMs = Interlocked.Read(ref _passiveUiMaximumGapTicks) * 1000d / Stopwatch.Frequency;
                var peakDb = BitConverter.Int32BitsToSingle(Volatile.Read(ref _passiveAudioPeakBits));
                var spectrumFrames = Interlocked.Read(ref _renderedSpectrumFrames) - spectrumStart;
                var afFrames = Interlocked.Read(ref _renderedAfSpectrumFrames) - afStart;
                var outputs = _audioOutputs.Select((output, index) => output is null
                    ? $"CH{index + 1}=closed"
                    : $"CH{index + 1}=submitted:{output.SubmittedBuffers},completed:{output.CompletedBuffers},drop:{output.DroppedBuffers},underrun:{output.StarvationEvents},pending:{output.PendingBuffers},pcmPeak:{output.MaximumInputPeak:0.000000},volume:{output.VolumePercent},configuredDevice:{(index == 0 ? _appSettings.Audio1.DeviceName : _appSettings.Audio2.DeviceName)}").ToArray();
                var report = $"source={_source.Name}\r\nrunning={_source.IsRunning}\r\nsampleRate={_source.SampleRate}\r\n" +
                    $"seconds={seconds}\r\nuiMaxGapMs={uiMaxGapMs:0.###}\r\nspectrumFrames={spectrumFrames}\r\nafFrames={afFrames}\r\n" +
                    $"pcmSamples={Interlocked.Read(ref _passivePcmSamples)}\r\naudioPeakDb={peakDb:0.###}\r\n{string.Join("\r\n", outputs)}\r\n" +
                    $"subVfos={string.Join(",", Volatile.Read(ref _subVfoReceivers).Select(item => $"{item.Name}:{item.OutputChannel}"))}\r\n" +
                    $"availableDevices={string.Join(" | ", _audioDevices.Select(device => $"{device.Id}:{device.Name}"))}";
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "neurosdr-passive-rx.log"), report); } catch { }
                var outputsHealthy = _audioOutputs.All(output => output is null ||
                    (output.SubmittedBuffers >= seconds * 25L && output.DroppedBuffers == 0));
                AutomatedExitCode = _source.IsRunning && spectrumFrames >= seconds * 5L &&
                    uiMaxGapMs < 250 && Interlocked.Read(ref _passivePcmSamples) >= seconds * 40_000L &&
                    peakDb > -120 && outputsHealthy ? 0 : 103;
                Close();
            };
            finish.Start();
        };
    }

    internal void BeginWebExitTest()
    {
        _automatedSoak = true;
        _appSettings.WebRemoteEnabled = true;
        _appSettings.WebRemotePort = 18765; // avoid colliding with a live user session
        _appSettings.WebRemoteBindAllInterfaces = false;
        Shown += (_, _) =>
        {
            ApplyWebRemoteHost(forceRestart: true);
            var finish = new System.Windows.Forms.Timer { Interval = 2500 };
            finish.Tick += (_, _) =>
            {
                finish.Stop();
                finish.Dispose();
                var hostWasRunning = _remoteWebHost is not null;
                AutomatedExitCode = hostWasRunning ? 0 : 104;
                Close();
            };
            finish.Start();
        };
    }

    internal void BeginAutomatedSoak(int seconds)
    {
        _automatedSoak = true;
        _automatedRestartStateHealthy = Interlocked.Read(ref _pendingCenterFrequency) == _rfCenterFrequency &&
            (_source is IFixedCenterFrequencySampleSource || _source.CenterFrequency == DeviceCenterFrequency(_rfCenterFrequency)) &&
            _modeBox.SelectedItem?.ToString() == _demodulator.Mode.ToString() &&
            (int)_bandwidthBox.Value == _demodulator.Bandwidth;
        _appSettings.Audio1.Enabled = true;
        _appSettings.Audio2.Enabled = true;
        _appSettings.Audio1.DeviceId = -1;
        _appSettings.Audio2.DeviceId = -1;
        _appSettings.Audio2.Volume = 0;
        _appSettings.EnabledAfPluginIds = ["builtin.af.ftx", "builtin.af.cw"];
        UpdateAfPluginDisplayLayout();
        _automatedAfLayoutHealthy = _frequencyInput.FindForm() is null && ReferenceEquals(_statusLabel.FindForm(), this) &&
            _afPanel.Height == Math.Clamp(_appSettings.AfPluginDisplayHeight + ReclaimedHeaderAfHeight, AfPanelMinHeight, 720) &&
            _afDisplayVfoPanel.Controls.Count == _appSettings.SubVfos.Count + 2 &&
            _afDisplayVfoPanel.Height == 48 && !string.IsNullOrWhiteSpace(_afDisplayVfoInfo.Text) &&
            _subVfoList.Controls.Cast<Control>().Count(control => control.Tag is string) == _appSettings.SubVfos.Count &&
            DescendantControls(_subVfoList).Count(control => Equals(control.Tag, "sub-rf-meter")) == _appSettings.SubVfos.Count &&
            SelectedAfVfoId().Equals("main", StringComparison.OrdinalIgnoreCase) &&
            _afDisplay.DisplayVfoName.Equals("MAIN VFO", StringComparison.OrdinalIgnoreCase);
        _automatedAfPluginViewHealthy = _afPluginTabs.Count == _appSettings.AfPluginInstances.Count &&
            _afPluginTabs.Values.Select(binding => binding.Page).Distinct().Count() == _afPluginTabs.Count &&
            _afPluginTabs.Values.Select(binding => binding.VfoBox).Distinct().Count() == _afPluginTabs.Count &&
            _afPluginTabs.Values.All(binding => binding.VfoBox.Parent is not null) &&
            _afPluginTabs.Values.Count(binding =>
                binding.PluginId.Equals("builtin.af.weatherfax", StringComparison.OrdinalIgnoreCase) ||
                binding.PluginId.Equals("builtin.af.flfax", StringComparison.OrdinalIgnoreCase) ||
                _afPluginHost.Plugin(binding.InstanceId) is IAfFrequencyPresetPlugin) ==
            _afPluginTabs.Values.SelectMany(binding => DescendantControls(binding.Page))
                .Count(control => Equals(control.Tag, "frequency-preset"));
        ApplyAfPluginActivation();
        _volumeSlider.Value = 0;
        _rxVolumeBar2.Value = 0;
        UpdateRxPanel();
        Shown += (_, _) =>
        {
            for (var index = 0; index < 52; index++)
            {
                var slot = index / 13L + 1;
                OnAfPluginResult(new AfPluginResult("builtin.af.ftx", "FTX_DECODE", $"TEST{index}", DateTime.UtcNow,
                    Fields: new Dictionary<string, string>
                    {
                        ["slot"] = slot.ToString(), ["utc"] = $"00:00:0{slot}", ["db"] = "20",
                        ["dt"] = "0.0", ["freq"] = "1000", ["message"] = $"TEST{index}"
                    }));
            }
            foreach (var character in new[] { "A", "B" })
                OnAfPluginResult(new AfPluginResult("builtin.af.cw", "CW_DECODE", character, DateTime.UtcNow,
                    Fields: new Dictionary<string, string>
                    {
                        ["channel"] = "29", ["slotHz"] = "3300", ["trackedHz"] = "3301",
                        ["wpm"] = "18", ["text"] = character
                    }));
            var uniqueSlots = _ftxResultList.Items.Cast<ListViewItem>().Select(item => (long)item.Tag!).Distinct().ToArray();
            _automatedAfPluginViewHealthy &= _ftxResultList.Items.Count <= 50 && uniqueSlots.Length == 3 && !uniqueSlots.Contains(1) &&
                _cwRows.TryGetValue("29", out var cwRow) && cwRow.SubItems[2].Text.EndsWith("AB", StringComparison.Ordinal);
            ToggleReceiver();
            var dragKickoff = new System.Windows.Forms.Timer { Interval = 1_000 };
            dragKickoff.Tick += (_, _) =>
            {
                dragKickoff.Stop();
                dragKickoff.Dispose();
                if (_source is not ISampleSourceMetrics metrics) return;
                var samplesBeforeDrag = metrics.TotalSamples;
                var centerBeforeDrag = _source.CenterFrequency;
                var rfBeforeDrag = _rfCenterFrequency;
                var liveRetuned = false;
                var liveRfMoved = false;
                var startX = Math.Max(20, _display.Width / 5);
                var y = Math.Max(40, _display.Height / 4);
                var step = 0;
                _display.BeginAutomatedCenterDrag(startX, y);
                var dragTimer = new System.Windows.Forms.Timer { Interval = 50 };
                dragTimer.Tick += (_, _) =>
                {
                    step++;
                    var x = startX + step * 10;
                    _display.ContinueAutomatedCenterDrag(x, y);
                    if (_source.CenterFrequency != centerBeforeDrag) liveRetuned = true;
                    if (_rfCenterFrequency != rfBeforeDrag) liveRfMoved = true;
                    if (step < 10) return;
                    dragTimer.Stop();
                    dragTimer.Dispose();
                    _display.EndAutomatedCenterDrag(x, y);
                    var deliveryRate = _source is IRemoteAudioSampleSource remote
                        ? remote.AudioSampleRate : _source.SampleRate;
                    // Remote: waterfall drag moves RF viewport only — audio VFO must stay put.
                    _automatedCenterDragHealthy = _source is IRemoteAudioSampleSource
                        ? liveRfMoved && _rfCenterFrequency != rfBeforeDrag && _source.CenterFrequency == _tunedFrequency
                        : liveRetuned && metrics.TotalSamples - samplesBeforeDrag >= deliveryRate / 5;
                };
                dragTimer.Start();
            };
            dragKickoff.Start();
            var scanKickoff = new System.Windows.Forms.Timer { Interval = 2_000 };
            scanKickoff.Tick += (_, _) =>
            {
                scanKickoff.Stop();
                scanKickoff.Dispose();
                if (_source is not ISampleSourceMetrics metrics) return;
                var samplesBeforeScan = metrics.TotalSamples;
                var baseFrequency = _rfCenterFrequency;
                MemoryChannel[] testChannels =
                [
                    MemoryChannel.Create("AUTO-A", baseFrequency, RadioMode.WFM, 180_000),
                    MemoryChannel.Create("AUTO-B", Math.Min(RadioLimits.MaximumFrequency, baseFrequency + 1_100_000), RadioMode.USB, 2_700)
                ];
                StartMemoryScan(testChannels, 500, false, -20);
                var scanStop = new System.Windows.Forms.Timer { Interval = 1_800 };
                scanStop.Tick += (_, _) =>
                {
                    scanStop.Stop();
                    scanStop.Dispose();
                    _automatedMemoryScanHealthy = _scanChannel is not null && metrics.TotalSamples - samplesBeforeScan >= _source.SampleRate;
                    StopMemoryScan();
                };
                scanStop.Start();
            };
            scanKickoff.Start();
            var rangeKickoff = new System.Windows.Forms.Timer { Interval = Math.Max(4_500, seconds * 580) };
            rangeKickoff.Tick += (_, _) =>
            {
                rangeKickoff.Stop();
                rangeKickoff.Dispose();
                if (_source is not ISampleSourceMetrics metrics) return;
                var samplesBeforeScan = metrics.TotalSamples;
                var start = Math.Clamp(_tunedFrequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency - 300_000);
                StartRangeScan(start, start + 300_000, 50_000, 300, false, -20);
                var rangeStop = new System.Windows.Forms.Timer { Interval = 1_500 };
                rangeStop.Tick += (_, _) =>
                {
                    rangeStop.Stop();
                    rangeStop.Dispose();
                    _automatedRangeScanHealthy = _rangeScanFrequency is not null && metrics.TotalSamples - samplesBeforeScan >= _source.SampleRate / 2;
                    StopMemoryScan();
                };
                rangeStop.Start();
            };
            rangeKickoff.Start();
            var afDragKickoff = new System.Windows.Forms.Timer { Interval = Math.Max(6_500, seconds * 720) };
            afDragKickoff.Tick += (_, _) =>
            {
                afDragKickoff.Stop();
                afDragKickoff.Dispose();
                if (_source is not ISampleSourceMetrics metrics) return;
                var samplesBeforeDrag = metrics.TotalSamples;
                var framesBeforeDrag = Interlocked.Read(ref _renderedAfSpectrumFrames);
                var step = 0;
                var dragTimer = new System.Windows.Forms.Timer { Interval = 50 };
                dragTimer.Tick += (_, _) =>
                {
                    step++;
                    _afDisplay.SetFilterFromXForVerification(true, Math.Max(1, _afDisplay.Width * step / 40));
                    _afDisplay.SetFilterFromXForVerification(false, Math.Max(2, _afDisplay.Width * (40 - step) / 40));
                    if (step < 10) return;
                    dragTimer.Stop();
                    dragTimer.Dispose();
                    _automatedAfDragHealthy = metrics.TotalSamples - samplesBeforeDrag >= _source.SampleRate / 5 &&
                        Interlocked.Read(ref _renderedAfSpectrumFrames) > framesBeforeDrag;
                };
                dragTimer.Start();
            };
            afDragKickoff.Start();
            var vfoLevelKickoff = new System.Windows.Forms.Timer { Interval = Math.Max(7_000, seconds * 620) };
            vfoLevelKickoff.Tick += (_, _) =>
            {
                vfoLevelKickoff.Stop();
                vfoLevelKickoff.Dispose();
                var original = _tunedFrequency;
                var frames = Interlocked.Read(ref _renderedSpectrumFrames);
                _afFrequencyLabel.WheelForVerification(_afFrequencyLabel.Width - 18, 120);
                _afFrequencyLabel.WheelForVerification(_afFrequencyLabel.Width - 18, -120);
                _display.SetLevelForVerification(true, Math.Max(10, _display.Height / 8));
                _display.SetLevelForVerification(false, Math.Max(10, _display.Height - 40));
                _automatedVfoLevelHealthy = _tunedFrequency == original && Interlocked.Read(ref _renderedSpectrumFrames) >= frames;
            };
            vfoLevelKickoff.Start();
            var pluginKickoff = new System.Windows.Forms.Timer { Interval = Math.Max(8_000, seconds * 800) };
            pluginKickoff.Tick += (_, _) =>
            {
                pluginKickoff.Stop();
                pluginKickoff.Dispose();
                var framesBeforeSwitch = Interlocked.Read(ref _renderedSpectrumFrames);
                ApplyDisplayPlugins(new PluginSelection("builtin.spectrum.filled", "builtin.waterfall.monochrome"), false);
                var pluginCheck = new System.Windows.Forms.Timer { Interval = 900 };
                pluginCheck.Tick += (_, _) =>
                {
                    pluginCheck.Stop();
                    pluginCheck.Dispose();
                    _automatedDisplayPluginHealthy = Interlocked.Read(ref _renderedSpectrumFrames) > framesBeforeSwitch;
                };
                pluginCheck.Start();
            };
            pluginKickoff.Start();
            var modeTimer = new System.Windows.Forms.Timer { Interval = Math.Max(750, seconds * 333) };
            modeTimer.Tick += (_, _) =>
            {
                modeTimer.Stop();
                modeTimer.Dispose();
                _modeBox.SelectedItem = RadioMode.USB.ToString();
                _viewBandwidth = Math.Max(MinimumViewBandwidth(), _source.SampleRate / 64);
                _viewCenterFrequency = ClampViewCenter(_tunedFrequency, _viewBandwidth);
                UpdateZoomControls();
                ConfigureDisplay();
            };
            modeTimer.Start();
            var offsetTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1_000, seconds * 500) };
            offsetTimer.Tick += (_, _) =>
            {
                offsetTimer.Stop();
                offsetTimer.Dispose();
                _modeBox.SelectedItem = RadioMode.WFM.ToString();
                _tunedFrequency = _rfCenterFrequency + 300_000;
                UpdateTuningDisplay();
            };
            offsetTimer.Start();
            var timer = new System.Windows.Forms.Timer { Interval = seconds * 1_000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                var metrics = _source as ISampleSourceMetrics;
                // A waveOut handle can report one startup transition before its
                // pre-roll is established. Judge the steady stream by sustained
                // submissions/completions and forbid dropped blocks.
                var audioContinuous = _audioOutputs.All(output => output is null ||
                    (output.DroppedBuffers == 0 && output.StarvationEvents <= 2 &&
                     output.SubmittedBuffers >= seconds * 30L && output.CompletedBuffers >= seconds * 20L));
                var remoteSoak = _source is IRemoteAudioSampleSource;
                var minimumLocalFrames = seconds * Math.Max(3L, _appSettings.RfDisplayFramesPerSecond / 2L);
                var healthy = remoteSoak
                    ? _source.IsRunning && Interlocked.Read(ref _renderedSpectrumFrames) >= seconds * 5L &&
                      _automatedCenterDragHealthy && _source.CenterFrequency == _tunedFrequency &&
                      _automatedDisplayPluginHealthy && _automatedVfoLevelHealthy && _automatedAfPluginViewHealthy && _automatedAfLayoutHealthy &&
                      metrics is not null && metrics.TotalSamples > 0 && metrics.LastDeliveryAgeMilliseconds < 1_000 && audioContinuous
                    : _source.IsRunning && Interlocked.Read(ref _renderedSpectrumFrames) >= minimumLocalFrames &&
                      _automatedCenterDragHealthy && _automatedMemoryScanHealthy && _automatedRangeScanHealthy &&
                      _automatedAfDragHealthy && _automatedDisplayPluginHealthy && _automatedVfoLevelHealthy &&
                      _automatedRestartStateHealthy && _automatedAfPluginViewHealthy && _automatedAfLayoutHealthy &&
                      (metrics is null || metrics.LastDeliveryAgeMilliseconds < 1_000) && audioContinuous;
                if (!healthy)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "neurosdr-ui-soak.log"),
                            $"source={_source.Name}\r\nrunning={_source.IsRunning}\r\nframes={Interlocked.Read(ref _renderedSpectrumFrames)}/{minimumLocalFrames}\r\n" +
                            $"centerDrag={_automatedCenterDragHealthy}\r\nmemoryScan={_automatedMemoryScanHealthy}\r\nrangeScan={_automatedRangeScanHealthy}\r\n" +
                            $"afDrag={_automatedAfDragHealthy}\r\ndisplayPlugin={_automatedDisplayPluginHealthy}\r\nvfoLevel={_automatedVfoLevelHealthy}\r\n" +
                            $"restart={_automatedRestartStateHealthy}\r\nafPluginView={_automatedAfPluginViewHealthy}\r\nafLayout={_automatedAfLayoutHealthy}\r\naudioContinuous={audioContinuous}\r\n" +
                            $"audioUnderruns={_audioOutputs.Sum(output => output?.StarvationEvents ?? 0)}\r\nlastAge={metrics?.LastDeliveryAgeMilliseconds}");
                    }
                    catch { }
                }
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                if (_source.IsRunning) ToggleReceiver();
                stopwatch.Stop();
                AutomatedExitCode = healthy && stopwatch.ElapsedMilliseconds < 2_000 ? 0 : 61;
                Close();
            };
            timer.Start();
        };
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text, AutoSize = false, Width = 215, Height = 29,
        Padding = new Padding(0, 11, 0, 0), ForeColor = Color.FromArgb(84, 171, 197),
        Font = new Font("Segoe UI Semibold", 8f)
    };

    private static Panel ValuePanel(string title, string value)
    {
        var panel = new Panel { Width = 215, Height = value.Contains('\n') ? 60 : 49, Margin = new Padding(0, 3, 0, 3) };
        panel.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(213, 40), ForeColor = Color.FromArgb(212, 221, 232), Location = new Point(0, 20) });
        panel.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.FromArgb(125, 142, 160), Location = new Point(0, 1) });
        return panel;
    }

    private static Panel InlineControls(Control left, Control right)
    {
        var panel = new Panel { Width = 185, Height = 29, Margin = new Padding(0, 1, 0, 1) };
        left.Location = new Point(0, 3);
        right.Location = new Point(100, 1);
        panel.Controls.Add(left);
        panel.Controls.Add(right);
        return panel;
    }

    private static void StyleButton(Button button, Color color)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 9f);
    }

    private sealed record AfPluginTabBinding(
        string InstanceId,
        string PluginId,
        TabPage Page,
        ComboBox VfoBox,
        ListView? ResultList,
        Label? StatusLabel,
        Queue<long>? FtxSlots,
        Dictionary<string, ListViewItem>? CwRows,
        Control? PluginView);

    internal int ExerciseCaptureRateChangeForVerification()
    {
        _automatedSoak = true;
        _appSettings.SingleActiveAfPlugin = false;
        _appSettings.Audio1.Volume = 0;
        _appSettings.Audio2.Volume = 0;
        var displayPlugins = _afPlugins.Plugins
            .Where(plugin => plugin.Info.Capabilities.HasFlag(AfPluginCapabilities.Display))
            .OrderBy(plugin => plugin.Info.IsBuiltIn)
            .Select(plugin => plugin.Info.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        if (displayPlugins.Length == 0) return 114;
        _appSettings.AfPluginInstances = displayPlugins.Select(id => new AfPluginInstanceSettings
        {
            InstanceId = $"{id}-rate-verify",
            PluginId = id,
            Variant = id.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase) ? "FT8" : "",
            VfoId = "main"
        }).ToList();
        _appSettings.EnabledAfPluginIds = displayPlugins.ToList();
        _appSettings.EnabledIqPluginIds = ["builtin.iq.kiwitimecode", "builtin.iq.adsb", "builtin.iq.lte"];
        _appSettings.AfPluginVfoRoutes = _appSettings.AfPluginInstances.ToDictionary(
            instance => instance.InstanceId, instance => instance.VfoId, StringComparer.OrdinalIgnoreCase);
        _afPluginHost.Rebuild(_appSettings.AfPluginInstances);
        _afPluginHost.SetRoutes(_appSettings.AfPluginVfoRoutes);
        UpdateAfPluginDisplayLayout();
        UpdateAfPluginDisplayLayout();
        ApplyAfPluginActivation();
        ApplyIqPluginActivation();
        if (_afPluginTabs.Values.Any(binding => binding.PluginView is { IsDisposed: true }) ||
            _kiwiTimecodeView.IsDisposed || _adsbView.IsDisposed || _lteView.IsDisposed)
            return 115;
        foreach (var binding in _afPluginTabs.Values)
        {
            if (binding.PluginView is IAfResultView view)
                view.ApplyResult(new AfPluginResult(binding.PluginId, "STATUS", "rate-verify", DateTime.UtcNow));
        }
        _kiwiTimecodeView.ApplyResult(new IqPluginResult("builtin.iq.kiwitimecode", "STATUS", "rate-verify", DateTime.UtcNow));
        _adsbView.ApplyResult(new IqPluginResult("builtin.iq.adsb", "STATUS", "rate-verify", DateTime.UtcNow));
        _lteView.ApplyResult(new IqPluginResult("builtin.iq.lte", "STATUS", "rate-verify", DateTime.UtcNow));

        if (_source is not IConfigurableSampleRateSource configurable) return 0;
        var original = configurable.ConfiguredSampleRate;
        var wide = configurable.SupportedSampleRates.LastOrDefault(rate => rate != original);
        if (wide == 0) return 0;
        ChangeCaptureSampleRateForVerification(wide);
        if (_source.SampleRate != wide || _source.IsRunning) return 116;
        ToggleReceiver();
        if (!_source.IsRunning) return 117;
        ChangeCaptureSampleRateForVerification(original);
        if (_source.SampleRate != original) return 118;
        if (_source.IsRunning) ToggleReceiver();
        return _source.IsRunning ? 119 : 0;
    }

    private void ChangeCaptureSampleRateForVerification(int requestedRate)
    {
        if (_source is not IConfigurableSampleRateSource configurable) return;
        var wasRunning = _source.IsRunning;
        var oldSampleRate = _source.SampleRate;
        var uiTimerWasRunning = _uiTimer.Enabled;
        _uiTimer.Stop();
        _pluginUiSuspended = true;
        PauseDecoderPlugins();
        try
        {
            if (wasRunning) ToggleReceiver();
            configurable.ConfiguredSampleRate = requestedRate;
            _appSettings.HardwareSampleRates[_source.Name] = requestedRate;
            _sampleRateValueLabel.Text = $"{_source.SampleRate / 1_000_000d:0.000} MS/s";
            if (_viewBandwidth >= oldSampleRate)
            {
                _viewBandwidth = requestedRate;
                _viewCenterFrequency = _rfCenterFrequency;
            }
            else
            {
                _viewBandwidth = Math.Clamp(_viewBandwidth, MinimumViewBandwidth(), requestedRate);
                _viewCenterFrequency = ClampViewCenter(_viewCenterFrequency, _viewBandwidth);
            }
            UpdateAfPluginDisplayLayout();
            ApplyAfPluginActivation();
            ApplyIqPluginActivation();
            if (_source is not IFixedCenterFrequencySampleSource)
            {
                ApplyLogicalCenterToSource();
                _spectrumPipeline.Reset();
                Interlocked.Exchange(ref _pendingSpectrum, null);
                ConfigureDisplay();
            }
            ApplyDisplayPlugins(_pluginSelection, false);
            UpdateRxPanel();
            if (wasRunning) ToggleReceiver();
        }
        finally
        {
            _pluginUiSuspended = false;
            if (uiTimerWasRunning || _source.IsRunning) _uiTimer.Start();
        }
    }
}
