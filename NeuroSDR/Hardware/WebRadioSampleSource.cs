using NeuroSDR.Core;
using WebSdr.Protocol;
using WebSdr.Protocol.Kiwi;
using WebSdr.Protocol.OpenWebRx;

namespace NeuroSDR.Hardware;

internal enum WebRadioKind { WebSdr, KiwiSdr, OpenWebRx }

internal sealed class WebSdrSourceProvider(WebRadioKind kind) : ISampleSourceProvider
{
    public string Name => $"Virtual {kind}";

    public IEnumerable<SampleSourceDiscoveryResult> Discover()
    {
        var source = new WebRadioSampleSource(kind);
        yield return new SampleSourceDiscoveryResult(source, $"{source.Name}: URL required");
    }
}

/// <summary>
/// Virtual hardware adapter for the three protocol clients in samples/WebSDR.
/// It deliberately exposes no synthetic IQ: server PCM and spectrum stay separate.
/// </summary>
internal sealed class WebRadioSampleSource : IRemoteAudioSampleSource, ISampleSourceMetrics
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly SemaphoreSlim _viewportGate = new(1, 1);
    private readonly WebRadioKind _kind;
    private CancellationTokenSource? _lifetime;
    private Task? _connectTask;
    private SoundStreamClient? _webSound;
    private WaterfallStreamClient? _webWaterfall;
    private KiwiSoundClient? _kiwiSound;
    private KiwiWaterfallClient? _kiwiWaterfall;
    private OpenWebRxClient? _openClient;
    private BandInfoDocument _webBandInfo = new();
    private ReceiverSettings _settings = new() { FrequencyKhz = 7_074, Name = "NeuroSDR" };
    private long _frequency = 7_074_000;
    private int _audioSampleRate = 12_000;
    private int _remoteSpanHz = 2_000_000;
    private int _maximumSpectrumSpanHz = 2_000_000;
    private long _remoteSpectrumCenter = 7_074_000;
    private int _running;
    private int _connected;
    private long _totalSamples;
    private long _lastDeliveryTick;
    private string _status = "Enter a URL and press Enter.";
    private int _applyPending;
    private int _kiwiZoom = 8;
    private int _soundRecoverBusy;
    private long _lastSoundRecoverTick;
    private Task? _audioHealthTask;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Task _teardownTask = Task.CompletedTask;

    public WebRadioSampleSource(WebRadioKind kind)
    {
        _kind = kind;
        ServerUrl = kind switch
        {
            WebRadioKind.WebSdr => "http://websdr.ewi.utwente.nl:8901/",
            WebRadioKind.KiwiSdr => "http://kiwisdr.areg.org.au:8073/",
            _ => "https://websdr.120v.ac/"
        };
    }

    public string Name => _kind switch
    {
        WebRadioKind.WebSdr => "Virtual WebSDR",
        WebRadioKind.KiwiSdr => "Virtual KiwiSDR",
        _ => "Virtual OpenWebRX"
    };

    public string ServerUrl { get; set; }
    public long CenterFrequency
    {
        get => Interlocked.Read(ref _frequency);
        set
        {
            var frequency = Math.Clamp(value, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
            if (Interlocked.Exchange(ref _frequency, frequency) == frequency) return;
            _settings.FrequencyKhz = frequency / 1_000d;
        }
    }

    // There is no IQ sample rate. This value is the current server spectrum span so
    // existing display sizing remains meaningful.
    public int SampleRate => Math.Max(5_000, Volatile.Read(ref _remoteSpanHz));
    public int AudioSampleRate => Volatile.Read(ref _audioSampleRate);
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public string ConnectionStatus => _status;
    public bool SupportsServerSpectrumViewport => _kind switch
    {
        WebRadioKind.WebSdr => _webWaterfall?.IsConnected == true && _webBandInfo.Bands.Count > 0,
        WebRadioKind.KiwiSdr => _kiwiWaterfall?.IsConnected == true,
        _ => false
    };
    public int MaximumSpectrumSpan => Math.Max(SampleRate, Volatile.Read(ref _maximumSpectrumSpanHz));
    public long TotalSamples => Interlocked.Read(ref _totalSamples);
    public long DeliveredSamples => TotalSamples;
    public long DroppedSamples => 0;
    public long LastDeliveryAgeMilliseconds => TotalSamples == 0 ? long.MaxValue : Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastDeliveryTick));

    // Kept for the base hardware contract. Remote receivers never raise fake IQ.
    public event Action<Complex32[]>? SamplesAvailable { add { } remove { } }
    public event Action<float[], int>? AudioSamplesAvailable;
    public event Action<RemoteSpectrumFrame>? RemoteSpectrumAvailable;
    public event Action<string>? ConnectionStatusChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        _lifetime = new CancellationTokenSource();
        _connectTask = Task.Run(() => ConnectCoreAsync(_lifetime.Token));
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await StopConnectionAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _running, 1);
        // Session lifetime is independent of the caller's connect timeout. Linking them
        // used to tear down receive/keepalive when a short-lived token fired.
        _lifetime = new CancellationTokenSource();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _connectTask = ConnectCoreAsync(connectCts.Token);
        try
        {
            await _connectTask.ConfigureAwait(false);
        }
        catch
        {
            await StopConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sync stop for the <see cref="ISampleSource"/> contract. Never call this from the
    /// WinForms UI thread while remote PCM may still be delivering — use
    /// <see cref="StopConnectionAsync"/> instead (AVOID: GetResult deadlock with Control.Invoke).
    /// </summary>
    public void Stop()
    {
        // Drop the running flag immediately so new PCM/spectrum callbacks no-op.
        Interlocked.Exchange(ref _running, 0);
        Interlocked.Exchange(ref _connected, 0);
        try { _lifetime?.Cancel(); } catch { }

        var teardown = EnqueueTeardown();
        // Blocking the UI message pump while a receive callback does Control.Invoke
        // deadlocks the app (WebSDR→Kiwi source switch). On a sync context, only kick
        // teardown; callers that need completion await StopConnectionAsync.
        if (SynchronizationContext.Current is not null)
            return;

        try { teardown.Wait(TimeSpan.FromSeconds(4)); } catch { /* ignore */ }
    }

    public Task StopConnectionAsync(CancellationToken cancellationToken = default) => EnqueueTeardown(cancellationToken);

    private Task EnqueueTeardown(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _running, 0);
        Interlocked.Exchange(ref _connected, 0);
        try { _lifetime?.Cancel(); } catch { }

        lock (_sync)
        {
            _teardownTask = TeardownWhenReadyAsync(_teardownTask, cancellationToken);
            return _teardownTask;
        }
    }

    private async Task TeardownWhenReadyAsync(Task prior, CancellationToken cancellationToken)
    {
        try { await prior.ConfigureAwait(false); } catch { /* ignore prior teardown errors */ }
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectClientsAsync().ConfigureAwait(false);
            var task = _connectTask;
            if (task is not null && task.Id != Task.CurrentId)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
                catch { /* ignore */ }
            }
            _connectTask = null;
            _audioHealthTask = null;
            _lifetime?.Dispose();
            _lifetime = null;
            SetStatus("Disconnected");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task ApplyReceiverAsync(RadioMode mode, int bandwidthHz, CancellationToken cancellationToken = default)
    {
        _settings.Mode = ToRemoteMode(mode);
        var halfKhz = Math.Max(0.05, bandwidthHz / 2_000d);
        (_settings.LoKhz, _settings.HiKhz) = _settings.Mode switch
        {
            DemodMode.Usb => (0.05, Math.Max(0.1, bandwidthHz / 1_000d)),
            DemodMode.Lsb => (-Math.Max(0.1, bandwidthHz / 1_000d), -0.05),
            _ => (-halfKhz, halfKhz)
        };
        await ApplySettingsSafeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteSpectrumViewport> SetSpectrumViewportAsync(long centerFrequency, int requestedSpanHz,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsServerSpectrumViewport)
            return new RemoteSpectrumViewport(centerFrequency, requestedSpanHz, false);

        await _viewportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _kind switch
            {
                WebRadioKind.WebSdr => await SetWebSdrViewportAsync(centerFrequency, requestedSpanHz, cancellationToken).ConfigureAwait(false),
                WebRadioKind.KiwiSdr => await SetKiwiViewportAsync(centerFrequency, requestedSpanHz, cancellationToken).ConfigureAwait(false),
                _ => new RemoteSpectrumViewport(centerFrequency, requestedSpanHz, false)
            };
        }
        finally { _viewportGate.Release(); }
    }

    private async Task<RemoteSpectrumViewport> SetWebSdrViewportAsync(long centerFrequency, int requestedSpanHz, CancellationToken ct)
    {
        if (_webWaterfall?.IsConnected != true || _webBandInfo.Bands.Count == 0)
            return new RemoteSpectrumViewport(centerFrequency, requestedSpanHz, false);
        var band = _webBandInfo.Bands[Math.Clamp(_settings.Band, 0, _webBandInfo.Bands.Count - 1)];
        var maxZoom = Math.Clamp(band.MaxZoom, 0, 20);
        var fullSpanKhz = Math.Max(1, band.SampleRateKhz);
        var wantedKhz = Math.Clamp(requestedSpanHz / 1_000d, fullSpanKhz / Math.Pow(2, maxZoom), fullSpanKhz);
        // Nearest discrete zoom (Floor always jumped wider and shifted start/end on RX START).
        var zoom = Math.Clamp((int)Math.Round(Math.Log(fullSpanKhz / wantedKhz, 2)), 0, maxZoom);
        var actualSpanKhz = fullSpanKhz / Math.Pow(2, zoom);
        var centerKhz = centerFrequency / 1_000d;
        centerKhz = Math.Clamp(centerKhz, band.MinFreqKhz + actualSpanKhz / 2, band.MaxFreqKhz - actualSpanKhz / 2);

        var finestBinKhz = fullSpanKhz / (Math.Pow(2, maxZoom) * 1024d);
        var rawStart = (centerKhz - band.MinFreqKhz - actualSpanKhz / 2) / finestBinKhz;
        var alignment = 1 << Math.Clamp(maxZoom - zoom, 0, 20);
        var totalUnits = 1024 << maxZoom;
        var viewportUnits = 1024 << (maxZoom - zoom);
        var start = (int)Math.Floor(rawStart / alignment) * alignment;
        start = Math.Clamp(start, 0, Math.Max(0, totalUnits - viewportUnits));
        await _webWaterfall.SetZoomAsync(zoom, start, ct).ConfigureAwait(false);

        var actualMinKhz = band.MinFreqKhz + start * finestBinKhz;
        _remoteSpectrumCenter = (long)Math.Round((actualMinKhz + actualSpanKhz / 2) * 1_000);
        _remoteSpanHz = Math.Max(5_000, (int)Math.Round(actualSpanKhz * 1_000));
        return new RemoteSpectrumViewport(_remoteSpectrumCenter, _remoteSpanHz, true);
    }

    private async Task<RemoteSpectrumViewport> SetKiwiViewportAsync(long centerFrequency, int requestedSpanHz, CancellationToken ct)
    {
        if (_kiwiWaterfall?.IsConnected != true)
            return new RemoteSpectrumViewport(centerFrequency, requestedSpanHz, false);
        var bandwidthKhz = Math.Max(1, _kiwiWaterfall.BandwidthKhz);
        var maxZoom = Math.Clamp(_kiwiWaterfall.ZoomCap, 0, 14);
        var wantedKhz = Math.Clamp(requestedSpanHz / 1_000d, bandwidthKhz / Math.Pow(2, maxZoom), bandwidthKhz);
        _kiwiZoom = Math.Clamp((int)Math.Round(Math.Log(bandwidthKhz / wantedKhz, 2)), 0, maxZoom);
        await _kiwiWaterfall.ConfigureAsync(centerFrequency / 1_000d, _kiwiZoom, ct).ConfigureAwait(false);
        // Use the cf we sent — StartKhz may be clamped for labels and must not rewrite the center.
        _remoteSpectrumCenter = (long)Math.Round(_kiwiWaterfall.CenterKhz * 1_000);
        _remoteSpanHz = Math.Max(5_000, (int)Math.Round(_kiwiWaterfall.SpanKhz * 1_000));
        return new RemoteSpectrumViewport(_remoteSpectrumCenter, _remoteSpanHz, true);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("An HTTP or HTTPS Web SDR URL is required.");
            SetStatus($"Connecting · {uri.Host}");
            _settings.FrequencyKhz = CenterFrequency / 1_000d;
            switch (_kind)
            {
                case WebRadioKind.WebSdr: await ConnectWebSdrAsync(uri, cancellationToken).ConfigureAwait(false); break;
                case WebRadioKind.KiwiSdr: await ConnectKiwiAsync(uri, cancellationToken).ConfigureAwait(false); break;
                default: await ConnectOpenWebRxAsync(uri, cancellationToken).ConfigureAwait(false); break;
            }
            Interlocked.Exchange(ref _connected, 1);
            SetStatus($"Connected · {uri.Host}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _connected, 0);
            Interlocked.Exchange(ref _running, 0);
            SetStatus($"Connection failed · {exception.GetBaseException().Message}");
            await DisconnectClientsAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConnectWebSdrAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            _webBandInfo = await BandInfoParser.LoadAsync(uri, ct: ct).ConfigureAwait(false);
            var band = _webBandInfo.Bands.Select((value, index) => (value, index))
                .FirstOrDefault(item => item.value.Contains(_settings.FrequencyKhz));
            var frequencyWasInBand = band.value is not null;
            if (band.value is null && _webBandInfo.Bands.Count > 0) band = (_webBandInfo.Bands[0], 0);
            if (band.value is not null)
            {
                _settings.Band = band.index;
                if (!frequencyWasInBand)
                {
                    _settings.FrequencyKhz = band.value.VfoKhz > 0 ? band.value.VfoKhz : band.value.CenterFreqKhz;
                    Interlocked.Exchange(ref _frequency, ReceiverSettings.ToAbsoluteHz(_settings.FrequencyKhz));
                }
                _remoteSpectrumCenter = (long)Math.Round(band.value.CenterFreqKhz * 1_000);
                _remoteSpanHz = Math.Max(5_000, (int)Math.Round(band.value.SampleRateKhz * 1_000));
                _maximumSpectrumSpanHz = _remoteSpanHz;
            }
        }
        catch (Exception exception) { SetStatus($"Band info warning · {exception.Message}"); }
        _webSound = new SoundStreamClient(uri);
        _webSound.StatusChanged += SetStatus;
        _webSound.ErrorOccurred += OnWebSoundFault;
        Volatile.Write(ref _audioSampleRate, 8_000);
        _webSound.Decoder.SampleRateChanged += rate => Volatile.Write(ref _audioSampleRate, rate);
        _webSound.Decoder.SamplesAvailable += OnWebPcm;
        await _webSound.ConnectAsync(ct).ConfigureAwait(false);
        await _webSound.SendParamsAsync(_settings, ct).ConfigureAwait(false);
        try
        {
            _webWaterfall = new WaterfallStreamClient(uri);
            _webWaterfall.StatusChanged += SetStatus;
            _webWaterfall.ErrorOccurred += OnError;
            _webWaterfall.RowReceived += OnSpectrumRow;
            await _webWaterfall.ConnectAsync(_settings.Band, 1024, 0, 0, ct).ConfigureAwait(false);
        }
        catch (Exception exception) { SetStatus($"Waterfall warning · {exception.Message}"); }
        // Sound (/~~stream) and waterfall use separate sockets — SND can die while WF lives.
        var life = _lifetime?.Token ?? ct;
        _audioHealthTask = Task.Run(() => AudioHealthLoopAsync(life), CancellationToken.None);
    }

    private async Task ConnectKiwiAsync(Uri uri, CancellationToken ct)
    {
        if (_settings.FrequencyKhz < 10)
        {
            _settings.FrequencyKhz = 14_100;
            Interlocked.Exchange(ref _frequency, 14_100_000);
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _kiwiSound = new KiwiSoundClient { UserName = "NeuroSDR", UseCompression = true };
        _kiwiSound.StatusChanged += SetStatus;
        _kiwiSound.ErrorOccurred += OnError;
        _kiwiSound.SampleRateChanged += rate => Volatile.Write(ref _audioSampleRate, rate);
        _kiwiSound.SamplesAvailable += OnPcm;
        _kiwiSound.Ready += () => ready.TrySetResult();
        await _kiwiSound.ConnectAsync(uri, ct).ConfigureAwait(false);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        await _kiwiSound.SendInitialRxAsync(_settings, ct).ConfigureAwait(false);
        try
        {
            _kiwiWaterfall = new KiwiWaterfallClient
            {
                SessionId = _kiwiSound.SessionId,
                Endpoint = _kiwiSound.Endpoint
            };
            _kiwiWaterfall.StatusChanged += SetStatus;
            _kiwiWaterfall.ErrorOccurred += OnError;
            _kiwiWaterfall.RowReceived += row =>
            {
                _remoteSpectrumCenter = (long)Math.Round(_kiwiWaterfall.CenterKhz * 1_000);
                _remoteSpanHz = Math.Max(5_000, (int)Math.Round(_kiwiWaterfall.SpanKhz * 1_000));
                OnSpectrumRow(row);
            };
            _kiwiWaterfall.PrepareTune(_settings.FrequencyKhz, _kiwiZoom);
            await _kiwiWaterfall.ConnectAsync(uri, ct).ConfigureAwait(false);
            if (_kiwiWaterfall.FreqOffsetKhz == 0 && _settings.FrequencyKhz > _kiwiWaterfall.BandwidthKhz)
                await Task.Delay(400, ct).ConfigureAwait(false);
            await _kiwiWaterfall.ConfigureAsync(_settings.FrequencyKhz, _kiwiZoom, ct).ConfigureAwait(false);
            _maximumSpectrumSpanHz = Math.Max(5_000, (int)Math.Round(_kiwiWaterfall.BandwidthKhz * 1_000));
        }
        catch (Exception exception) { SetStatus($"Waterfall warning · {exception.Message}"); }
    }

    private async Task ConnectOpenWebRxAsync(Uri uri, CancellationToken ct)
    {
        ReceiverSettings.ApplyOpenWebRxHash(uri, _settings);
        Interlocked.Exchange(ref _frequency, ReceiverSettings.ToAbsoluteHz(_settings.FrequencyKhz));
        _openClient = new OpenWebRxClient { ClientName = "NeuroSDR" };
        _openClient.StatusChanged += SetStatus;
        _openClient.ErrorOccurred += OnError;
        _openClient.SamplesAvailable += (samples, rate) => { Volatile.Write(ref _audioSampleRate, rate); OnPcm(samples); };
        _openClient.SpectrumRow += row =>
        {
            _remoteSpectrumCenter = (long)Math.Round(_openClient.CenterFreqHz);
            _remoteSpanHz = Math.Max(5_000, (int)Math.Round(_openClient.SampleRateHz));
            _maximumSpectrumSpanHz = _remoteSpanHz;
            OnSpectrumRow(row);
        };
        await _openClient.ConnectAsync(uri, ct).ConfigureAwait(false);
        var halfSpan = _openClient.SampleRateHz / 2;
        var requestedHz = ReceiverSettings.ToAbsoluteHz(_settings.FrequencyKhz);
        if (requestedHz < _openClient.CenterFreqHz - halfSpan || requestedHz > _openClient.CenterFreqHz + halfSpan)
        {
            var start = _openClient.StartFreqHz > 0 ? _openClient.StartFreqHz : _openClient.CenterFreqHz;
            _settings.FrequencyKhz = start / 1_000d;
            Interlocked.Exchange(ref _frequency, (long)Math.Round(start));
        }
        await _openClient.StartAndTuneAsync(_settings, ct).ConfigureAwait(false);
    }

    private async Task ApplySettingsSafeAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _applyPending, 1);
        if (!await _applyGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            while (Interlocked.Exchange(ref _applyPending, 0) != 0)
            {
                _settings.FrequencyKhz = CenterFrequency / 1_000d;
                var settings = CopySettings(_settings);
                if (_webSound?.IsConnected == true) await _webSound.SendParamsAsync(settings, ct).ConfigureAwait(false);
                if (_kiwiSound?.IsConnected == true)
                    await _kiwiSound.ApplyRxAsync(settings, ct).ConfigureAwait(false);
                if (_openClient?.IsConnected == true) await _openClient.ApplyTuneAsync(settings, ct).ConfigureAwait(false);
            }
        }
        catch (Exception exception) { OnError(exception); }
        finally { _applyGate.Release(); }
    }

    private static ReceiverSettings CopySettings(ReceiverSettings source) => new()
    {
        FrequencyKhz = source.FrequencyKhz,
        Band = source.Band,
        LoKhz = source.LoKhz,
        HiKhz = source.HiKhz,
        Mode = source.Mode,
        Name = source.Name,
        Mute = source.Mute,
        Squelch = source.Squelch,
        SquelchLevelDbm = source.SquelchLevelDbm,
        AutoNotch = source.AutoNotch,
        NoiseReduction = source.NoiseReduction,
        Volume = source.Volume
    };

    private void OnWebPcm(ReadOnlyMemory<short> pcm)
    {
        var rate = _webSound?.Decoder.SampleRateHz ?? 0;
        if (rate >= 1_000)
            Volatile.Write(ref _audioSampleRate, rate);
        OnPcm(pcm);
    }

    private void OnPcm(ReadOnlyMemory<short> pcm)
    {
        if (!IsRunning || pcm.Length == 0) return;
        var samples = new float[pcm.Length];
        var span = pcm.Span;
        for (var index = 0; index < samples.Length; index++) samples[index] = span[index] / 32768f;
        Interlocked.Add(ref _totalSamples, samples.Length);
        Interlocked.Exchange(ref _lastDeliveryTick, Environment.TickCount64);
        AudioSamplesAvailable?.Invoke(samples, AudioSampleRate);
    }

    private void OnSpectrumRow(byte[] row)
    {
        if (!IsRunning || row.Length <= 1) return;
        // WebSDR format-10 intensity floor is typically mid/high (~145–220 in the
        // reference client). Linear 0..255→dB mapping paints the whole waterfall
        // yellow/limit even when the website (black/white levels) looks dark.
        if (_kind == WebRadioKind.WebSdr)
            NormalizeWebSdrRow(row);
        RemoteSpectrumAvailable?.Invoke(new RemoteSpectrumFrame(row, _remoteSpectrumCenter, _remoteSpanHz));
    }

    /// <summary>
    /// Map WebSDR intensities into a full 0..255 display range using per-row
    /// percentiles (with the classic 145/220 defaults as soft anchors).
    /// </summary>
    private static void NormalizeWebSdrRow(byte[] row)
    {
        if (row.Length < 8) return;
        var sorted = new byte[row.Length];
        Buffer.BlockCopy(row, 0, sorted, 0, row.Length);
        Array.Sort(sorted);
        var p05 = sorted[Math.Clamp(row.Length * 5 / 100, 0, row.Length - 1)];
        var p40 = sorted[Math.Clamp(row.Length * 40 / 100, 0, row.Length - 1)];
        var p98 = sorted[Math.Clamp(row.Length * 98 / 100, 0, row.Length - 1)];
        // Classic HTML client defaults are Black=145 / White=220. Use those when the
        // row already sits in that band; otherwise raise black toward p40 so hot
        // format-10 floors (appr.org.br etc.) do not paint solid yellow.
        int black;
        int white;
        if (p05 is >= 120 and <= 160 && p98 is >= 200 and <= 245)
        {
            black = 145;
            white = 220;
        }
        else
        {
            black = Math.Clamp(Math.Max((int)p40 - 8, (int)p05), 0, 250);
            white = Math.Clamp(Math.Max((int)p98 + 2, black + 28), black + 1, 255);
        }
        var span = Math.Max(1, white - black);
        for (var i = 0; i < row.Length; i++)
            row[i] = (byte)Math.Clamp((row[i] - black) * 255 / span, 0, 255);
    }

    private void OnError(Exception exception) => SetStatus($"Remote error · {exception.GetBaseException().Message}");

    private void OnWebSoundFault(Exception exception)
    {
        if (!IsRunning || _kind != WebRadioKind.WebSdr) return;
        if (Volatile.Read(ref _soundRecoverBusy) != 0) return;
        SetStatus($"Audio fault · {exception.GetBaseException().Message}");
        _ = RecoverWebSoundAsync();
    }

    private async Task RecoverWebSoundAsync()
    {
        if (!IsRunning || _kind != WebRadioKind.WebSdr) return;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastSoundRecoverTick) < 8_000) return;
        if (Interlocked.Exchange(ref _soundRecoverBusy, 1) != 0) return;
        Interlocked.Exchange(ref _lastSoundRecoverTick, now);
        try
        {
            if (!IsRunning) return;
            if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var uri)) return;
            SetStatus("Audio stalled · reconnecting sound…");
            var old = _webSound;
            _webSound = null;
            if (old is not null)
            {
                try { await old.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
            var sound = new SoundStreamClient(uri);
            sound.StatusChanged += SetStatus;
            sound.ErrorOccurred += OnWebSoundFault;
            sound.Decoder.SampleRateChanged += rate => Volatile.Write(ref _audioSampleRate, rate);
            sound.Decoder.SamplesAvailable += OnWebPcm;
            var ct = _lifetime?.Token ?? CancellationToken.None;
            await sound.ConnectAsync(ct).ConfigureAwait(false);
            await sound.SendParamsAsync(CopySettings(_settings), ct).ConfigureAwait(false);
            _webSound = sound;
            Interlocked.Exchange(ref _lastDeliveryTick, Environment.TickCount64);
            SetStatus($"Audio reconnected · {uri.Host}");
        }
        catch (Exception exception)
        {
            SetStatus($"Audio reconnect failed · {exception.GetBaseException().Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _soundRecoverBusy, 0);
        }
    }

    private async Task AudioHealthLoopAsync(CancellationToken ct)
    {
        try
        {
            // Allow first audio packets before declaring a stall.
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (!IsRunning || _kind != WebRadioKind.WebSdr) continue;
                var age = LastDeliveryAgeMilliseconds;
                var soundOpen = _webSound?.IsConnected == true;
                if (soundOpen && age < 5_000) continue;
                // Waterfall can keep running on a separate socket while ~~stream dies.
                if (age >= 5_000 || !soundOpen)
                    await RecoverWebSoundAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetStatus(string status)
    {
        lock (_sync) _status = status;
        ConnectionStatusChanged?.Invoke(status);
    }

    private async Task DisconnectClientsAsync()
    {
        if (_webWaterfall is not null) { await _webWaterfall.DisposeAsync().ConfigureAwait(false); _webWaterfall = null; }
        if (_webSound is not null) { await _webSound.DisposeAsync().ConfigureAwait(false); _webSound = null; }
        if (_kiwiWaterfall is not null) { await _kiwiWaterfall.DisposeAsync().ConfigureAwait(false); _kiwiWaterfall = null; }
        if (_kiwiSound is not null) { await _kiwiSound.DisposeAsync().ConfigureAwait(false); _kiwiSound = null; }
        if (_openClient is not null) { await _openClient.DisposeAsync().ConfigureAwait(false); _openClient = null; }
    }

    private static DemodMode ToRemoteMode(RadioMode mode) => mode switch
    {
        RadioMode.USB => DemodMode.Usb,
        RadioMode.LSB => DemodMode.Lsb,
        RadioMode.CW => DemodMode.Cw,
        RadioMode.AM => DemodMode.Am,
        RadioMode.SAM => DemodMode.AmSync,
        RadioMode.NFM or RadioMode.DMR or RadioMode.DSTAR or RadioMode.C4FM => DemodMode.NbFm,
        RadioMode.FREEDV => DemodMode.Lsb,
        RadioMode.WFM => DemodMode.Wfm,
        _ => DemodMode.Usb
    };

    public void Dispose()
    {
        Interlocked.Exchange(ref _running, 0);
        Interlocked.Exchange(ref _connected, 0);
        try { _lifetime?.Cancel(); } catch { }
        try
        {
            var teardown = EnqueueTeardown();
            if (SynchronizationContext.Current is null)
                teardown.Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* ignore */ }
        _applyGate.Dispose();
        _viewportGate.Dispose();
        _lifecycleGate.Dispose();
    }
}
