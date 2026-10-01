using NeuroSatellite.Controls;
using NeuroSatellite.Satellite;
using NeuroSDR.Settings;
using System.Globalization;

namespace NeuroSDR.Controls;

/// <summary>
/// Beginner-friendly satellite pass view: world map, polar sky plot, and visible-pass list.
/// </summary>
internal sealed class SatelliteTrackingPanel : UserControl
{
    private readonly SatelliteManagerControl _manager = new() { Visible = false, Height = 0, Dock = DockStyle.Bottom };
    private const int MinMapWidth = 280;
    private const int MinSkySide = 180;
    private const int MaxSkySide = 300;
    private readonly Panel _center = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(4, 10, 15) };
    private readonly OfflineWorldMapControl _map = new() { Dock = DockStyle.Fill, ShowLegend = false };
    private readonly Panel _skyHost = new() { BackColor = Color.FromArgb(4, 10, 15) };
    private readonly Panel _skySquareHost = new() { BackColor = Color.FromArgb(4, 10, 15) };
    private readonly Panel _mapPanel = new()
    {
        BackColor = Color.FromArgb(4, 10, 15),
        Padding = new Padding(4, 4, 6, 4),
        MinimumSize = new Size(MinMapWidth, 160)
    };
    private readonly SatelliteSkyViewControl _sky = new();
    private readonly DopplerStripControl _dopplerStrip = new() { Dock = DockStyle.Top, Height = 34 };
    private readonly ListView _passList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(3, 14, 21),
        ForeColor = Color.FromArgb(215, 227, 235),
        Font = new Font("Segoe UI", 8.5f)
    };
    private readonly Label _dopplerLegend = new()
    {
        Dock = DockStyle.Bottom,
        Height = 20,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(9, 24, 32),
        ForeColor = Color.FromArgb(148, 190, 210),
        Font = new Font("Segoe UI", 7.5f),
        Padding = new Padding(8, 0, 0, 0),
        Text = "Blue = frequency drops as satellite moves away · Green = rises as it approaches"
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Top,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        BackColor = Color.FromArgb(13, 31, 41),
        ForeColor = Color.FromArgb(190, 220, 235),
        Font = new Font("Segoe UI Semibold", 8f),
        Padding = new Padding(8, 0, 0, 0)
    };
    private readonly Button _loadButton;
    private readonly Button _visibleButton;
    private readonly Button _scheduleButton;
    private readonly Button _clearPriorityButton;
    private readonly Button _catalogButton;
    private readonly CheckBox _autoTrackCheck;
    private readonly CheckBox _autoRefreshCheck;
    private readonly CheckBox _frequencyOnlyCheck;
    private readonly Label _soloBanner;
    private readonly ComboBox _homeBox;
    private readonly Button _homeEditButton;
    private readonly Button _homeMapButton;
    private readonly NumericUpDown _latBox;
    private readonly NumericUpDown _lonBox;
    private readonly NumericUpDown _altBox;
    private List<SatelliteHomeLocation> _homes = [];
    private List<SatelliteHomeLocation> _remoteHomes = [];
    private List<SatelliteTrackPreference> _trackPreferences = [];
    private ObserverLocation _observer = new(0, 0, 0);
    private string _locationLabel = "Home";
    private bool _loading;
    private bool _followRemoteSdrQth = true;
    private bool _showUpcomingOnly;
    private string _lastTrackingSignature = string.Empty;
    private bool _forcePassRebuild;
    private int? _selectedNorad;
    private int? _soloNorad;
    private string _soloName = "";
    private List<SatelliteInfo> _lastCatalog = [];
    private bool _soloTrack;
    private bool _autoSdrHandoff;
    private int _handoffMinElevation = 12;
    private int _handoffSnrDb = 6;
    private Font? _selectedPassFont;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<SatelliteInfo, bool>? IsReceivableOnCurrentSdr { get; set; }
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private string? _lastDatasetStatusLine;
    private DateTime? _lastPositionRefreshLocal;
    private int _visibleCount;
    private int _upcomingCount;
    private readonly System.Windows.Forms.Timer _statusClockTimer = new() { Interval = 1000 };

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int CatalogRefreshHours { get; set; } = 12;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int FrequencyRefreshHours { get; set; } = 24;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int AmsatStatusRefreshMinutes { get; set; } = 15;

    public SatelliteTrackingPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(4, 10, 15);
        _loadButton = MakeButton("Refresh", 8, 88);
        _visibleButton = MakeButton("Visible now", 100, 100);
        _scheduleButton = MakeButton("Upcoming", 204, 88);
        _clearPriorityButton = MakeButton("Clear priority", 296, 108);
        _catalogButton = MakeButton("Sat info", 0, 88);
        _catalogButton.Dock = DockStyle.Right;
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(13, 31, 41) };
        toolbar.Controls.Add(_catalogButton);
        toolbar.Controls.AddRange([_loadButton, _visibleButton, _scheduleButton, _clearPriorityButton]);

        _autoTrackCheck = new CheckBox
        {
            Text = "AUTO TRACKING",
            AutoSize = true,
            Dock = DockStyle.Right,
            ForeColor = Color.FromArgb(216, 230, 240),
            Font = new Font("Segoe UI Semibold", 8f),
            Padding = new Padding(0, 0, 8, 0)
        };
        var mapHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            BackColor = Color.FromArgb(10, 26, 34),
            Padding = new Padding(8, 4, 8, 0)
        };
        _autoRefreshCheck = new CheckBox
        {
            Text = "AUTO REFRESH",
            AutoSize = true,
            Dock = DockStyle.Right,
            ForeColor = Color.FromArgb(216, 230, 240),
            Font = new Font("Segoe UI Semibold", 8f),
            Padding = new Padding(0, 0, 12, 0)
        };
        _frequencyOnlyCheck = new CheckBox
        {
            Text = "FREQ ONLY",
            AutoSize = true,
            Dock = DockStyle.Right,
            Checked = true,
            ForeColor = Color.FromArgb(216, 230, 240),
            Font = new Font("Segoe UI Semibold", 8f),
            Padding = new Padding(0, 0, 12, 0)
        };
        mapHeader.Controls.Add(_frequencyOnlyCheck);
        mapHeader.Controls.Add(_autoRefreshCheck);
        mapHeader.Controls.Add(_autoTrackCheck);
        _soloBanner = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(255, 190, 90, 255),
            Font = new Font("Segoe UI Semibold", 8f),
            Cursor = Cursors.Hand,
            AutoEllipsis = true
        };
        mapHeader.Controls.Add(_soloBanner);
        var headerTips = new ToolTip();
        headerTips.SetToolTip(_frequencyOnlyCheck, "Show only satellites with published TX/RX frequencies");
        headerTips.SetToolTip(_autoRefreshCheck, "Rebuild the pass list as satellites rise and set");
        headerTips.SetToolTip(_autoTrackCheck, "Tune MAIN to the highest-priority visible satellite");
        headerTips.SetToolTip(_soloBanner, "Double-click to open this satellite and change solo tracking");

        _homeBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(5, 17, 25),
            ForeColor = Color.FromArgb(207, 224, 235),
            FlatStyle = FlatStyle.Flat
        };
        _homeEditButton = new Button
        {
            Text = "Edit",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 66, 83),
            ForeColor = Color.FromArgb(222, 233, 240)
        };
        _homeMapButton = new Button
        {
            Text = "Map",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 66, 83),
            ForeColor = Color.FromArgb(222, 233, 240)
        };
        _latBox = CoordBox(-90, 90, 0m, 4);
        _lonBox = CoordBox(-180, 180, 0m, 4);
        _altBox = CoordBox(0, 4000, 50, 0);
        var locationBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            BackColor = Color.FromArgb(10, 26, 34),
            ColumnCount = 10,
            RowCount = 1,
            Padding = new Padding(8, 4, 8, 4)
        };
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        locationBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        locationBar.Controls.Add(MakeCaption("HOME"), 0, 0);
        locationBar.Controls.Add(_homeBox, 1, 0);
        locationBar.Controls.Add(_homeEditButton, 2, 0);
        locationBar.Controls.Add(_homeMapButton, 3, 0);
        locationBar.Controls.Add(MakeCaption("LAT"), 4, 0);
        locationBar.Controls.Add(_latBox, 5, 0);
        locationBar.Controls.Add(MakeCaption("LON"), 6, 0);
        locationBar.Controls.Add(_lonBox, 7, 0);
        locationBar.Controls.Add(_altBox, 8, 0);
        locationBar.Controls.Add(MakeCaption("m"), 9, 0);
        _homeBox.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            ApplySelectedHome(fireRemoteSelect: true);
        };
        _homeEditButton.Click += (_, _) => EditHomes();
        _homeMapButton.Click += (_, _) => PickHomeOnMap();
        foreach (var box in new[] { _latBox, _lonBox, _altBox })
            box.ValueChanged += (_, _) => PublishObserverChange();

        _passList.Columns.Add("Satellite", 132);
        _passList.Columns.Add("State", 58);
        _passList.Columns.Add("El°", 38, HorizontalAlignment.Right);
        _passList.Columns.Add("RX MHz", 88, HorizontalAlignment.Right);
        _passList.Columns.Add("Doppler", 68, HorizontalAlignment.Right);
        _passList.Columns.Add("When", 96, HorizontalAlignment.Center);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 168 };
        bottom.Controls.Add(_passList);
        bottom.Controls.Add(_dopplerStrip);
        bottom.Controls.Add(_dopplerLegend);
        _skySquareHost.Controls.Add(_sky);
        _skyHost.Controls.Add(_skySquareHost);
        _mapPanel.Controls.Add(_map);
        _mapPanel.Controls.Add(mapHeader);
        _center.Controls.AddRange([_skyHost, _mapPanel]);
        _center.Resize += (_, _) => LayoutVisualizerArea();
        Controls.Add(_center);
        Controls.Add(bottom);
        Controls.Add(_manager);
        Controls.Add(toolbar);
        Controls.Add(locationBar);
        Controls.Add(_status);
        _sky.RefreshInterval = TimeSpan.FromSeconds(2);
        _sky.AutoRefreshEnabled = false;
        _sky.ShowSummary = false;
        _sky.RefreshRequested += (_, _) => RefreshPositionsRequested?.Invoke(this, EventArgs.Empty);
        _sky.SatelliteDoubleClicked += (_, satellite) => ShowSatelliteDetails(satellite);
        _map.SatelliteDoubleClicked += (_, satellite) => ShowSatelliteDetails(satellite);
        _map.SiteSelected += (_, site) => SelectHomeFromMap(site.Id);
        _map.SiteDoubleClicked += (_, site) => SelectHomeFromMap(site.Id);
        _soloBanner.DoubleClick += (_, _) => OpenSoloSatelliteDetails();
        _soloBanner.MouseDoubleClick += (_, _) => OpenSoloSatelliteDetails();
        _loadButton.Click += async (_, _) => await EnsureSatellitesLoadedAsync(
            CatalogRefreshHours,
            FrequencyRefreshHours,
            AmsatStatusRefreshMinutes,
            forceRefresh: true);
        _visibleButton.Click += async (_, _) => await ShowVisibleAsync();
        _scheduleButton.Click += async (_, _) => await ShowUpcomingAsync();
        _catalogButton.Click += (_, _) => OpenSatelliteCatalog();
        var catalogTip = new ToolTip();
        catalogTip.SetToolTip(_catalogButton, "All satellites: frequency, status, and receivable WebSDR / Kiwi sites");
        _autoTrackCheck.Checked = true;
        _autoTrackCheck.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            AutoTrackChanged?.Invoke(this, _autoTrackCheck.Checked);
        };
        _autoRefreshCheck.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _sky.AutoRefreshEnabled = _autoRefreshCheck.Checked;
            if (_autoRefreshCheck.Checked)
                _forcePassRebuild = true;
            AutoRefreshChanged?.Invoke(this, _autoRefreshCheck.Checked);
        };
        _frequencyOnlyCheck.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _forcePassRebuild = true;
            FrequencyFilterChanged?.Invoke(this, _frequencyOnlyCheck.Checked);
            SatellitesLoaded?.Invoke(this, EventArgs.Empty);
        };
        _passList.SelectedIndexChanged += (_, _) =>
        {
            if (_passList.SelectedItems.Count == 0) return;
            if (_passList.SelectedItems[0].Tag is not SatelliteInfo info) return;
            _selectedNorad = info.NoradCatalogId;
            _map.SelectedNorad = _selectedNorad;
            _sky.SelectedNorad = _selectedNorad;
            _map.SoloNorad = SoloNorad;
            _sky.SoloNorad = SoloNorad;
            RefreshPassRowStyles();
            SatelliteSelected?.Invoke(this, info);
        };
        _passList.DoubleClick += (_, _) =>
        {
            if (_passList.SelectedItems.Count == 0) return;
            if (_passList.SelectedItems[0].Tag is SatelliteInfo info)
                ShowSatelliteDetails(info);
        };
        SetStatus("Select the SATELLITE scene to load passes automatically.");
        _statusClockTimer.Tick += (_, _) => RefreshStatusLine();
        VisibleChanged += (_, _) => _statusClockTimer.Enabled = Visible;
        LayoutVisualizerArea();
    }

    private void LayoutVisualizerArea()
    {
        if (_center.ClientSize.Width < MinSkySide + MinMapWidth || _center.ClientSize.Height < MinSkySide)
            return;

        var height = _center.ClientSize.Height;
        var width = _center.ClientSize.Width;
        var maxSkyFromWidth = (int)(width * 0.38);
        var skySide = Math.Clamp(Math.Min(Math.Min(height, MaxSkySide), maxSkyFromWidth), MinSkySide, width - MinMapWidth);
        var mapWidth = width - skySide;
        if (mapWidth < MinMapWidth)
        {
            mapWidth = MinMapWidth;
            skySide = Math.Max(MinSkySide, width - mapWidth);
        }

        _skyHost.SetBounds(0, 0, skySide, height);
        _skySquareHost.SetBounds(0, 0, skySide, height);
        _mapPanel.SetBounds(skySide, 0, width - skySide, height);

        var square = Math.Min(_skySquareHost.ClientSize.Width, _skySquareHost.ClientSize.Height);
        if (square < 40) return;
        var x = (_skySquareHost.ClientSize.Width - square) / 2;
        var y = (_skySquareHost.ClientSize.Height - square) / 2;
        _sky.SetBounds(x, y, square, square);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutVisualizerArea();
    }

    public event EventHandler? RefreshPositionsRequested;
    public event EventHandler<bool>? AutoTrackChanged;
    public event EventHandler<bool>? AutoRefreshChanged;
    public event EventHandler<bool>? FrequencyFilterChanged;
    public event EventHandler? SoloSettingsChanged;
    public event EventHandler<SatelliteInfo>? SatelliteSelected;
    public event EventHandler<SatelliteCatalogPick>? CatalogHandoverRequested;

    public void FocusSatellite(SatelliteInfo satellite)
    {
        _selectedNorad = satellite.NoradCatalogId;
        _map.SelectedNorad = _selectedNorad;
        _sky.SelectedNorad = _selectedNorad;
        RestorePassSelection(_selectedNorad);
        RefreshPassRowStyles();
        SatelliteSelected?.Invoke(this, satellite);
    }

    private void OpenSatelliteCatalog()
    {
        var satellites = _manager.Satellites.Count > 0 ? _manager.Satellites : _lastCatalog;
        if (satellites.Count == 0)
        {
            SetStatus("Load satellites first (Refresh).");
            return;
        }
        if (!SatelliteCatalogDialog.TryPick(
                FindForm()!, satellites, _manager, NeuroSDR.Hardware.RemoteSdrCatalog.LoadDirectory(), out var pick))
            return;
        CatalogHandoverRequested?.Invoke(this, pick);
    }
    public event EventHandler<ObserverLocationSettings>? ObserverLocationChanged;
    public SatelliteManagerControl Manager => _manager;
    public bool AutoTrackEnabled => _autoTrackCheck.Checked;
    public bool AutoRefreshEnabled => _autoRefreshCheck.Checked;
    public bool ShowFrequencyOnly => _frequencyOnlyCheck.Checked;
    public bool SoloTrackEnabled => _soloTrack && _soloNorad is > 0;
    public int? SoloNorad => _soloTrack ? _soloNorad : null;
    public bool AutoSdrHandoffEnabled => _autoSdrHandoff;
    public int HandoffMinElevation => _handoffMinElevation;
    public int HandoffSnrDb => _handoffSnrDb;
    public int? SelectedNorad => _selectedNorad;

    public void LoadObserverSettings(AppSettings settings)
    {
        _loading = true;
        _observer = new ObserverLocation(settings.SatelliteLatitude, settings.SatelliteLongitude, settings.SatelliteAltitudeMeters);
        _locationLabel = settings.SatelliteLocationLabel;
        _latBox.Value = (decimal)Math.Clamp(settings.SatelliteLatitude, (double)_latBox.Minimum, (double)_latBox.Maximum);
        _lonBox.Value = (decimal)Math.Clamp(settings.SatelliteLongitude, (double)_lonBox.Minimum, (double)_lonBox.Maximum);
        _altBox.Value = (decimal)Math.Clamp(settings.SatelliteAltitudeMeters, (double)_altBox.Minimum, (double)_altBox.Maximum);
        _loading = false;
    }

    public ObserverLocation Observer => _observer;

    public void SetAutoTrack(bool enabled)
    {
        _loading = true;
        _autoTrackCheck.Checked = enabled;
        _loading = false;
    }

    public void SetAutoRefresh(bool enabled)
    {
        _loading = true;
        _autoRefreshCheck.Checked = enabled;
        _sky.AutoRefreshEnabled = enabled;
        _loading = false;
    }

    public void SetShowFrequencyOnly(bool enabled)
    {
        _loading = true;
        _frequencyOnlyCheck.Checked = enabled;
        _loading = false;
    }

    public void SetSoloTrack(bool enabled, int norad = 0)
    {
        _soloTrack = enabled && norad > 0;
        _soloNorad = _soloTrack ? norad : null;
        ApplySoloVisuals();
    }

    public void SetAutoSdrHandoff(bool enabled, int minElevation, int snrDb)
    {
        _autoSdrHandoff = enabled;
        _handoffMinElevation = Math.Clamp(minElevation, 0, 45);
        _handoffSnrDb = Math.Clamp(snrDb, 0, 30);
    }

    public void UpdateTrackingView(IReadOnlyList<SatelliteInfo> satellites)
    {
        IEnumerable<SatelliteInfo> source = satellites;
        if (_frequencyOnlyCheck.Checked)
            source = source.Where(item => item.HasFrequencyInfo);
        var catalog = source.ToList();
        _lastCatalog = catalog;
        var visible = catalog.Where(item => item.IsAboveHorizon && item.HasTle)
            .OrderBy(item => TrackGroup(item))
            .ThenByDescending(item => TrackPriorityValue(item))
            .ThenByDescending(item => item.RecentlyHeard)
            .ThenByDescending(item => item.ElevationDegrees ?? -90)
            .ToList();
        var mapSatellites = catalog
            .Where(item => item.HasTle && item.CurrentGeographicPosition is not null)
            .ToList();
        var upcoming = catalog
            .Where(item => !item.IsAboveHorizon && item.HasTle && item.NextVisibilityAt.HasValue)
            .OrderBy(item => TrackGroup(item))
            .ThenByDescending(item => TrackPriorityValue(item))
            .ThenBy(item => item.NextVisibilityAt)
            .Take(12)
            .ToList();
        _visibleCount = visible.Count;
        _upcomingCount = catalog.Count(item =>
            !item.IsAboveHorizon && item.HasTle && item.NextVisibilityAt.HasValue);
        _map.SetTrackingData(_observer, mapSatellites);
        _map.SelectedNorad = _selectedNorad;
        _map.SoloNorad = SoloNorad;
        RefreshMapSites();
        _sky.SetSatellites(visible);
        _sky.SelectedNorad = _selectedNorad;
        _sky.SoloNorad = SoloNorad;
        if (SoloNorad is int soloId)
        {
            var named = catalog.Concat(_manager.Satellites)
                .FirstOrDefault(item => item.NoradCatalogId == soloId);
            if (named is not null) _soloName = named.Name;
        }
        UpdateSoloBanner();
        var dopplerTracks = visible
            .Select(item =>
            {
                var nominal = item.Radio?.GetReceiveFrequencyHz() ?? 0;
                var shift = item.CurrentLookAngle is not null && nominal > 0
                    ? item.CurrentLookAngle.CalculateDopplerShiftHz(nominal)
                    : 0;
                return new DopplerStripControl.DopplerTrack(ShortName(item.Name), shift, nominal);
            })
            .Where(track => track.NominalHz > 0)
            .ToList();
        _dopplerStrip.SetTracks(dopplerTracks);
        var signature = BuildTrackingSignature(visible, upcoming);
        var rebuild = _forcePassRebuild || _passList.Items.Count == 0 ||
                      signature != _lastTrackingSignature;
        _forcePassRebuild = false;
        if (rebuild)
        {
            _lastTrackingSignature = signature;
            RebuildPassList(visible, upcoming);
        }
        else
            UpdatePassListInPlace(visible, upcoming);
        MarkPositionsRefreshed();
    }

    private void MarkPositionsRefreshed()
    {
        _lastPositionRefreshLocal = DateTime.Now;
        RefreshStatusLine();
    }

    private void RefreshStatusLine()
    {
        if (_showUpcomingOnly)
        {
            _status.Text = $"{_upcomingCount} upcoming pass(es) from {_locationLabel}";
            return;
        }

        var now = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var updated = _lastPositionRefreshLocal?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";
        var catalog = string.IsNullOrWhiteSpace(_lastDatasetStatusLine) ? "" : $" · {_lastDatasetStatusLine}";
        _status.Text = $"{now} · Positions {updated} · {_visibleCount} visible · {_upcomingCount} upcoming{catalog}";
    }

    public async Task EnsureSatellitesLoadedAsync(
        int tleRefreshHours,
        int frequencyRefreshHours,
        int amsatStatusRefreshMinutes,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken);
        _loadButton.Enabled = false;
        try
        {
            _showUpcomingOnly = false;
            var options = new SatelliteDatasetOptions(
                TimeSpan.FromHours(Math.Clamp(tleRefreshHours, 1, (int)SatelliteCachePolicy.MaximumTleMaxAge.TotalHours)),
                TimeSpan.FromHours(Math.Clamp(frequencyRefreshHours, 1, 336)),
                TimeSpan.FromMinutes(Math.Clamp(amsatStatusRefreshMinutes, 5, 240)),
                ForceRefresh: forceRefresh);
            if (_manager.Satellites.Count == 0)
                SetStatus("Loading satellites...");
            var metadata = await _manager.EnsureSatelliteDatasetAsync(options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _manager.UpdatePositionsAsync(
                _observer, TimeSpan.FromHours(24), predictPasses: true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _lastDatasetStatusLine = metadata.FormatStatusLine();
            _forcePassRebuild = true;
            SatellitesLoaded?.Invoke(this, EventArgs.Empty);
            MarkPositionsRefreshed();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetStatus($"Satellite load failed: {exception.Message}");
        }
        finally
        {
            _loadButton.Enabled = true;
            _loadGate.Release();
        }
    }

    public async Task LoadSatellitesAsync(
        int tleRefreshHours = 12,
        int frequencyRefreshHours = 24,
        int amsatStatusRefreshMinutes = 15)
    {
        await EnsureSatellitesLoadedAsync(
            tleRefreshHours, frequencyRefreshHours, amsatStatusRefreshMinutes, forceRefresh: true);
    }

    public async Task ShowVisibleAsync()
    {
        try
        {
            _showUpcomingOnly = false;
            _visibleButton.Enabled = false;
            _manager.DisplayFilter = new SatelliteDisplayFilter(VisibleOnly: true, HasTleOnly: true);
            await _manager.UpdatePositionsAsync(_observer, TimeSpan.FromHours(24));
            _forcePassRebuild = true;
            SatellitesLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            SetStatus($"Refresh failed: {exception.Message}");
        }
        finally { _visibleButton.Enabled = true; }
    }

    public async Task ShowUpcomingAsync()
    {
        try
        {
            _showUpcomingOnly = true;
            _scheduleButton.Enabled = false;
            _manager.DisplayFilter = new SatelliteDisplayFilter(HasTleOnly: true);
            await _manager.UpdatePositionsAsync(_observer, TimeSpan.FromHours(24));
            _forcePassRebuild = true;
            SatellitesLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            SetStatus($"Schedule failed: {exception.Message}");
        }
        finally { _scheduleButton.Enabled = true; }
    }

    public async Task RefreshPositionsAsync()
    {
        if (_manager.Satellites.Count == 0) return;
        await _manager.UpdatePositionsAsync(_observer, TimeSpan.FromHours(24), predictPasses: false);
        SatellitesLoaded?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SatellitesLoaded;
    public event EventHandler? TrackPreferencesChanged;

    public IReadOnlyList<SatelliteTrackPreference> TrackPreferences => _trackPreferences;

    public void SetHomes(IEnumerable<SatelliteHomeLocation> homes, string? selectedHomeId)
    {
        _homes = homes.Where(item => !item.IsRemoteSdr).Select(item => item.Clone()).ToList();
        RefreshHomeCombo(selectedHomeId, fireRemoteSelect: false);
    }

    /// <summary>
    /// Inject remote WebSDR / Kiwi / OpenWebRX stations (with GPS) into the HOME list for
    /// satellite-through-web-SDR listening from that station's QTH.
    /// </summary>
    public void SetRemoteSdrHomes(IEnumerable<SatelliteHomeLocation> remoteHomes)
    {
        var keepId = SelectedHomeId;
        _remoteHomes = remoteHomes
            .Where(item => item.IsRemoteSdr &&
                           item.Latitude is >= -90 and <= 90 &&
                           item.Longitude is >= -180 and <= 180)
            .Select(item => item.Clone())
            .ToList();
        RefreshHomeCombo(keepId, fireRemoteSelect: false);
        RefreshMapSites();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool FollowRemoteSdrQth
    {
        get => _followRemoteSdrQth;
        set => _followRemoteSdrQth = value;
    }

    /// <summary>
    /// Point the satellite observer at a remote SDR ground station (Doppler / pass geometry).
    /// Does not trigger a remote reconnect — caller already owns the active site.
    /// </summary>
    public bool ApplyRemoteSdrObserver(string protocol, string name, string url, double latitude, double longitude, double altitudeMeters = 50)
    {
        if (!_followRemoteSdrQth) return false;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) return false;

        var id = RemoteHomeId(url);
        var label = $"{ShortProtocol(protocol)} · {name}";
        var existing = _remoteHomes.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new SatelliteHomeLocation
            {
                Id = id,
                Name = label,
                Latitude = latitude,
                Longitude = longitude,
                AltitudeMeters = altitudeMeters,
                RemoteSdrUrl = url,
                RemoteSdrProtocol = protocol
            };
            _remoteHomes.Insert(0, existing);
            RefreshHomeCombo(id, fireRemoteSelect: false);
        }
        else
        {
            existing.Name = label;
            existing.Latitude = latitude;
            existing.Longitude = longitude;
            existing.AltitudeMeters = altitudeMeters;
            existing.RemoteSdrUrl = url;
            existing.RemoteSdrProtocol = protocol;
            ApplyObserverCoords(existing, fireRemoteSelect: false);
        }

        return true;
    }

    public void SetTrackPreferences(IEnumerable<SatelliteTrackPreference> preferences)
    {
        _trackPreferences = preferences.Select(item => item.Clone()).ToList();
    }

    private void ClearAllPriorityTracking()
    {
        if (_trackPreferences.All(item => !item.PriorityTracking)) return;
        foreach (var item in _trackPreferences)
            item.PriorityTracking = false;
        _forcePassRebuild = true;
        TrackPreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private int TrackGroup(SatelliteInfo item)
    {
        var onSdr = IsReceivableOnCurrentSdr?.Invoke(item) ?? true;
        if (onSdr)
        {
            if (item.NoradCatalogId is int norad && Preference(norad).PriorityTracking) return 0;
            if (item.CanCommunicate) return 1;
            return 2;
        }
        if (item.NoradCatalogId is int id && Preference(id).PriorityTracking) return 3;
        if (item.CanCommunicate) return 4;
        return 5;
    }

    private int TrackPriorityValue(SatelliteInfo item) =>
        item.NoradCatalogId is int norad && Preference(norad).PriorityTracking
            ? Preference(norad).Priority
            : 0;

    private SatelliteTrackPreference Preference(int norad) =>
        _trackPreferences.FirstOrDefault(item => item.NoradCatalogId == norad)
        ?? new SatelliteTrackPreference { NoradCatalogId = norad };

    public List<SatelliteHomeLocation> ExportHomes() =>
        _homes.Where(item => !item.IsRemoteSdr).Select(item => item.Clone()).ToList();

    public string? SelectedHomeId => (_homeBox.SelectedItem as SatelliteHomeLocation)?.Id;

    public SatelliteHomeLocation? TryGetLocalHome()
    {
        if (_homeBox.SelectedItem is SatelliteHomeLocation selected && !selected.IsRemoteSdr)
            return selected;
        return _homes.FirstOrDefault(item => !item.IsRemoteSdr);
    }

    public bool RestoreLocalHome(string? preferredId)
    {
        var home = _homes.FirstOrDefault(item =>
                       !item.IsRemoteSdr &&
                       !string.IsNullOrWhiteSpace(preferredId) &&
                       item.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase))
                   ?? _homes.FirstOrDefault(item => !item.IsRemoteSdr);
        if (home is null) return false;
        if (_homeBox.SelectedItem is SatelliteHomeLocation current &&
            current.Id.Equals(home.Id, StringComparison.OrdinalIgnoreCase) &&
            !current.IsRemoteSdr)
            return true;
        ApplyObserverCoords(home, fireRemoteSelect: false);
        RefreshMapSites();
        return true;
    }

    public SatelliteHomeLocation EnsureLocalHome(string name, double latitude, double longitude, double altitudeMeters)
    {
        var existing = _homes.FirstOrDefault(item => !item.IsRemoteSdr);
        if (existing is not null) return existing;
        var home = new SatelliteHomeLocation
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Local RX" : name,
            Latitude = latitude,
            Longitude = longitude,
            AltitudeMeters = altitudeMeters
        };
        _homes.Add(home);
        RefreshHomeCombo(home.Id, fireRemoteSelect: false);
        return home;
    }

    private static string RemoteHomeId(string url) =>
        "remotesdr:" + url.Trim().TrimEnd('/').ToLowerInvariant();

    private static string ShortProtocol(string protocol) => protocol switch
    {
        "KiwiSDR" => "Kiwi",
        "OpenWebRX" => "OWRX",
        "WebSDR" => "Web",
        _ => protocol
    };

    private IEnumerable<SatelliteHomeLocation> CombinedHomes()
    {
        foreach (var home in _homes.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            yield return home;
        foreach (var home in _remoteHomes)
            yield return home;
    }

    private void ApplySelectedHome(bool fireRemoteSelect)
    {
        if (_homeBox.SelectedItem is not SatelliteHomeLocation home) return;
        ApplyObserverCoords(home, fireRemoteSelect);
    }

    private void ApplyObserverCoords(SatelliteHomeLocation home, bool fireRemoteSelect)
    {
        _loading = true;
        try
        {
            _latBox.Value = (decimal)Math.Clamp(home.Latitude, (double)_latBox.Minimum, (double)_latBox.Maximum);
            _lonBox.Value = (decimal)Math.Clamp(home.Longitude, (double)_lonBox.Minimum, (double)_lonBox.Maximum);
            _altBox.Value = (decimal)Math.Clamp(home.AltitudeMeters, (double)_altBox.Minimum, (double)_altBox.Maximum);
            _locationLabel = home.Name;
            if (_homeBox.SelectedItem is not SatelliteHomeLocation selected ||
                !selected.Id.Equals(home.Id, StringComparison.OrdinalIgnoreCase))
            {
                var match = _homeBox.Items.Cast<object>()
                    .OfType<SatelliteHomeLocation>()
                    .FirstOrDefault(item => item.Id.Equals(home.Id, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    _homeBox.SelectedItem = match;
            }
        }
        finally
        {
            _loading = false;
        }

        PublishObserverChange();
        if (fireRemoteSelect && home.IsRemoteSdr)
            RemoteSdrHomeSelected?.Invoke(this, home);
    }

    private void RefreshHomeCombo(string? selectedHomeId, bool fireRemoteSelect = false)
    {
        _loading = true;
        try
        {
            _homeBox.Items.Clear();
            foreach (var home in CombinedHomes())
                _homeBox.Items.Add(home);
            var all = CombinedHomes().ToList();
            var selected = all.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(selectedHomeId) &&
                item.Id.Equals(selectedHomeId, StringComparison.OrdinalIgnoreCase));
            _homeBox.SelectedItem = selected ?? (_homeBox.Items.Count > 0 ? _homeBox.Items[0] : null);
        }
        finally
        {
            _loading = false;
        }

        if (_homeBox.SelectedItem is SatelliteHomeLocation selectedHome)
            ApplyObserverCoords(selectedHome, fireRemoteSelect);
        RefreshMapSites();
    }

    private void RefreshMapSites()
    {
        var markers = CombinedHomes()
            .Select(home => new SiteMapMarker(
                home.Id,
                home.Name,
                home.IsRemoteSdr ? home.RemoteSdrUrl ?? home.Name : "HOME",
                home.Latitude,
                home.Longitude))
            .ToList();
        _map.SetSiteMarkers(markers, SelectedHomeId);
    }

    private void SelectHomeFromMap(string siteId)
    {
        var home = CombinedHomes().FirstOrDefault(item => item.Id.Equals(siteId, StringComparison.OrdinalIgnoreCase));
        if (home is null) return;
        var match = _homeBox.Items.Cast<object>()
            .OfType<SatelliteHomeLocation>()
            .FirstOrDefault(item => item.Id.Equals(home.Id, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _loading = true;
            try { _homeBox.SelectedItem = match; }
            finally { _loading = false; }
        }
        ApplyObserverCoords(home, fireRemoteSelect: home.IsRemoteSdr);
        RefreshMapSites();
    }

    private void EditHomes()
    {
        if (!SatelliteHomeEditorDialog.Show(FindForm()!, _homes)) return;
        HomesChanged?.Invoke(this, EventArgs.Empty);
        RefreshHomeCombo(SelectedHomeId, fireRemoteSelect: false);
    }

    private void PickHomeOnMap()
    {
        if (!SatelliteMapPickerDialog.TryPick(FindForm()!, (double)_latBox.Value, (double)_lonBox.Value, out var lat, out var lon))
            return;
        _loading = true;
        _latBox.Value = (decimal)Math.Clamp(lat, (double)_latBox.Minimum, (double)_latBox.Maximum);
        _lonBox.Value = (decimal)Math.Clamp(lon, (double)_lonBox.Minimum, (double)_lonBox.Maximum);
        _loading = false;
        PublishObserverChange();
    }

    private void ShowSatelliteDetails(SatelliteInfo satellite)
    {
        if (satellite.NoradCatalogId is not int norad) return;
        var preference = _trackPreferences.FirstOrDefault(item => item.NoradCatalogId == norad)
            ?? new SatelliteTrackPreference { NoradCatalogId = norad };
        var thisIsSolo = _soloTrack && _soloNorad == norad;
        if (!SatelliteDetailDialog.Show(
                FindForm()!, satellite, preference, thisIsSolo, _autoSdrHandoff,
                _handoffMinElevation, _handoffSnrDb,
                out var solo, out var autoSdr, out var minEl, out var snrDb))
            return;
        _trackPreferences.RemoveAll(item => item.NoradCatalogId == norad);
        _trackPreferences.Add(preference);
        if (solo)
        {
            _soloTrack = true;
            _soloNorad = norad;
            _soloName = satellite.Name;
        }
        else if (_soloNorad == norad)
        {
            _soloTrack = false;
            _soloNorad = null;
            _soloName = "";
        }
        _autoSdrHandoff = autoSdr;
        _handoffMinElevation = minEl;
        _handoffSnrDb = snrDb;
        ApplySoloVisuals();
        TrackPreferencesChanged?.Invoke(this, EventArgs.Empty);
        SoloSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSoloSatelliteDetails()
    {
        if (SoloNorad is not int norad) return;
        var satellite = _lastCatalog.FirstOrDefault(item => item.NoradCatalogId == norad)
            ?? _manager.Satellites.FirstOrDefault(item => item.NoradCatalogId == norad);
        if (satellite is not null)
            ShowSatelliteDetails(satellite);
    }

    private void ApplySoloVisuals()
    {
        _map.SoloNorad = SoloNorad;
        _sky.SoloNorad = SoloNorad;
        UpdateSoloBanner();
        RefreshPassRowStyles();
    }

    private void UpdateSoloBanner()
    {
        if (SoloNorad is int && !string.IsNullOrWhiteSpace(_soloName))
        {
            _soloBanner.Text = $"SOLO TRACKING · {_soloName}";
            _soloBanner.ForeColor = Color.FromArgb(255, 210, 90, 255);
            _soloBanner.Cursor = Cursors.Hand;
        }
        else
        {
            _soloBanner.Text = "";
            _soloBanner.Cursor = Cursors.Default;
        }
    }

    public event EventHandler? HomesChanged;
    public event EventHandler<SatelliteHomeLocation>? RemoteSdrHomeSelected;

    private void PublishObserverChange()
    {
        if (_loading) return;
        _observer = new ObserverLocation((double)_latBox.Value, (double)_lonBox.Value, (double)_altBox.Value);
        if (_homeBox.SelectedItem is SatelliteHomeLocation home)
            _locationLabel = home.Name;
        else
            _locationLabel = "Custom";
        ObserverLocationChanged?.Invoke(this, new ObserverLocationSettings(
            _observer.LatitudeDegrees, _observer.LongitudeDegrees, _observer.AltitudeMeters, _locationLabel));
    }

    private void RebuildPassList(IReadOnlyList<SatelliteInfo> visible, IReadOnlyList<SatelliteInfo> upcoming)
    {
        _passList.BeginUpdate();
        try
        {
            _passList.Items.Clear();
            if (!_showUpcomingOnly)
            {
                if (visible.Count == 0)
                    AddSectionRow("— Nothing above horizon right now —");
                foreach (var satellite in visible)
                    AddSatelliteRow(satellite, "LIVE");
            }
            if (_showUpcomingOnly || upcoming.Count > 0)
            {
                if (!_showUpcomingOnly) AddSectionRow("— Upcoming passes (local time) —");
                foreach (var satellite in upcoming)
                    AddSatelliteRow(satellite, "SOON");
            }
            RestorePassSelection(_selectedNorad);
        }
        finally { _passList.EndUpdate(); }
    }

    private void UpdatePassListInPlace(IReadOnlyList<SatelliteInfo> visible, IReadOnlyList<SatelliteInfo> upcoming)
    {
        foreach (ListViewItem row in _passList.Items)
        {
            if (row.Tag is not SatelliteInfo old) continue;
            var next = visible.Concat(upcoming).FirstOrDefault(item => item.NoradCatalogId == old.NoradCatalogId);
            if (next is null) continue;
            row.Tag = next;
            row.SubItems[2].Text = next.IsAboveHorizon ? $"{next.ElevationDegrees:F0}" : "-";
            row.SubItems[3].Text = next.DopplerCorrectedReceiveHz is long hz
                ? $"{hz / 1_000_000d:F6}"
                : next.ReceiveFrequency;
            var nominal = next.Radio?.GetReceiveFrequencyHz();
            var doppler = nominal is long freq && next.CurrentLookAngle is not null
                ? next.CurrentLookAngle.CalculateDopplerShiftHz(freq)
                : 0;
            row.SubItems[4].Text = doppler == 0 ? "-" : $"{doppler:+#;-#;0} Hz";
            ApplyPassRowAppearance(row, next);
        }
    }

    private void RestorePassSelection(int? norad)
    {
        if (norad is null) return;
        foreach (ListViewItem row in _passList.Items)
        {
            if (row.Tag is not SatelliteInfo info || info.NoradCatalogId != norad) continue;
            row.Selected = true;
            row.EnsureVisible();
            break;
        }
    }

    private string BuildTrackingSignature(
        IReadOnlyList<SatelliteInfo> visible,
        IReadOnlyList<SatelliteInfo> upcoming)
    {
        static string Row(SatelliteInfo item, string state) =>
            $"{item.NoradCatalogId}:{state}:{item.RecentlyHeard}:{item.CanCommunicate}:{item.NextVisibilityAt?.ToUnixTimeSeconds() / 60}";
        var rows = new List<string> { _showUpcomingOnly ? "upcoming" : "live" };
        if (!_showUpcomingOnly)
            rows.AddRange(visible.Select(item => Row(item, "LIVE")));
        if (_showUpcomingOnly || upcoming.Count > 0)
            rows.AddRange(upcoming.Select(item => Row(item, "SOON")));
        return string.Join("|", rows);
    }

    private void AddSectionRow(string text)
    {
        _passList.Items.Add(new ListViewItem([text, "", "", "", "", ""])
        {
            ForeColor = Color.FromArgb(110, 140, 155),
            BackColor = Color.FromArgb(8, 20, 28)
        });
    }

    private void AddSatelliteRow(SatelliteInfo satellite, string state)
    {
        var look = satellite.CurrentLookAngle;
        var nominal = satellite.Radio?.GetReceiveFrequencyHz();
        var doppler = nominal is long freq && look is not null ? look.CalculateDopplerShiftHz(freq) : 0;
        var when = state == "LIVE"
            ? "now"
            : satellite.NextVisibilityAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
        var stateLabel = state switch
        {
            "LIVE" when satellite.RecentlyHeard => "LIVE·HEARD",
            _ => state
        };
        var solo = satellite.NoradCatalogId is int sid && sid == _soloNorad && _soloTrack;
        var row = new ListViewItem([
            solo ? $"SOLO · {satellite.Name}" : satellite.Name,
            stateLabel,
            satellite.IsAboveHorizon ? $"{satellite.ElevationDegrees:F0}" : "-",
            satellite.DopplerCorrectedReceiveHz is long hz ? $"{hz / 1_000_000d:F6}" : satellite.ReceiveFrequency,
            doppler == 0 ? "-" : $"{doppler:+#;-#;0} Hz",
            when
        ]) { Tag = satellite };
        if (look is not null)
        {
            row.ForeColor = look.RangeRateKilometersPerSecond switch
            {
                < -0.05 => Color.FromArgb(120, 230, 170),
                > 0.05 => Color.FromArgb(130, 190, 255),
                _ => state == "SOON" ? Color.FromArgb(180, 200, 215) : Color.FromArgb(215, 227, 235)
            };
        }
        else if (state == "SOON")
            row.ForeColor = Color.FromArgb(180, 200, 215);
        ApplyPassRowAppearance(row, satellite);
        _passList.Items.Add(row);
    }

    private void RefreshPassRowStyles()
    {
        foreach (ListViewItem row in _passList.Items)
        {
            if (row.Tag is SatelliteInfo info)
                ApplyPassRowAppearance(row, info);
        }
    }

    private void ApplyPassRowAppearance(ListViewItem row, SatelliteInfo satellite)
    {
        var norad = satellite.NoradCatalogId;
        var solo = norad is int id && id == _soloNorad && _soloTrack;
        var selected = norad is int pick && pick == _selectedNorad;
        row.Text = solo ? $"SOLO · {satellite.Name}" : satellite.Name;
        if (solo)
        {
            _selectedPassFont ??= new Font(_passList.Font, FontStyle.Bold);
            row.Font = _selectedPassFont;
            row.ForeColor = Color.FromArgb(230, 140, 255);
            return;
        }
        if (selected)
        {
            _selectedPassFont ??= new Font(_passList.Font, FontStyle.Bold);
            row.Font = _selectedPassFont;
            row.ForeColor = Color.FromArgb(255, 210, 120);
            return;
        }
        row.Font = _passList.Font;
        var look = satellite.CurrentLookAngle;
        if (look is not null)
        {
            row.ForeColor = look.RangeRateKilometersPerSecond switch
            {
                < -0.05 => Color.FromArgb(120, 230, 170),
                > 0.05 => Color.FromArgb(130, 190, 255),
                _ => satellite.IsAboveHorizon ? Color.FromArgb(215, 227, 235) : Color.FromArgb(180, 200, 215)
            };
        }
        else
            row.ForeColor = Color.FromArgb(180, 200, 215);
    }

    private void SetStatus(string text) => _status.Text = text;

    private static string ShortName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length <= 10 ? trimmed : trimmed[..9] + "…";
    }

    private static Button MakeButton(string text, int x, int width = 118) => new()
    {
        Text = text,
        Location = new Point(x, 3),
        Size = new Size(width, 24),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 66, 83),
        ForeColor = Color.FromArgb(222, 233, 240),
        Font = new Font("Segoe UI Semibold", 8f)
    };

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(130, 165, 182),
        Font = new Font("Segoe UI Semibold", 7f)
    };

    private static NumericUpDown CoordBox(decimal min, decimal max, decimal value, int decimals) => new()
    {
        Minimum = min,
        Maximum = max,
        DecimalPlaces = decimals,
        Increment = decimals == 0 ? 1 : 0.0001m,
        Value = Math.Clamp(value, min, max),
        BackColor = Color.FromArgb(5, 17, 25),
        ForeColor = Color.FromArgb(207, 224, 235),
        BorderStyle = BorderStyle.FixedSingle
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusClockTimer.Dispose();
            _loadGate.Dispose();
            _selectedPassFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed record ObserverLocationSettings(double Latitude, double Longitude, double AltitudeMeters, string Label);
