using System.Reflection;
using System.Runtime.InteropServices;

namespace NeuroSDR.Controls;

internal static partial class DarkNativeTheme
{
    public static void Apply(Control control)
    {
        if (control.IsHandleCreated) Apply(control.Handle);
        control.HandleCreated += (_, _) => Apply(control.Handle);
    }

    /// <summary>Dark theme + double-buffer for ListViews that otherwise blank until clicked.</summary>
    public static void ApplyListView(ListView list)
    {
        Apply(list);
        EnableDoubleBuffer(list);
        if (!list.IsHandleCreated)
            list.HandleCreated += (_, _) => EnableDoubleBuffer(list);
    }

    public static void EnableDoubleBuffer(Control control)
    {
        try
        {
            typeof(Control)
                .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(control, true);
        }
        catch { }
    }

    private static void Apply(IntPtr handle)
    {
        try
        {
            SetWindowTheme(handle, "DarkMode_Explorer", null);
            var enabled = 1;
            DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        }
        catch { }
    }

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
