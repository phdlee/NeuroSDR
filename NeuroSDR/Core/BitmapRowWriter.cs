using System.Drawing.Imaging;

namespace NeuroSDR.Core;

internal static unsafe class BitmapRowWriter
{
    public static void ScrollDownAndWrite(Bitmap bitmap, Func<int, Color> colorAt)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var rowBytes = Math.Abs(stride);
            var origin = (byte*)data.Scan0;
            for (var y = bitmap.Height - 1; y > 0; y--)
            {
                var source = origin + (y - 1) * stride;
                var destination = origin + y * stride;
                Buffer.MemoryCopy(source, destination, rowBytes, rowBytes);
            }
            var top = origin;
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = colorAt(x);
                top[x * 3] = color.B;
                top[x * 3 + 1] = color.G;
                top[x * 3 + 2] = color.R;
            }
        }
        finally { bitmap.UnlockBits(data); }
    }
}
