using NeuroSDR.Audio;
using NeuroSDR.Controls;
using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Plugins.DigitalVoice;
using NeuroSDR.Recording;
using NeuroSDR.Settings;

namespace NeuroSDR;

/// <summary>Digital voice (DMR/D-STAR/C4FM), WFM/CW/analog mode panels, and external AF feeds.</summary>
public partial class frmNeuroSDR
{
    private bool _digitalVoiceLineInRestarting;
    private System.Windows.Forms.Timer? _digitalVoiceLineInMeter;
    private System.Windows.Forms.Timer? _digitalAgcTimer;
    private int _digitalAgcObserve;
    private int _digitalAgcPeakBits;
    private bool _digitalAgcHolding;
    private int _digitalAgcApplied;
    private int _digitalAgcQuietTicks;
    private long _digitalAgcOverloadSeen;

    /// <summary>Full-scale IQ. Weak signals stay far below this, so they keep the slider gain.</summary>
    private const float DigitalAgcAttackPeak = 0.82f;
    private const float DigitalAgcReleasePeak = 0.45f;

    private void ConfigureDigitalModeUi()
    {
        DigitalVoicePacer.Configure(
            () =>
            {
                var index = DigitalVoicePlayback.OwnedOutputIndex;
                return index >= 0 && index < _audioOutputs.Length ? _audioOutputs[index] : null;
            },
            block =>
            {
                if (SelectedAfVfoId().Equals("main", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Exchange(ref _pendingDigitalVoiceSpectrum, block);
            });
        _digitalMode.TextMessageAvailable += OnDigitalTextMessage;
        _digitalModePanel.Bind(_digitalMode);
        _digitalModePanel.OptionsChanged += () =>
        {
            _appSettings.DigitalVoiceOutputChannel = _digitalModePanel.OutputChannel;
            _appSettings.DigitalVoiceFeedAgc = _digitalModePanel.FeedAgc;
            _appSettings.DigitalModeAgc = _digitalModePanel.DigitalModeAgc;
            _appSettings.DigitalVoiceFeedVolume = _digitalModePanel.FeedVolumePercent;
            _appSettings.DigitalVoiceFeedSource = DigitalVoiceFeedSourceToSettings(_digitalModePanel.FeedSource);
            _appSettings.DigitalVoiceLineInDeviceId = _digitalModePanel.LineInDeviceId;
            _appSettings.DigitalVoiceWavPath = _digitalModePanel.SelectedWavPath;
            _appSettings.FreeDvModem = _digitalModePanel.FreeDvModem;
            _appSettings.FreeDvSideband = _digitalModePanel.FreeDvSideband;
            ApplyFreedvOptions();
            _digitalMode.ApplyFeedOptions(
                _digitalModePanel.FeedAgc,
                _digitalModePanel.FeedVolumePercent,
                _digitalModePanel.OutputChannel);
            Volatile.Write(ref _digitalAgcObserve, _digitalModePanel.DigitalModeAgc ? 1 : 0);
            if (!_digitalModePanel.DigitalModeAgc)
                ApplyUserRfGain();
            UpdateOutputOwnerCaptions();
        };
        _digitalModePanel.FeedSourceChanged += ApplyDigitalVoiceFeedSource;
        _digitalModePanel.LineInDeviceChanged += () =>
        {
            _appSettings.DigitalVoiceLineInDeviceId = _digitalModePanel.LineInDeviceId;
            if (_digitalVoiceFeedSource == DigitalVoiceFeedSource.LineIn)
                RestartDigitalVoiceLineIn();
        };
        _digitalModePanel.WavBrowseRequested += BrowseDigitalVoiceWav;
        _digitalModePanel.WavPlayStopRequested += ToggleDigitalVoiceWav;
        _digitalModePanel.LoadOptions(
            Math.Clamp(_appSettings.DigitalVoiceOutputChannel, 1, 2),
            _appSettings.DigitalVoiceFeedAgc,
            Math.Clamp(_appSettings.DigitalVoiceFeedVolume, 0, 100),
            DigitalVoiceFeedSourceFromSettings(_appSettings.DigitalVoiceFeedSource),
            _appSettings.DigitalVoiceLineInDeviceId,
            _appSettings.DigitalVoiceWavPath,
            _appSettings.FreeDvModem,
            _appSettings.FreeDvSideband);
        ApplyFreedvOptions();
        _digitalMode.ApplyFeedOptions(
            _appSettings.DigitalVoiceFeedAgc,
            _appSettings.DigitalVoiceFeedVolume,
            Math.Clamp(_appSettings.DigitalVoiceOutputChannel, 1, 2));
        _digitalVoiceFeedSource = DigitalVoiceFeedSourceFromSettings(_appSettings.DigitalVoiceFeedSource);
        _digitalModePanel.SetDigitalModeAgc(_appSettings.DigitalModeAgc);
        Volatile.Write(ref _digitalAgcObserve, _appSettings.DigitalModeAgc ? 1 : 0);
        _digitalAgcTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _digitalAgcTimer.Tick += (_, _) => OnDigitalAgcTick();
        _digitalAgcTimer.Start();

        _analogModePanel.LoadOptions(_appSettings.AfcEnabled, _appSettings.AfcSpeedIndex, _appSettings.AfcRangeHz);
        _analogModePanel.OptionsChanged += () =>
        {
            _appSettings.AfcEnabled = _analogModePanel.AfcEnabled;
            _appSettings.AfcSpeedIndex = _analogModePanel.AfcSpeedIndex;
            _appSettings.AfcRangeHz = _analogModePanel.AfcRangeHz;
            _demodulator.AfcEnabled = false;
            if (!_appSettings.AfcEnabled)
            {
                ResetAfcOffset();
                _analogModePanel.SetStatus("AFC off");
            }
        };

        _wfmModePanel.LoadOptions(
            _appSettings.WfmStereoEnabled,
            _appSettings.WfmHfSoftEnabled,
            _appSettings.WfmHideAfPlugins,
            _appSettings.WfmEqGainsDb,
            _appSettings.WfmEqSelectedPreset,
            _appSettings.WfmEqPresets);
        ApplyWfmEqualizer();
        _wfmModePanel.OptionsChanged += () =>
        {
            _appSettings.WfmStereoEnabled = _wfmModePanel.StereoEnabled;
            _appSettings.WfmHfSoftEnabled = _wfmModePanel.HfSoftEnabled;
            _appSettings.WfmHideAfPlugins = _wfmModePanel.HideAfPlugins;
            ApplyWfmEqualizer();
            ApplyWfmStereoSetting(reopenAudio: true);
            UpdateAfPluginDisplayLayout();
            ApplyWfmAfChrome();
        };
        _wfmModePanel.EqChanged += () =>
        {
            _appSettings.WfmEqGainsDb = _wfmModePanel.EqGainsDb;
            _appSettings.WfmEqSelectedPreset = _wfmModePanel.SelectedEqPreset;
            ApplyWfmEqualizer();
        };
        _wfmModePanel.PresetsChanged += () =>
        {
            _appSettings.WfmEqPresets = _wfmModePanel.ExportPresets();
            _appSettings.WfmEqSelectedPreset = _wfmModePanel.SelectedEqPreset;
            _wfmStationPanel.SetEqPresetChoices(_appSettings.WfmEqPresets.Select(p => p.Name));
            SaveSettings();
        };
        _wfmStationPanel.Bind(
            () => _tunedFrequency,
            () => _wfmModePanel.SelectedEqPreset,
            RecallWfmStation);
        _wfmStationPanel.LoadStations(
            _appSettings.WfmStations,
            _appSettings.WfmEqPresets.Select(p => p.Name));
        _wfmStationPanel.StationsChanged += () =>
        {
            _appSettings.WfmStations = _wfmStationPanel.ExportStations();
            SaveSettings();
            UpdateWfmStationMarkers();
        };
        _demodulator.StereoEnabled = _appSettings.WfmStereoEnabled;

        _cwModePanel.LoadOptions(
            _appSettings.CwAfFilterWidthHz is 100 or 200 or 300 or 400
                ? _appSettings.CwAfFilterWidthHz : 200,
            _appSettings.CwAfFilterAutoPeak);
        _cwModePanel.OptionsChanged += () =>
        {
            _appSettings.CwAfFilterAutoPeak = _cwModePanel.AutoPeakEnabled;
            _appSettings.CwAfFilterWidthHz = _cwModePanel.SelectedWidthHz;
        };
        _cwModePanel.FilterSelected += ApplyCwAfFilterPreset;

        _enFilterVoicePanel.OptionsChanged += ApplyEnFilterOptionsFromUi;
        _enFilterCwPanel.OptionsChanged += ApplyEnFilterOptionsFromUi;
        _enFilterCwPanel.AutoRequested += () => _enFilter.ArmCwAuto();
        LoadEnFilterFromSettings();

        var tips = new ToolTip();
        tips.SetToolTip(_modeDstarButton, "D-STAR");
        tips.SetToolTip(_modeC4fmButton, "C4FM / Yaesu System Fusion (YSF)");
        tips.SetToolTip(_modeFreedvButton, "FreeDV (codec2) — HF SSB digital voice, typically 700D on 40 m LSB");
        tips.SetToolTip(_rxSceneBox, "RX Scene — full state (VFO, gain, SUB, AF plugins)");
        tips.SetToolTip(_rxSceneSaveButton, "Overwrite selected scene with current RX state");
        tips.SetToolTip(_rxSceneNewButton, "Save current RX state as a new named scene");
        tips.SetToolTip(_rxSceneDelButton, "Delete selected scene (at least one SCENE always remains)");
        tips.SetToolTip(_sceneChannelStrip, "SCENE freqs · ADD · chip right-click Edit/Delete · empty right-click Clear / copy from Scene · ★ = GLOBAL");
        tips.SetToolTip(_rfPopOutOverlayButton, "Pop Out — RF spectrum/waterfall on another monitor (saved in SCENE)");
        SyncDigitalModeEngine();
    }

    private static readonly HashSet<string> AbsorbedDigitalAfPluginIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "builtin.af.dsdfmepcm",
        "builtin.af.digitalvoice"
    };

    private void PurgeAbsorbedDigitalAfPlugins()
    {
        _appSettings.AfPluginInstances.RemoveAll(instance => AbsorbedDigitalAfPluginIds.Contains(instance.PluginId));
        _appSettings.EnabledAfPluginIds = _appSettings.EnabledAfPluginIds
            .Where(id => !AbsorbedDigitalAfPluginIds.Contains(id)).ToList();
        foreach (var id in AbsorbedDigitalAfPluginIds)
            _appSettings.AfPluginVfoRoutes.Remove(id);
    }

    private void SyncDigitalModeEngine()
    {
        var mode = _demodulator.Mode;
        var channel = Math.Clamp(_appSettings.DigitalVoiceOutputChannel, 1, 2);
        ApplyFreedvOptions();
        _digitalMode.SetMode(mode, channel);
        _digitalModePanel.ShowForMode(mode);
        _analogModePanel.ShowForMode(mode);
        _enFilterVoicePanel.ShowForMode(mode);
        _wfmModePanel.ShowForMode(mode == RadioMode.WFM);
        _cwModePanel.ShowForMode(mode == RadioMode.CW);
        _enFilterCwPanel.ShowForMode(mode == RadioMode.CW);
        if (mode == RadioMode.CW && _lastEnFilterUiMode != RadioMode.CW &&
            _lastEnFilterUiMode != (RadioMode)(-1))
            ApplyCwShiftToFilterCenter();
        _lastEnFilterUiMode = mode;
        if (OutputOwner(0) is null)
            _enFilter.SetMode(mode);
        ApplyWfmStereoSetting(reopenAudio: false);
        ApplyWfmAfChrome();
        if (!RadioModes.IsDigitalVoice(mode))
        {
            StopDigitalVoiceExternalFeeds();
            _lastDigitalOverlay = "";
            if (!_afDisplay.IsDisposed) _afDisplay.SetDigitalOverlay("");
        }
        else
        {
            ApplyDigitalVoiceFeedSource();
        }
        UpdateOutputOwnerCaptions();
        UpdateExtendsPluginViews();
    }

    private void ApplyFreedvOptions()
    {
        SyncFreedvSideband();
        _digitalMode.ApplyFreedvOptions(_appSettings.FreeDvModem ?? "Auto", _demodulator.SsbLower);
        if (_source is IRemoteAudioSampleSource remote && _demodulator.Mode == RadioMode.FREEDV)
            _ = remote.ApplyReceiverAsync(RadioModes.DemodMode(RadioMode.FREEDV, _demodulator.SsbLower), _demodulator.Bandwidth);
    }

    private void SyncFreedvSideband()
    {
        var pref = _appSettings.FreeDvSideband?.Trim() ?? "Auto";
        var lower = pref.Equals("USB", StringComparison.OrdinalIgnoreCase)
            ? false
            : pref.Equals("LSB", StringComparison.OrdinalIgnoreCase) || _tunedFrequency < 10_000_000;
        _demodulator.SsbLower = lower;
        if (!_display.IsDisposed) _display.SsbLower = lower;
    }

    private static DigitalVoiceFeedSource DigitalVoiceFeedSourceFromSettings(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "wav" or "wavfile" or "file" => DigitalVoiceFeedSource.WavFile,
            "linein" or "line" or "wavein" => DigitalVoiceFeedSource.LineIn,
            _ => DigitalVoiceFeedSource.Rf
        };

    private static string DigitalVoiceFeedSourceToSettings(DigitalVoiceFeedSource source) => source switch
    {
        DigitalVoiceFeedSource.WavFile => "Wav",
        DigitalVoiceFeedSource.LineIn => "LineIn",
        _ => "Rf"
    };

    private void ApplyDigitalVoiceFeedSource()
    {
        if (!RadioModes.IsDigitalVoice(_demodulator.Mode))
        {
            StopDigitalVoiceExternalFeeds();
            return;
        }

        var wanted = _digitalModePanel.FeedSource;
        _digitalVoiceFeedSource = wanted;
        _appSettings.DigitalVoiceFeedSource = DigitalVoiceFeedSourceToSettings(wanted);
        _appSettings.DigitalVoiceLineInDeviceId = _digitalModePanel.LineInDeviceId;
        _appSettings.DigitalVoiceWavPath = _digitalModePanel.SelectedWavPath;

        if (wanted == DigitalVoiceFeedSource.LineIn)
        {
            StopDigitalVoiceWav();
            RestartDigitalVoiceLineIn();
            return;
        }

        StopDigitalVoiceLineIn();
        if (wanted != DigitalVoiceFeedSource.WavFile)
            StopDigitalVoiceWav();
        _digitalModePanel.SetFeedActivity("");
    }

    private void RestartDigitalVoiceLineIn()
    {
        if (_digitalVoiceLineInRestarting) return;
        _digitalVoiceLineInRestarting = true;
        try
        {
            StopDigitalVoiceLineIn();
            if (_digitalVoiceFeedSource != DigitalVoiceFeedSource.LineIn) return;
            if (!_digitalMode.IsActive) return;
            try
            {
                var deviceId = _digitalModePanel.LineInDeviceId;
                var name = WaveInDevices.EnumerateForCapture()
                    .FirstOrDefault(d => d.Id == deviceId)?.Name ?? $"WaveIn {deviceId}";
                _digitalVoiceWaveIn = WaveInCapture.Open(deviceId, out var openStatus);
                _digitalVoiceWaveIn.SamplesAvailable += OnDigitalVoiceExternalPcm;
                _digitalVoiceWaveIn.Start();
                _digitalMode.NotifyCarrierRetune();
                _digitalModePanel.SetFeedActivity($"Line In · {name}\n{openStatus}");
                EnsureDigitalVoiceLineInMeter();
            }
            catch (Exception exception)
            {
                StopDigitalVoiceLineIn();
                _digitalModePanel.SetFeedActivity("Line In failed · " + exception.Message);
                MessageBox.Show(this,
                    exception.Message +
                    "\n\nTips: choose “CABLE Output” (not Input).\n" +
                    "Match VB-Cable Control Panel sample rate (try 48000 or 44100).\n" +
                    "In VLC set audio device to CABLE Input.",
                    "Line In", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _digitalVoiceLineInRestarting = false;
        }
    }

    private void EnsureDigitalVoiceLineInMeter()
    {
        if (_digitalVoiceLineInMeter is not null) return;
        _digitalVoiceLineInMeter = new System.Windows.Forms.Timer { Interval = 400 };
        _digitalVoiceLineInMeter.Tick += (_, _) =>
        {
            if (_digitalVoiceWaveIn is not { IsRunning: true } capture ||
                _digitalVoiceFeedSource != DigitalVoiceFeedSource.LineIn)
            {
                _digitalVoiceLineInMeter?.Stop();
                return;
            }
            var peak = capture.Peak;
            var db = peak > 1e-6f ? 20f * MathF.Log10(peak) : -120f;
            var frames = capture.FramesReceived;
            var name = WaveInDevices.EnumerateForCapture()
                .FirstOrDefault(d => d.Id == _digitalModePanel.LineInDeviceId)?.Name ?? "WaveIn";
            var silence = frames > capture.DeviceSampleRate && db < -70
                ? " · SILENCE (wrong device / VB-Cable rate?)"
                : "";
            _digitalModePanel.SetFeedActivity(
                $"Line In · {name}\n" +
                $"{capture.DeviceSampleRate / 1_000d:0.#} kHz ×{capture.Channels} · peak {db:0} dBFS · rx {frames / 1_000d:0.0}k{silence}");
        };
        _digitalVoiceLineInMeter.Start();
    }

    private void BrowseDigitalVoiceWav()
    {
        using var dialog = new OpenFileDialog
        {
            Title = RadioModes.IsFmDigitalVoice(_demodulator.Mode)
                ? "NFM AF WAV for digital voice decode"
                : "SSB AF WAV for FreeDV decode",
            Filter = "WAV audio (*.wav)|*.wav|All files (*.*)|*.*",
            FileName = _digitalModePanel.SelectedWavPath
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _digitalModePanel.SetWavPath(dialog.FileName);
        _appSettings.DigitalVoiceWavPath = dialog.FileName;
        _digitalModePanel.SetWavPlaying(false);
    }

    private void ToggleDigitalVoiceWav()
    {
        if (_digitalVoiceWavPlayer is not null)
        {
            StopDigitalVoiceWav();
            return;
        }
        var path = _digitalModePanel.SelectedWavPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BrowseDigitalVoiceWav();
            path = _digitalModePanel.SelectedWavPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        }
        if (_digitalModePanel.FeedSource != DigitalVoiceFeedSource.WavFile)
        {
            _digitalModePanel.LoadOptions(
                _digitalModePanel.OutputChannel,
                _digitalModePanel.FeedAgc,
                _digitalModePanel.FeedVolumePercent,
                DigitalVoiceFeedSource.WavFile,
                _digitalModePanel.LineInDeviceId,
                path);
            ApplyDigitalVoiceFeedSource();
        }
        if (!AfPcmWavPlayer.TryOpen(path, out var player, out var status) || player is null)
        {
            MessageBox.Show(this, status, "WAV decode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        StopDigitalVoiceLineIn();
        _digitalVoiceFeedSource = DigitalVoiceFeedSource.WavFile;
        _digitalVoiceWavPlayer = player;
        _digitalVoiceWavPlayer.SamplesAvailable += OnDigitalVoiceExternalPcm;
        _digitalVoiceWavPlayer.Completed += message =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                StopDigitalVoiceWav();
                _digitalModePanel.SetFeedActivity(message);
            });
        };
        _digitalVoiceWavPlayer.Start();
        _digitalModePanel.SetWavPlaying(true);
        _digitalModePanel.SetFeedActivity(status + (_demodulator.Mode == RadioMode.FREEDV
            ? "\n→ codec2 FreeDV @ 8 kHz"
            : "\n→ dsdfme PCM @ 48 kHz"));
    }

    private void OnDigitalVoiceExternalPcm(float[] pcm)
    {
        if (!_digitalMode.IsActive || pcm.Length == 0) return;
        if (_digitalVoiceFeedSource is not (DigitalVoiceFeedSource.LineIn or DigitalVoiceFeedSource.WavFile))
            return;
        _digitalMode.ProcessAf(pcm);
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(PublishDigitalOverlayIfChanged);
        else PublishDigitalOverlayIfChanged();
    }

    private void StopDigitalVoiceExternalFeeds()
    {
        StopDigitalVoiceLineIn();
        StopDigitalVoiceWav();
        _digitalVoiceFeedSource = DigitalVoiceFeedSource.Rf;
    }

    private void StopDigitalVoiceLineIn()
    {
        try { _digitalVoiceLineInMeter?.Stop(); } catch { }
        var capture = Interlocked.Exchange(ref _digitalVoiceWaveIn, null);
        if (capture is null) return;
        try { capture.SamplesAvailable -= OnDigitalVoiceExternalPcm; } catch { }
        try { capture.Stop(); } catch { }
        try { capture.Dispose(); } catch { }
    }

    private void StopDigitalVoiceWav()
    {
        var player = Interlocked.Exchange(ref _digitalVoiceWavPlayer, null);
        if (player is null)
        {
            if (!_digitalModePanel.IsDisposed) _digitalModePanel.SetWavPlaying(false);
            return;
        }
        try { player.SamplesAvailable -= OnDigitalVoiceExternalPcm; } catch { }
        try { player.Dispose(); } catch { }
        if (!_digitalModePanel.IsDisposed) _digitalModePanel.SetWavPlaying(false);
    }

    private void ApplyWfmStereoSetting(bool reopenAudio)
    {
        var stereo = _demodulator.Mode == RadioMode.WFM && _appSettings.WfmStereoEnabled;
        _demodulator.StereoEnabled = stereo;
        if (!stereo) _wfmModePanel.SetStatus("Mono");
        var hasStereoOut = _audioOutputs[0]?.Channels == 2;
        if (!reopenAudio && stereo == hasStereoOut) return;
        if (!_audioEnabled || !_audioCheck.Checked) return;
        try { OpenAudioOutputs(); }
        catch (Exception exception) { _statusLabel.Text = exception.Message; }
    }

    private void ApplyWfmEqualizer()
    {
        _wfmEq.SetGainsDb(_wfmModePanel.EqGainsDb);
    }

    private void PublishDigitalOverlayIfChanged()
    {
        var text = _digitalMode.OverlayText;
        if (text.Equals(_lastDigitalOverlay, StringComparison.Ordinal)) return;
        if (text.StartsWith("MSG ", StringComparison.Ordinal) ||
            text.StartsWith("ALIAS ", StringComparison.Ordinal))
        {
            _lastDigitalOverlay = text;
            return;
        }
        _lastDigitalOverlay = text;
        void Apply()
        {
            if (_afDisplay.IsDisposed) return;
            _afDisplay.SetDigitalOverlay(text);
            if (_demodulator.Mode == RadioMode.FREEDV)
            {
                if (text.Length == 0) return;
                _afDisplay.PushDigitalMessage(text);
                PublishRemoteDigitalOverlay(text);
                return;
            }
            if (text.Length > 0)
            {
                _afDisplay.PushDigitalMessage(text);
                PublishRemoteDigitalOverlay(text);
            }
        }
        if (InvokeRequired) BeginInvoke(Apply);
        else Apply();
    }

    private void OnDigitalTextMessage(string line)
    {
        if (_demodulator.Mode == RadioMode.FREEDV) return;
        void Apply()
        {
            if (_afDisplay.IsDisposed) return;
            _afDisplay.SetDigitalOverlay(line);
            _afDisplay.PushDigitalMessage(line);
            _lastDigitalOverlay = line;
        }
        if (InvokeRequired) BeginInvoke(Apply);
        else Apply();
    }

    private RadioMode _lastEnFilterUiMode = (RadioMode)(-1);

    private float[] ApplyEnFilter(float[] audio, RadioMode mode)
    {
        if (audio.Length == 0) return audio;
        return _enFilter.Process(audio, mode);
    }

    private void CopyEnFilterOptions(EnFilterProcessor filter)
    {
        filter.VoiceEnabled = _appSettings.EnFilterVoiceEnabled;
        filter.VoiceBlanker = _appSettings.EnFilterVoiceBlanker;
        filter.VoiceNotch = _appSettings.EnFilterVoiceNotch;
        filter.VoiceAgc = _appSettings.EnFilterVoiceAgc;
        filter.VoiceEq = _appSettings.EnFilterVoiceEq;
        filter.VoiceWet = Math.Clamp(_appSettings.EnFilterVoiceWetPercent, 0, 100) / 100f;
        filter.CwEnabled = _appSettings.EnFilterCwEnabled;
        filter.CwBpf = _appSettings.EnFilterCwBpf;
        filter.CwAle = _appSettings.EnFilterCwAle;
        filter.CwApf = _appSettings.EnFilterCwApf;
        filter.CwGate = _appSettings.EnFilterCwGate;
        filter.CwCenterHz = _appSettings.EnFilterCwCenterHz;
        filter.CwBandwidthHz = _appSettings.EnFilterCwBandwidthHz;
        filter.ApplyOptions();
    }

    private void LoadEnFilterFromSettings()
    {
        _enFilterVoicePanel.LoadOptions(
            _appSettings.EnFilterVoiceEnabled,
            _appSettings.EnFilterVoiceBlanker,
            _appSettings.EnFilterVoiceNotch,
            _appSettings.EnFilterVoiceAgc,
            _appSettings.EnFilterVoiceEq,
            _appSettings.EnFilterVoiceWetPercent,
            _enFilter.Available);
        _enFilterCwPanel.LoadOptions(
            _appSettings.EnFilterCwEnabled,
            _appSettings.EnFilterCwBpf,
            _appSettings.EnFilterCwAle,
            _appSettings.EnFilterCwApf,
            _appSettings.EnFilterCwGate,
            _appSettings.EnFilterCwCenterHz,
            _appSettings.EnFilterCwBandwidthHz,
            _enFilter.Available);
        ApplyEnFilterOptionsFromUi();
    }

    private void NoteDigitalAgcPeak(Complex32[] samples)
    {
        if (!RadioModes.IsDigitalVoice(_demodulator.Mode)) return;
        float peak = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            var iAbs = sample.I < 0 ? -sample.I : sample.I;
            var qAbs = sample.Q < 0 ? -sample.Q : sample.Q;
            if (iAbs > peak) peak = iAbs;
            if (qAbs > peak) peak = qAbs;
            if (peak >= 0.98f) break;
        }
        int observed;
        do
        {
            observed = Volatile.Read(ref _digitalAgcPeakBits);
            if (peak <= BitConverter.Int32BitsToSingle(observed)) return;
        }
        while (Interlocked.CompareExchange(ref _digitalAgcPeakBits, BitConverter.SingleToInt32Bits(peak), observed) != observed);
    }

    private void OnDigitalAgcTick()
    {
        if (!DigitalAgcShouldRun())
        {
            Interlocked.Exchange(ref _digitalAgcPeakBits, 0);
            if (_source is IRfOverloadSource idle)
                _digitalAgcOverloadSeen = idle.RfOverloadEvents;
            if (_digitalAgcHolding) ApplyUserRfGain();
            return;
        }

        var peakBits = Interlocked.Exchange(ref _digitalAgcPeakBits, 0);
        var peak = BitConverter.Int32BitsToSingle(peakBits);
        var overloaded = ConsumeRfOverload() || peak >= DigitalAgcAttackPeak;
        var ceiling = _gainSlider.Value;
        if (overloaded)
        {
            _digitalAgcQuietTicks = 0;
            var applied = _digitalAgcHolding ? _digitalAgcApplied : ceiling;
            var next = Math.Max(0, applied - 6);
            if (next < applied)
                HoldDigitalAgc(next, ceiling);
            return;
        }

        if (!_digitalAgcHolding) return;
        if (peak >= DigitalAgcReleasePeak)
        {
            _digitalAgcQuietTicks = 0;
            return;
        }
        _digitalAgcQuietTicks++;
        if (_digitalAgcQuietTicks < 8) return;
        var restored = Math.Min(ceiling, _digitalAgcApplied + 2);
        if (restored >= ceiling)
            ApplyUserRfGain();
        else
            HoldDigitalAgc(restored, ceiling);
    }

    private bool DigitalAgcShouldRun() =>
        _digitalModePanel.DigitalModeAgc &&
        _source is IGainControlledSampleSource &&
        _source.IsRunning &&
        _digitalVoiceFeedSource == DigitalVoiceFeedSource.Rf &&
        RadioModes.IsDigitalVoice(_demodulator.Mode);

    private bool ConsumeRfOverload()
    {
        if (_source is not IRfOverloadSource overload) return false;
        var count = overload.RfOverloadEvents;
        var fresh = count > _digitalAgcOverloadSeen;
        _digitalAgcOverloadSeen = count;
        return fresh;
    }

    private void HoldDigitalAgc(int applied, int ceiling)
    {
        _digitalAgcHolding = true;
        _digitalAgcApplied = applied;
        if (_source is IGainControlledSampleSource gain)
            gain.GainPercent = applied;
        _digitalModePanel.SetDigitalAgcReduction(Math.Max(0, ceiling - applied));
    }

    private void ApplyUserRfGain()
    {
        _digitalAgcHolding = false;
        _digitalAgcQuietTicks = 0;
        _digitalAgcApplied = _gainSlider.Value;
        if (_source is IGainControlledSampleSource gain)
            gain.GainPercent = _gainSlider.Value;
        _digitalModePanel.SetDigitalAgcReduction(0);
    }

    private void NoteDigitalAgcCeiling(int ceiling)
    {
        if (_digitalAgcHolding && ceiling < _digitalAgcApplied)
            _digitalAgcApplied = ceiling;
        var applied = _digitalAgcHolding ? Math.Min(ceiling, _digitalAgcApplied) : ceiling;
        if (_source is IGainControlledSampleSource gain)
            gain.GainPercent = applied;
        _digitalModePanel.SetDigitalAgcReduction(_digitalAgcHolding ? Math.Max(0, ceiling - applied) : 0);
    }

    private void CaptureEnFilterSettings()
    {
        _appSettings.EnFilterVoiceEnabled = _enFilterVoicePanel.FilterEnabled;
        _appSettings.EnFilterVoiceBlanker = _enFilterVoicePanel.Blanker;
        _appSettings.EnFilterVoiceNotch = _enFilterVoicePanel.Notch;
        _appSettings.EnFilterVoiceAgc = _enFilterVoicePanel.Agc;
        _appSettings.EnFilterVoiceEq = _enFilterVoicePanel.Eq;
        _appSettings.EnFilterVoiceWetPercent = _enFilterVoicePanel.WetPercent;
        _appSettings.EnFilterCwEnabled = _enFilterCwPanel.FilterEnabled;
        _appSettings.EnFilterCwBpf = _enFilterCwPanel.Bpf;
        _appSettings.EnFilterCwAle = _enFilterCwPanel.Ale;
        _appSettings.EnFilterCwApf = _enFilterCwPanel.Apf;
        _appSettings.EnFilterCwGate = _enFilterCwPanel.Gate;
        _appSettings.EnFilterCwCenterHz = _enFilterCwPanel.CenterHz;
        _appSettings.EnFilterCwBandwidthHz = _enFilterCwPanel.BandwidthHz;
    }

    private void ApplyEnFilterOptionsFromUi()
    {
        CaptureEnFilterSettings();
        CopyEnFilterOptions(_enFilter);
        var cwFilter = CwEnFilterActive;
        foreach (var processor in _audioProcessors)
            processor.AfFilterEnabled = cwFilter ? false : _afFilterCheck.Checked;
        foreach (var receiver in Volatile.Read(ref _subVfoReceivers))
            receiver.Processor.AfFilterEnabled = cwFilter ? false : _afFilterCheck.Checked;
        ConfigureAfDisplay();
    }

    private bool CwEnFilterActive =>
        _demodulator.Mode == RadioMode.CW && _enFilterCwPanel.FilterEnabled;

    private void ApplyCwShiftToFilterCenter()
    {
        var shift = Math.Abs(_demodulator.CwPitchHz);
        if (shift < 200) shift = 700;
        _enFilterCwPanel.SetCenterHz(shift);
    }
}
