namespace NeuroSDR.Controls;

internal static class RxSceneNameDialog
{
    public static string? Prompt(IWin32Window? owner, string title, string initial)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 110),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Color.FromArgb(12, 28, 38),
            ForeColor = Color.FromArgb(220, 232, 240)
        };
        var box = new TextBox
        {
            Location = new Point(12, 16),
            Width = 336,
            Text = initial,
            BackColor = Color.FromArgb(18, 32, 42),
            ForeColor = Color.FromArgb(220, 232, 240),
            BorderStyle = BorderStyle.FixedSingle
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(172, 56),
            Size = new Size(84, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 66, 83),
            ForeColor = Color.FromArgb(222, 233, 240)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(264, 56),
            Size = new Size(84, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 48, 58),
            ForeColor = Color.FromArgb(200, 210, 220)
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        cancel.FlatAppearance.BorderColor = Color.FromArgb(70, 90, 105);
        dlg.Controls.AddRange([box, ok, cancel]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return dlg.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
