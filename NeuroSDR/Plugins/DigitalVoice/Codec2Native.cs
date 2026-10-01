using System.Runtime.InteropServices;

namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>P/Invoke for drowe67/codec2 FreeDV API (libcodec2.dll).</summary>
internal static class Codec2Native
{
    public const int Mode1600 = 0;
    public const int Mode700C = 6;
    public const int Mode700D = 7;
    public const int Mode700E = 13;

    private const string Dll = "libcodec2";

    static Codec2Native() => HostNativeDllResolver.EnsureRegistered();

    public static bool LibraryAvailable()
    {
        HostNativeDllResolver.EnsureRegistered();
        var root = AppContext.BaseDirectory;
        foreach (var name in new[] { "libcodec2.dll", "codec2.dll" })
        {
            foreach (var folder in new[] { root, Path.Combine(root, "native"), Path.Combine(root, "native", "win-x64") })
            {
                if (File.Exists(Path.Combine(folder, name))) return true;
            }
        }
        return false;
    }

    public static string ModeName(int mode) => mode switch
    {
        Mode1600 => "1600",
        Mode700C => "700C",
        Mode700D => "700D",
        Mode700E => "700E",
        _ => mode.ToString()
    };

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_open")]
    public static extern IntPtr Open(int mode);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_close")]
    public static extern void Close(IntPtr freedv);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_nin")]
    public static extern int Nin(IntPtr freedv);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_rx")]
    public static extern int Rx(IntPtr freedv, short[] speechOut, short[] demodIn);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_get_n_max_speech_samples")]
    public static extern int GetNMaxSpeechSamples(IntPtr freedv);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_get_n_max_modem_samples")]
    public static extern int GetNMaxModemSamples(IntPtr freedv);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_get_sync")]
    public static extern int GetSync(IntPtr freedv);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_get_modem_stats")]
    public static extern void GetModemStats(IntPtr freedv, out int sync, out float snrEst);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_set_squelch_en")]
    public static extern void SetSquelchEn(IntPtr freedv, [MarshalAs(UnmanagedType.I1)] bool squelchEn);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_set_eq")]
    public static extern void SetEq(IntPtr freedv, [MarshalAs(UnmanagedType.I1)] bool eq);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_set_snr_squelch_thresh")]
    public static extern void SetSnrSquelchThresh(IntPtr freedv, float threshDb);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "freedv_get_rx_status")]
    public static extern int GetRxStatus(IntPtr freedv);

    public const int RxTrialSync = 0x1;
    public const int RxSync = 0x2;
}
