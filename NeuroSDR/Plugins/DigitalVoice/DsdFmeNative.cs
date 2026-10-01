using System.Runtime.InteropServices;

namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// P/Invoke for the host-embedded DSD-FME library. Dibit input is the cheap path:
/// NeuroSDR already timed 4FSK, so DSD skips its 48 kHz slicer.
/// </summary>
internal static class DsdFmeNative
{
    private const string Dll = "dsdfme";

    static DsdFmeNative() => HostNativeDllResolver.EnsureRegistered();

    public static bool LibraryAvailable()
    {
        HostNativeDllResolver.EnsureRegistered();
        var root = AppContext.BaseDirectory;
        foreach (var path in new[]
                 {
                     Path.Combine(root, "dsdfme.dll"),
                     Path.Combine(root, "native", "dsdfme.dll"),
                     Path.Combine(root, "native", "win-x64", "dsdfme.dll"),
                     Path.Combine(root, "native", "win-x86", "dsdfme.dll"),
                 })
        {
            if (File.Exists(path)) return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Config
    {
        public int InputSampleRate;
        public int DmrOnly;
        public int InvertedDmr;
        public int Verbose;
        public int DibitInput;
        public int Protocol; // 0=auto, 1=DMR, 2=D-STAR, 3=YSF/C4FM
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        public int Carrier;
        public int SyncType;
        public int LastTg;
        public int LastSrc;
        public int LastTgR;
        public int LastSrcR;
        public int ColorCode;
        public int Encrypted;
        public int Slot;
        public int InQueued;
        public int OutQueued;
        public int DibitQueued;
    }

    public const int TextCap = 256;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct TextMessage
    {
        public int Slot;
        public int Source;
        public int Target;
        public int Kind; // 0 = SMS/text, 1 = talker alias
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TextCap)]
        public string Text;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct Identity
    {
        public int Protocol;
        public int SyncType;
        public int ColorCode;
        public int Slot;
        public int Src;
        public int Dst;
        public int SrcR;
        public int DstR;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string SrcCall;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string DstCall;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Rpt1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Rpt2;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Info;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_create")]
    internal static extern IntPtr Create(in Config config);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_destroy")]
    internal static extern void Destroy(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_push_pcm16")]
    internal static extern int PushPcm16(IntPtr handle, short[] samples, int count);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_push_dibits")]
    internal static extern int PushDibits(IntPtr handle, byte[] dibits, int count);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_pull_pcm16")]
    internal static extern int PullPcm16(IntPtr handle, [Out] short[] samples, int maxCount);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_get_status")]
    internal static extern int GetStatus(IntPtr handle, out Status status);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_pull_text")]
    internal static extern int PullText(IntPtr handle, out TextMessage message);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_get_identity")]
    internal static extern int GetIdentity(IntPtr handle, out Identity identity);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_output_rate")]
    internal static extern int OutputRate();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dsdfme_output_channels")]
    internal static extern int OutputChannels();
}
