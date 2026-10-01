using System.Runtime.InteropServices;

namespace NeuroSDR.Controls;

internal sealed class DarkFlowLayoutPanel : FlowLayoutPanel
{
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = SetWindowTheme(Handle, "DarkMode_Explorer", null);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);
}
