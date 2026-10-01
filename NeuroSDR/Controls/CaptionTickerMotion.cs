namespace NeuroSDR.Controls;

internal static class CaptionTickerMotion
{
    public const int TickMs = 32;
    public const float IdlePxPerSec = 60f;
    public const float MinPxPerSec = 52f;
    public const float MaxPxPerSec = 92f;
    public const float BufferSeconds = 8f;

    public const float JoinGapPx = 24f;

    public static float TrailPixels() => IdlePxPerSec * BufferSeconds;

    /// <summary>
    /// Unseen tail: join immediately. Visible last text: start at the right edge.
    /// The 5–10s blank lives only after the newest text, not between captions.
    /// </summary>
    public static float GapBeforeNewText(float lastTextEnd, float viewWidth)
    {
        if (lastTextEnd > viewWidth) return JoinGapPx;
        return Math.Max(0f, viewWidth - lastTextEnd);
    }

    public static float EstimateWidth(string line1, string line2)
    {
        var n = Math.Max(line1.Length, line2.Length);
        return Math.Max(28f, n * 7.4f);
    }

    public static float PixelsPerSecond(float waitingPx, float controlWidth, float pressure)
    {
        var width = Math.Max(120f, controlWidth);
        var backlog = Math.Clamp(waitingPx / width, 0f, 3f);
        var heat = Math.Clamp(pressure, 0f, 1f);
        var speed = IdlePxPerSec + 8f * backlog + 14f * heat;
        return Math.Clamp(speed, MinPxPerSec, MaxPxPerSec);
    }

    public static float DecayPressure(float pressure, int elapsedMs) =>
        pressure * MathF.Pow(0.82f, elapsedMs / 1_000f);

    public static float ArrivalPressure(float pressure, int addedChars) =>
        Math.Clamp(pressure * 0.8f + 0.12f + Math.Clamp(addedChars / 140f, 0.04f, 0.22f), 0f, 1f);
}
