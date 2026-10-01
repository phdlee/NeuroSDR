using NeuroSDR.Core;

namespace NeuroSDR.Dsp;

/// <summary>48 kHz ENFilter (RNNoise voice / dedicated CW) on demodulated AF.</summary>
internal sealed class EnFilterProcessor : IDisposable
{
    private readonly object _gate = new();
    private readonly float[] _inFrame = new float[EnFilterNative.FrameSize];
    private readonly float[] _outFrame = new float[EnFilterNative.FrameSize];
    private readonly List<float> _pending = new(EnFilterNative.FrameSize * 2);
    private readonly List<float> _ready = new(EnFilterNative.FrameSize * 2);
    private bool _streamLive;
    private float _lastSample;
    private IntPtr _engine;
    private bool _available;
    private RadioMode _mode = RadioMode.USB;
    private EnFilterNative.NrConfig _cfg;

    public bool VoiceEnabled { get; set; }
    public bool VoiceBlanker { get; set; }
    public bool VoiceNotch { get; set; }
    public bool VoiceAgc { get; set; }
    public bool VoiceEq { get; set; } = true;
    public float VoiceWet { get; set; } = 1f;
    public bool CwEnabled { get; set; }
    public bool CwBpf { get; set; } = true;
    public bool CwAle { get; set; } = true;
    public bool CwApf { get; set; } = true;
    public bool CwGate { get; set; } = true;
    public float CwCenterHz { get; set; } = 700;
    public float CwBandwidthHz { get; set; } = 70;

    public bool Available => _available;

    public EnFilterProcessor()
    {
        HostNativeDllResolver.EnsureRegistered();
        _available = EnFilterNative.TryLoad();
        if (!_available) return;
        try
        {
            _engine = EnFilterNative.Create();
            if (_engine == IntPtr.Zero)
            {
                _available = false;
                return;
            }
            PushConfig(forceVoice: true);
        }
        catch
        {
            _available = false;
        }
    }

    public static bool IsVoiceMode(RadioMode mode) =>
        mode is RadioMode.AM or RadioMode.SAM or RadioMode.NFM or RadioMode.WFM
            or RadioMode.USB or RadioMode.LSB;

    public void SetMode(RadioMode mode)
    {
        lock (_gate)
        {
            if (_mode == mode) return;
            _mode = mode;
            ClearStream();
            if (_engine == IntPtr.Zero) return;
            EnFilterNative.Reset(_engine);
            PushConfigLocked();
        }
    }

    public void ApplyOptions()
    {
        lock (_gate)
        {
            if (_engine == IntPtr.Zero) return;
            PushConfigLocked();
        }
    }

    public void ArmCwAuto()
    {
        lock (_gate)
        {
            if (_engine == IntPtr.Zero) return;
            EnFilterNative.ArmCwAuto(_engine);
        }
    }

    public float[] Process(float[] input, RadioMode mode)
    {
        if (!_available || input.Length == 0) return input;
        var voice = IsVoiceMode(mode) && VoiceEnabled;
        var cw = mode == RadioMode.CW && CwEnabled;
        if (!voice && !cw) return input;

        lock (_gate)
        {
            if (_engine == IntPtr.Zero) return input;
            if (_mode != mode)
            {
                _mode = mode;
                ClearStream();
                EnFilterNative.Reset(_engine);
                PushConfigLocked();
            }

            _pending.AddRange(input);
            while (_pending.Count >= EnFilterNative.FrameSize)
            {
                for (var i = 0; i < EnFilterNative.FrameSize; i++)
                    _inFrame[i] = Math.Clamp(_pending[i], -1f, 1f) * 32767f;
                _pending.RemoveRange(0, EnFilterNative.FrameSize);
                var vad = EnFilterNative.ProcessFrame(_engine, _outFrame, _inFrame);
                var wetAmt = voice ? Math.Clamp(VoiceWet, 0f, 1f) : 1f;
                var mix = wetAmt >= 0.995f ? 1f : Math.Clamp(wetAmt + (1f - wetAmt) * Math.Clamp(vad, 0f, 1f), 0f, 1f);
                for (var i = 0; i < EnFilterNative.FrameSize; i++)
                {
                    var wet = Math.Clamp(_outFrame[i] / 32768f, -1f, 1f);
                    var dry = Math.Clamp(_inFrame[i] / 32767f, -1f, 1f);
                    _ready.Add(mix * wet + (1f - mix) * dry);
                }
            }

            var output = new float[input.Length];
            var take = Math.Min(output.Length, _ready.Count);
            var pad = output.Length - take;
            // The first frames are still filling. After that, a short block must
            // never be replaced by a burst of zeros — that is a click, not latency.
            var hold = _streamLive ? _lastSample : 0f;
            for (var i = 0; i < pad; i++) output[i] = hold;
            for (var i = 0; i < take; i++) output[pad + i] = _ready[i];
            if (take > 0) _ready.RemoveRange(0, take);
            if (take == output.Length) _streamLive = true;
            if (output.Length > 0) _lastSample = output[^1];
            return output;
        }
    }

    private void ClearStream()
    {
        _pending.Clear();
        _ready.Clear();
        _streamLive = false;
        _lastSample = 0f;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_engine == IntPtr.Zero) return;
            EnFilterNative.Destroy(_engine);
            _engine = IntPtr.Zero;
        }
    }

    private void PushConfig(bool forceVoice)
    {
        lock (_gate) PushConfigLocked(forceVoice);
    }

    private void PushConfigLocked(bool forceVoice = false)
    {
        var cw = !forceVoice && _mode == RadioMode.CW;
        if (cw) EnFilterNative.PresetCw(out _cfg);
        else EnFilterNative.PresetVoice(out _cfg);
        _cfg.struct_size = MarshalSize();
        _cfg.version = EnFilterNative.ConfigVersion;
        if (cw)
        {
            _cfg.path_mode = EnFilterNative.PathCw;
            _cfg.cw_enable_bpf = CwBpf ? 1 : 0;
            _cfg.cw_enable_ale = CwAle ? 1 : 0;
            _cfg.cw_enable_apf = CwApf ? 1 : 0;
            _cfg.cw_enable_gate = CwGate ? 1 : 0;
            _cfg.cw_bpf_center_hz = Math.Clamp(CwCenterHz, 200f, 1500f);
            _cfg.cw_bpf_bw_hz = Math.Clamp(CwBandwidthHz, 20f, 400f);
        }
        else
        {
            _cfg.path_mode = EnFilterNative.PathVoice;
            _cfg.enable_rnnoise = 1;
            _cfg.enable_blanker = VoiceBlanker ? 1 : 0;
            _cfg.enable_notch = VoiceNotch ? 1 : 0;
            _cfg.enable_agc = VoiceAgc ? 1 : 0;
            _cfg.enable_eq = VoiceEq ? 1 : 0;
            _cfg.enable_classifier = 1;
            _cfg.enable_wiener = 0;
            _cfg.enable_hybrid_blend = 0;
        }
        EnFilterNative.SetConfig(_engine, ref _cfg);
    }

    private static int MarshalSize() =>
        System.Runtime.InteropServices.Marshal.SizeOf<EnFilterNative.NrConfig>();
}
