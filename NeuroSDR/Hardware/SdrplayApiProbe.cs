using System.Runtime.InteropServices;
using System.Text;

namespace NeuroSDR.Hardware;

public sealed record SdrplayDeviceInfo(string SerialNumber, byte HardwareVersion);

public static class SdrplayApiProbe
{
    private const string InstalledDll = @"C:\Program Files\SDRplay\API\x64\sdrplay_api.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArg();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ApiVersion(out float version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDevices(IntPtr devices, ref uint count, uint maximum);

    public static unsafe string DescribeInstallation(out IReadOnlyList<SdrplayDeviceInfo> devices)
    {
        devices = Array.Empty<SdrplayDeviceInfo>();
        if (!NativeLibrary.TryLoad(InstalledDll, out var library)) return "SDRplay API is not installed";

        try
        {
            var open = Get<NoArg>(library, "sdrplay_api_Open");
            var close = Get<NoArg>(library, "sdrplay_api_Close");
            var versionCall = Get<ApiVersion>(library, "sdrplay_api_ApiVersion");
            var getDevices = Get<GetDevices>(library, "sdrplay_api_GetDevices");
            if (open() != 0) return "Failed to connect to the SDRplay API service";
            try
            {
                _ = versionCall(out var version);
                const int deviceSize = 96; // API 3.x sdrplay_api_DeviceT, Windows x64 ABI
                var memory = Marshal.AllocHGlobal(deviceSize * 16);
                try
                {
                    Span<byte> cleared = new((void*)memory, deviceSize * 16);
                    cleared.Clear();
                    uint count = 0;
                    var result = getDevices(memory, ref count, 16);
                    var found = new List<SdrplayDeviceInfo>();
                    if (result == 0)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            var start = memory + i * deviceSize;
                            var serialBytes = new byte[64];
                            Marshal.Copy(start, serialBytes, 0, serialBytes.Length);
                            var terminator = Array.IndexOf(serialBytes, (byte)0);
                            var serial = Encoding.ASCII.GetString(serialBytes, 0, terminator < 0 ? 64 : terminator);
                            found.Add(new SdrplayDeviceInfo(serial, Marshal.ReadByte(start, 64)));
                        }
                    }
                    devices = found;
                    return $"SDRplay API {version:0.00} · {found.Count} device(s)";
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            finally { _ = close(); }
        }
        catch (Exception exception)
        {
            return $"SDRplay API check failed: {exception.Message}";
        }
        finally { NativeLibrary.Free(library); }
    }

    private static T Get<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
}
