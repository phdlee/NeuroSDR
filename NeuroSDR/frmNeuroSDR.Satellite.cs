using NeuroSatellite.Satellite;
using NeuroSDR.Controls;
using NeuroSDR.Core;
using NeuroSDR.Hardware;
using NeuroSDR.Settings;

namespace NeuroSDR;

public partial class frmNeuroSDR
{
    private SatelliteTrackingPanel? _satellitePanel;
    private readonly System.Windows.Forms.Timer _satelliteTrackTimer = new() { Interval = 2_000 };
    private readonly System.Windows.Forms.Timer _satelliteCatalogTimer = new() { Interval = 6 * 60 * 60 * 1_000 };
    private readonly Dictionary<int, string> _satelliteSubVfoByNorad = new();
    private int? _activePriorityNorad;
    private int? _listPinnedNorad;
    private int? _autoTrackDemodNorad;
    private bool _handoffBusy;
    private long _lastHandoffTick;
    private bool? _afFilterEnabledBeforeSatellite;
    private bool _suppressAfFilterEvents;
    private CancellationTokenSource? _satelliteLoadCts;
    private string? _lastLocalHomeId;

    private bool IsSatelliteSceneActive =>
        (_rxSceneBox.SelectedItem as RxScene)?.Id is string id && RxScene.IsSatelliteScene(id);

