namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// Built-in digital-voice path: NFM discriminator AF → int16 PCM → dsdfme.dll.
/// </summary>
internal sealed class DigitalModeEngine : IDisposable
{
    private const float AgcTarget = 0.40f;
    private const float AgcMinScale = 0.50f;
    private const float AgcMaxScale = 1.50f;
    /// <summary>
    /// Software FM is df/12500. ×2.95 puts a ±1944 Hz DMR outer symbol near ±15000 int16.
    /// Speaker volume does not scale this feed.
    /// </summary>
    internal const float DiscriminatorFeedGain = 2.95f;

    private readonly object _sync = new();
    private readonly DsdFmeSession _dsd = new();
    private readonly FreeDvSession _freedv = new();
    private readonly RadioIdDirectory _radioIds = new();
    private readonly short[] _pcm = new short[4_800];
    private Core.RadioMode _protocol = Core.RadioMode.DMR;
    private bool _active;
    private int _outputChannel = 1;
    private bool _feedAgc;
    private int _feedVolumePercent = 70;
    private float _peak = 1e-3f;
    private float _lastScale = 1f;
    private string _status = "idle";
    private string _freedvModem = "Auto";
    private bool _freedvLower = true;
    private string _lastOverlay = "";
    private string _lastCallKey = "";
    private DateTime _lastStatusUtc = DateTime.MinValue;

    public DigitalModeEngine() => _radioIds.Reload();

    public bool IsActive
    {
        get { lock (_sync) return _active; }
    }

    public int OutputChannel
    {
        get { lock (_sync) return _outputChannel; }
        set
        {
            lock (_sync)
            {
                _outputChannel = Math.Clamp(value, 1, 2);
                if (_active) ApplySpeakerOwnershipLocked();
            }
        }
    }

    public bool FeedAgcEnabled
    {
        get { lock (_sync) return _feedAgc; }
        set { lock (_sync) _feedAgc = value; }
    }

    public int FeedVolumePercent
    {
        get { lock (_sync) return _feedVolumePercent; }
        set { lock (_sync) _feedVolumePercent = Math.Clamp(value, 0, 100); }
    }

    public string Status
    {
        get { lock (_sync) return _status; }
    }

    public string OverlayText
    {
        get { lock (_sync) return _lastOverlay; }
    }

    public event Action<string>? TextMessageAvailable;
    public event Action? StatusChanged;

    public void ApplyFreedvOptions(string modem, bool lowerSideband)
    {
        lock (_sync)
        {
            var modemChanged = !string.Equals(_freedvModem, modem, StringComparison.OrdinalIgnoreCase);
            _freedvModem = string.IsNullOrWhiteSpace(modem) ? "Auto" : modem.Trim();
            _freedvLower = lowerSideband;
            if (_active && _protocol == Core.RadioMode.FREEDV && modemChanged)
                StartFreedvLocked();
        }
    }

    public void ApplyFeedOptions(bool agc, int feedVolumePercent, int outputChannel)
    {
        lock (_sync)
        {
            _feedAgc = agc;
            _feedVolumePercent = Math.Clamp(feedVolumePercent, 0, 100);
            _outputChannel = Math.Clamp(outputChannel, 1, 2);
            if (_active) ApplySpeakerOwnershipLocked();
        }
    }

    public void ReloadRadioIds()
    {
        lock (_sync) _radioIds.Reload();
        StatusChanged?.Invoke();
    }

