using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Hardware;
using NeuroSDR.Plugins;
using NeuroSDR.Settings;
using NeuroSDR.Web;
using System.Collections.Concurrent;

namespace NeuroSDR;

public partial class frmNeuroSDR
{
    private NeuroSDRRemoteRadioBridge? _remoteBridge;
    private RemoteWebHost? _remoteWebHost;
    private long _nextRemoteSpectrumPublishTick;
    private readonly ConcurrentQueue<AfPluginRemoteEvent> _remoteAfFeed = new();
    private readonly object _remoteAudioSync = new();
    private float[] _remoteAudioStaging = new float[RemoteAudioPacketSamples * 4];
    private int _remoteAudioStagingCount;
    private float _remoteAudioAgc = 1f;
    private bool _remoteAudioAnalog = true;
    private const int RemoteSpectrumBins = 384;
    private const int RemoteAfFeedCap = 200;
    private const int RemoteAudioSampleRate = AudioDemodulator.AudioSampleRate;
    /// <summary>20 ms packets @ 48 kHz — steady web stream without dropping RF bursts.</summary>
    private const int RemoteAudioPacketSamples = 960;

    private void StartRemoteWebIfEnabled() => ApplyWebRemoteHost(forceRestart: false);

    private void ApplyWebRemoteHost(bool forceRestart)
    {
        var want = _appSettings.WebRemoteEnabled;
        var running = _remoteWebHost is not null;
        if (!want)
        {
            if (running) _ = StopRemoteWebAsync(updateStatus: true);
            return;
        }

        if (running && !forceRestart) return;
        _ = RestartRemoteWebAsync();
    }

