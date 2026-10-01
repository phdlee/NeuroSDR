using System.Runtime.InteropServices;

namespace NeuroSDR.Dsp;

internal static class EnFilterNative
{
    private const string Dll = "ENFilter";

    public const int ConfigVersion = 5;
    public const int StatusVersion = 4;
    public const int PathVoice = 0;
    public const int PathCw = 1;
    public const int FrameSize = 480;
    public const int SampleRate = 48_000;

    [StructLayout(LayoutKind.Sequential)]
    public struct NrConfig
    {
        public int struct_size;
        public int version;
        public int path_mode;
        public int enable_blanker;
        public int enable_notch;
        public int enable_classifier;
        public int enable_rnnoise;
        public int enable_wiener;
        public int enable_hybrid_blend;
        public int enable_agc;
        public int enable_eq;
        public float blanker_threshold;
        public int blanker_hold_samples;
        public float blanker_avg_ms;
        public float notch_mu;
        public float notch_bw_hz;
        public float notch_min_tone_snr_db;
        public float notch_adapt_alpha;
        public float vad_threshold;
        public float snr_noise_tau_ms;
        public float snr_strong_db;
        public float snr_weak_db;
        public float wiener_strength;
        public float wiener_noise_floor_db;
        public float vss_mu_max;
        public float vss_mu_min;
        public int vss_taps;
        public float agc_target_db;
        public float agc_max_gain_db;
        public float agc_attack_ms;
        public float agc_decay_ms;
        public float eq_low_cut_hz;
        public float eq_high_cut_hz;
        public float eq_presence_hz;
        public float eq_presence_gain_db;
        public float blend_silence_ratio;
        public float blend_dry_mix;
        public int blend_mode;
        public float blend_vad_low;
        public float blend_vad_high;
        public float blend_fixed_ai;
        public float blend_smooth;
        public int cw_enable_blanker;
        public int cw_enable_bpf;
        public float cw_bpf_center_hz;
        public float cw_bpf_bw_hz;
        public int cw_enable_ale;
        public float cw_ale_mu;
        public int cw_ale_delay;
        public int cw_ale_taps;
        public int cw_enable_apf;
        public float cw_apf_q;
        public float cw_apf_gain_db;
        public int cw_enable_goertzel;
        public float cw_goertzel_thr;
        public int cw_enable_gate;
        public float cw_gate_threshold;
        public float cw_gate_attack_ms;
        public float cw_gate_release_ms;
        public int cw_enable_regen;
        public float cw_regen_mix;
        public int cw_enable_agc;
        public float cw_agc_target_db;
        public float cw_agc_max_gain_db;
        public int cw_auto_arm;
        public float cw_auto_seconds;
        public int cw_enable_afc;
        public float cw_afc_range_hz;
        public float cw_afc_smooth;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nr_config_preset_voice")]
    public static extern void PresetVoice(out NrConfig cfg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nr_config_preset_cw")]
    public static extern void PresetCw(out NrConfig cfg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nr_create")]
    public static extern IntPtr Create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nr_destroy")]
    public static extern void Destroy(IntPtr eng);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nr_set_config")]
    public static extern int SetConfig(IntPtr eng, ref NrConfig cfg);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nr_reset")]
    public static extern void Reset(IntPtr eng);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nr_process_frame_f32")]
    public static extern float ProcessFrame(IntPtr eng, float[] output, float[] input);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nr_cw_arm_auto")]
    public static extern void ArmCwAuto(IntPtr eng);

    public static bool TryLoad()
    {
        HostNativeDllResolver.EnsureRegistered();
        try
        {
            PresetVoice(out _);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
