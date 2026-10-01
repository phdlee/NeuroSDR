namespace NeuroSDR.Core;

/// <summary>
/// After a waterfall pan/zoom, FFT rows still describe the previous server window.
/// Adopting those centers snaps the view back before the new viewport arrives.
/// </summary>
internal static class RemoteViewportGate
{
    public static bool IsStaleFrame(long frameCenterHz, long requestedCenterHz, int spanHz) =>
        IsStaleFrame(frameCenterHz, requestedCenterHz, spanHz, requestedSpanHz: 0);

    public static bool IsStaleFrame(long frameCenterHz, long requestedCenterHz, int frameSpanHz, int requestedSpanHz)
    {
        var centerSlop = Math.Max(5_000L, Math.Max(1, frameSpanHz) / 20L);
        if (Math.Abs(frameCenterHz - requestedCenterHz) > centerSlop) return true;
        if (requestedSpanHz <= 0) return false;
        var spanSlop = Math.Max(8_000, Math.Max(frameSpanHz, requestedSpanHz) / 5);
        return Math.Abs(frameSpanHz - requestedSpanHz) > spanSlop;
    }
}
