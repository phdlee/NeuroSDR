using NeuroSDR.Dsp;

namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// SSB AF @ 48 kHz → 8 kHz → codec2 freedv_rx. Auto tries 700D, 700E, 1600, 700C
/// (typical HF; 40 m LSB is most often 700D).
/// </summary>
internal sealed class FreeDvSession : IDisposable
{
    private const int HostRate = 48_000;
    private const int ModemRate = 8_000;
    private const float SpeechRmsGate = 0.012f;
    private static readonly int[] AutoModes =
    [
        Codec2Native.Mode700D,
        Codec2Native.Mode700E,
        Codec2Native.Mode1600,
        Codec2Native.Mode700C
    ];

    private readonly List<Decoder> _decoders = [];
    private readonly short[] _modemScratch = new short[4_096];
    private StreamingFloatResampler _toModem = new(HostRate, ModemRate);
    private float _modemPeak = 1e-3f;
    private int _wantedMode = -1; // -1 = auto
    private int _lockedMode = -1;
    private DateTime _lostSyncUtc = DateTime.MinValue;
    private DateTime _lastSpeechUtc = DateTime.MinValue;
    private string _status = "idle";
    private string _overlayCaption = "";
    private bool _started;

    public string Status => _status;
    public string OverlayCaption => _overlayCaption;
    public bool IsStarted => _started;
    public int WantedMode => _wantedMode;
    public bool HasDecodedVoice =>
        _started && (DateTime.UtcNow - _lastSpeechUtc).TotalMilliseconds < 700;

    public bool EnsureStarted(int codec2ModeOrAuto)
    {
        if (!Codec2Native.LibraryAvailable())
        {
            _status = "libcodec2.dll not found (build drowe67/codec2)";
            return false;
        }

        if (_started && _wantedMode == codec2ModeOrAuto && _decoders.Count > 0)
            return true;

        Dispose();
        _wantedMode = codec2ModeOrAuto;
        _lockedMode = codec2ModeOrAuto >= 0 ? codec2ModeOrAuto : -1;
        _toModem = new StreamingFloatResampler(HostRate, ModemRate);
        var modes = codec2ModeOrAuto >= 0 ? new[] { codec2ModeOrAuto } : AutoModes;
        foreach (var mode in modes)
        {
            var decoder = Decoder.TryOpen(mode);
            if (decoder is null) continue;
            _decoders.Add(decoder);
        }

        if (_decoders.Count == 0)
        {
            _status = "freedv_open failed";
            return false;
        }

        _started = true;
        _overlayCaption = "";
        _status = codec2ModeOrAuto >= 0
            ? $"FreeDV {Codec2Native.ModeName(codec2ModeOrAuto)} · searching"
            : "FreeDV AUTO · 700D/700E/1600/700C";
        return true;
    }

    public void PushPcm16_48k(ReadOnlySpan<short> pcm48k)
    {
        if (!_started || _decoders.Count == 0 || pcm48k.Length == 0) return;
        var host = new float[pcm48k.Length];
        for (var i = 0; i < pcm48k.Length; i++)
            host[i] = pcm48k[i] * (1f / 32768f);
        var modem = _toModem.Process(host);
        if (modem.Length == 0) return;

        var produced = 0;
        foreach (var sample in modem)
        {
            var mag = Math.Abs(sample);
            if (mag > _modemPeak) _modemPeak = mag;
            else _modemPeak = 0.995f * _modemPeak + 0.005f * mag;
            if (produced == _modemScratch.Length)
            {
                DrainModem(_modemScratch.AsSpan(0, produced));
                produced = 0;
            }
            var scale = _modemPeak > 1e-4f
                ? Math.Clamp(0.55f / _modemPeak, 0.4f, 8f)
                : 1f;
            _modemScratch[produced++] = (short)Math.Clamp(
                (int)Math.Round(sample * scale * 32767f), short.MinValue, short.MaxValue);
        }
        if (produced > 0) DrainModem(_modemScratch.AsSpan(0, produced));
    }

    public void ResetAcquisition()
    {
        foreach (var decoder in _decoders) decoder.ClearPending();
        if (_wantedMode < 0) _lockedMode = -1;
        _lostSyncUtc = DateTime.MinValue;
        _lastSpeechUtc = DateTime.MinValue;
        _overlayCaption = "";
        DigitalVoicePlayback.Clear();
    }

    public void Dispose()
    {
        foreach (var decoder in _decoders) decoder.Dispose();
        _decoders.Clear();
        _started = false;
        _lastSpeechUtc = DateTime.MinValue;
        _overlayCaption = "";
        _status = "idle";
    }

