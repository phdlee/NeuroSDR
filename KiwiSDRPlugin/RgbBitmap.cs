using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ENSdr.Plugins;

namespace KiwiSDRPlugin;

internal static class RgbBitmap
{
    public static bool TryCreate(AfPluginResult result, out Bitmap bitmap)
    {
        bitmap = null!;
        if (result.BinaryData is not { Length: > 0 } rgb || result.Fields is null ||
            !int.TryParse(result.Fields.GetValueOrDefault("width"), out var width) ||
            !int.TryParse(result.Fields.GetValueOrDefault("height"), out var height) ||
            !int.TryParse(result.Fields.GetValueOrDefault("stride"), out var sourceStride) ||
            width <= 0 || height <= 0 || sourceStride < width * 3 || rgb.Length < sourceStride * height) return false;
        var created = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = created.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < height; y++)
            {
                Array.Clear(row);
                for (var x = 0; x < width; x++)
                {
                    var source = y * sourceStride + x * 3;
                    var target = x * 3;
                    row[target] = rgb[source + 2];
                    row[target + 1] = rgb[source + 1];
                    row[target + 2] = rgb[source];
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        catch
        {
            created.UnlockBits(data);
            created.Dispose();
            return false;
        }
        created.UnlockBits(data);
        bitmap = created;
        return true;
    }
}