    private void ConfigureSatelliteUi()
    {
        _satellitePanel = new SatelliteTrackingPanel { Visible = false, Dock = DockStyle.Fill };
        _graphPanel.Controls.Add(_satellitePanel);
        _satellitePanel.BringToFront();
        _satellitePanel.RefreshPositionsRequested += (_, _) => _ = RefreshSatelliteTrackingAsync();
        _satellitePanel.SatellitesLoaded += (_, _) => _ = ApplySatelliteTrackingAsync();
        _satellitePanel.AutoTrackChanged += (_, enabled) =>
        {
            _appSettings.SatelliteAutoTrack = enabled;
            SaveSettings();
            SyncSatelliteTrackTimer();
            if (enabled && IsSatelliteSceneActive)
                _ = ApplySatelliteTrackingAsync();
            else
            {
                _autoTrackDemodNorad = null;
                _listPinnedNorad = null;
            }
        };
        _satellitePanel.AutoRefreshChanged += (_, enabled) =>
        {
            _appSettings.SatelliteAutoRefresh = enabled;
            SaveSettings();
            SchedulePersistCurrentSceneLayout();
            SyncSatelliteTrackTimer();
            if (IsSatelliteSceneActive)
                _ = ApplySatelliteTrackingAsync();
        };
        _satellitePanel.FrequencyFilterChanged += (_, enabled) =>
        {
            _appSettings.SatelliteShowFrequencyOnly = enabled;
            SaveSettings();
            SchedulePersistCurrentSceneLayout();
        };
        _satellitePanel.SoloSettingsChanged += (_, _) =>
        {
            _appSettings.SatelliteSoloTrack = _satellitePanel.SoloTrackEnabled;
            _appSettings.SatelliteSoloNorad = _satellitePanel.SoloNorad ?? 0;
            _appSettings.SatelliteAutoSdrHandoff = _satellitePanel.AutoSdrHandoffEnabled;
            _appSettings.SatelliteHandoffMinElevation = _satellitePanel.HandoffMinElevation;
            _appSettings.SatelliteHandoffSnrDb = _satellitePanel.HandoffSnrDb;
            _listPinnedNorad = _satellitePanel.SoloNorad;
            _lastHandoffTick = 0;
            SaveSettings();
            if (IsSatelliteSceneActive)
                _ = ApplySatelliteTrackingAsync();
        };
        _satellitePanel.ObserverLocationChanged += (_, location) =>
        {
            _appSettings.SatelliteLatitude = location.Latitude;
            _appSettings.SatelliteLongitude = location.Longitude;
            _appSettings.SatelliteAltitudeMeters = location.AltitudeMeters;
            _appSettings.SatelliteLocationLabel = location.Label;
            if (_satellitePanel.SelectedHomeId is string homeId)
            {
                _appSettings.SatelliteSelectedHomeId = homeId;
                if (!homeId.StartsWith("remotesdr:", StringComparison.OrdinalIgnoreCase))
                    _lastLocalHomeId = homeId;
            }
            SaveSettings();
            SchedulePersistCurrentSceneLayout();
            _ = RefreshSatelliteTrackingAsync();
        };
        _satellitePanel.HomesChanged += (_, _) =>
        {
            _appSettings.SatelliteHomes = _satellitePanel.ExportHomes();
            if (_satellitePanel.SelectedHomeId is string homeId)
                _appSettings.SatelliteSelectedHomeId = homeId;
            SaveSettings();
        };
        _satellitePanel.RemoteSdrHomeSelected += (_, home) =>
        {
            if (string.IsNullOrWhiteSpace(home.RemoteSdrUrl)) return;
            var url = home.RemoteSdrUrl.Trim();
            if (_source is IRemoteAudioSampleSource remote &&
                string.Equals(remote.ServerUrl.Trim().TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(_webUrlInput.Text.Trim().TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                _source is IRemoteAudioSampleSource { IsConnected: true })
                return;
            EnsureRemoteSourceForProtocol(home.RemoteSdrProtocol);
            _ = ApplyRemoteUrlAsync(url);
        };
        _satellitePanel.TrackPreferencesChanged += (_, _) =>
        {
            _appSettings.SatelliteTrackPreferences = _satellitePanel.TrackPreferences
                .Select(item => item.Clone()).ToList();
            SaveSettings();
            SchedulePersistCurrentSceneLayout();
            if (IsSatelliteSceneActive)
                _ = ApplySatelliteTrackingAsync();
        };
        _satellitePanel.SatelliteSelected += (_, satellite) => TuneSelectedSatellite(satellite);
        _satellitePanel.CatalogHandoverRequested += async (_, pick) =>
        {
            if (pick.Site is { } site)
            {
                await EnsureRemoteSourceForProtocolAsync(site.Protocol).ConfigureAwait(true);
                await ApplyRemoteUrlAsync(site.Url, site.Bands).ConfigureAwait(true);
                try { await _satellitePanel.RefreshPositionsAsync().ConfigureAwait(true); }
                catch { /* still select the satellite */ }
            }
            _satellitePanel.FocusSatellite(pick.Satellite);
        };
        _satellitePanel.IsReceivableOnCurrentSdr = SatelliteInCurrentSdrBand;
        _satelliteTrackTimer.Tick += (_, _) => _ = RefreshSatelliteTrackingAsync();
        _satelliteCatalogTimer.Tick += (_, _) =>
        {
            if (IsSatelliteSceneActive)
                BeginSatelliteAutoLoad();
        };
        ApplySatelliteObserverSummary();
        Shown += (_, _) =>
        {
            if (IsSatelliteSceneActive)
                BeginSatelliteAutoLoad();
        };
    }

    private void ApplySatelliteSceneUi(bool enabled, bool persist)
    {
        if (_satellitePanel is null) return;
        if (enabled)
        {
            ApplySatelliteAudioFilter(suspend: true);
            SyncSatelliteTrackTimer();
            _satelliteCatalogTimer.Start();
            RefreshSatelliteRemoteHomes();
            TrySyncSatelliteObserverFromCurrentRemote();
            BeginSatelliteAutoLoad();
        }
            else
            {
                _satelliteTrackTimer.Stop();
                _satelliteCatalogTimer.Stop();
                _satelliteLoadCts?.Cancel();
                _activePriorityNorad = null;
                ClearSatelliteManagedSubVfos();
            ApplySatelliteAudioFilter(suspend: false);
        }
        ApplySatelliteGraphChrome();
        if (persist)
        {
            SaveSettings();
            SchedulePersistCurrentSceneLayout();
        }
    }

    private void ApplySatelliteGraphChrome()
    {
        var satellite = IsSatelliteSceneActive;
        if (_satellitePanel is not null)
        {
            _satellitePanel.Visible = satellite;
            if (satellite) _satellitePanel.BringToFront();
        }
        if (!_afDisplay.IsDisposed)
            _afDisplay.Visible = !satellite && _demodulator.Mode != RadioMode.WFM;
        if (!_wfmAudioVisual.IsDisposed)
            _wfmAudioVisual.Visible = !satellite && _demodulator.Mode == RadioMode.WFM;
        if (!_wfmStationPanel.IsDisposed)
            _wfmStationPanel.Visible = !satellite && _demodulator.Mode == RadioMode.WFM;
        _afFilterCheck.Visible = !satellite &&
                                 (_demodulator.Mode != RadioMode.WFM || !_appSettings.WfmHideAfPlugins);
        _afDisplayVfoPanel.Visible = !satellite && _afDisplayVfoPanel.Controls.Count > 0;
    }

    private void ApplySatelliteAudioFilter(bool suspend)
    {
        if (suspend)
        {
            if (_afFilterEnabledBeforeSatellite is null)
                _afFilterEnabledBeforeSatellite = _appSettings.AfFilterEnabled;
            _suppressAfFilterEvents = true;
            try { _afFilterCheck.Checked = false; }
            finally { _suppressAfFilterEvents = false; }
            SetAfFilterRuntimeEnabled(false);
            return;
        }

        if (_afFilterEnabledBeforeSatellite is not bool previous) return;
        _suppressAfFilterEvents = true;
        try { _afFilterCheck.Checked = previous; }
        finally { _suppressAfFilterEvents = false; }
        _afFilterEnabledBeforeSatellite = null;
        SetAfFilterRuntimeEnabled(previous);
    }

    private void SetAfFilterRuntimeEnabled(bool enabled)
    {
        foreach (var processor in _audioProcessors)
            processor.AfFilterEnabled = enabled;
        foreach (var receiver in Volatile.Read(ref _subVfoReceivers))
            receiver.Processor.AfFilterEnabled = enabled;
        if (!_afDisplay.IsDisposed && !IsSatelliteSceneActive)
            ConfigureAfDisplay();
    }

    private void BeginSatelliteAutoLoad()
    {
        if (_satellitePanel is null || !IsSatelliteSceneActive) return;

        void StartLoad()
        {
            if (_satellitePanel is null || !IsSatelliteSceneActive || IsDisposed) return;
            _satelliteLoadCts?.Cancel();
            _satelliteLoadCts = new CancellationTokenSource();
            var token = _satelliteLoadCts.Token;
            _ = AutoLoadSatellitesAsync(token);
        }

        if (!IsHandleCreated) return;

        if (InvokeRequired)
            BeginInvoke(StartLoad);
        else
            StartLoad();
    }

    private async Task AutoLoadSatellitesAsync(CancellationToken cancellationToken)
    {
        if (_satellitePanel is null || !IsSatelliteSceneActive) return;
        try
        {
            await _satellitePanel.EnsureSatellitesLoadedAsync(
                _appSettings.SatelliteCacheRefreshHours,
                _appSettings.SatelliteFrequencyRefreshHours,
                _appSettings.SatelliteAmsatStatusRefreshMinutes,
                forceRefresh: false,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            await ApplySatelliteTrackingAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!IsDisposed)
                _statusLabel.Text = $"Satellite load: {exception.Message}";
        }
    }

    private void ApplySatelliteObserverSummary()
    {
        if (_satellitePanel is null) return;
        _satellitePanel.CatalogRefreshHours = _appSettings.SatelliteCacheRefreshHours;
        _satellitePanel.FrequencyRefreshHours = _appSettings.SatelliteFrequencyRefreshHours;
        _satellitePanel.AmsatStatusRefreshMinutes = _appSettings.SatelliteAmsatStatusRefreshMinutes;
        _satellitePanel.SetHomes(_appSettings.SatelliteHomes, _appSettings.SatelliteSelectedHomeId);
        if (!string.IsNullOrWhiteSpace(_appSettings.SatelliteSelectedHomeId) &&
            !_appSettings.SatelliteSelectedHomeId.StartsWith("remotesdr:", StringComparison.OrdinalIgnoreCase))
            _lastLocalHomeId = _appSettings.SatelliteSelectedHomeId;
        _satellitePanel.SetTrackPreferences(_appSettings.SatelliteTrackPreferences);
        _satellitePanel.LoadObserverSettings(_appSettings);
        _satellitePanel.SetAutoTrack(_appSettings.SatelliteAutoTrack);
        _satellitePanel.SetAutoRefresh(_appSettings.SatelliteAutoRefresh);
        _satellitePanel.SetShowFrequencyOnly(_appSettings.SatelliteShowFrequencyOnly);
        _satellitePanel.SetSoloTrack(_appSettings.SatelliteSoloTrack, _appSettings.SatelliteSoloNorad);
        _satellitePanel.SetAutoSdrHandoff(
            _appSettings.SatelliteAutoSdrHandoff,
            _appSettings.SatelliteHandoffMinElevation,
            _appSettings.SatelliteHandoffSnrDb);
        if (_appSettings.SatelliteSoloNorad > 0)
            _listPinnedNorad = _appSettings.SatelliteSoloNorad;
        // Defer remote-SDR home injection until satellite scene is active — avoids
        // filling 300 combo items (and accidental reconnect loops) on every cold start.
        if (IsSatelliteSceneActive)
        {
            RefreshSatelliteRemoteHomes();
            TrySyncSatelliteObserverFromCurrentRemote();
        }
    }

    private void RefreshSatelliteRemoteHomes()
    {
        if (_satellitePanel is null) return;
        var protocol = RemoteSdrCatalog.ProtocolForSourceName(_source.Name);
        IEnumerable<RemoteSdrEntry> candidates = _remoteDirectory.Where(item => item.HasCoordinates);
        if (protocol is not null)
            candidates = candidates.Where(item => item.Protocol == protocol);
        var homes = candidates
            .Take(300)
            .Select(entry => new SatelliteHomeLocation
            {
                Id = "remotesdr:" + entry.Url.Trim().TrimEnd('/').ToLowerInvariant(),
                Name = $"{ShortRemoteProtocol(entry.Protocol)} · {entry.Name}",
                Latitude = entry.Latitude!.Value,
                Longitude = entry.Longitude!.Value,
                AltitudeMeters = entry.AltitudeMeters ?? 50,
                RemoteSdrUrl = entry.Url,
                RemoteSdrProtocol = entry.Protocol
            })
            .ToList();
        _satellitePanel.SetRemoteSdrHomes(homes);
    }

    private static string ShortRemoteProtocol(string protocol) => protocol switch
    {
        "KiwiSDR" => "Kiwi",
        "OpenWebRX" => "OWRX",
        "WebSDR" => "Web",
        _ => protocol
    };

    private void RestoreLocalSatelliteObserver()
    {
        if (_satellitePanel is null) return;
        var preferred = _lastLocalHomeId;
        if (string.IsNullOrWhiteSpace(preferred) &&
            !string.IsNullOrWhiteSpace(_appSettings.SatelliteSelectedHomeId) &&
            !_appSettings.SatelliteSelectedHomeId.StartsWith("remotesdr:", StringComparison.OrdinalIgnoreCase))
            preferred = _appSettings.SatelliteSelectedHomeId;

        if (!_satellitePanel.RestoreLocalHome(preferred))
        {
            var fallback = DefaultLocalObserver();
            var seeded = _satellitePanel.EnsureLocalHome(
                fallback.Label, fallback.Lat, fallback.Lon, fallback.Alt);
            preferred = seeded.Id;
            _satellitePanel.RestoreLocalHome(preferred);
        }

        if (_satellitePanel.TryGetLocalHome() is not { } home) return;
        _appSettings.SatelliteLatitude = home.Latitude;
        _appSettings.SatelliteLongitude = home.Longitude;
        _appSettings.SatelliteAltitudeMeters = home.AltitudeMeters;
        _appSettings.SatelliteLocationLabel = home.Name;
        _appSettings.SatelliteSelectedHomeId = home.Id;
        _lastLocalHomeId = home.Id;
        _appSettings.SatelliteHomes = _satellitePanel.ExportHomes();
    }

    private void TrySyncSatelliteObserverFromCurrentRemote()
    {
        if (_satellitePanel is null || _source is not IRemoteAudioSampleSource remote) return;
        var entry = _siteBox.SelectedItem as RemoteSdrEntry
                    ?? RemoteSdrCatalog.FindByUrl(remote.ServerUrl)
                    ?? RemoteSdrCatalog.FindByUrl(_webUrlInput.Text);
        if (entry is null || !entry.HasCoordinates) return;
        if (_satellitePanel.ApplyRemoteSdrObserver(
                entry.Protocol,
                entry.Name,
                entry.Url,
                entry.Latitude!.Value,
                entry.Longitude!.Value,
                entry.AltitudeMeters ?? 50) && IsSatelliteSceneActive)
        {
            _ = RefreshSatelliteTrackingAsync();
        }
    }

    private void EnsureRemoteSourceForProtocol(string? protocol)
    {
        protocol = RemoteSdrCatalog.NormalizeProtocol(protocol ?? "");
        if (protocol is null) return;
        var wanted = protocol switch
        {
            "WebSDR" => "Virtual WebSDR",
            "KiwiSDR" => "Virtual KiwiSDR",
            "OpenWebRX" => "Virtual OpenWebRX",
            _ => null
        };
        if (wanted is null || _source.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return;
        var index = -1;
        for (var i = 0; i < _sources.Count; i++)
        {
            if (!_sources[i].Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            break;
        }
        if (index < 0) return;
        _suppressSourceChange = true;
        try
        {
            _rxSourceBox.SelectedIndex = index;
        }
        finally { _suppressSourceChange = false; }
        SelectSource();
        UpdateRxPanel();
    }

    private void TuneSelectedSatellite(SatelliteInfo satellite)
    {
        _listPinnedNorad = satellite.NoradCatalogId;
        _activePriorityNorad = satellite.NoradCatalogId;
        _autoTrackDemodNorad = null;
        if (!_source.IsRunning) return;
        var frequency = SatelliteReceiveHz(satellite);
        if (frequency is null or <= 0) return;
        if (!TryApplySatelliteFrequency(satellite, frequency.Value, applyDemodulator: true)) return;
        _statusLabel.Text = $"Tuned to {satellite.Name} · {frequency.Value / 1_000_000d:F6} MHz";
    }

    private async Task RefreshSatelliteTrackingAsync()
    {
        if (_satellitePanel is null || !IsSatelliteSceneActive) return;
        try { await _satellitePanel.RefreshPositionsAsync(); }
        catch (Exception exception) { _statusLabel.Text = $"Satellite refresh: {exception.Message}"; }
        await ApplySatelliteTrackingAsync();
    }

    private async Task ApplySatelliteTrackingAsync()
    {
        if (_satellitePanel is null || !IsSatelliteSceneActive) return;
        var satellites = _satellitePanel.Manager.Satellites;
        _satellitePanel.UpdateTrackingView(satellites);
        if (!_source.IsRunning) return;
        // Doppler steps and satellite demod changes restart DMR/D-STAR acquisition.
        // Leave a digital-voice session on the frequency the operator selected.
        if (RadioModes.IsFmDigitalVoice(_demodulator.Mode)) return;
        var soloNorad = ResolveSoloNorad();
        var soloSat = soloNorad is int id
            ? satellites.FirstOrDefault(item => item.NoradCatalogId == id)
            : null;
        if (soloSat is not null && _satellitePanel.AutoSdrHandoffEnabled)
            await MaybeHandoffRemoteSdrAsync(soloSat).ConfigureAwait(true);
        satellites = _satellitePanel.Manager.Satellites;
        if (!_satellitePanel.AutoTrackEnabled) return;
        var visible = satellites
            .Where(item => item.IsAboveHorizon && item.HasTle && SatelliteReceiveHz(item) is > 0 &&
                           SatelliteInCurrentSdrBand(item))
            .ToList();
        if (soloNorad is int solo)
            visible = visible.Where(item => item.NoradCatalogId == solo).ToList();
        if (visible.Count == 0)
        {
            _activePriorityNorad = null;
            if (soloSat is not null && _satellitePanel.AutoSdrHandoffEnabled)
                return;
            var nextPriority = satellites
                .Where(item => item.NoradCatalogId is int norad && GetTrackPreference(norad).PriorityTracking &&
                               item.NextVisibilityAt.HasValue)
                .OrderBy(item => item.NextVisibilityAt)
                .FirstOrDefault();
            if (nextPriority is not null)
                _statusLabel.Text =
                    $"{nextPriority.Name} below horizon · next {nextPriority.NextVisibilityDisplay}";
            else if (satellites.Any(item => item.IsAboveHorizon && item.HasTle))
                _statusLabel.Text = "Visible satellites are outside this SDR's frequency range";
            return;
        }
        var ordered = OrderVisibleForTracking(visible);
        if (ordered.Count == 0) return;
        ApplySatelliteRfPlan(ordered);
        EnsureSatelliteAfPlugins(ordered);
        var occupancy = Volatile.Read(ref _channelOccupiedFlag) != 0 ? "SIG" : "NOISE";
        var snr = Volatile.Read(ref _channelSnrDb);
        _statusLabel.Text =
            $"{ordered[0].Name} · {occupancy} {snr:0.0} dB · el {ordered[0].ElevationDegrees:F0}°";
    }

    private int? ResolveSoloNorad()
    {
        if (_satellitePanel is null || !_satellitePanel.SoloTrackEnabled) return null;
        return _satellitePanel.SoloNorad;
    }

    private SatelliteTrackPreference GetTrackPreference(int noradCatalogId)
        => _appSettings.SatelliteTrackPreferences.FirstOrDefault(item => item.NoradCatalogId == noradCatalogId)
           ?? new SatelliteTrackPreference { NoradCatalogId = noradCatalogId };

    private void SyncSatelliteTrackTimer()
    {
        if (_satellitePanel is not null && IsSatelliteSceneActive &&
            _satellitePanel.AutoTrackEnabled && !_satellitePanel.AutoRefreshEnabled)
            _satelliteTrackTimer.Start();
        else
            _satelliteTrackTimer.Stop();
    }

    private List<SatelliteInfo> OrderVisibleForTracking(IReadOnlyList<SatelliteInfo> visible)
    {
        if (_listPinnedNorad is int pin && visible.All(item => item.NoradCatalogId != pin))
            _listPinnedNorad = null;
        var ordered = visible
            .Where(item => item.NoradCatalogId.HasValue)
            .OrderBy(item => item.NoradCatalogId == _listPinnedNorad ? 0 : 1)
            .ThenBy(item => TrackingGroup(item))
            .ThenByDescending(item => GetTrackPreference(item.NoradCatalogId!.Value).Priority)
            .ThenByDescending(item => item.NoradCatalogId == _activePriorityNorad ? 1 : 0)
            .ThenByDescending(item => item.ElevationDegrees ?? -90)
            .ToList();
        if (ordered.Count > 0)
            _activePriorityNorad = ordered[0].NoradCatalogId;
        return ordered;
    }

    private int TrackingGroup(SatelliteInfo item)
    {
        var onSdr = SatelliteInCurrentSdrBand(item);
        if (onSdr)
        {
            if (item.NoradCatalogId is int norad && GetTrackPreference(norad).PriorityTracking) return 0;
            if (item.CanCommunicate) return 1;
            return 2;
        }
        if (item.NoradCatalogId is int id && GetTrackPreference(id).PriorityTracking) return 3;
        if (item.CanCommunicate) return 4;
        return 5;
    }

    private bool SatelliteInCurrentSdrBand(SatelliteInfo item)
    {
        var hz = item.Radio?.GetReceiveFrequencyHz() ?? SatelliteReceiveHz(item);
        if (hz is not > 0) return false;
        if (_source is not IRemoteAudioSampleSource) return true;
        var entry = CurrentRemoteSdrEntry();
        if (entry is null || entry.Bands.Count == 0) return true;
        return RemoteSdrBands.ContainsHz(hz.Value, entry.Bands, 50_000);
    }

    private RemoteSdrEntry? CurrentRemoteSdrEntry()
    {
        if (_source is not IRemoteAudioSampleSource remote) return null;
        var url = remote.ServerUrl;
        return _remoteDirectory.FirstOrDefault(item => RemoteSdrCatalog.SameSite(item.Url, url))
               ?? RemoteSdrCatalog.FindByUrl(url)
               ?? RemoteSdrCatalog.FindByUrl(_webUrlInput.Text);
    }

    private void ApplySatelliteRfPlan(IReadOnlyList<SatelliteInfo> visible)
    {
        var primary = visible[0];
        if (_source is IRemoteAudioSampleSource)
        {
            ApplySatelliteToMain(primary);
            return;
        }
        var frequencies = visible.Select(item => SatelliteReceiveHz(item)!.Value).ToList();
        var min = frequencies.Min();
        var max = frequencies.Max();
        var span = max - min;
        var centerTarget = span <= _source.SampleRate * 80L / 100
            ? (min + max) / 2
            : SatelliteReceiveHz(primary)!.Value;
        if (CanRetuneRfCenter(centerTarget))
            CenterSpectrumOnFrequency(centerTarget);
        ApplySatelliteToMain(primary);
        for (var index = 1; index < visible.Count; index++)
            EnsureSatelliteSubVfo(visible[index]);
        PruneSatelliteSubVfos(visible);
    }

    private bool CanRetuneRfCenter(long frequencyHz)
    {
        if (_source is IFixedCenterFrequencySampleSource || _iqRecorder is not null) return false;
        frequencyHz = Math.Clamp(frequencyHz, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        return Math.Abs(frequencyHz - _rfCenterFrequency) > _source.SampleRate / 10;
    }

    private void ApplySatelliteToMain(SatelliteInfo satellite)
    {
        var frequency = SatelliteReceiveHz(satellite);
        if (frequency is null or <= 0) return;
        var switchSat = satellite.NoradCatalogId != _autoTrackDemodNorad;
        TryApplySatelliteFrequency(satellite, frequency.Value, applyDemodulator: switchSat);
    }

    private bool TryApplySatelliteFrequency(SatelliteInfo satellite, long frequency, bool applyDemodulator)
    {
        var constrained = ConstrainFrequencyForCurrentSource(frequency);
        if (_source is IRemoteAudioSampleSource && Math.Abs(constrained - frequency) > 50_000)
        {
            _statusLabel.Text =
                $"{satellite.Name} RX {frequency / 1_000_000d:F3} MHz is outside this SDR";
            return false;
        }
        if (applyDemodulator)
        {
            var mode = ParseSatelliteMode(satellite.Radio?.DownlinkMode);
            var bandwidth = SatelliteBandwidth(mode, satellite.Radio?.DownlinkMode);
            ApplySatelliteDemodulator(mode, bandwidth);
            _autoTrackDemodNorad = satellite.NoradCatalogId;
        }
        if (Math.Abs(_tunedFrequency - constrained) >= 25)
            ChangeVfoFromDigitalDisplay(constrained);
        if (_source is IRemoteAudioSampleSource remote && remote.IsRunning)
            QueueRemoteViewport(immediate: false);
        else if (CanRetuneRfCenter(constrained))
            CenterSpectrumOnFrequency(constrained);
        return true;
    }

    private static long? SatelliteReceiveHz(SatelliteInfo satellite)
    {
        var doppler = satellite.DopplerCorrectedReceiveHz;
        return doppler is > 0 ? doppler : satellite.Radio?.GetReceiveFrequencyHz();
    }

    private void ApplySatelliteDemodulator(RadioMode mode, int bandwidth)
    {
        _suppressModeDefaults = true;
        try
        {
            var modeText = mode.ToString();
            if (_modeBox.Items.Contains(modeText))
                _modeBox.SelectedItem = modeText;
            _demodulator.Mode = mode;
            var clamped = Math.Clamp(bandwidth, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
            _demodulator.Bandwidth = clamped;
            if ((int)_bandwidthBox.Value != clamped)
                _bandwidthBox.Value = clamped;
        }
        finally { _suppressModeDefaults = false; }
        ConfigureDisplay();
        EnsureViewFitsFilter();
        ConfigureAfDisplay();
        UpdateRxPanel();
        if (_source is IRemoteAudioSampleSource remote && remote.IsRunning)
            _ = remote.ApplyReceiverAsync(RadioModes.DemodMode(_demodulator.Mode, _demodulator.SsbLower), _demodulator.Bandwidth);
    }

    private void EnsureSatelliteSubVfo(SatelliteInfo satellite)
    {
        if (satellite.NoradCatalogId is not int norad) return;
        var frequency = SatelliteReceiveHz(satellite);
        if (frequency is null or <= 0) return;
        var mode = ParseSatelliteMode(satellite.Radio?.DownlinkMode);
        var bandwidth = SatelliteBandwidth(mode, satellite.Radio?.DownlinkMode);
        var name = ShortSatelliteName(satellite.Name);
        if (_satelliteSubVfoByNorad.TryGetValue(norad, out var existingId))
        {
            var existing = _appSettings.SubVfos.FirstOrDefault(item => item.Id == existingId);
            if (existing is not null)
            {
                var structural = existing.Mode != mode || existing.Bandwidth != bandwidth || existing.Name != name;
                if (!structural && Math.Abs(existing.Frequency - frequency.Value) < 25)
                    return;
                existing.Frequency = frequency.Value;
                existing.Mode = mode;
                existing.Bandwidth = bandwidth;
                existing.Name = name;
                if (structural)
                    RebuildSubVfoReceivers();
                else
                    ApplySubVfoFrequencyInPlace(existing);
                return;
            }
            _satelliteSubVfoByNorad.Remove(norad);
        }
        var settings = new SubVfoSettings
        {
            Name = name,
            Frequency = frequency.Value,
            Mode = mode,
            Bandwidth = bandwidth,
            OutputChannel = 0
        };
        _appSettings.SubVfos.Add(settings);
        _satelliteSubVfoByNorad[norad] = settings.Id;
        RebuildSubVfoReceivers();
    }

    private void PruneSatelliteSubVfos(IReadOnlyList<SatelliteInfo> visible)
    {
        var live = visible.Where(item => item.NoradCatalogId.HasValue)
            .Select(item => item.NoradCatalogId!.Value)
            .ToHashSet();
        foreach (var pair in _satelliteSubVfoByNorad.ToArray())
        {
            if (live.Contains(pair.Key)) continue;
            DeleteSubVfo(pair.Value);
            _satelliteSubVfoByNorad.Remove(pair.Key);
        }
    }

    private void ClearSatelliteManagedSubVfos()
    {
        foreach (var id in _satelliteSubVfoByNorad.Values.ToArray())
            DeleteSubVfo(id);
        _satelliteSubVfoByNorad.Clear();
    }

    private void EnsureSatelliteAfPlugins(IReadOnlyList<SatelliteInfo> visible)
    {
        var layoutChanged = false;
        var routesChanged = false;
        foreach (var satellite in visible)
        {
            foreach (var pluginId in DetectSatellitePayloadPlugins(satellite))
            {
                if (EnsureAfPluginEnabled(pluginId))
                    layoutChanged = true;
                var vfoId = SatelliteVfoId(satellite);
                routesChanged |= RouteAfPluginToVfoIfNeeded(pluginId, vfoId);
            }
        }
        if (layoutChanged)
        {
            UpdateAfPluginDisplayLayout();
            ApplyAfPluginActivation();
        }
        else if (routesChanged)
            _afPluginHost.SetRoutes(_appSettings.AfPluginVfoRoutes);
    }

    private string SatelliteVfoId(SatelliteInfo satellite)
    {
        if (satellite.NoradCatalogId is int norad &&
            _satelliteSubVfoByNorad.TryGetValue(norad, out var subId))
            return subId;
        return "main";
    }

    private bool EnsureAfPluginEnabled(string pluginId)
    {
        var changed = false;
        if (!_appSettings.EnabledAfPluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
        {
            _appSettings.EnabledAfPluginIds.Add(pluginId);
            changed = true;
        }
        if (!_appSettings.AfPluginInstances.Any(instance =>
                instance.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)))
        {
            _appSettings.AfPluginInstances.Add(new AfPluginInstanceSettings { PluginId = pluginId, VfoId = "main" });
            changed = true;
        }
        return changed;
    }

    private bool RouteAfPluginToVfoIfNeeded(string pluginId, string vfoId)
    {
        var changed = false;
        foreach (var instance in _appSettings.AfPluginInstances.Where(item =>
                     item.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)))
        {
            if (instance.VfoId.Equals(vfoId, StringComparison.OrdinalIgnoreCase))
                continue;
            instance.VfoId = vfoId;
            changed = true;
        }
        if (_appSettings.AfPluginVfoRoutes.TryGetValue(pluginId, out var existing) &&
            existing.Equals(vfoId, StringComparison.OrdinalIgnoreCase))
            return changed;
        _appSettings.AfPluginVfoRoutes[pluginId] = vfoId;
        return true;
    }

    private static IEnumerable<string> DetectSatellitePayloadPlugins(SatelliteInfo satellite)
    {
        var mode = (satellite.Radio?.DownlinkMode ?? string.Empty).ToUpperInvariant();
        var details = (satellite.Details ?? string.Empty).ToUpperInvariant();
        var name = satellite.Name.ToUpperInvariant();
        if (mode.Contains("SSTV") || details.Contains("SSTV") || name.Contains("SSTV"))
            yield return "builtin.af.sstv";
        if (mode.Contains("CW") || !string.IsNullOrWhiteSpace(satellite.Radio?.BeaconFrequency) ||
            details.Contains("BEACON") || name.Contains("CW"))
            yield return "builtin.af.cw";
    }

    private async Task MaybeHandoffRemoteSdrAsync(SatelliteInfo satellite)
    {
        if (_handoffBusy || _satellitePanel is null || !satellite.HasTle) return;
        if (_source is not IRemoteAudioSampleSource remote) return;
        var hz = satellite.Radio?.GetReceiveFrequencyHz() ?? SatelliteReceiveHz(satellite);
        if (hz is not > 0) return;

        var currentEl = satellite.ElevationDegrees ?? -90;
        var minEl = _satellitePanel.HandoffMinElevation;
        var occupied = Volatile.Read(ref _channelOccupiedFlag) != 0;
        var inBand = SatelliteInCurrentSdrBand(satellite);
        var belowHorizon = currentEl < 0;
        var hunt = !inBand || belowHorizon;
        var fade = inBand && !belowHorizon &&
                   (currentEl < minEl || (!occupied && currentEl < minEl + 8));
        if (!hunt && !fade) return;

        var now = Environment.TickCount64;
        if (now - _lastHandoffTick < 15_000) return;

        var protocol = RemoteSdrCatalog.ProtocolForSourceName(_source.Name);
        var currentUrl = remote.ServerUrl;
        var observer = _satellitePanel.Observer;
        var mhz = hz.Value / 1_000_000d;
        _statusLabel.Text = hunt
            ? $"Auto SDR Handover: looking for a site that can receive {satellite.Name} ({mhz:F3} MHz)…"
            : $"Auto SDR Handover: {satellite.Name} fading (el {currentEl:F0}°)…";

        _handoffBusy = true;
        try
        {
            var best = await Task.Run(() => PickHandoverSite(
                satellite, hz.Value, protocol, currentUrl, observer, currentEl, minEl, hunt)).ConfigureAwait(true);
            if (best is null)
            {
                _lastHandoffTick = now;
                _statusLabel.Text = hunt
                    ? $"Auto SDR Handover: no site currently hears {satellite.Name} on {mhz:F3} MHz"
                    : $"{satellite.Name} below handover EL · next {satellite.NextVisibilityDisplay}";
                return;
            }

            var pick = best.Value;
            _lastHandoffTick = Environment.TickCount64;
            _statusLabel.Text = $"Auto SDR Handover → {pick.Entry.Name} · el {pick.Elevation:F0}°";
            await EnsureRemoteSourceForProtocolAsync(pick.Entry.Protocol).ConfigureAwait(true);
            await ApplyRemoteUrlAsync(pick.Entry.Url, pick.Entry.Bands).ConfigureAwait(true);
            try { await _satellitePanel.RefreshPositionsAsync().ConfigureAwait(true); }
            catch { /* keep the new site even if TLE refresh fails */ }
            if (SatelliteReceiveHz(satellite) is long tuneHz)
                TryApplySatelliteFrequency(satellite, tuneHz, applyDemodulator: true);
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"Auto SDR Handover failed: {exception.Message}";
        }
        finally { _handoffBusy = false; }
    }

    private readonly record struct HandoverPick(RemoteSdrEntry Entry, double Elevation);

    private HandoverPick? PickHandoverSite(
        SatelliteInfo satellite,
        long hz,
        string? preferredProtocol,
        string currentUrl,
        ObserverLocation observer,
        double currentEl,
        int minEl,
        bool hunt)
    {
        HandoverPick? bestPreferred = null;
        HandoverPick? bestAny = null;
        foreach (var entry in _remoteDirectory)
        {
            if (!entry.HasCoordinates) continue;
            if (entry.Bands.Count == 0 || !RemoteSdrBands.ContainsHz(hz, entry.Bands, 50_000)) continue;
            if (RemoteSdrCatalog.SameSite(entry.Url, currentUrl)) continue;
            double lookEl;
            try
            {
                lookEl = _satellitePanel!.Manager.GetLookAngle(
                    satellite,
                    new ObserverLocation(entry.Latitude!.Value, entry.Longitude!.Value, entry.AltitudeMeters ?? 50))
                    .ElevationDegrees;
            }
            catch
            {
                continue;
            }

            if (lookEl <= 0) continue;
            if (hunt)
            {
                if (lookEl < 1) continue;
            }
            else if (lookEl < minEl || lookEl <= currentEl + 5)
                continue;

            var pick = new HandoverPick(entry, lookEl);
            if (preferredProtocol is not null &&
                string.Equals(entry.Protocol, preferredProtocol, StringComparison.OrdinalIgnoreCase))
                bestPreferred = BetterHandover(bestPreferred, pick, observer);
            bestAny = BetterHandover(bestAny, pick, observer);
        }

        return bestPreferred ?? bestAny;
    }

    private static HandoverPick? BetterHandover(HandoverPick? current, HandoverPick candidate, ObserverLocation observer)
    {
        if (current is null) return candidate;
        var a = current.Value;
        if (candidate.Elevation > a.Elevation + 8) return candidate;
        if (a.Elevation > candidate.Elevation + 8) return a;
        var dNew = SiteDistanceKm(observer, candidate.Entry);
        var dOld = SiteDistanceKm(observer, a.Entry);
        if (Math.Abs(candidate.Elevation - a.Elevation) <= 8 && dNew + 80 < dOld) return candidate;
        return candidate.Elevation > a.Elevation ? candidate : a;
    }

    private static double SiteDistanceKm(ObserverLocation observer, RemoteSdrEntry entry)
    {
        if (entry.Latitude is not double lat || entry.Longitude is not double lon) return double.MaxValue;
        const double deg = Math.PI / 180;
        var dLat = (lat - observer.LatitudeDegrees) * deg;
        var dLon = (lon - observer.LongitudeDegrees) * deg;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(observer.LatitudeDegrees * deg) * Math.Cos(lat * deg) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 12742 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private async Task EnsureRemoteSourceForProtocolAsync(string? protocol)
    {
        protocol = RemoteSdrCatalog.NormalizeProtocol(protocol ?? "");
        if (protocol is null) return;
        var wanted = protocol switch
        {
            "WebSDR" => "Virtual WebSDR",
            "KiwiSDR" => "Virtual KiwiSDR",
            "OpenWebRX" => "Virtual OpenWebRX",
            _ => null
        };
        if (wanted is null || _source.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return;
        var index = -1;
        for (var i = 0; i < _sources.Count; i++)
        {
            if (!_sources[i].Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            break;
        }
        if (index < 0) return;
        _suppressSourceChange = true;
        try
        {
            _rxSourceBox.SelectedIndex = index;
        }
        finally { _suppressSourceChange = false; }
        await SelectSourceCoreAsync(_sources[index]).ConfigureAwait(true);
        UpdateRxPanel();
    }

    private static RadioMode ParseSatelliteMode(string? modeText)
    {
        var mode = (modeText ?? string.Empty).ToUpperInvariant();
        if (mode.Contains("CW")) return RadioMode.CW;
        if (mode.Contains("LSB")) return RadioMode.LSB;
        if (mode.Contains("USB") || mode.Contains("SSB")) return RadioMode.USB;
        if (mode.Contains("AM") && !mode.Contains("FM")) return RadioMode.AM;
        if (mode.Contains("FM")) return RadioMode.NFM;
        return RadioMode.NFM;
    }

    private static int SatelliteBandwidth(RadioMode mode, string? modeText)
    {
        var text = (modeText ?? string.Empty).ToUpperInvariant();
        if (text.Contains("SSTV")) return 2_400;
        return mode switch
        {
            RadioMode.CW => 500,
            RadioMode.USB or RadioMode.LSB or RadioMode.FREEDV => 2_700,
            RadioMode.NFM => text.Contains("WFM") ? 180_000 : 15_000,
            _ => 15_000
        };
    }

    private static string ShortSatelliteName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length <= 12 ? $"SAT {trimmed}" : $"SAT {trimmed[..9]}…";
    }
}