    private void DrainModem(ReadOnlySpan<short> modem)
    {
        var now = DateTime.UtcNow;
        Decoder? locked = null;
        if (_lockedMode >= 0)
            locked = _decoders.FirstOrDefault(d => d.Mode == _lockedMode);

        if (locked is not null)
        {
            locked.Push(modem, emitAudio: true, out var sync, out var snr, out var speech);
            if (speech)
            {
                _lastSpeechUtc = now;
                _overlayCaption = $"FreeDV {Codec2Native.ModeName(locked.Mode)}";
            }
            if (sync)
            {
                _lostSyncUtc = DateTime.MinValue;
                _status = $"FreeDV {Codec2Native.ModeName(locked.Mode)} SYNC SNR {snr:0.0} dB";
            }
            else
            {
                if (_lostSyncUtc == DateTime.MinValue) _lostSyncUtc = now;
                _status = $"FreeDV {Codec2Native.ModeName(locked.Mode)} lost sync";
                if (!HasDecodedVoice) _overlayCaption = "";
                if (_wantedMode < 0 && (now - _lostSyncUtc).TotalSeconds >= 2.5)
                {
                    _lockedMode = -1;
                    _lostSyncUtc = DateTime.MinValue;
                }
            }
            return;
        }

        var bestSnr = float.NegativeInfinity;
        Decoder? best = null;
        foreach (var decoder in _decoders)
        {
            decoder.Push(modem, emitAudio: false, out var sync, out var snr, out _);
            if (!sync) continue;
            if (snr < bestSnr) continue;
            bestSnr = snr;
            best = decoder;
        }

        if (best is not null)
        {
            _lockedMode = best.Mode;
            _lostSyncUtc = DateTime.MinValue;
            _status = $"FreeDV {Codec2Native.ModeName(best.Mode)} SYNC SNR {bestSnr:0.0} dB";
        }
        else
        {
            var names = string.Join("/", _decoders.Select(d => Codec2Native.ModeName(d.Mode)));
            _status = $"FreeDV searching {names}";
            _overlayCaption = "";
        }
    }

    private sealed class Decoder : IDisposable
    {
        public int Mode { get; }
        private IntPtr _handle;
        private readonly short[] _speech;
        private readonly short[] _pending;
        private int _pendingCount;

        private Decoder(int mode, IntPtr handle, short[] speech, short[] pending)
        {
            Mode = mode;
            _handle = handle;
            _speech = speech;
            _pending = pending;
        }

        public static Decoder? TryOpen(int mode)
        {
            var handle = Codec2Native.Open(mode);
            if (handle == IntPtr.Zero) return null;
            try
            {
                Codec2Native.SetSquelchEn(handle, true);
                Codec2Native.SetSnrSquelchThresh(handle, -2f);
                if (mode is Codec2Native.Mode700D or Codec2Native.Mode700E)
                    Codec2Native.SetEq(handle, true);
                var speechN = Math.Max(320, Codec2Native.GetNMaxSpeechSamples(handle));
                var modemN = Math.Max(2_048, Codec2Native.GetNMaxModemSamples(handle) * 2);
                return new Decoder(mode, handle, new short[speechN], new short[modemN]);
            }
            catch
            {
                Codec2Native.Close(handle);
                return null;
            }
        }

        public void ClearPending() => _pendingCount = 0;

        public void Push(ReadOnlySpan<short> modem, bool emitAudio, out bool sync, out float snr, out bool speech)
        {
            sync = false;
            snr = -20f;
            speech = false;
            if (_handle == IntPtr.Zero) return;
            var offset = 0;
            while (offset < modem.Length)
            {
                var room = _pending.Length - _pendingCount;
                if (room <= 0)
                {
                    _pendingCount = 0;
                    break;
                }
                var take = Math.Min(room, modem.Length - offset);
                modem.Slice(offset, take).CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += take;
                offset += take;

                while (true)
                {
                    var nin = Codec2Native.Nin(_handle);
                    if (nin <= 0 || _pendingCount < nin) break;
                    var nout = Codec2Native.Rx(_handle, _speech, _pending);
                    var rxStatus = Codec2Native.GetRxStatus(_handle);
                    ShiftPending(nin);
                    var realSync = (rxStatus & Codec2Native.RxSync) != 0;
                    if (nout > 0 && realSync && SpeechRms(_speech.AsSpan(0, nout)) >= SpeechRmsGate)
                    {
                        speech = true;
                        if (emitAudio)
                            DigitalVoicePlayback.PushDecodedMono8k(_speech.AsSpan(0, nout));
                    }
                }
            }

            Codec2Native.GetModemStats(_handle, out var syncFlag, out snr);
            var status = Codec2Native.GetRxStatus(_handle);
            sync = (status & Codec2Native.RxSync) != 0 || (syncFlag != 0 && Codec2Native.GetSync(_handle) != 0);
            // Trial-only lock is not a decode.
            if ((status & Codec2Native.RxSync) == 0)
                sync = false;
        }

        private static float SpeechRms(ReadOnlySpan<short> speech)
        {
            if (speech.Length == 0) return 0;
            double sum = 0;
            foreach (var sample in speech)
            {
                var v = sample * (1.0 / 32768.0);
                sum += v * v;
            }
            return (float)Math.Sqrt(sum / speech.Length);
        }

        private void ShiftPending(int nin)
        {
            var remain = _pendingCount - nin;
            if (remain > 0) Array.Copy(_pending, nin, _pending, 0, remain);
            _pendingCount = Math.Max(0, remain);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            Codec2Native.Close(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
