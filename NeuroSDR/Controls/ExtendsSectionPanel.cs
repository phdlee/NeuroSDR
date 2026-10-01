namespace NeuroSDR.Controls;

/// <summary>Bordered EXTENDS AREA section with a small caption.</summary>
internal class ExtendsSectionPanel : Panel
{
    private readonly Label _caption = new();
    private readonly Panel _body = new();

    public ExtendsSectionPanel(string caption)
    {
        Width = 228;
        Margin = new Padding(0, 4, 0, 6);
        Padding = new Padding(8, 22, 8, 8);
        BackColor = Color.FromArgb(10, 20, 28);
        BorderStyle = BorderStyle.FixedSingle;

        _caption.AutoSize = false;
        _caption.Location = new Point(6, 3);
        _caption.Size = new Size(214, 16);
        _caption.ForeColor = Color.FromArgb(84, 171, 197);
        _caption.Font = new Font("Segoe UI Semibold", 7.5f);
        _caption.Text = caption;
        _caption.BackColor = Color.Transparent;

        _body.Location = new Point(6, 22);
        _body.Size = new Size(214, 40);
        _body.BackColor = Color.Transparent;

        Controls.Add(_caption);
        Controls.Add(_body);
    }

    public ControlCollection BodyControls => _body.Controls;

    public void SetCaption(string caption) => _caption.Text = caption;

    public void SetBodyHeight(int height)
    {
        _body.Height = Math.Max(16, height);
        Height = _body.Bottom + 8;
    }

    public void Place(Control control)
    {
        _body.Controls.Add(control);
        var bottom = 0;
        foreach (Control child in _body.Controls)
            bottom = Math.Max(bottom, child.Bottom);
        SetBodyHeight(bottom);
    }
}