    public void SetMode(Core.RadioMode mode, int outputChannel)
    {
        lock (_sync)
        {
            var want = Core.RadioModes.IsDigitalVoice(mode);
            _outputChannel = Math.Clamp(outputChannel, 1, 2);
            if (!want)
            {
                if (_active) StopLocked();
                return;
            }

            var protocolChanged = _active && _protocol != mode;
            _protocol = mode;
            if (!_active || protocolChanged)
            {
                DigitalVoicePlayback.Clear();
                _dsd.Dispose();
                _freedv.Dispose();
                _peak = 1e-3f;
                _lastScale = 1f;
                _lastCallKey = "";
                _lastOverlay = "";
                if (mode == Core.RadioMode.FREEDV)
                {
                    if (!StartFreedvLocked())
                        return;
                }
                else
                {
                    var nativeProtocol = mode switch
                    {
                        Core.RadioMode.DMR => 1,
                        Core.RadioMode.DSTAR => 2,
                        Core.RadioMode.C4FM => 3,
                        _ => 0
                    };
                    if (!_dsd.EnsureStarted(pcmMode: true, invertedDmr: false, protocol: nativeProtocol))
                    {
                        _active = false;
                        DigitalVoicePlayback.OwnedOutputIndex = -1;
                        DigitalVoicePacer.Stop();
                        _status = _dsd.Status;
                        StatusChanged?.Invoke();
                        return;
                    }
                    DigitalVoicePacer.Start();
                }
            }

            _active = true;
            ApplySpeakerOwnershipLocked();
            var rid = _radioIds.Count > 0 ? $"RadioID {_radioIds.Count}" : "RadioID none";
            _status = mode == Core.RadioMode.FREEDV
                ? $"{mode} · SSB → codec2 · {_freedv.Status} · OUT{_outputChannel}"
                : $"{mode} · PCM · {_dsd.Status} · {rid} · OUT{_outputChannel}";
            StatusChanged?.Invoke();
        }
    }

    public void NotifyCarrierRetune()
    {
        lock (_sync)
        {
            if (!_active) return;
            // Fresh DMR/D-STAR/C4FM acquisition after AUTO (or any big VFO jump).
            // A half-synced session after a weak first lock often never recovers.
            DigitalVoicePlayback.Clear();
            _dsd.Dispose();
            _freedv.Dispose();
            _peak = 1e-3f;
            _lastScale = 1f;
            _lastCallKey = "";
            _lastOverlay = "";
            if (_protocol == Core.RadioMode.FREEDV)
            {
                StartFreedvLocked();
                _status = $"{_protocol} · re-acquire · {_freedv.Status}";
                StatusChanged?.Invoke();
                return;
            }
            var nativeProtocol = _protocol switch
            {
                Core.RadioMode.DMR => 1,
                Core.RadioMode.DSTAR => 2,
                Core.RadioMode.C4FM => 3,
                _ => 0
            };
            if (!_dsd.EnsureStarted(pcmMode: true, invertedDmr: false, protocol: nativeProtocol))
            {
                _status = _dsd.Status;
                StatusChanged?.Invoke();
                return;
            }
            _status = $"{_protocol} · re-acquire · {_dsd.Status}";
            StatusChanged?.Invoke();
        }
    }

    public void ProcessAf(ReadOnlySpan<float> pcm48k) => ProcessAf(pcm48k, softwareDiscriminator: false);