    private async Task RestartRemoteWebAsync()
    {
        await StopRemoteWebAsync(updateStatus: false).ConfigureAwait(false);
        if (!_appSettings.WebRemoteEnabled || IsDisposed || Disposing) return;
        try
        {
            _remoteBridge = new NeuroSDRRemoteRadioBridge(this);
            var options = new RemoteWebHostOptions
            {
                Enabled = true,
                Port = Math.Clamp(_appSettings.WebRemotePort, 1024, 65535),
                BindAllInterfaces = _appSettings.WebRemoteBindAllInterfaces,
                AccessToken = _appSettings.WebRemoteAccessToken ?? ""
            };
            _remoteWebHost = new RemoteWebHost(_remoteBridge, options);
            await _remoteWebHost.StartAsync().ConfigureAwait(false);
            if (IsDisposed || Disposing) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed || Disposing || _remoteWebHost is null) return;
                    _statusLabel.Text = $"Web remote · {_remoteWebHost.Status}";
                });
            }
            catch (InvalidOperationException) { }
        }
        catch (Exception exception)
        {
            try
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed && !Disposing)
                        _statusLabel.Text = $"Web remote failed: {exception.GetBaseException().Message}";
                });
            }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// Stop Kestrel without deadlocking the WinForms UI thread.
    /// FormClosing must never await ASP.NET Core on the UI SynchronizationContext.
    /// </summary>
    private void StopRemoteWebBlocking()
    {
        var host = _remoteWebHost;
        _remoteWebHost = null;
        _remoteBridge = null;
        if (host is null) return;
        try
        {
            var done = Task.Run(async () =>
            {
                await host.DisposeAsync().ConfigureAwait(false);
            });
            if (!done.Wait(TimeSpan.FromSeconds(2)))
            {
                // Best-effort: abandon hang so the process can exit.
                try { host.ForceAbort(); } catch { }
            }
        }
        catch { }
    }

    private async Task StopRemoteWebAsync(bool updateStatus)
    {
        var host = _remoteWebHost;
        _remoteWebHost = null;
        _remoteBridge = null;
        if (host is null) return;
        try
        {
            // Always leave the WinForms sync context before tearing down Kestrel.
            await Task.Run(async () =>
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { }

        if (!updateStatus || IsDisposed || Disposing) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && !Disposing) _statusLabel.Text = "Web remote stopped";
            });
        }
        catch (InvalidOperationException) { }
    }

    private void PublishRemoteState()
    {
        try { _remoteBridge?.RaiseStateChanged(); }
        catch { }
    }

    private long _nextRemoteLiveTick;

    private void PublishRemoteLive()
    {
        if (_remoteWebHost is null) return;
        var now = Environment.TickCount64;
        if (now < _nextRemoteLiveTick) return;
        _nextRemoteLiveTick = now + 250;
        try { _remoteBridge?.RaiseLive(); }
        catch { }
    }

    internal RadioLiveUpdate CaptureRemoteLive() => new()
    {
        Running = _source.IsRunning,
        FrequencyHz = Interlocked.Read(ref _tunedFrequency),
        SignalDb = Volatile.Read(ref _signalLevelDb),
        AudioLevelDb = Volatile.Read(ref _audioLevelDb),
        Squelch1Open = _squelchCheck.Checked && _audioProcessors[0].SquelchOpen,
        Squelch2Open = _squelchCheck2.Checked && _audioProcessors[1].SquelchOpen,
        StereoLed = _stereoLed.IsOn,
        Status = _statusLabel.Text ?? ""
    };

    private void PublishRemoteSpectrum(float[] spectrum) =>
        PublishRemoteSpectrum(spectrum, alreadyWindowed: false, windowCenterHz: 0, windowSpanHz: 0);

    /// <summary>
    /// Kiwi/WebSDR/OpenWebRX rows are already the server waterfall window.
    /// Cropping them with the audio sample rate collapses the view to a few kilohertz.
    /// </summary>
    private void PublishRemoteSpectrum(float[] spectrum, bool alreadyWindowed, long windowCenterHz, int windowSpanHz)
    {
        if (_remoteBridge is null || spectrum.Length == 0) return;
        var now = Environment.TickCount64;
        var interval = Math.Max(50, 1000 / Math.Clamp(_appSettings.RfDisplayFramesPerSecond, 5, 20));
        if (now < _nextRemoteSpectrumPublishTick) return;
        _nextRemoteSpectrumPublishTick = now + interval;
        FlushRemoteCwLines();

        float[] view;
        long viewCenter;
        int viewSpan;
        if (alreadyWindowed)
        {
            view = spectrum;
            viewCenter = windowCenterHz;
            viewSpan = Math.Max(1, windowSpanHz);
        }
        else
        {
            var rfCenter = Interlocked.Read(ref _rfCenterFrequency);
            var sampleRate = Math.Max(1, _source.SampleRate);
            viewCenter = Interlocked.Read(ref _viewCenterFrequency);
            viewSpan = Math.Clamp(Volatile.Read(ref _viewBandwidth), 1, sampleRate);
            view = CropSpectrumToView(spectrum, rfCenter, sampleRate, viewCenter, viewSpan);
        }
        var levels = DownsampleSpectrum(view, RemoteSpectrumBins);
        _remoteBridge.RaiseSpectrum(new SpectrumRemoteFrame
        {
            CenterHz = viewCenter,
            SpanHz = viewSpan,
            TunedHz = Interlocked.Read(ref _tunedFrequency),
            FilterHz = _demodulator.Bandwidth,
            Mode = _demodulator.Mode.ToString(),
            Levels = levels
        });
    }

    private static float[] CropSpectrumToView(float[] spectrum, long rfCenter, int sampleRate, long viewCenter, int viewSpan)
    {
        if (spectrum.Length < 2 || viewSpan >= sampleRate) return spectrum;
        var captureLeft = rfCenter - sampleRate / 2d;
        var viewLeft = viewCenter - viewSpan / 2d;
        var viewRight = viewCenter + viewSpan / 2d;
        var first = Math.Clamp((int)Math.Floor((viewLeft - captureLeft) * spectrum.Length / sampleRate), 0, spectrum.Length - 1);
        var last = Math.Clamp((int)Math.Ceiling((viewRight - captureLeft) * spectrum.Length / sampleRate), first, spectrum.Length - 1);
        var count = Math.Max(2, last - first + 1);
        var slice = new float[count];
        Array.Copy(spectrum, first, slice, 0, count);
        return slice;
    }

    /// <summary>Push demodulated mono AF to web clients (independent of desktop WaveOut volume).</summary>
    private void PublishRemoteAudio(float[] mono, bool decodedVoice = false)
    {
        if (_remoteBridge is null || mono.Length == 0) return;

        lock (_remoteAudioSync)
        {
            if (decodedVoice && _remoteAudioAnalog)
            {
                _remoteAudioStagingCount = 0;
                _remoteAudioAgc = 1f;
            }
            _remoteAudioAnalog = !decodedVoice;

            EnsureRemoteAudioCapacity(mono.Length);
            Array.Copy(mono, 0, _remoteAudioStaging, _remoteAudioStagingCount, mono.Length);
            _remoteAudioStagingCount += mono.Length;

            while (_remoteAudioStagingCount >= RemoteAudioPacketSamples)
            {
                var packet = new float[RemoteAudioPacketSamples];
                Array.Copy(_remoteAudioStaging, 0, packet, 0, RemoteAudioPacketSamples);
                var remain = _remoteAudioStagingCount - RemoteAudioPacketSamples;
                if (remain > 0)
                    Array.Copy(_remoteAudioStaging, RemoteAudioPacketSamples, _remoteAudioStaging, 0, remain);
                _remoteAudioStagingCount = remain;

                var pcm = decodedVoice ? FloatsToPcm16(packet, gain: 1f) : FloatsToPcm16WithAgc(packet);
                _remoteBridge.RaiseAudio(pcm);
            }
        }
    }

    private void ClearRemoteAudioStaging()
    {
        lock (_remoteAudioSync)
        {
            if (!_remoteAudioAnalog && _remoteAudioStagingCount == 0) return;
            _remoteAudioStagingCount = 0;
            _remoteAudioAnalog = false;
        }
    }

    private static byte[] FloatsToPcm16(float[] mono, float gain)
    {
        var pcm = new byte[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            var sample = (short)Math.Clamp((int)(mono[i] * gain * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    private void EnsureRemoteAudioCapacity(int incoming)
    {
        var need = _remoteAudioStagingCount + incoming;
        if (need <= _remoteAudioStaging.Length) return;
        var next = Math.Max(need, _remoteAudioStaging.Length * 2);
        Array.Resize(ref _remoteAudioStaging, next);
    }

    private byte[] FloatsToPcm16WithAgc(float[] mono)
    {
        var peak = 1e-6f;
        for (var i = 0; i < mono.Length; i++)
        {
            var a = MathF.Abs(mono[i]);
            if (a > peak) peak = a;
        }
        // Soft AGC so quiet USB/FT8 AF is audible on phone speakers; desktop WaveOut volume stays separate.
        var target = 0.55f;
        var desired = Math.Clamp(target / peak, 0.8f, 12f);
        _remoteAudioAgc = _remoteAudioAgc * 0.92f + desired * 0.08f;
        var gain = _remoteAudioAgc;

        var pcm = new byte[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            var sample = (short)Math.Clamp((int)(mono[i] * gain * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    private readonly ConcurrentDictionary<string, long> _remoteLastFtxSlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RemoteCwLineState> _remoteCwLines = new(StringComparer.OrdinalIgnoreCase);
    private long _nextRemoteCwFlushTick;

    private sealed class RemoteCwLineState
    {
        public string PluginId = "";
        public string PluginName = "";
        public string Channel = "";
        public string Text = "";
        public long FrequencyHz;
        public Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase);
        public DateTime TimestampUtc;
        public bool Dirty;
    }

    private void PublishRemoteAfPlugin(AfPluginResult result)
    {
        if (_remoteBridge is null) return;
        // Status / collect / auto-DT noise stays on desktop status labels only.
        if (!IsRemoteAfFeedKind(result.Kind)) return;

        var typeId = _afPluginHost.PluginTypeId(result.PluginId) ?? result.PluginId;
        var variant = _afPluginHost.PluginVariant(result.PluginId) ?? "";
        var name = ResolveRemoteAfPluginName(typeId, variant, _afPlugins);
        var frequencyHz = ResolveRemoteAfFrequencyHz(result);
        var text = result.Fields?.GetValueOrDefault("message")
                   ?? result.Text
                   ?? "";

        var fields = result.Fields is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(result.Fields, StringComparer.OrdinalIgnoreCase);
        fields["rfHz"] = frequencyHz.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Multi-channel CW emits one character at a time — coalesce into channel lines so the
        // web feed updates like the desktop list instead of flooding SignalR / redrawing per glyph.
        if (typeId.Equals("builtin.af.cw", StringComparison.OrdinalIgnoreCase) &&
            result.Kind.Equals("CW_DECODE", StringComparison.OrdinalIgnoreCase))
        {
            BufferRemoteCwDecode(result.PluginId, name, frequencyHz, text, fields, result.TimestampUtc);
            return;
        }

        if (result.Kind.Equals("FTX_DECODE", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(fields.GetValueOrDefault("slot"), out var slot))
        {
            if (_remoteLastFtxSlot.TryGetValue(result.PluginId, out var prev) && prev != slot)
                EmitRemoteAf(result.PluginId, name, "PERIOD_SEP", "—————————", frequencyHz, fields, result.TimestampUtc);
            _remoteLastFtxSlot[result.PluginId] = slot;
        }

        EmitRemoteAf(result.PluginId, name, result.Kind, text, frequencyHz, fields, result.TimestampUtc);
    }

    private void BufferRemoteCwDecode(
        string pluginId,
        string pluginName,
        long frequencyHz,
        string incoming,
        Dictionary<string, string> fields,
        DateTime timestampUtc)
    {
        var channel = fields.GetValueOrDefault("channel", "?");
        var key = $"{pluginId}:{channel}";
        var piece = fields.GetValueOrDefault("text", incoming) ?? "";
        var state = _remoteCwLines.GetOrAdd(key, _ => new RemoteCwLineState
        {
            PluginId = pluginId,
            PluginName = pluginName,
            Channel = channel
        });
        lock (state)
        {
            state.PluginName = pluginName;
            state.FrequencyHz = frequencyHz;
            state.TimestampUtc = timestampUtc;
            if (piece.Length > 0)
            {
                state.Text = (state.Text + piece);
                if (state.Text.Length > 240)
                    state.Text = state.Text[^240..];
            }
            foreach (var pair in fields)
                state.Fields[pair.Key] = pair.Value;
            state.Fields["message"] = FormatRemoteCwMessage(state);
            state.Fields["text"] = state.Text;
            state.Dirty = true;
        }

        var now = Environment.TickCount64;
        // Flush promptly on word boundaries; otherwise batch ~8×/sec.
        var flushNow = piece == " " || piece.Contains('\n') || now >= Volatile.Read(ref _nextRemoteCwFlushTick);
        if (flushNow) FlushRemoteCwLines();
    }

    private static string FormatRemoteCwMessage(RemoteCwLineState state)
    {
        var hz = state.Fields.GetValueOrDefault("trackedHz", "");
        var wpm = state.Fields.GetValueOrDefault("wpm", "");
        var prefix = string.IsNullOrWhiteSpace(hz)
            ? ""
            : string.IsNullOrWhiteSpace(wpm) ? $"{hz} Hz" : $"{hz} Hz · {wpm} WPM";
        return string.IsNullOrEmpty(prefix) ? state.Text : $"{prefix}  {state.Text}";
    }

    private void FlushRemoteCwLines()
    {
        Volatile.Write(ref _nextRemoteCwFlushTick, Environment.TickCount64 + 120);
        foreach (var pair in _remoteCwLines)
        {
            var state = pair.Value;
            Dictionary<string, string> fields;
            string text;
            string pluginId;
            string pluginName;
            long frequencyHz;
            DateTime timestampUtc;
            lock (state)
            {
                if (!state.Dirty) continue;
                if (string.IsNullOrEmpty(state.Text))
                {
                    state.Dirty = false;
                    continue;
                }
                state.Dirty = false;
                pluginId = state.PluginId;
                pluginName = state.PluginName;
                frequencyHz = state.FrequencyHz;
                timestampUtc = state.TimestampUtc;
                text = state.Fields.GetValueOrDefault("message", state.Text);
                fields = new Dictionary<string, string>(state.Fields, StringComparer.OrdinalIgnoreCase);
            }
            EmitRemoteAf(pluginId, pluginName, "CW_LINE", text, frequencyHz, fields, timestampUtc);
        }
    }

    private void EmitRemoteAf(
        string pluginId,
        string name,
        string kind,
        string text,
        long frequencyHz,
        Dictionary<string, string> fields,
        DateTime timestampUtc)
    {
        var evt = new AfPluginRemoteEvent
        {
            PluginId = pluginId,
            PluginName = name,
            Kind = kind,
            Text = text,
            FrequencyHz = frequencyHz,
            UtcTicks = timestampUtc.Ticks,
            Fields = fields
        };
        _remoteAfFeed.Enqueue(evt);
        while (_remoteAfFeed.Count > RemoteAfFeedCap && _remoteAfFeed.TryDequeue(out _)) { }
        _remoteBridge?.RaiseAf(evt);
    }

    private static bool IsRemoteAfFeedKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return false;
        if (kind.Equals("STATUS", StringComparison.OrdinalIgnoreCase)) return false;
        if (kind.Equals("AUTO DT", StringComparison.OrdinalIgnoreCase)) return false;
        if (kind.Equals("AUTO_DT_STATE", StringComparison.OrdinalIgnoreCase)) return false;
        if (kind.Equals("TIME_ADJUST", StringComparison.OrdinalIgnoreCase)) return false;
        if (kind.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) return false;
        if (kind.Equals("PERIOD_SEP", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.Equals("CAPTION", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.Equals("DIGITAL_VOICE", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.Contains("DECODE", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.Equals("CW_LINE", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.EndsWith("_CHAR", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.Equals("QSO_RECORD", StringComparison.OrdinalIgnoreCase)) return true;
        if (kind.EndsWith("_LINE", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ResolveRemoteAfPluginName(string typeId, string variant, AfPluginCatalog catalog)
    {
        if (typeId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase))
            return variant.Equals("FT4", StringComparison.OrdinalIgnoreCase) ? "FT4" : "FT8";
        return catalog.Plugins
            .FirstOrDefault(registration => registration.Info.Id.Equals(typeId, StringComparison.OrdinalIgnoreCase))
            ?.Info.Name ?? typeId;
    }

    private long ResolveRemoteAfFrequencyHz(AfPluginResult result)
    {
        var instance = _appSettings.AfPluginInstances.FirstOrDefault(item =>
            item.InstanceId.Equals(result.PluginId, StringComparison.OrdinalIgnoreCase));
        var vfoId = instance?.VfoId ?? "main";
        long rfHz;
        if (vfoId.Equals("main", StringComparison.OrdinalIgnoreCase))
            rfHz = Interlocked.Read(ref _tunedFrequency);
        else
        {
            var sub = _appSettings.SubVfos.FirstOrDefault(s =>
                s.Id.Equals(vfoId, StringComparison.OrdinalIgnoreCase));
            rfHz = sub?.Frequency ?? Interlocked.Read(ref _tunedFrequency);
        }

        var typeId = _afPluginHost.PluginTypeId(result.PluginId) ?? result.PluginId;
        if (typeId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase) &&
            result.Fields is not null &&
            float.TryParse(result.Fields.GetValueOrDefault("freq"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var afHz) &&
            vfoId.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            // Same RF estimate as the desktop waterfall overlay.
            rfHz = Interlocked.Read(ref _tunedFrequency) + (long)Math.Round(afHz - 1500);
        }

        return rfHz;
    }

    private static string FormatRemoteFrequency(long hz)
    {
        if (hz >= 1_000_000)
            return (hz / 1_000_000d).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + " MHz";
        if (hz >= 1_000)
            return (hz / 1_000d).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " kHz";
        return hz.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Hz";
    }

    private static float[] DownsampleSpectrum(float[] spectrum, int bins)
    {
        if (spectrum.Length <= bins) return (float[])spectrum.Clone();
        var result = new float[bins];
        var step = spectrum.Length / (double)bins;
        for (var i = 0; i < bins; i++)
        {
            var start = (int)(i * step);
            var end = Math.Min(spectrum.Length, (int)((i + 1) * step));
            if (end <= start) end = Math.Min(spectrum.Length, start + 1);
            var peak = -140f;
            for (var n = start; n < end; n++) peak = Math.Max(peak, spectrum[n]);
            result[i] = peak;
        }
        return result;
    }

    internal RadioRemoteSnapshot CaptureRemoteSnapshot()
    {
        var active = _appSettings.AfPluginInstances
            .Where(instance => _afPluginHost.IsEnabled(instance.InstanceId))
            .Select(instance => new ActiveAfPluginRemoteInfo
            {
                Id = instance.InstanceId,
                TypeId = instance.PluginId,
                Name = ResolveRemoteAfPluginName(
                    instance.PluginId,
                    string.IsNullOrWhiteSpace(instance.Variant) ? _appSettings.FtxMode : instance.Variant,
                    _afPlugins)
            })
            .ToArray();
        var scene = _rxSceneBox.SelectedItem as RxScene
            ?? _appSettings.RxScenes.FirstOrDefault(s =>
                s.Id.Equals(_appSettings.SelectedRxSceneId, StringComparison.OrdinalIgnoreCase));
        var channels = EnumerateVisibleSceneChannels()
            .Select(ch => new SceneChannelRemoteInfo
            {
                Id = ch.Id,
                Name = ch.Name ?? "",
                Label = ch.ToString(),
                FrequencyHz = ch.Frequency,
                Mode = ch.Mode.ToString(),
                BandwidthHz = ch.Bandwidth,
                Global = ch.Global
            })
            .ToArray();
        var scenes = (_appSettings.RxScenes ?? [])
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new RxSceneRemoteInfo { Id = s.Id, Name = s.Name })
            .ToArray();
        var subVfos = (_appSettings.SubVfos ?? [])
            .Select(s => new SubVfoRemoteInfo
            {
                Id = s.Id,
                Name = s.Name,
                FrequencyHz = s.Frequency,
                Mode = s.Mode.ToString(),
                BandwidthHz = s.Bandwidth,
                Enabled = true
            })
            .ToArray();

        return new RadioRemoteSnapshot
        {
            Running = _source.IsRunning,
            WebRemoteEnabled = _appSettings.WebRemoteEnabled,
            Source = _source.Name,
            FrequencyHz = Interlocked.Read(ref _tunedFrequency),
            RfCenterHz = Interlocked.Read(ref _rfCenterFrequency),
            ViewCenterHz = Interlocked.Read(ref _viewCenterFrequency),
            ViewBandwidthHz = Volatile.Read(ref _viewBandwidth),
            Mode = _demodulator.Mode.ToString(),
            FilterBandwidthHz = _demodulator.Bandwidth,
            GainPercent = _gainSlider.Value,
            SignalDb = Volatile.Read(ref _signalLevelDb),
            Volume1 = _rxVolumeBar.Value,
            Volume2 = _rxVolumeBar2.Value,
            AudioSampleRate = RemoteAudioSampleRate,
            Squelch1Enabled = _squelchCheck.Checked,
            Squelch1Threshold = _rxSquelchBar.Value,
            Squelch2Enabled = _squelchCheck2.Checked,
            Squelch2Threshold = _rxSquelchBar2.Value,
            Squelch1Open = _squelchCheck.Checked && _audioProcessors[0].SquelchOpen,
            Squelch2Open = _squelchCheck2.Checked && _audioProcessors[1].SquelchOpen,
            Audio2Enabled = _appSettings.Audio2.Enabled,
            ActiveAfPlugins = active,
            AvailableSources = CaptureRemoteSources().ToArray(),
            AvailableModes = CaptureRemoteModes().ToArray(),
            Status = CaptureRemoteStatusLine(),
            WebUrl = _remoteWebHost?.BaseAddress?.ToString() ?? "",
            SelectedSceneId = scene?.Id ?? _appSettings.SelectedRxSceneId ?? "",
            SelectedSceneName = scene?.Name ?? "",
            SelectedChannelId = _appSettings.SelectedSceneChannelId ?? "",
            Scenes = scenes,
            Channels = channels,
            SubVfos = subVfos,
            Bands = RemoteBandCatalog,
            AudioLevelDb = Volatile.Read(ref _audioLevelDb),
            StereoLed = _stereoLed.IsOn,
            WfmStereo = _appSettings.WfmStereoEnabled,
            WfmHfSoft = _appSettings.WfmHfSoftEnabled,
            WfmEqPreset = _wfmModePanel.SelectedEqPreset ?? "",
            WfmEqPresets = _wfmModePanel.EqPresets.Select(p => p.Name).ToArray(),
            AfcEnabled = _appSettings.AfcEnabled,
            AfcSpeedIndex = _appSettings.AfcSpeedIndex,
            AfcRangeHz = _appSettings.AfcRangeHz,
            AfcStatus = _analogModePanel.StatusText,
            AnalogTone = _analogModePanel.ToneText,
            CwLowerSide = _appSettings.CwLowerSide,
            CwAfWidthHz = _appSettings.CwAfFilterWidthHz,
            AgcEnabled = _agcCheck.Checked,
            NoiseReduction = _noiseReductionCheck.Checked,
            NoiseReductionStrength = _noiseReductionSlider.Value,
            NotchEnabled = _notchCheck.Checked,
            AfFilterEnabled = _afFilterCheck.Checked,
            FreeDvModem = _appSettings.FreeDvModem ?? "Auto",
            FreeDvSideband = _appSettings.FreeDvSideband ?? "Auto",
            DigitalOutputChannel = _digitalModePanel.OutputChannel,
            DigitalPcmAgc = _digitalModePanel.FeedAgc,
            DigitalFeedVolume = _digitalModePanel.FeedVolumePercent,
            DigitalStatus = _digitalMode.Status,
            DigitalOverlay = _lastDigitalOverlay ?? "",
            IsRemoteSource = _source is IRemoteAudioSampleSource,
            SiteUrl = _source is IRemoteAudioSampleSource remoteSite ? remoteSite.ServerUrl : "",
            SiteName = _siteBox.SelectedItem is RemoteSdrEntry site ? site.ToString() : _siteBox.Text,
            Sites = [],
            SatelliteActive = IsSatelliteSceneActive,
            SatelliteStatus = IsSatelliteSceneActive ? (scene?.Name ?? "SATELLITE") : "",
            AfSpanHz = _demodulator.Mode == RadioMode.WFM
                ? 4_000
                : Math.Clamp(_demodulator.Bandwidth <= 4_000 ? 3_500 : _demodulator.Bandwidth, 3_500, 12_000)
        };
    }

    /// <summary>Compact status for web — never dump the full hardware probe string.</summary>
    private string CaptureRemoteStatusLine()
    {
        var name = _source?.Name ?? "—";
        if (_source?.IsRunning == true) return $"Receiving · {name}";
        var text = _statusLabel.Text ?? "";
        // Keep short operational messages (scene, error) if they are not the probe dump.
        if (!string.IsNullOrWhiteSpace(text) &&
            text.Length < 120 &&
            !text.Contains("found ·", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("library not found", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("No RTL-SDR", StringComparison.OrdinalIgnoreCase))
            return text;
        return $"Ready · {name}";
    }

    private static readonly BandRemoteInfo[] RemoteBandCatalog =
    [
        new() { Id = "mw", Name = "MW", FrequencyHz = 1_000_000, Mode = "AM" },
        new() { Id = "sw", Name = "SW", FrequencyHz = 6_000_000, Mode = "AM" },
        new() { Id = "160", Name = "160", FrequencyHz = 1_900_000, Mode = "LSB" },
        new() { Id = "80", Name = "80", FrequencyHz = 3_650_000, Mode = "LSB" },
        new() { Id = "40", Name = "40", FrequencyHz = 7_100_000, Mode = "LSB" },
        new() { Id = "30", Name = "30", FrequencyHz = 10_120_000, Mode = "USB" },
        new() { Id = "20", Name = "20", FrequencyHz = 14_200_000, Mode = "USB" },
        new() { Id = "17", Name = "17", FrequencyHz = 18_100_000, Mode = "USB" },
        new() { Id = "15", Name = "15", FrequencyHz = 21_200_000, Mode = "USB" },
        new() { Id = "12", Name = "12", FrequencyHz = 24_950_000, Mode = "USB" },
        new() { Id = "10", Name = "10", FrequencyHz = 28_400_000, Mode = "USB" },
        new() { Id = "6", Name = "6", FrequencyHz = 50_150_000, Mode = "USB" },
        new() { Id = "fm", Name = "FM", FrequencyHz = 89_100_000, Mode = "WFM" },
        new() { Id = "air", Name = "AIR", FrequencyHz = 118_300_000, Mode = "AM" }
    ];

    internal IReadOnlyList<string> CaptureRemoteSources() =>
        _sources.Select(source => source.Name).ToArray();

    internal IReadOnlyList<string> CaptureRemoteModes() =>
        Enum.GetNames<RadioMode>();

    internal IReadOnlyList<AfPluginRemoteEvent> CaptureRemoteAfFeed(int maxItems)
    {
        FlushRemoteCwLines();
        var items = _remoteAfFeed.ToArray();
        if (items.Length <= maxItems) return items;
        return items.AsSpan(items.Length - maxItems).ToArray();
    }

    internal void RemoteSetRunning(bool running)
    {
        if (_source.IsRunning == running) return;
        ToggleReceiver();
        PublishRemoteState();
    }

    internal void RemoteSetFrequency(long hz)
    {
        ChangeVfoFromDigitalDisplay(hz, userInitiated: true);
        PublishRemoteState();
    }

    internal void RemoteNudgeFrequency(long deltaHz)
    {
        var next = Interlocked.Read(ref _tunedFrequency) + deltaHz;
        ChangeVfoFromDigitalDisplay(next, userInitiated: true);
        PublishRemoteState();
    }

    internal void RemoteSetMode(string mode)
    {
        if (!Enum.TryParse<RadioMode>(mode, true, out var parsed)) return;
        if (_modeBox.Items.Contains(parsed.ToString()))
            _modeBox.SelectedItem = parsed.ToString();
        PublishRemoteState();
    }

    internal void RemoteSetBandwidth(int hz)
    {
        _bandwidthBox.Value = Math.Clamp(hz, (int)_bandwidthBox.Minimum, (int)_bandwidthBox.Maximum);
        PublishRemoteState();
    }

    internal void RemoteSetGain(int percent)
    {
        _gainSlider.Value = Math.Clamp(percent, _gainSlider.Minimum, _gainSlider.Maximum);
        PublishRemoteState();
    }

    internal void RemoteSetVolume(int channel, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (channel <= 1)
        {
            _rxVolumeBar.Value = percent;
            _appSettings.Audio1.Volume = percent;
            if (_audioOutputs[0] is not null) _audioOutputs[0]!.VolumePercent = percent;
        }
        else
        {
            _rxVolumeBar2.Value = percent;
            _appSettings.Audio2.Volume = percent;
            if (_audioOutputs[1] is not null) _audioOutputs[1]!.VolumePercent = percent;
        }
        PublishRemoteState();
    }

    internal void RemoteSetSquelch(int channel, bool enabled, int thresholdDb)
    {
        thresholdDb = Math.Clamp(thresholdDb, -140, 0);
        if (channel <= 1)
        {
            _squelchCheck.Checked = enabled;
            _rxSquelchBar.Value = thresholdDb;
            _squelchThreshold.Value = thresholdDb;
            _appSettings.Audio1.SquelchEnabled = enabled;
            _appSettings.Audio1.SquelchThreshold = thresholdDb;
            _audioProcessors[0].SquelchEnabled = enabled;
            _audioProcessors[0].SquelchThresholdDb = thresholdDb;
        }
        else
        {
            _squelchCheck2.Checked = enabled;
            _rxSquelchBar2.Value = thresholdDb;
            _appSettings.Audio2.SquelchEnabled = enabled;
            _appSettings.Audio2.SquelchThreshold = thresholdDb;
            _audioProcessors[1].SquelchEnabled = enabled;
            _audioProcessors[1].SquelchThresholdDb = thresholdDb;
        }
        PublishRemoteState();
    }

    internal void RemoteSetSource(string name)
    {
        var index = _sources.FindIndex(source => source.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        if (_rxSourceBox.SelectedIndex != index)
            _rxSourceBox.SelectedIndex = index;
        PublishRemoteState();
    }

    internal void RemoteApplyScene(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId)) return;
        var scene = _appSettings.RxScenes.FirstOrDefault(s =>
            s.Id.Equals(sceneId, StringComparison.OrdinalIgnoreCase));
        if (scene is null) return;
        RefreshRxSceneCombo(scene.Id);
        ApplyRxScene(scene, applyPopOut: true, persist: false);
        PublishRemoteState();
    }

    internal void RemoteSelectChannel(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return;
        var channel = EnumerateVisibleSceneChannels()
            .FirstOrDefault(c => c.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase));
        if (channel is null) return;
        ApplySceneFrequencyChannel(channel);
        PublishRemoteState();
    }

    internal void RemoteSelectBand(string bandId)
    {
        var band = RemoteBandCatalog.FirstOrDefault(b =>
            b.Id.Equals(bandId, StringComparison.OrdinalIgnoreCase));
        if (band is null) return;
        if (!Enum.TryParse<RadioMode>(band.Mode, true, out var mode)) return;
        SelectBand(band.FrequencyHz, mode);
        PublishRemoteState();
    }

    internal void RemoteCenterViewOnTune()
    {
        if (_source is IRemoteAudioSampleSource)
            CenterSpectrumOnFrequency(Interlocked.Read(ref _tunedFrequency));
        else
            RecenterOnVfo();
        PublishRemoteState();
    }

    internal void RemoteSetViewBandwidth(int hz)
    {
        if (_source is IRemoteAudioSampleSource remote)
        {
            var max = Math.Max(MinimumViewBandwidth(), remote.MaximumSpectrumSpan);
            _viewBandwidth = Math.Clamp(hz, MinimumViewBandwidth(), max);
            _viewCenterFrequency = ClampViewCenter(Interlocked.Read(ref _tunedFrequency), _viewBandwidth);
            ConfigureDisplay();
            UpdateZoomControls();
            if (remote.IsRunning)
                QueueRemoteViewport(immediate: false);
            PublishRemoteState();
            return;
        }

        var localMax = Math.Max(5_000, _source.SampleRate);
        _viewBandwidth = Math.Clamp(hz, 5_000, localMax);
        _viewCenterFrequency = ClampViewCenter(Interlocked.Read(ref _tunedFrequency), _viewBandwidth);
        ConfigureDisplay();
        UpdateZoomControls();
        PublishRemoteState();
    }

    private long _nextRemoteAfSpectrumPublishTick;

    internal void PublishRemoteAfSpectrum(float[] spectrum)
    {
        if (_remoteBridge is null || spectrum.Length == 0) return;
        var now = Environment.TickCount64;
        if (now < _nextRemoteAfSpectrumPublishTick) return;
        _nextRemoteAfSpectrumPublishTick = now + 80;
        var span = _demodulator.Mode == RadioMode.WFM
            ? 4_000
            : Math.Clamp(_demodulator.Bandwidth <= 4_000 ? 3_500 : _demodulator.Bandwidth, 3_500, 12_000);
        _remoteBridge.RaiseAfSpectrum(new SpectrumRemoteFrame
        {
            CenterHz = span / 2,
            SpanHz = span,
            TunedHz = 0,
            FilterHz = _demodulator.Bandwidth,
            Mode = _demodulator.Mode.ToString(),
            Levels = DownsampleSpectrum(spectrum, 256)
        });
    }

    internal void PublishRemoteDigitalOverlay(string text)
    {
        if (_remoteBridge is null || string.IsNullOrWhiteSpace(text)) return;
        EmitRemoteAf("builtin.digital", _demodulator.Mode.ToString(), "DIGITAL_VOICE", text,
            Interlocked.Read(ref _tunedFrequency),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["message"] = text },
            DateTime.UtcNow);
    }

    internal RemoteSiteRemoteInfo[] CaptureRemoteSites(string query)
    {
        var protocol = _source is IRemoteAudioSampleSource remote
            ? RemoteSdrCatalog.ProtocolForSourceName(remote.Name) ?? ""
            : "";
        IEnumerable<RemoteSdrEntry> rows = _remoteDirectory;
        if (!string.IsNullOrWhiteSpace(protocol))
            rows = rows.Where(item => item.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query))
        {
            rows = rows.Where(item =>
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (item.Location ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (item.City ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (item.Country ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        return rows.Take(40).Select(item => new RemoteSiteRemoteInfo
        {
            Name = item.ToString(),
            Url = item.Url,
            Location = string.IsNullOrWhiteSpace(item.Location) ? $"{item.City} {item.Country}".Trim() : item.Location
        }).ToArray();
    }

    internal IReadOnlyList<RemoteSiteRemoteInfo> RemoteGetSites(string query) => CaptureRemoteSites(query);

    internal void RemoteSetSiteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _ = ApplyRemoteUrlAsync(url.Trim());
        PublishRemoteState();
    }

    internal void RemoteSetAfDsp(bool agc, bool noiseReduction, int nrStrength, bool notch, bool afFilter)
    {
        _agcCheck.Checked = agc;
        _noiseReductionCheck.Checked = noiseReduction;
        _noiseReductionSlider.Value = Math.Clamp(nrStrength, _noiseReductionSlider.Minimum, _noiseReductionSlider.Maximum);
        _notchCheck.Checked = notch;
        _afFilterCheck.Checked = afFilter;
        _appSettings.AgcEnabled = agc;
        _appSettings.NotchEnabled = notch;
        _appSettings.AfFilterEnabled = afFilter;
        ApplyAudioDspSettings();
        PublishRemoteState();
    }

    internal void RemoteSetAfc(bool enabled, int speedIndex, int rangeHz)
    {
        _analogModePanel.LoadOptions(enabled, speedIndex, rangeHz);
        _appSettings.AfcEnabled = enabled;
        _appSettings.AfcSpeedIndex = Math.Clamp(speedIndex, 0, 2);
        _appSettings.AfcRangeHz = rangeHz is 300 or 500 or 1_000 or 2_000 or 3_000 or 5_000 ? rangeHz : 1_000;
        _demodulator.AfcEnabled = false;
        if (!enabled) ResetAfcOffset();
        PublishRemoteState();
    }

    internal void RemoteSetWfm(bool stereo, bool hfSoft, string? eqPreset)
    {
        _wfmModePanel.LoadOptions(
            stereo, hfSoft, _appSettings.WfmHideAfPlugins,
            _appSettings.WfmEqGainsDb, eqPreset ?? _appSettings.WfmEqSelectedPreset, _appSettings.WfmEqPresets);
        _appSettings.WfmStereoEnabled = stereo;
        _appSettings.WfmHfSoftEnabled = hfSoft;
        if (!string.IsNullOrWhiteSpace(eqPreset))
            _wfmModePanel.ApplyEqPresetByName(eqPreset);
        ApplyWfmEqualizer();
        ApplyWfmStereoSetting(reopenAudio: true);
        ApplyWfmAfChrome();
        PublishRemoteState();
    }

    internal void RemoteSetFreedv(string modem, string sideband)
    {
        _digitalModePanel.LoadOptions(
            _digitalModePanel.OutputChannel,
            _digitalModePanel.FeedAgc,
            _digitalModePanel.FeedVolumePercent,
            _digitalModePanel.FeedSource,
            _digitalModePanel.LineInDeviceId,
            _digitalModePanel.SelectedWavPath,
            modem, sideband);
        _appSettings.FreeDvModem = _digitalModePanel.FreeDvModem;
        _appSettings.FreeDvSideband = _digitalModePanel.FreeDvSideband;
        ApplyFreedvOptions();
        PublishRemoteState();
    }

    internal void RemoteSetDigitalFeed(int outputChannel, bool pcmAgc, int feedVolumePercent)
    {
        _digitalModePanel.LoadOptions(
            outputChannel, pcmAgc, feedVolumePercent,
            _digitalModePanel.FeedSource,
            _digitalModePanel.LineInDeviceId,
            _digitalModePanel.SelectedWavPath,
            _digitalModePanel.FreeDvModem,
            _digitalModePanel.FreeDvSideband);
        _appSettings.DigitalVoiceOutputChannel = _digitalModePanel.OutputChannel;
        _appSettings.DigitalVoiceFeedAgc = _digitalModePanel.FeedAgc;
        _appSettings.DigitalVoiceFeedVolume = _digitalModePanel.FeedVolumePercent;
        _digitalMode.ApplyFeedOptions(
            _digitalModePanel.FeedAgc,
            _digitalModePanel.FeedVolumePercent,
            _digitalModePanel.OutputChannel);
        PublishRemoteState();
    }

    internal void RemoteSetCw(bool lowerSide, int afWidthHz)
    {
        _cwSideBox.SelectedIndex = lowerSide ? 1 : 0;
        _appSettings.CwLowerSide = lowerSide;
        _demodulator.CwPitchHz = lowerSide ? -700 : 700;
        if (afWidthHz is 100 or 200 or 300 or 400)
            ApplyCwAfFilterPreset(afWidthHz);
        PublishRemoteState();
    }
}

internal sealed class NeuroSDRRemoteRadioBridge : INeuroSDRRemoteRadio
{
    private readonly frmNeuroSDR _form;

    public event Action<RadioRemoteSnapshot>? StateChanged;
    public event Action<RadioLiveUpdate>? LiveChanged;
    public event Action<SpectrumRemoteFrame>? SpectrumAvailable;
    public event Action<SpectrumRemoteFrame>? AfSpectrumAvailable;
    public event Action<byte[]>? AudioAvailable;
    public event Action<AfPluginRemoteEvent>? AfPluginEvent;

    public NeuroSDRRemoteRadioBridge(frmNeuroSDR form) => _form = form;

    public RadioRemoteSnapshot GetSnapshot() => Invoke(() => _form.CaptureRemoteSnapshot());
    public IReadOnlyList<string> GetSources() => Invoke(() => _form.CaptureRemoteSources());
    public IReadOnlyList<string> GetModes() => Invoke(() => _form.CaptureRemoteModes());
    public IReadOnlyList<AfPluginRemoteEvent> GetAfFeed(int maxItems = 80) =>
        Invoke(() => _form.CaptureRemoteAfFeed(maxItems));

    public void SetRunning(bool running) => Invoke(() => _form.RemoteSetRunning(running));
    public void SetFrequency(long hz) => Invoke(() => _form.RemoteSetFrequency(hz));
    public void NudgeFrequency(long deltaHz) => Invoke(() => _form.RemoteNudgeFrequency(deltaHz));
    public void SetMode(string mode) => Invoke(() => _form.RemoteSetMode(mode));
    public void SetBandwidth(int hz) => Invoke(() => _form.RemoteSetBandwidth(hz));
    public void SetGain(int percent) => Invoke(() => _form.RemoteSetGain(percent));
    public void SetVolume(int channel, int percent) => Invoke(() => _form.RemoteSetVolume(channel, percent));
    public void SetSquelch(int channel, bool enabled, int thresholdDb) =>
        Invoke(() => _form.RemoteSetSquelch(channel, enabled, thresholdDb));
    public void SetSource(string name) => Invoke(() => _form.RemoteSetSource(name));
    public void ApplyScene(string sceneId) => Invoke(() => _form.RemoteApplyScene(sceneId));
    public void SelectChannel(string channelId) => Invoke(() => _form.RemoteSelectChannel(channelId));
    public void SelectBand(string bandId) => Invoke(() => _form.RemoteSelectBand(bandId));
    public void CenterViewOnTune() => Invoke(() => _form.RemoteCenterViewOnTune());
    public void SetViewBandwidth(int hz) => Invoke(() => _form.RemoteSetViewBandwidth(hz));
    public IReadOnlyList<RemoteSiteRemoteInfo> GetSites(string query) =>
        Invoke(() => _form.RemoteGetSites(query));
    public void SetSiteUrl(string url) => Invoke(() => _form.RemoteSetSiteUrl(url));
    public void SetAfDsp(bool agc, bool noiseReduction, int nrStrength, bool notch, bool afFilter) =>
        Invoke(() => _form.RemoteSetAfDsp(agc, noiseReduction, nrStrength, notch, afFilter));
    public void SetAfc(bool enabled, int speedIndex, int rangeHz) =>
        Invoke(() => _form.RemoteSetAfc(enabled, speedIndex, rangeHz));
    public void SetWfm(bool stereo, bool hfSoft, string? eqPreset) =>
        Invoke(() => _form.RemoteSetWfm(stereo, hfSoft, eqPreset));
    public void SetFreedv(string modem, string sideband) =>
        Invoke(() => _form.RemoteSetFreedv(modem, sideband));
    public void SetDigitalFeed(int outputChannel, bool pcmAgc, int feedVolumePercent) =>
        Invoke(() => _form.RemoteSetDigitalFeed(outputChannel, pcmAgc, feedVolumePercent));
    public void SetCw(bool lowerSide, int afWidthHz) =>
        Invoke(() => _form.RemoteSetCw(lowerSide, afWidthHz));

    public void RaiseStateChanged()
    {
        if (_form.IsDisposed) return;
        StateChanged?.Invoke(_form.CaptureRemoteSnapshot());
    }

    public void RaiseLive()
    {
        if (_form.IsDisposed) return;
        LiveChanged?.Invoke(_form.CaptureRemoteLive());
    }

    public void RaiseSpectrum(SpectrumRemoteFrame frame) => SpectrumAvailable?.Invoke(frame);
    public void RaiseAfSpectrum(SpectrumRemoteFrame frame) => AfSpectrumAvailable?.Invoke(frame);
    public void RaiseAudio(byte[] pcm) => AudioAvailable?.Invoke(pcm);
    public void RaiseAf(AfPluginRemoteEvent evt) => AfPluginEvent?.Invoke(evt);

    private void Invoke(Action action)
    {
        if (_form.IsDisposed || _form.Disposing) return;
        if (_form.InvokeRequired) _form.Invoke(action);
        else action();
    }

    private T Invoke<T>(Func<T> func)
    {
        if (_form.IsDisposed || _form.Disposing) return default!;
        if (_form.InvokeRequired) return _form.Invoke(func);
        return func();
    }
}
