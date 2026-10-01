namespace NeuroSDR.Controls;

internal sealed class AboutNeuroSdrForm : Form
{
    private AboutNeuroSdrForm()
    {
        Text = $"About NeuroSDR {AppIcons.ProductVersion}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 22, 30);
        ForeColor = Color.FromArgb(228, 238, 246);
        Font = new Font("Segoe UI", 9.25f);
        ClientSize = new Size(440, 228);
        Icon = AppIcons.Application;

        var picture = new PictureBox
        {
            Image = AppIcons.About,
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(20, 28),
            Size = new Size(96, 96)
        };
        var title = new Label
        {
            Text = $"NeuroSDR {AppIcons.ProductVersion}",
            Font = new Font("Segoe UI Semibold", 16f),
            ForeColor = Color.FromArgb(120, 210, 230),
            Location = new Point(132, 28),
            AutoSize = true
        };
        var body = new Label
        {
            Text = $"AI-assisted software-defined radio\r\nVersion {AppIcons.ProductVersion}\r\n© {DateTime.Now.Year} NeuroSDR",
            Location = new Point(134, 68),
            Size = new Size(280, 72),
            ForeColor = Color.FromArgb(190, 210, 222)
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(88, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 66, 83),
            ForeColor = Color.White,
            Location = new Point(332, 180)
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        Controls.AddRange([picture, title, body, ok]);
        AcceptButton = ok;
        CancelButton = ok;
    }

    public static void ShowFor(Form owner)
    {
        using var dialog = new AboutNeuroSdrForm();
        dialog.ShowDialog(owner);
    }
}
