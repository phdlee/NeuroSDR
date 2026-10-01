namespace NeuroSDR.Controls;

/// <summary>
/// Shows per-satellite Doppler shift as colored markers on a approaching/receding scale.
/// </summary>
internal sealed class DopplerStripControl : Control
{
    private readonly List<DopplerTrack> _tracks = [];

    public DopplerStripControl()
    {
        DoubleBuffered = true;
        Height = 34;
        MinimumSize = new Size(120, 34);
        BackColor = Color.FromArgb(6, 16, 24);
    }

    public void SetTracks(IEnumerable<DopplerTrack> tracks)
    {
        _tracks.Clear();
        _tracks.AddRange(tracks);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = new Rectangle(8, 10, Math.Max(20, Width - 16), Math.Max(12, Height - 18));
        using var back = new SolidBrush(Color.FromArgb(18, 36, 48));
        g.FillRectangle(back, bounds);
        DrawGradient(g, bounds);
        using var frame = new Pen(Color.FromArgb(72, 110, 130));
        g.DrawRectangle(frame, bounds);
        using var zeroPen = new Pen(Color.FromArgb(120, 180, 200), 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        g.DrawLine(zeroPen, bounds.Left + bounds.Width / 2, bounds.Top, bounds.Left + bounds.Width / 2, bounds.Bottom);
        using var labelFont = new Font("Segoe UI", 6.5f);
        using var labelBrush = new SolidBrush(Color.FromArgb(130, 170, 190));
        g.DrawString("receding", labelFont, labelBrush, bounds.Left + 2, bounds.Top + 1);
        var recSize = g.MeasureString("approaching", labelFont);
        g.DrawString("approaching", labelFont, labelBrush, bounds.Right - recSize.Width - 2, bounds.Top + 1);
        const float maxHz = 4_000f;
        foreach (var track in _tracks)
        {
            var t = Math.Clamp((float)track.ShiftHz / maxHz, -1f, 1f);
            var x = bounds.Left + bounds.Width / 2f + t * (bounds.Width / 2f - 8f);
            var y = bounds.Top + bounds.Height / 2f;
            var color = track.ShiftHz switch
            {
                < -40 => Color.FromArgb(255, 96, 165, 250),
                > 40 => Color.FromArgb(255, 72, 220, 120),
                _ => Color.FromArgb(255, 220, 230, 235)
            };
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, x - 4, y - 4, 8, 8);
            using var nameFont = new Font("Segoe UI Semibold", 6.5f);
            var name = track.Name.Length > 10 ? track.Name[..9] + "…" : track.Name;
            var nameSize = g.MeasureString(name, nameFont);
            var nameY = Math.Min(bounds.Bottom - nameSize.Height - 1, bounds.Top + 12);
            g.DrawString(name, nameFont, brush, x - nameSize.Width / 2, nameY);
        }
    }

    private static void DrawGradient(Graphics g, Rectangle bounds)
    {
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            bounds,
            Color.FromArgb(48, 90, 180),
            Color.FromArgb(48, 180, 110),
            System.Drawing.Drawing2D.LinearGradientMode.Horizontal);
        g.FillRectangle(brush, bounds);
    }

    internal readonly record struct DopplerTrack(string Name, double ShiftHz, long NominalHz);
}
