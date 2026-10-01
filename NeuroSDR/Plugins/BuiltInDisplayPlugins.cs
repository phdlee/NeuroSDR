using NeuroSDR.Core;

namespace NeuroSDR.Plugins;

internal sealed class NeonLineSpectrumPlugin : ISpectrumRendererPlugin
{
    public string Id => "builtin.spectrum.neon-line";
    public string Name => "Neon Line";
    public string Description => "Default blue line spectrum";

    public void Render(Graphics graphics, Rectangle bounds, SpectrumRenderFrame frame)
    {
        var pointCount = Math.Min(bounds.Width, frame.Spectrum.Length);
        if (pointCount < 2) return;
        var points = BuildPoints(bounds, frame, pointCount);
        using var glow = new Pen(Color.FromArgb(55, 40, 210, 255), 5);
        using var trace = new Pen(Color.FromArgb(90, 225, 255), 1.3f);
        graphics.DrawLines(glow, points);
        graphics.DrawLines(trace, points);
    }

    internal static PointF[] BuildPoints(Rectangle bounds, SpectrumRenderFrame frame, int pointCount)
    {
        var points = new PointF[pointCount];
        for (var x = 0; x < pointCount; x++)
        {
            var screenX = (float)x * bounds.Width / (pointCount - 1);
            var index = frame.SpectrumIndexForX((int)screenX);
            points[x] = new PointF(bounds.Left + screenX + frame.VisualOffsetPixels, PowerToY(frame.Spectrum[index] + frame.LevelOffsetDb, bounds));
        }
        return points;
    }

    internal static float PowerToY(float power, Rectangle bounds) =>
        bounds.Bottom - Math.Clamp((power + 130) / 140f, 0, 1) * bounds.Height;
}

internal sealed class FilledSpectrumPlugin : ISpectrumRendererPlugin
{
    public string Id => "builtin.spectrum.filled";
    public string Name => "Filled Peaks";
    public string Description => "Alternate purple filled spectrum";

    public void Render(Graphics graphics, Rectangle bounds, SpectrumRenderFrame frame)
    {
        var pointCount = Math.Min(bounds.Width, frame.Spectrum.Length);
        if (pointCount < 2) return;
        var points = NeonLineSpectrumPlugin.BuildPoints(bounds, frame, pointCount);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddLines(points);
        path.AddLine(points[^1], new PointF(points[^1].X, bounds.Bottom));
        path.AddLine(new PointF(points[^1].X, bounds.Bottom), new PointF(points[0].X, bounds.Bottom));
        path.CloseFigure();
        using var fill = new System.Drawing.Drawing2D.LinearGradientBrush(bounds,
            Color.FromArgb(150, 118, 84, 205), Color.FromArgb(22, 28, 24, 70), 90f);
        using var trace = new Pen(Color.FromArgb(220, 185, 154, 255), 1.2f);
        graphics.FillPath(fill, path);
        graphics.DrawLines(trace, points);
    }
}

internal abstract class BitmapWaterfallPlugin : IWaterfallRendererPlugin
{
    private Bitmap? _bitmap;
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    protected abstract Color MapPower(float power);

    public void Resize(int width, int height, Color background)
    {
        width = Math.Max(2, width);
        height = Math.Max(2, height);
        if (_bitmap?.Width == width && _bitmap.Height == height) return;
        _bitmap?.Dispose();
        _bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        Clear(background);
    }

    public void Push(float[] spectrum, Func<int, int> spectrumIndexForX, int visualOffsetPixels, float levelOffsetDb, Color background)
    {
        if (_bitmap is null) return;
        BitmapRowWriter.ScrollDownAndWrite(_bitmap, x =>
        {
            var sourceX = x - visualOffsetPixels;
            return sourceX < 0 || sourceX >= _bitmap.Width
                ? background
                : MapPower(spectrum[spectrumIndexForX(sourceX)] + levelOffsetDb);
        });
    }

    public void Render(Graphics graphics, Rectangle bounds)
    {
        if (_bitmap is not null) graphics.DrawImage(_bitmap, bounds);
    }

