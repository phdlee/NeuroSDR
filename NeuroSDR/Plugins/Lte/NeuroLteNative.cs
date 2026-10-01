using System.Runtime.InteropServices;
using System.Text;

namespace NeuroSDR.Plugins.Lte;

/// <summary>P/Invoke for ens_lte.dll (srsRAN MIB→SIB1). Optional — falls back to managed PSS/SSS.</summary>
internal static class NeuroLteNative
{
    private const string Dll = "ens_lte";

    static NeuroLteNative() => HostNativeDllResolver.EnsureRegistered();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Plmn
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Mcc;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 4)]
        public string Mnc;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Cell
    {
        public int Pci;
        public int Earfcn;
        public double FreqMhz;
        public int NofPrb;
        public int NofPorts;
        public int ExtendedCp;
        public float PssPsr;
        public float PssPowerDb;
        public float CfoHz;
        public int Sfn;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
        public string Tac;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
        public string CellId;
        public int PlmnCount;
        public Plmn P0, P1, P2, P3, P4, P5;
        public int Stage;
        public int Hits;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ens_lte_create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ens_lte_destroy(IntPtr dec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ens_lte_reset(IntPtr dec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ens_lte_hint_cell(IntPtr dec, int pci, int extendedCp, float cfoHz);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ens_lte_process_iq(IntPtr dec, float[] iq, int nComplex, int rateHz, long centerHz);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ens_lte_cell_count(IntPtr dec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ens_lte_get_cell(IntPtr dec, int index, out Cell cell);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ens_lte_status(IntPtr dec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ens_lte_version();

    public static bool TryProbe(out string detail)
    {
        try
        {
            var p = ens_lte_create();
            if (p == IntPtr.Zero)
            {
                detail = "ens_lte_create null";
                return false;
            }
            detail = Marshal.PtrToStringAnsi(ens_lte_version()) ?? "ens_lte";
            ens_lte_destroy(p);
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.GetBaseException().Message;
            return false;
        }
    }

    public static string StatusOf(IntPtr dec) =>
        dec == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ens_lte_status(dec)) ?? "";

    public static string FormatPlmn(in Cell c)
    {
        if (c.PlmnCount <= 0 || string.IsNullOrEmpty(c.P0.Mcc)) return "";
        return string.IsNullOrEmpty(c.P0.Mnc) ? c.P0.Mcc : $"{c.P0.Mcc}-{c.P0.Mnc}";
    }

    public static float[] ToInterleaved(ReadOnlySpan<NeuroSDR.Core.Complex32> iq)
    {
        var f = new float[iq.Length * 2];
        for (var i = 0; i < iq.Length; i++)
        {
            f[2 * i] = iq[i].I;
            f[2 * i + 1] = iq[i].Q;
        }
        return f;
    }
}
