using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace NeuroSDR;

internal static class AppIcons
{
    private const int ButtonGlyph = 16;

    public static Icon Application { get; } = LoadApplicationIcon();
    public static Image Set { get; } = Glyph("settings", ButtonGlyph, IconMarks.Set(ButtonGlyph));
    public static Image RxStart { get; } = Glyph("play", ButtonGlyph, IconMarks.RxStart(ButtonGlyph));
    public static Image RxStop { get; } = Glyph("stop", ButtonGlyph, IconMarks.RxStop(ButtonGlyph));
    public static Image About { get; } = Glyph("radio", 96, IconMarks.App(96));
    public static Image Search { get; } = Glyph("search", ButtonGlyph, null);
    public static Image Folder { get; } = Glyph("folder", ButtonGlyph, null);
    public static Image Record { get; } = Glyph("record", ButtonGlyph, null);
    public static Image Star { get; } = Glyph("star", ButtonGlyph, null);
    public static Image Schedule { get; } = Glyph("calendar-clock", ButtonGlyph, null);
    public static Image Memory { get; } = Glyph("list", ButtonGlyph, null);

    public static readonly Padding SidebarMargin = new(0, 4, 0, 0);
    private static readonly Padding SidebarPad = new(8, 0, 8, 0);

    public static void ApplySetButton(Button button) => Place(button, Set, new Padding(4, 0, 6, 0));

    public static void ApplyRxButton(Button button, bool running)
    {
        button.Image = running ? RxStop : RxStart;
        button.Text = running ? "RX STOP" : "RX START";
        button.BackColor = running
            ? Color.FromArgb(180, 67, 74)
            : Color.FromArgb(25, 143, 177);
        Place(button, button.Image, new Padding(2, 0, 2, 0));
    }

    public static void ApplyFindButton(Button button)
    {
        if (Search is null) return;
        Place(button, Search, new Padding(4, 0, 4, 0));
    }

    public static void ApplyOpenIqButton(Button button)
    {
        if (Folder is null) return;
        StyleSidebar(button);
        Place(button, Folder, SidebarPad);
    }

    public static void ApplyFavoriteButton(Button button)
    {
        if (Star is null) return;
        button.Text = "Add";
        Place(button, Star, new Padding(6, 0, 6, 0));
    }

    public static void ApplyRecordButton(Button button, bool recording)
    {
        button.Text = recording ? "Stop IQ Recording" : "Start IQ Recording";
        StyleSidebar(button);
        Place(button, recording ? RxStop : Record, SidebarPad);
    }

    public static void ApplyAfRecordButton(Button button, bool recording)
    {
        button.Text = recording ? "REC" : "Record";
        Place(button, recording ? RxStop : Record, SidebarPad);
    }

    public static void ApplySmartRecordButton(Button button, bool active)
    {
        Place(button, active ? Record : Schedule, SidebarPad);
    }

    public static void ApplyMemoryButton(Button button)
    {
        StyleSidebar(button);
        Place(button, Memory, SidebarPad);
    }

    public static void StyleSidebar(Button button)
    {
        button.Height = 30;
        button.Margin = SidebarMargin;
        button.TextAlign = ContentAlignment.MiddleLeft;
    }

    public static string ProductVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
            return asm.GetName().Version?.ToString(3) ?? "1.0";
        }
    }

    private static void Place(Button button, Image image, Padding padding)
    {
        button.Image = image;
        button.ImageAlign = ContentAlignment.MiddleLeft;
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.Padding = padding;
        button.UseMnemonic = false;
    }

    private static Icon LoadApplicationIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "neurosdr.ico");
        return File.Exists(path) ? new Icon(path) : IconMarks.ApplicationIcon();
    }

    private static Image Glyph(string name, int size, Image? fallback)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", name + ".png");
        if (!File.Exists(path))
            return fallback ?? new Bitmap(size, size);
        using var source = Image.FromFile(path);
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        return bitmap;
    }
}
