using System.ComponentModel;
using NeuroSDR.Core;

namespace NeuroSDR.Controls;

internal sealed class DigitalFrequencyControl : Control
{
    private long _frequency;
    private long _adjustFrequency;
    private bool _showAdjustFrequency;
    private int _selectedDigit = -1;
    private DateTime _lastInteraction;
    private readonly System.Windows.Forms.Timer _focusTimer = new() { Interval = 1_000 };
    private readonly System.Windows.Forms.Timer _longPressTimer = new() { Interval = 1_000 };
    private int _longPressDigit = -1;
    private static readonly byte[] Segments = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];

    public DigitalFrequencyControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(2, 11, 17);
        ForeColor = Color.FromArgb(112, 220, 248);
        SetStyle(ControlStyles.ResizeRedraw, true);
        TabStop = true;
        _focusTimer.Tick += (_, _) =>
        {
            if (_selectedDigit < 0 || DateTime.UtcNow - _lastInteraction < TimeSpan.FromMinutes(1)) return;
            _selectedDigit = -1;
            if (FindForm() is { } form) form.ActiveControl = null;
            Invalidate();
        };
        _focusTimer.Start();
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer.Stop();
            if (_longPressDigit is < 6 or > 8) return;
            ResetSelectedDigitAndLower(_longPressDigit);
            _longPressDigit = -1;
        };
    }

    public event Action<long>? FrequencyChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public long Frequency
    {
        get => _frequency;
        set
        {
            var bounded = Math.Clamp(value, 0, RadioLimits.MaximumFrequency);
            var next = bounded >= 1_000_000_000 ? bounded / 10 * 10 : bounded;
            if (_frequency == next) return;
            _frequency = next;
            Invalidate();
        }
    }

    /// <summary>
    /// When AUTO is following a peak, keep <see cref="Frequency"/> on the home/standby dial
    /// and show the live tune on a compact second line.
    /// </summary>
    public void SetAdjustFrequency(long hz)
    {
        var bounded = Math.Clamp(hz, 0, RadioLimits.MaximumFrequency);
        var next = bounded >= 1_000_000_000 ? bounded / 10 * 10 : bounded;
        if (_showAdjustFrequency && _adjustFrequency == next) return;
        _adjustFrequency = next;
        _showAdjustFrequency = true;
        Invalidate();
    }

    public void ClearAdjustFrequency()
    {
        if (!_showAdjustFrequency) return;
        _showAdjustFrequency = false;
        _adjustFrequency = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var ghzLayout = _frequency >= 1_000_000_000;
        var digits = (ghzLayout ? _frequency / 10 : _frequency).ToString("D9");
        var content = RectangleF.Inflate(ClientRectangle, -9, -6);
        if (_showAdjustFrequency)
        {
            // Main digits keep ~2/3; ADJUST line uses the bottom ~1/3 height.
            var adjustBand = Math.Max(12f, content.Height / 3f);
            content = new RectangleF(content.Left, content.Top, content.Width, content.Height - adjustBand - 2);
        }
        var gap = Math.Max(2f, content.Width * .004f);
        var dotWidth = Math.Max(5f, Width * .015f);
        var dotCount = ghzLayout ? 3 : 2;
        var digitWidth = (content.Width - gap * 10 - dotWidth * dotCount) / 9f;
        var x = content.Left + gap;
        for (var index = 0; index < digits.Length; index++)
        {
            DrawDigit(e.Graphics, digits[index] - '0', new RectangleF(x, content.Top, digitWidth, content.Height));
            if (index == _selectedDigit)
            {
                using var underline = new Pen(Color.FromArgb(205, ForeColor), 1.7f);
                e.Graphics.DrawLine(underline, x + 1, content.Bottom + 1, x + digitWidth - 1, content.Bottom + 1);
            }
            x += digitWidth + gap;
            if (IsSeparatorAfter(index, ghzLayout))
            {
                using var dot = new SolidBrush(ForeColor);
                var size = Math.Max(2.5f, content.Height * .12f);
                e.Graphics.FillEllipse(dot, x + (dotWidth - size) / 2, content.Bottom - size - 1, size, size);
                x += dotWidth;
            }
        }

        if (_showAdjustFrequency)
        {
            var mhz = _adjustFrequency >= 1_000_000_000
                ? (_adjustFrequency / 1_000_000d).ToString("0.#####")
                : (_adjustFrequency / 1_000_000d).ToString("0.######");
            var label = $"ADJUST FREQ : {mhz}";
            var fontSize = Math.Clamp(Height / 9f, 6.5f, 9f);
            using var font = new Font("Consolas", fontSize, FontStyle.Bold);
            using var brush = new SolidBrush(Color.FromArgb(255, 193, 69));
            var y = Height - fontSize - 5;
            e.Graphics.DrawString(label, font, brush, 8, y);
        }

        using var border = new Pen(Focused ? Color.FromArgb(214, 151, 59) : Color.FromArgb(72, 116, 139), Focused ? 1.6f : 1f);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _selectedDigit = DigitFromX(e.X);
        Touch();
        if (_selectedDigit < 0) return;
        if (e.Y < Height / 3) ChangeSelectedDigit(1);
        else if (e.Y >= Height * 2 / 3) ChangeSelectedDigit(-1);
        else if (_selectedDigit >= 6)
        {
            _longPressDigit = _selectedDigit;
            Capture = true;
            _longPressTimer.Stop();
            _longPressTimer.Start();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_longPressTimer.Enabled) return;
        if (DigitFromX(e.X) == _longPressDigit && e.Y >= Height / 3 && e.Y < Height * 2 / 3) return;
        CancelLongPress();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) CancelLongPress();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var digit = DigitFromX(e.X);
        if (digit < 0) return;
        Focus();
        _selectedDigit = digit;
        Touch();
        ChangeSelectedDigit(Math.Sign(e.Delta));
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Touch(); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _selectedDigit = -1; Invalidate(); }

    private void Touch() { _lastInteraction = DateTime.UtcNow; Invalidate(); }

    private void ChangeSelectedDigit(int direction)
    {
        if (_selectedDigit < 0) return;
        var step = (long)Math.Pow(10, (_frequency >= 1_000_000_000 ? 9 : 8) - _selectedDigit);
        Frequency = Math.Clamp(_frequency + direction * step, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        FrequencyChanged?.Invoke(_frequency);
    }

    private void ResetSelectedDigitAndLower(int digit)
    {
        var exponent = (_frequency >= 1_000_000_000 ? 9 : 8) - digit;
        var step = (long)Math.Pow(10, exponent + 1);
        var reset = _frequency / step * step;
        if (reset == _frequency) return;
        Frequency = reset;
        Touch();
        FrequencyChanged?.Invoke(_frequency);
    }

    private void CancelLongPress()
    {
        _longPressTimer.Stop();
        _longPressDigit = -1;
        Capture = false;
    }

    private int DigitFromX(int mouseX)
    {
        var content = RectangleF.Inflate(ClientRectangle, -9, -6);
        if (_showAdjustFrequency)
        {
            var adjustBand = Math.Max(12f, content.Height / 3f);
            content = new RectangleF(content.Left, content.Top, content.Width, content.Height - adjustBand - 2);
        }
        var gap = Math.Max(2f, content.Width * .004f);
        var dotWidth = Math.Max(5f, Width * .015f);
        var ghzLayout = _frequency >= 1_000_000_000;
        var digitWidth = (content.Width - gap * 10 - dotWidth * (ghzLayout ? 3 : 2)) / 9f;
        var x = content.Left + gap;
        for (var index = 0; index < 9; index++)
        {
            if (mouseX >= x && mouseX <= x + digitWidth) return index;
            x += digitWidth + gap;
            if (IsSeparatorAfter(index, ghzLayout)) x += dotWidth;
        }
        return -1;
    }

    internal void ChangeDigitForVerification(int digit, int direction)
    {
        _selectedDigit = Math.Clamp(digit, 0, 8);
        ChangeSelectedDigit(Math.Sign(direction));
    }

    internal void ClickForVerification(int x, int y) => OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    internal void WheelForVerification(int x, int delta) => OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, x, Height / 2, delta));
    internal int SelectedDigitForVerification => _selectedDigit;
    internal void ResetLowerDigitsForVerification(int digit) => ResetSelectedDigitAndLower(Math.Clamp(digit, 6, 8));

    private static bool IsSeparatorAfter(int index, bool ghzLayout) =>
        ghzLayout ? index is 0 or 3 or 6 : index is 2 or 5;

    private void DrawDigit(Graphics graphics, int digit, RectangleF bounds)
    {
        var thickness = Math.Max(2f, Math.Min(bounds.Width, bounds.Height) * .13f);
        var half = bounds.Height / 2f;
        RectangleF[] segments =
        [
            new(bounds.Left + thickness, bounds.Top, bounds.Width - thickness * 2, thickness),
            new(bounds.Right - thickness, bounds.Top + thickness, thickness, half - thickness * 1.5f),
            new(bounds.Right - thickness, bounds.Top + half + thickness * .5f, thickness, half - thickness * 1.5f),
            new(bounds.Left + thickness, bounds.Bottom - thickness, bounds.Width - thickness * 2, thickness),
            new(bounds.Left, bounds.Top + half + thickness * .5f, thickness, half - thickness * 1.5f),
            new(bounds.Left, bounds.Top + thickness, thickness, half - thickness * 1.5f),
            new(bounds.Left + thickness, bounds.Top + half - thickness / 2, bounds.Width - thickness * 2, thickness)
        ];
        using var on = new SolidBrush(ForeColor);
        using var off = new SolidBrush(Color.FromArgb(20, ForeColor));
        for (var index = 0; index < segments.Length; index++)
            graphics.FillRectangle((Segments[digit] & (1 << index)) != 0 ? on : off, segments[index]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _focusTimer.Dispose();
            _longPressTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SignalMeterControl : Control
{
    private float _value = -140;
    private float _displayed = -140;
    public SignalMeterControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(4, 14, 20);
        ForeColor = Color.FromArgb(215, 229, 237);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Caption { get; set; } = "LEVEL";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public float Minimum { get; set; } = -140;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public float Maximum { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, Minimum, Maximum);
            var factor = _value > _displayed ? .32f : .16f;
            _displayed += (_value - _displayed) * factor;
            if (Math.Abs(_value - _displayed) < .05f) _displayed = _value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Enabled ? BackColor : Color.FromArgb(13, 24, 30));
        var ratio = Enabled ? (_displayed - Minimum) / Math.Max(1, Maximum - Minimum) : 0;
        var bar = new Rectangle(3, Height - 8, Width - 6, 5);
        using var empty = new SolidBrush(Enabled ? Color.FromArgb(34, 51, 62) : Color.FromArgb(35, 39, 42));
        using var fill = new SolidBrush(Enabled ? Color.FromArgb(202, 126, 46) : Color.FromArgb(72, 65, 59));
        e.Graphics.FillRectangle(empty, bar);
        e.Graphics.FillRectangle(fill, bar.X, bar.Y, (int)Math.Round(bar.Width * ratio), bar.Height);
        using var font = new Font("Segoe UI Semibold", 7f);
        using var brush = new SolidBrush(Enabled ? ForeColor : Color.FromArgb(104, 111, 116));
        e.Graphics.DrawString(Enabled ? $"{Caption} {_displayed:0.0}" : $"{Caption} OUT", font, brush, 2, 1);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}

internal sealed class StatusLedControl : Control
{
    private bool _isOn;
    public StatusLedControl() { DoubleBuffered = true; BackColor = Color.FromArgb(20, 45, 58); }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string Caption { get; set; } = "SQL";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsOn { get => _isOn; set { _isOn = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var diameter = Math.Min(12, Height - 4);
        using var lamp = new SolidBrush(_isOn ? Color.FromArgb(238, 151, 48) : Color.FromArgb(60, 45, 48));
        using var border = new Pen(Color.FromArgb(132, 145, 153));
        e.Graphics.FillEllipse(lamp, 2, (Height - diameter) / 2, diameter, diameter);
        e.Graphics.DrawEllipse(border, 2, (Height - diameter) / 2, diameter, diameter);
        using var font = new Font("Segoe UI Semibold", 7f);
        using var text = new SolidBrush(Color.FromArgb(210, 225, 234));
        e.Graphics.DrawString(Caption, font, text, diameter + 6, 3);
    }
}
