using System.Runtime.InteropServices;

namespace NeuroSDR.Audio;

public sealed record WaveInDeviceInfo(int Id, string Name);

public static class WaveInDevices
{
    /// <summary>1-based ids for DSD+ (-i). Prefer <see cref="EnumerateForCapture"/> for waveInOpen.</summary>
    public static IReadOnlyList<WaveInDeviceInfo> Enumerate()
    {
        var devices = new List<WaveInDeviceInfo>();
        var count = waveInGetNumDevs();
        for (uint id = 0; id < count; id++)
        {
            if (waveInGetDevCaps(id, out var caps, (uint)Marshal.SizeOf<WaveInCaps>()) != 0) continue;
            var name = string.IsNullOrWhiteSpace(caps.Name) ? $"WaveIn {id}" : caps.Name.Trim();
            devices.Add(new WaveInDeviceInfo((int)id + 1, name)); // DSD+ uses 1-based device numbers
        }
        return devices;
    }

    /// <summary>0-based ids for waveInOpen (−1 = Windows default / WAVE_MAPPER). Includes VB-Cable etc.</summary>
    public static IReadOnlyList<WaveInDeviceInfo> EnumerateForCapture()
    {
        var devices = new List<WaveInDeviceInfo> { new(-1, "Windows Default Input") };
        var count = waveInGetNumDevs();
        for (uint id = 0; id < count; id++)
        {
            if (waveInGetDevCaps(id, out var caps, (uint)Marshal.SizeOf<WaveInCaps>()) != 0) continue;
            var name = string.IsNullOrWhiteSpace(caps.Name) ? $"WaveIn {id}" : caps.Name.Trim();
            devices.Add(new WaveInDeviceInfo((int)id, name));
        }
        return devices;
    }

    public static int FindPreferred(string hint)
    {
        var devices = Enumerate();
        var match = devices.FirstOrDefault(d =>
            d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? devices.FirstOrDefault()?.Id ?? 1;
    }

    public static int FindPreferredCapture(string hint)
    {
        var devices = EnumerateForCapture();
        var match = devices.FirstOrDefault(d =>
            d.Id >= 0 && d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? -1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WaveInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern uint waveInGetDevCaps(uint deviceId, out WaveInCaps caps, uint size);
}