    /// <summary>
    /// True where a column in the newest waterfall rows is a steady bright stripe.
    /// Reads pixels already stored in the bitmap. Does not touch the receiver.
    /// </summary>
    public void FillOnAirColumns(ReadOnlySpan<int> xs, Span<bool> onAir)
    {
        for (var i = 0; i < onAir.Length; i++) onAir[i] = false;
        if (_bitmap is null || xs.Length == 0 || onAir.Length < xs.Length) return;
        var rows = Math.Min(28, _bitmap.Height);
        if (rows < 8) return;
        var rect = new Rectangle(0, 0, _bitmap.Width, rows);
        var data = _bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            FillOnAirColumnsLocked(data, _bitmap.Width, rows, xs, onAir);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    private static unsafe void FillOnAirColumnsLocked(
        System.Drawing.Imaging.BitmapData data, int width, int rows, ReadOnlySpan<int> xs, Span<bool> onAir)
    {
        var stride = data.Stride;
        var origin = (byte*)data.Scan0;
        var step = Math.Max(8, width / 48);
        Span<int> floorSamples = stackalloc int[64];
        var floorCount = 0;
        for (var x = 0; x < width && floorCount < floorSamples.Length; x += step)
        {
            floorSamples[floorCount++] = ColumnScore(origin, stride, rows, x);
        }
        if (floorCount == 0) return;
        floorSamples[..floorCount].Sort();
        var floor = floorSamples[floorCount * 35 / 100];

        for (var i = 0; i < xs.Length; i++)
        {
            var x = xs[i];
            if (x < 0 || x >= width) continue;
            var score = ColumnScore(origin, stride, rows, x);
            if (x > 0) score = Math.Max(score, ColumnScore(origin, stride, rows, x - 1));
            if (x + 1 < width) score = Math.Max(score, ColumnScore(origin, stride, rows, x + 1));
            onAir[i] = score >= 36 && score >= floor + 22;
        }
    }

    private static unsafe int ColumnScore(byte* origin, int stride, int rows, int x)
    {
        var sum = 0;
        var pixel = x * 3;
        for (var y = 0; y < rows; y++)
        {
            var p = origin + y * stride + pixel;
            sum += (p[2] * 2 + p[1]) / 3;
        }
        return sum / rows;
    }

    public void Clear(Color background)
    {
        if (_bitmap is null) return;
        using var graphics = Graphics.FromImage(_bitmap);
        graphics.Clear(background);
    }

    public void Shift(int pixels, Color background)
    {
        if (_bitmap is null || pixels == 0) return;
        var shifted = new Bitmap(_bitmap.Width, _bitmap.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(shifted))
        {
            graphics.Clear(background);
            graphics.DrawImageUnscaled(_bitmap, pixels, 0);
        }
        _bitmap.Dispose();
        _bitmap = shifted;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

internal sealed class NightWaterfallPlugin : BitmapWaterfallPlugin
{
    public override string Id => "builtin.waterfall.night";
    public override string Name => "Night Heat";
    public override string Description => "Default blue, purple, and orange waterfall";
    protected override Color MapPower(float power)
    {
        var value = Math.Clamp((power + 120) / 100f, 0, 1);
        if (value < .25f) return Color.FromArgb(0, 0, (int)(35 + value * 500));
        if (value < .6f) return Color.FromArgb((int)((value - .25f) * 360), 35, 190);
        if (value < .82f) return Color.FromArgb((int)(126 + (value - .6f) * 430), (int)(45 + (value - .6f) * 430), 28);
        return Color.FromArgb(255, (int)(140 + (value - .82f) * 630), (int)((value - .82f) * 850));
    }
}

internal sealed class MonochromeWaterfallPlugin : BitmapWaterfallPlugin
{
    public override string Id => "builtin.waterfall.monochrome";
    public override string Name => "Amber Mono";
    public override string Description => "Alternate black-to-bright-amber waterfall";
    protected override Color MapPower(float power)
    {
        var value = Math.Clamp((power + 120) / 100f, 0, 1);
        var intensity = (int)Math.Round(value * 255);
        return Color.FromArgb(intensity, (int)(intensity * .55), (int)(intensity * .12));
    }
}