    public void ProcessAf(ReadOnlySpan<float> pcm48k, bool softwareDiscriminator)
    {
        lock (_sync)
        {
            if (!_active) return;
            if (_protocol == Core.RadioMode.FREEDV)
            {
                if (!StartFreedvLocked()) return;
            }
            else
            {
                var nativeProtocol = _protocol switch
                {
                    Core.RadioMode.DMR => 1,
                    Core.RadioMode.DSTAR => 2,
                    Core.RadioMode.C4FM => 3,
                    _ => 0
                };
                if (!_dsd.EnsureStarted(pcmMode: true, invertedDmr: false, protocol: nativeProtocol))
                    return;
            }

            foreach (var sample in pcm48k)
            {
                var mag = Math.Abs(sample);
                if (mag > _peak) _peak = mag;
                else _peak = 0.999f * _peak + 0.001f * mag;
            }

            float scale;
            if (softwareDiscriminator && _protocol != Core.RadioMode.FREEDV)
                scale = DiscriminatorFeedGain;
            else if (_feedAgc)
            {
                var agcScale = _peak > 1e-4f
                    ? Math.Clamp(AgcTarget / _peak, AgcMinScale, AgcMaxScale)
                    : 1f;
                scale = agcScale * (_feedVolumePercent / 100f);
            }
            else
            {
                scale = _feedVolumePercent / 100f;
            }
            _lastScale = scale;

            var offset = 0;
            while (offset < pcm48k.Length)
            {
                var take = Math.Min(_pcm.Length, pcm48k.Length - offset);
                for (var i = 0; i < take; i++)
                {
                    var sample = Math.Clamp(pcm48k[offset + i] * scale, -1f, 1f);
                    _pcm[i] = (short)Math.Clamp((int)Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
                }
                if (_protocol == Core.RadioMode.FREEDV)
                    _freedv.PushPcm16_48k(_pcm.AsSpan(0, take));
                else
                    _dsd.PushPcm16(_pcm.AsSpan(0, take));
                offset += take;
            }

            if (_protocol == Core.RadioMode.FREEDV)
            {
                ApplySpeakerOwnershipLocked();
                _lastOverlay = _freedv.HasDecodedVoice ? _freedv.OverlayCaption : "";
                UpdateFreedvStatusLocked();
                return;
            }

            _dsd.PumpDecodedAudio();
            DrainTextMessagesLocked();
            UpdateCallIdentityLocked();
            UpdateStatusLocked();
        }
    }

    private void DrainTextMessagesLocked()
    {
        foreach (var msg in _dsd.PullTextMessages())
        {
            var kind = msg.Kind == 1 ? "ALIAS" : "MSG";
            var srcLabel = FormatId(msg.Source);
            var dstLabel = FormatId(msg.Target);
            var line = $"{kind} TS{msg.Slot + 1} SRC {srcLabel} → DST {dstLabel} {msg.Text}".Trim();
            if (line.Length > 80) line = line[..80];
            TextMessageAvailable?.Invoke(line);
            _lastOverlay = line;
        }
    }

    private void UpdateCallIdentityLocked()
    {
        var id = _dsd.TryGetIdentity();
        if (id is not { } ident) return;

        string line;
        string key;
        if (ident.Protocol == 2 && (!string.IsNullOrWhiteSpace(ident.SrcCall) || !string.IsNullOrWhiteSpace(ident.DstCall)))
        {
            line = $"D-STAR SRC {ident.SrcCall} → DST {ident.DstCall}";
            if (!string.IsNullOrWhiteSpace(ident.Rpt1)) line += $"  RPT {ident.Rpt1}/{ident.Rpt2}";
            if (!string.IsNullOrWhiteSpace(ident.Info)) line += $"  {ident.Info.Trim()}";
            key = $"dstar|{ident.SrcCall}|{ident.DstCall}|{ident.Info}";
        }
        else if (ident.Protocol == 3 && (!string.IsNullOrWhiteSpace(ident.SrcCall) || !string.IsNullOrWhiteSpace(ident.DstCall)))
        {
            line = $"C4FM/YSF SRC {ident.SrcCall} → DST {ident.DstCall}";
            if (!string.IsNullOrWhiteSpace(ident.Rpt1)) line += $"  {ident.Rpt1}/{ident.Rpt2}";
            key = $"ysf|{ident.SrcCall}|{ident.DstCall}";
        }
        else
        {
            // Prefer active slot IDs; fall back to the other timeslot.
            var src = ident.Src != 0 ? ident.Src : ident.SrcR;
            var dst = ident.Dst != 0 ? ident.Dst : ident.DstR;
            if (src == 0 && dst == 0) return;
            line = $"{_protocol} SRC {FormatId(src)} → DST {FormatId(dst)}  CC {ident.ColorCode}";
            if (ident.SrcR != 0 || ident.DstR != 0)
                line += $"  | TS2 SRC {FormatId(ident.SrcR)} → DST {FormatId(ident.DstR)}";
            key = $"dmr|{src}|{dst}|{ident.SrcR}|{ident.DstR}|{ident.ColorCode}";
        }

        if (key.Equals(_lastCallKey, StringComparison.Ordinal)) return;
        _lastCallKey = key;
        _lastOverlay = line.Length > 80 ? line[..80] : line;
        TextMessageAvailable?.Invoke(_lastOverlay);
    }

    private string FormatId(int id)
    {
        if (id <= 0) return "-";
        var user = _radioIds.Lookup(id);
        return user is null ? id.ToString() : $"{id}({user.Callsign})";
    }

    public void Dispose()
    {
        lock (_sync) StopLocked();
        _dsd.Dispose();
        _freedv.Dispose();
    }

    private bool StartFreedvLocked()
    {
        var codecMode = _freedvModem.Trim().ToUpperInvariant() switch
        {
            "700D" => Codec2Native.Mode700D,
            "700E" => Codec2Native.Mode700E,
            "1600" => Codec2Native.Mode1600,
            "700C" => Codec2Native.Mode700C,
            _ => -1
        };
        if (_freedv.IsStarted && _freedv.WantedMode == codecMode)
        {
            DigitalVoicePacer.Start();
            ApplySpeakerOwnershipLocked();
            return true;
        }

        if (!_freedv.EnsureStarted(codecMode))
        {
            _active = false;
            DigitalVoicePlayback.OwnedOutputIndex = -1;
            DigitalVoicePacer.Stop();
            _status = _freedv.Status;
            StatusChanged?.Invoke();
            return false;
        }
        DigitalVoicePlayback.Clear();
        DigitalVoicePacer.Start();
        ApplySpeakerOwnershipLocked();
        _status = $"FREEDV · {(_freedvLower ? "LSB" : "USB")} · {_freedv.Status}";
        StatusChanged?.Invoke();
        return true;
    }

    private void ApplySpeakerOwnershipLocked()
    {
        if (_protocol == Core.RadioMode.FREEDV)
        {
            DigitalVoicePlayback.OwnedOutputIndex =
                _freedv.HasDecodedVoice && DigitalVoicePlayback.ReadyToOwnSpeaker
                    ? _outputChannel - 1
                    : -1;
            return;
        }
        DigitalVoicePlayback.OwnedOutputIndex = _outputChannel - 1;
    }

    private void UpdateFreedvStatusLocked()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastStatusUtc).TotalSeconds < 1) return;
        _lastStatusUtc = now;
        var m = DigitalVoicePlayback.SnapshotMetrics();
        _status =
            $"{_freedv.Status} · {(_freedvLower ? "LSB" : "USB")}\n" +
            $"pcm {(_feedAgc ? "AGC" : "FIX")}×{_lastScale:0.00} peak={_peak:0.00} feed={_feedVolumePercent}%\n" +
            $"voice={m.buffered} underrun={m.underruns}";
        StatusChanged?.Invoke();
    }

    private void StopLocked()
    {
        _active = false;
        DigitalVoicePacer.Stop();
        DigitalVoicePlayback.OwnedOutputIndex = -1;
        DigitalVoicePlayback.Clear();
        _dsd.Dispose();
        _freedv.Dispose();
        _status = "idle";
        _lastOverlay = "";
        _lastCallKey = "";
        StatusChanged?.Invoke();
    }

    private void UpdateStatusLocked()
    {
        var st = _dsd.TryGetStatus();
        if (st is not { } s) return;
        var now = DateTime.UtcNow;
        if ((now - _lastStatusUtc).TotalSeconds < 1) return;
        _lastStatusUtc = now;
        var m = DigitalVoicePlayback.SnapshotMetrics();
        var id = _dsd.TryGetIdentity();
        var call = id is { } x
            ? $"SRC {FormatId(x.Src != 0 ? x.Src : x.SrcR)} DST {FormatId(x.Dst != 0 ? x.Dst : x.DstR)}"
            : $"SRC {FormatId(s.LastSrc)} DST {FormatId(s.LastTg)}";
        _status =
            $"{_protocol} sync={s.SyncType} {call} cc={s.ColorCode}\n" +
            $"pcm {(_feedAgc ? "AGC" : "FIX")}×{_lastScale:0.00} peak={_peak:0.00} feed={_feedVolumePercent}%\n" +
            $"voice={m.buffered} underrun={m.underruns} RadioID={_radioIds.Count}";
        StatusChanged?.Invoke();
    }
}
