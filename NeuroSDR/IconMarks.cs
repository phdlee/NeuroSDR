using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NeuroSDR;

/// <summary>Vector glyphs with true alpha. No plate, no rounded-rect backing.</summary>
internal static class IconMarks
{
    private static readonly Color Cyan = Color.FromArgb(255, 56, 224, 242);
    private static readonly Color Mint = Color.FromArgb(255, 92, 232, 176);
    private static readonly Color White = Color.FromArgb(255, 248, 252, 255);

    public static Bitmap App(int size) => Render(size, DrawApp);
    public static Bitmap Set(int size) => Render(size, DrawSet);
    public static Bitmap RxStart(int size) => Render(size, DrawRxStart);
    public static Bitmap RxStop(int size) => Render(size, DrawRxStop);

    public static Icon ApplicationIcon()
    {
        using var stream = BuildIco([16, 24, 32, 48, 256], App);
        return new Icon(stream);
    }

    public static string FindAssetsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var assets = Path.Combine(dir.FullName, "Assets");
            if (File.Exists(Path.Combine(dir.FullName, "NeuroSDR.csproj")) && Directory.Exists(assets))
                return assets;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Assets");
    }

    public static void Bake(string directory)
    {
        Directory.CreateDirectory(directory);
        SavePng(Path.Combine(directory, "app.png"), App(256));
        SavePng(Path.Combine(directory, "set.png"), Set(256));
        SavePng(Path.Combine(directory, "rx-start.png"), RxStart(256));
        SavePng(Path.Combine(directory, "rx-stop.png"), RxStop(256));
        using var ico = BuildIco([16, 24, 32, 48, 64, 256], App);
        File.WriteAllBytes(Path.Combine(directory, "neurosdr.ico"), ico.ToArray());
    }

    private static Bitmap Render(int size, Action<Graphics, int> paint)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        paint(g, size);
        return bmp;
    }

    private static void DrawApp(Graphics g, int size)
    {
        float S(float n) => n * size;
        var cx = S(0.50f);
        var cy = S(0.50f);

        var plusLen = S(size >= 24 ? 0.15f : 0.18f);
        var plusW = Math.Max(1.7f, S(0.10f));
        using var plusPen = new Pen(White, plusW)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(plusPen, cx - plusLen, cy, cx + plusLen, cy);
        g.DrawLine(plusPen, cx, cy - plusLen, cx, cy + plusLen);

        var rings = size >= 48 ? 3 : size >= 24 ? 2 : 1;
        var arcW = Math.Max(1.2f, S(rings >= 3 ? 0.048f : 0.058f));
        const float sweep = 68f;
        float[] starts = [270f - sweep / 2f, -sweep / 2f, 90f - sweep / 2f, 180f - sweep / 2f];

        for (var i = 1; i <= rings; i++)
        {
            var t = rings == 1 ? 1f : (float)i / rings;
            var rad = S(0.24f + t * 0.20f);
            var color = i == rings ? Mint : Color.FromArgb(235, Cyan);
            using var arcPen = new Pen(color, arcW)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            foreach (var start in starts)
                g.DrawArc(arcPen, cx - rad, cy - rad, rad * 2f, rad * 2f, start, sweep);
        }
    }

    private static void DrawSet(Graphics g, int size)
    {
        float S(float n) => n * size;
        var cx = S(0.50f);
        var cy = S(0.50f);
        var outer = S(0.42f);
        var inner = S(0.28f);
        var hole = S(0.14f);
        const int teeth = 8;
        using var path = new GraphicsPath { FillMode = FillMode.Alternate };
        var pts = new List<PointF>(teeth * 6);
        var step = Math.PI * 2 / teeth;
        var tooth = step * 0.36;
        for (var i = 0; i < teeth; i++)
        {
            var a = i * step - step / 2;
            pts.Add(Polar(cx, cy, inner, a));
            pts.Add(Polar(cx, cy, inner, a + (step - tooth) / 2));
            pts.Add(Polar(cx, cy, outer, a + (step - tooth) / 2));
            pts.Add(Polar(cx, cy, outer, a + (step + tooth) / 2));
            pts.Add(Polar(cx, cy, inner, a + (step + tooth) / 2));
            pts.Add(Polar(cx, cy, inner, a + step));
        }
        path.AddPolygon(pts.ToArray());
        path.AddEllipse(cx - hole, cy - hole, hole * 2, hole * 2);
        using var brush = new SolidBrush(White);
        g.FillPath(brush, path);
        using var accent = new Pen(Cyan, Math.Max(1.2f, size / 18f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(accent, S(0.42f), S(0.62f), S(0.42f), S(0.40f));
        g.DrawLine(accent, S(0.50f), S(0.62f), S(0.50f), S(0.34f));
        g.DrawLine(accent, S(0.58f), S(0.62f), S(0.58f), S(0.28f));
    }

    private static void DrawRxStart(Graphics g, int size)
    {
        float S(float n) => n * size;
        using var play = new SolidBrush(White);
        var tri = new[]
        {
            new PointF(S(0.18f), S(0.22f)),
            new PointF(S(0.18f), S(0.78f)),
            new PointF(S(0.58f), S(0.50f))
        };
        g.FillPolygon(play, tri);
        using var pen = new Pen(Cyan, Math.Max(1.4f, size / 16f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var ox = S(0.52f);
        var oy = S(0.50f);
        for (var i = 1; i <= 2; i++)
        {
            var rad = S(0.16f + i * 0.14f);
            g.DrawArc(pen, ox - rad * 0.15f, oy - rad, rad * 1.15f, rad * 2, -55, 110);
        }
    }

    private static void DrawRxStop(Graphics g, int size)
    {
        float S(float n) => n * size;
        using var fill = new SolidBrush(White);
        var box = new RectangleF(S(0.28f), S(0.28f), S(0.44f), S(0.44f));
        using var path = Rounded(box, Math.Max(1.5f, size / 10f));
        g.FillPath(fill, path);
        using var pen = new Pen(Cyan, Math.Max(1.3f, size / 18f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var cy = S(0.50f);
        for (var i = 1; i <= 2; i++)
        {
            var rad = S(0.10f + i * 0.08f);
            g.DrawArc(pen, S(0.30f) - rad, cy - rad, rad * 2f, rad * 2f, 115, 130);
            g.DrawArc(pen, S(0.70f) - rad, cy - rad, rad * 2f, rad * 2f, -65, 130);
        }
    }

    private static PointF Polar(float cx, float cy, float r, double a) =>
        new(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r));

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void SavePng(string path, Bitmap bmp)
    {
        using (bmp)
            bmp.Save(path, ImageFormat.Png);
    }

    private static MemoryStream BuildIco(int[] sizes, Func<int, Bitmap> factory)
    {
        var payloads = new List<byte[]>(sizes.Length);
        foreach (var size in sizes)
        {
            using var bmp = factory(size);
            payloads.Add(ToIcoBmp(bmp));
        }

        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var s = sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(payloads[i].Length);
            w.Write(offset);
            offset += payloads[i].Length;
        }
        foreach (var payload in payloads)
            w.Write(payload);
        w.Flush();
        ms.Position = 0;
        return ms;
    }

    private static byte[] ToIcoBmp(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var xorStride = w * 4;
        var maskStride = ((w + 31) / 32) * 4;
        var xor = new byte[xorStride * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
            {
                var src = data.Scan0 + y * data.Stride;
                var destRow = (h - 1 - y) * xorStride;
                System.Runtime.InteropServices.Marshal.Copy(src, xor, destRow, xorStride);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        var mask = new byte[maskStride * h];
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h * 2);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(xor.Length + mask.Length);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(xor);
        bw.Write(mask);
        return ms.ToArray();
    }
}
