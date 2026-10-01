namespace NeuroSDR.Core;

internal static class RadioLimits
{
    public const long MinimumFrequency = 1_000;
    public const long MaximumFrequency = 9_000_000_000;

    public static long ToDeviceFrequency(long logicalFrequency, int offsetHz) =>
        Math.Clamp(logicalFrequency + offsetHz, MinimumFrequency, MaximumFrequency);

    public static long ToLogicalFrequency(long deviceFrequency, int offsetHz) =>
        Math.Clamp(deviceFrequency - offsetHz, MinimumFrequency, MaximumFrequency);
}
