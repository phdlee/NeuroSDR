using System.Drawing;
using System.Windows.Forms;

namespace NeuroSDR.Controls;

internal sealed class DarkTabControl : TabControl
{
    // Match AF plugin panel surfaces (decoder / control bars).
    private static readonly Color SurfaceColor = Color.FromArgb(10, 24, 33);
    private static readonly Color TabStripColor = Color.FromArgb(10, 24, 33);

    // A. Normal (inactive, in range)
    private static readonly Color TabColor = Color.FromArgb(14, 34, 44);
    private static readonly Color InactiveTextColor = Color.FromArgb(143, 168, 179);

    // B. Disable — VFO out of range (inactive)
    private static readonly Color OutOfRangeTabColor = Color.FromArgb(62, 42, 34);
    private static readonly Color OutOfRangeTextColor = Color.FromArgb(238, 151, 48);

    // C. Active tab (in range)
    private static readonly Color SelectedTabColor = Color.FromArgb(184, 118, 40);
    private static readonly Color SelectedTextColor = Color.White;

    // D. Active tab but VFO out of range (disable)
    private static readonly Color SelectedOutOfRangeTabColor = Color.FromArgb(105, 54, 42);
    private static readonly Color SelectedOutOfRangeTextColor = Color.FromArgb(255, 187, 126);

    private static readonly Color BorderColor = Color.FromArgb(40, 72, 86);

    public DarkTabControl()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        Appearance = TabAppearance.Normal;
        Multiline = false;
        BackColor = SurfaceColor;
        ForeColor = InactiveTextColor;
        DarkNativeTheme.Apply(this);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<TabPage, bool>? IsPageOutOfRange { get; set; }

    /// <summary>When set, gold/ACTIVE styling follows the running plugin instead of this control's SelectedIndex.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<TabPage, bool>? IsPluginActive { get; set; }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabPages.Count) return;
        DrawTab(e.Graphics, e.Index, GetTabRect(e.Index));
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != 0x000F || !IsHandleCreated || IsDisposed || Disposing) return; // WM_PAINT
        try
        {
            using var graphics = Graphics.FromHwnd(Handle);
            PaintDarkChrome(graphics);
        }
        catch (ArgumentException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void PaintDarkChrome(Graphics graphics)
    {
        var page = DisplayRectangle;

        using (var strip = new SolidBrush(TabStripColor))
        {
            // Cover the native light-theme tab header strip (the mismatched white band).
            if (page.Top > 0)
                graphics.FillRectangle(strip, 0, 0, Width, page.Top);
        }

        // Redraw tabs on top of the strip fill so text/background stay correct.
        for (var index = 0; index < TabCount; index++)
            DrawTab(graphics, index, GetTabRect(index));

        if (page.Width <= 0 || page.Height <= 0) return;

        using var surface = new SolidBrush(SurfaceColor);
        using var border = new Pen(BorderColor);

        graphics.FillRectangle(surface, 0, page.Top - 3, page.Left, Height - page.Top + 3);
        graphics.FillRectangle(surface, page.Right, page.Top - 3, Width - page.Right, Height - page.Top + 3);
        graphics.FillRectangle(surface, 0, page.Bottom, Width, Height - page.Bottom);
        graphics.FillRectangle(surface, page.Left, page.Top - 3, page.Width, 3);
        graphics.DrawRectangle(border, page.Left - 1, page.Top - 1, page.Width + 1, page.Height + 1);
    }

    private void DrawTab(Graphics graphics, int index, Rectangle bounds)
    {
        if (IsDisposed || Disposing || index < 0 || index >= TabPages.Count) return;

        var selected = IsPluginActive?.Invoke(TabPages[index]) ?? (SelectedIndex == index);
        var outOfRange = IsPageOutOfRange?.Invoke(TabPages[index]) == true;
        var backgroundColor = selected
            ? outOfRange ? SelectedOutOfRangeTabColor : SelectedTabColor
            : outOfRange ? OutOfRangeTabColor : TabColor;
        var textColor = selected
            ? outOfRange ? SelectedOutOfRangeTextColor : SelectedTextColor
            : outOfRange ? OutOfRangeTextColor : InactiveTextColor;

        using var background = new SolidBrush(backgroundColor);
        using var border = new Pen(BorderColor);
        graphics.FillRectangle(background, bounds);
        graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

        TabPages[index].ForeColor = textColor;

        TextRenderer.DrawText(
            graphics,
            TabPages[index].Text,
            Font,
            bounds,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}
