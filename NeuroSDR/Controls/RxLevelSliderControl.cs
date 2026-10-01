using System.ComponentModel;

namespace NeuroSDR.Controls;

internal sealed class RxLevelSliderControl : Control
{
    private int _minimum, _maximum = 100, _value;
    private bool _dragging;
    private string _caption = string.Empty;

    public RxLevelSliderControl()
    {
        DoubleBuffered = true;
        Height = 27;
        BackColor = Color.FromArgb(5, 12, 18);
        ForeColor = Color.FromArgb(220, 231, 239);
        FillColor = Color.FromArgb(45, 151, 207);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    public event EventHandler? ValueChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption
    {
        get => _caption;
        set { if (_caption == value) return; _caption = value; Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ValueSuffix { get; set; } = string.Empty;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum
    {
        get => _minimum;
        set { _minimum = value; Value = _value; }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        // Do not force Maximum >= Minimum+1 here: designer often sets Maximum=0
        // before Minimum=-140; the old clamp turned Maximum into 1 and SQL max
        // then wrote Value=1 into a NumericUpDown with Maximum=0.
        set { _maximum = value; Value = _value; }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var next = ClampToRange(value);
            if (next == _value) return;
            _value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        var bar = new Rectangle(1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2));
        using var border = new Pen(Color.FromArgb(92, 126, 145));
        e.Graphics.DrawRectangle(border, bar);
        var span = Math.Max(1, High - Low);
        var ratio = (_value - Low) / (double)span;
        var fillWidth = Math.Clamp((int)Math.Round((bar.Width - 2) * ratio), 0, bar.Width - 2);
        if (fillWidth > 0)
        {
            using var fill = new SolidBrush(FillColor);
            e.Graphics.FillRectangle(fill, bar.X + 1, bar.Y + 1, fillWidth, bar.Height - 1);
        }
        using var tick = new Pen(Color.FromArgb(90, 224, 235, 245));
        for (var index = 1; index < 10; index++)
        {
            var x = bar.Left + bar.Width * index / 10;
            e.Graphics.DrawLine(tick, x, bar.Top + 2, x, bar.Top + 6);
        }
        using var font = new Font("Segoe UI Semibold", 7.5f);
        using var brush = new SolidBrush(ForeColor);
        e.Graphics.DrawString(Caption, font, brush, 6, 6);
        var valueText = $"{_value}{ValueSuffix}";
        var size = e.Graphics.MeasureString(valueText, font);
        e.Graphics.DrawString(valueText, font, brush, Width - size.Width - 5, 6);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Cursors.Hand;
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;
    }

    private void SetFromX(int x)
    {
        var ratio = Math.Clamp(x / (double)Math.Max(1, Width - 1), 0, 1);
        Value = (int)Math.Round(Low + ratio * (High - Low));
    }

    private int Low => Math.Min(_minimum, _maximum);
    private int High => Math.Max(_minimum, _maximum);
    private int ClampToRange(int value) => Math.Clamp(value, Low, High);
}
