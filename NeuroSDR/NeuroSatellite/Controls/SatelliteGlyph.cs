using System.Drawing;
using System.Drawing.Drawing2D;

namespace NeuroSatellite.Controls;

internal static class SatelliteGlyph
{
    public static void Draw(Graphics graphics, float centerX, float centerY, Color color, float scale)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bodyW = 5f * scale;
        var bodyH = 2.4f * scale;
        var panelW = 2.6f * scale;
        var panelH = bodyH + 0.8f * scale;
        var left = centerX - bodyW / 2f;
        var top = centerY - bodyH / 2f;

        using var fill = new SolidBrush(color);
        using var edge = new Pen(Color.FromArgb(160, 0, 0, 0), Math.Max(0.6f, 0.7f * scale));

        graphics.FillRectangle(fill, left - panelW - 0.6f * scale, centerY - panelH / 2f, panelW, panelH);
        graphics.FillRectangle(fill, left + bodyW + 0.6f * scale, centerY - panelH / 2f, panelW, panelH);
        graphics.FillRectangle(fill, left, top, bodyW, bodyH);
        graphics.DrawRectangle(edge, left - panelW - 0.6f * scale, centerY - panelH / 2f, panelW, panelH);
        graphics.DrawRectangle(edge, left + bodyW + 0.6f * scale, centerY - panelH / 2f, panelW, panelH);
        graphics.DrawRectangle(edge, left, top, bodyW, bodyH);

        var dishR = 1.1f * scale;
        graphics.FillEllipse(fill, centerX - dishR, centerY - bodyH / 2f - dishR * 1.6f, dishR * 2f, dishR * 2f);
    }

    public static RectangleF IconBounds(float centerX, float centerY, float scale)
    {
        var half = 7f * scale;
        return new RectangleF(centerX - half, centerY - half, half * 2f, half * 2f);
    }
}
