using NeuroSDR.Dsp;
using NeuroSDR.Core;

namespace NeuroSDR.Controls;

internal sealed class AudioSpectrumWaterfallControl : Control
{
    private enum FilterEdge { None, Low, High }
    private enum FskDrag { None, Center, Deviation }

    private readonly object _sync = new();
    private float[]? _spectrum;
    private Bitmap? _waterfall;
    private int _maximumFrequency = 20_000;
    private int _filterLowFrequency;
    private int _filterHighFrequency = 20_000;
    private bool _filterEnabled = true;
    private FilterEdge _draggingEdge;
    private bool _cwPassbandEnabled;
    private int _cwCenterHz = 700;
    private int _cwBandwidthHz = 70;
    private bool _draggingCwCenter;
    private bool _fskMarkersEnabled;
    private int _fskCenterHz = 1_000;
    private int _fskDeviationHz = 85;
    private FskDrag _draggingFsk;
    private readonly List<PendingFtxCallsign> _pendingFtxCallsigns = [];
    private readonly List<PendingCwCharacter> _pendingCwCharacters = [];
    private readonly List<FtxPlacement> _previousSet = [];
    private readonly List<FtxPlacement> _currentSet = [];
    private long _currentSetSlot = -1;
    private int _rowsSinceCurrentSet;
    private string _digitalOverlay = "";
    private readonly List<PendingCaptionCredit> _pendingCaptions = [];
    private bool _captionTickerEnabled;

    public AudioSpectrumWaterfallControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(4, 10, 15);
        ForeColor = Color.FromArgb(190, 213, 226);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowQsoLines { get; set; } = true;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string DisplayVfoName { get; set; } = "MAIN VFO";
    public event Action<int, int>? FilterRangeChanged;
    public event Action<int>? CwCenterChanged;
    /// <summary>Raised when the user drags FSK center or ±deviation markers on the AF spectrum.</summary>
    public event Action<int, int>? FskMarkersChanged;

    public void Configure(int maximumFrequency, int filterLowFrequency, int filterHighFrequency, bool filterEnabled)
    {
        maximumFrequency = Math.Clamp(maximumFrequency, 1_000, AudioDemodulator.AudioSampleRate / 2);
        var scaleChanged = maximumFrequency != _maximumFrequency;
        _maximumFrequency = maximumFrequency;
        _filterLowFrequency = Math.Clamp(filterLowFrequency, 0, maximumFrequency - 50);
        _filterHighFrequency = Math.Clamp(filterHighFrequency, _filterLowFrequency + 50, maximumFrequency);
        _filterEnabled = filterEnabled;
        ClampFskToScale();
        if (scaleChanged) ClearWaterfall();
        Invalidate();
    }

    public void SetCwPassband(bool enabled, int centerHz, int bandwidthHz)
    {
        _cwPassbandEnabled = enabled;
        _cwBandwidthHz = Math.Clamp(bandwidthHz, 20, 400);
        _cwCenterHz = Math.Clamp(centerHz, 80, Math.Max(80, _maximumFrequency - 80));
        Invalidate();
    }

    /// <summary>
    /// Shows FSK decode targets: center and mark/space at center±deviation (full AF height).
    /// </summary>
    public void SetFskMarkers(bool enabled, int centerHz, int deviationHz)
    {
        _fskMarkersEnabled = enabled;
        _fskCenterHz = Math.Clamp(centerHz, 50, _maximumFrequency - 50);
        _fskDeviationHz = Math.Clamp(deviationHz, 10, Math.Max(10, _maximumFrequency / 2));
        ClampFskToScale();
        Invalidate();
    }

    public void SetDigitalOverlay(string? text)
    {
        lock (_sync)
        {
            _digitalOverlay = text?.Trim() ?? "";
        }
        Invalidate();
    }

    public void PushDigitalMessage(string message)
    {
        var text = message?.Trim() ?? "";
        if (text.Length == 0) return;
        if (text.Length > 42) text = text[..42];
        lock (_sync)
        {
            // Stagger X so simultaneous messages land in different lanes (no vertical stack clash).
            var index = _pendingFtxCallsigns.Count;
            var freq = Math.Clamp(_maximumFrequency * (0.22f + index * 0.12f), 400, _maximumFrequency - 200);
            _pendingFtxCallsigns.Add(new PendingFtxCallsign(freq, text, string.Empty, false, -1));
            if (_pendingFtxCallsigns.Count > 48)
                _pendingFtxCallsigns.RemoveRange(0, _pendingFtxCallsigns.Count - 48);
        }
    }

    public void PushSpectrum(float[] spectrum)
    {
        lock (_sync)
        {
            _spectrum = spectrum;
            EnsureWaterfall();
            if (_waterfall is null) return;
            BitmapRowWriter.ScrollDownAndWrite(_waterfall, x =>
            {
                var frequency = x * (double)_maximumFrequency / Math.Max(1, _waterfall.Width - 1);
                var index = Math.Clamp((int)Math.Round(frequency * spectrum.Length / (AudioDemodulator.AudioSampleRate / 2d)), 0, spectrum.Length - 1);
                return PowerColor(spectrum[index]);
            });
            if (_rowsSinceCurrentSet < _waterfall.Height) _rowsSinceCurrentSet++;
            using var graphics = Graphics.FromImage(_waterfall);
            DrawPendingFtxCallsigns(graphics);
            DrawPendingCwCharacters(graphics);
            DrawPendingCaptionCredits(graphics);
        }
        Invalidate();
    }

    public void PushCwMessage(float frequencyHz, string character)
    {
        var text = character?.Trim() ?? "";
        if (text.Length == 0) return;
        if (text.Length > 6) text = text[..6];
        lock (_sync)
        {
            _pendingCwCharacters.Add(new PendingCwCharacter(
                Math.Clamp(frequencyHz, 0, _maximumFrequency), text));
            if (_pendingCwCharacters.Count > 96)
                _pendingCwCharacters.RemoveRange(0, _pendingCwCharacters.Count - 96);
        }
    }

    public void ClearCwMessages()
    {
        lock (_sync) _pendingCwCharacters.Clear();
    }

    public void PushFtxMessage(float frequencyHz, string message, long slot = -1)
    {
        var callsign = ExtractFtxCallsign(message);
        if (callsign.Length == 0) return;
        var isCq = IsCqMessage(message);
        var called = isCq ? string.Empty : ExtractCalledCallsign(message);
        lock (_sync)
        {
            _pendingFtxCallsigns.Add(new PendingFtxCallsign(
                Math.Clamp(frequencyHz, 0, _maximumFrequency), callsign, called, isCq, slot));
            if (_pendingFtxCallsigns.Count > 48)
                _pendingFtxCallsigns.RemoveRange(0, _pendingFtxCallsigns.Count - 48);
        }
    }

    public void ClearFtxMessages()
    {
        lock (_sync)
        {
            _pendingFtxCallsigns.Clear();
            _pendingCwCharacters.Clear();
            _previousSet.Clear();
            _currentSet.Clear();
            _currentSetSlot = -1;
            _rowsSinceCurrentSet = 0;
        }
    }

    public void ResetDisplay(string vfoName)
    {
        lock (_sync)
        {
            DisplayVfoName = string.IsNullOrWhiteSpace(vfoName) ? "MAIN VFO" : vfoName;
            _spectrum = null;
            _pendingFtxCallsigns.Clear();
            _pendingCwCharacters.Clear();
            _previousSet.Clear();
            _currentSet.Clear();
            _currentSetSlot = -1;
            _rowsSinceCurrentSet = 0;
            ClearCaptionTickerCore();
            if (_waterfall is not null)
            {
                using var graphics = Graphics.FromImage(_waterfall);
                graphics.Clear(BackColor);
            }
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        // Keep spectrumHeight and waterfall height exact complements — do NOT use
        // spectrumHeight-1 here. That 1px shortfall repeatedly looked like the AF
        // spectrum baseline being clipped at the bottom.
        var spectrumHeight = Math.Max(65, Height * 48 / 100);
        var spectrumBounds = new Rectangle(0, 0, Width, spectrumHeight);
        DrawSpectrum(e.Graphics, spectrumBounds);
        lock (_sync)
        {
            EnsureWaterfall();
            if (_waterfall is not null)
                e.Graphics.DrawImage(_waterfall, new Rectangle(0, spectrumHeight, Width, Height - spectrumHeight));
        }
        DrawFilter(e.Graphics);
        DrawFskMarkers(e.Graphics);
        DrawDigitalOverlay(e.Graphics, spectrumBounds);
    }

    private void DrawDigitalOverlay(Graphics graphics, Rectangle spectrumBounds)
    {
        string text;
        lock (_sync) text = _digitalOverlay;
        if (string.IsNullOrWhiteSpace(text) || spectrumBounds.Width < 40) return;
        using var font = new Font("Segoe UI Semibold", 8.5f);
        using var shadow = new SolidBrush(Color.FromArgb(200, 2, 8, 12));
        using var brush = new SolidBrush(Color.FromArgb(255, 214, 168, 72));
        var size = graphics.MeasureString(text, font);
        var x = spectrumBounds.Left + 8;
        var y = spectrumBounds.Top + 6;
        graphics.DrawString(text, font, shadow, x + 1, y + 1);
        graphics.DrawString(text, font, brush, x, y);
    }

    public void SetCaptionTickerEnabled(bool enabled) =>
        SetCaptionTickerEnabled(enabled, clearWhenOff: true);

    public void SetCaptionTickerEnabled(bool enabled, bool clearWhenOff)
    {
        _captionTickerEnabled = enabled;
        if (!enabled && clearWhenOff) ClearCaptionTicker();
        Invalidate();
    }

    public void ClearCaptionTicker()
    {
        lock (_sync) ClearCaptionTickerCore();
        Invalidate();
    }

    private void ClearCaptionTickerCore()
    {
        _pendingCaptions.Clear();
    }

    public void PushCaptionTicker(string line1, string? line2 = null)
    {
        line1 = line1?.Trim() ?? "";
        line2 = line2?.Trim() ?? "";
        if (line1.Length == 0 || !_captionTickerEnabled) return;
        lock (_sync)
        {
            _pendingCaptions.Add(new PendingCaptionCredit(line1, line2));
            if (_pendingCaptions.Count > 16)
                _pendingCaptions.RemoveRange(0, _pendingCaptions.Count - 16);
            EnsureWaterfall();
            if (_waterfall is not null)
            {
                using var graphics = Graphics.FromImage(_waterfall);
                DrawPendingCaptionCredits(graphics);
            }
        }
        Invalidate();
    }

    private void DrawPendingCaptionCredits(Graphics graphics)
    {
        if (!_captionTickerEnabled || _pendingCaptions.Count == 0 || _waterfall is null) return;
        var items = _pendingCaptions.ToArray();
        _pendingCaptions.Clear();
        var width = Math.Max(40, _waterfall.Width - 10);
        using var font = new Font("Segoe UI Semibold", 9f);
        using var shadow = new SolidBrush(Color.FromArgb(210, 2, 8, 12));
        using var brush1 = new SolidBrush(Color.FromArgb(255, 255, 236, 170));
        using var brush2 = new SolidBrush(Color.FromArgb(255, 170, 220, 255));
        using var back = new SolidBrush(Color.FromArgb(150, 4, 18, 26));
        var y = 2f;
        foreach (var item in items)
        {
            var original = WrapCaption(graphics, font, item.Line1, width);
            var translated = item.Line2.Length == 0
                ? []
                : WrapCaption(graphics, font, item.Line2, width);
            var lineH = font.GetHeight(graphics) + 1f;
            var blockH = (original.Length + translated.Length) * lineH + 4f;
            graphics.FillRectangle(back, 4, y, width + 2, blockH);
            var yy = y + 1f;
            foreach (var line in original)
            {
                graphics.DrawString(line, font, shadow, 6, yy + 1);
                graphics.DrawString(line, font, brush1, 5, yy);
                yy += lineH;
            }
            foreach (var line in translated)
            {
                graphics.DrawString(line, font, shadow, 6, yy + 1);
                graphics.DrawString(line, font, brush2, 5, yy);
                yy += lineH;
            }
            y = yy + 3f;
            if (y > _waterfall.Height * 0.55f) break;
        }
    }

    private static string[] WrapCaption(Graphics graphics, Font font, string text, float maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [text];
        var lines = new List<string>();
        var current = words[0];
        for (var i = 1; i < words.Length; i++)
        {
            var trial = current + " " + words[i];
            if (graphics.MeasureString(trial, font).Width <= maxWidth)
            {
                current = trial;
                continue;
            }
            lines.Add(current);
            current = words[i];
            if (lines.Count >= 3) break;
        }
        if (lines.Count < 3 && current.Length > 0) lines.Add(current);
        return [.. lines];
    }

    private void DrawPendingFtxCallsigns(Graphics graphics)
    {
        if (_pendingFtxCallsigns.Count == 0 || _waterfall is null) return;
        var messages = _pendingFtxCallsigns.ToArray();
        _pendingFtxCallsigns.Clear();

        var slot = messages.Select(item => item.Slot).FirstOrDefault(value => value >= 0, _currentSetSlot);
        if (slot != _currentSetSlot)
        {
            _previousSet.Clear();
            foreach (var placement in _currentSet)
                _previousSet.Add(placement with { Y = placement.Y + _rowsSinceCurrentSet });
            _currentSet.Clear();
            _currentSetSlot = slot;
            _rowsSinceCurrentSet = 0;
        }

        using var font = new Font("Segoe UI", 6.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var bold = new Font("Segoe UI Semibold", 6.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var shadow = new SolidBrush(Color.FromArgb(210, 2, 8, 12));
        using var textBrush = new SolidBrush(Color.FromArgb(245, 226, 174));
        using var cqBrush = new SolidBrush(Color.FromArgb(255, 236, 170, 64));
        using var cqFill = new SolidBrush(Color.FromArgb(120, 90, 42, 8));
        using var cqPen = new Pen(Color.FromArgb(230, 238, 151, 48), 1f);
        using var qsoPen = new Pen(Color.FromArgb(160, 110, 220, 255), 1f);
        const int laneHeight = 18;
        const int laneCount = 6;
        var laneRight = Enumerable.Repeat(-10_000, laneCount).ToArray();
        var newPlacements = new List<FtxPlacement>(messages.Length);
        var usedLanesThisBatch = new bool[laneCount];

        foreach (var item in messages.OrderBy(message => message.FrequencyHz))
        {
            var label = item.IsCq ? $"CQ {item.Callsign}" : item.Callsign;
            var drawFont = item.IsCq ? bold : font;
            var x = FrequencyToX((int)Math.Round(item.FrequencyHz));
            var size = graphics.MeasureString(label, drawFont, PointF.Empty, StringFormat.GenericTypographic);
            var width = Math.Max(10, (int)Math.Ceiling(size.Width));
            var height = Math.Max(8, (int)Math.Ceiling(size.Height));
            x = Math.Clamp(x - width / 2, 2, Math.Max(2, Width - width - 2));
            var lane = -1;
            for (var index = 0; index < laneCount; index++)
            {
                if (usedLanesThisBatch[index]) continue;
                if (x > laneRight[index] + 6)
                {
                    lane = index;
                    break;
                }
            }
            if (lane < 0)
            {
                lane = Enumerable.Range(0, laneCount).FirstOrDefault(index => !usedLanesThisBatch[index], -1);
                if (lane < 0) lane = Array.IndexOf(laneRight, laneRight.Min());
            }
            usedLanesThisBatch[lane] = true;
            var y = 2 + lane * laneHeight;
            var placement = new FtxPlacement(item.Callsign, item.CalledCallsign, item.IsCq, x, y, width, height);
            newPlacements.Add(placement);

            if (ShowQsoLines &&
                item.CalledCallsign.Length > 0 &&
                !IsUnresolvedCallsign(item.CalledCallsign) &&
                !IsUnresolvedCallsign(item.Callsign))
            {
                foreach (var previous in _previousSet)
                {
                    if (IsUnresolvedCallsign(previous.Callsign)) continue;
                    if (!previous.Callsign.Equals(item.CalledCallsign, StringComparison.OrdinalIgnoreCase)) continue;
                    var previousY = previous.Y + _rowsSinceCurrentSet;
                    if (previousY < 0 || previousY >= _waterfall.Height - 1) continue;
                    var fromX = previous.X + previous.Width / 2;
                    var fromY = Math.Clamp(previousY + previous.Height / 2, 0, _waterfall.Height - 1);
                    var toX = x + width / 2;
                    var toY = y + height / 2;
                    graphics.DrawLine(qsoPen, fromX, fromY, toX, toY);
                }
            }

            if (item.IsCq)
            {
                var box = new Rectangle(x - 2, y - 1, width + 4, height + 2);
                graphics.FillRectangle(cqFill, box);
                graphics.DrawRectangle(cqPen, box);
                graphics.DrawString(label, drawFont, shadow, x + 1, y + 1, StringFormat.GenericTypographic);
                graphics.DrawString(label, drawFont, cqBrush, x, y, StringFormat.GenericTypographic);
                laneRight[lane] = x + width + 4;
            }
            else
            {
                graphics.DrawString(label, drawFont, shadow, x + 1, y + 1, StringFormat.GenericTypographic);
                graphics.DrawString(label, drawFont, textBrush, x, y, StringFormat.GenericTypographic);
                laneRight[lane] = x + width;
            }
        }

        _currentSet.AddRange(newPlacements);
    }

    private void DrawPendingCwCharacters(Graphics graphics)
    {
        if (_pendingCwCharacters.Count == 0 || _waterfall is null) return;
        var messages = _pendingCwCharacters.ToArray();
        _pendingCwCharacters.Clear();

        using var font = new Font("Consolas", 5.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var shadow = new SolidBrush(Color.FromArgb(180, 2, 8, 12));
        using var textBrush = new SolidBrush(Color.FromArgb(230, 120, 230, 255));
        const int laneHeight = 11;
        const int laneCount = 10;
        var laneRight = Enumerable.Repeat(-10_000, laneCount).ToArray();

        foreach (var item in messages.OrderBy(message => message.FrequencyHz))
        {
            var x = FrequencyToX((int)Math.Round(item.FrequencyHz));
            var size = graphics.MeasureString(item.Text, font, PointF.Empty, StringFormat.GenericTypographic);
            var width = Math.Max(5, (int)Math.Ceiling(size.Width));
            var height = Math.Max(6, (int)Math.Ceiling(size.Height));
            x = Math.Clamp(x - width / 2, 2, Math.Max(2, Width - width - 2));
            var lane = -1;
            for (var index = 0; index < laneCount; index++)
            {
                if (x > laneRight[index] + 4)
                {
                    lane = index;
                    break;
                }
            }
            if (lane < 0) lane = Array.IndexOf(laneRight, laneRight.Min());
            var y = 2 + lane * laneHeight;
            graphics.DrawString(item.Text, font, shadow, x + 1, y + 1, StringFormat.GenericTypographic);
            graphics.DrawString(item.Text, font, textBrush, x, y, StringFormat.GenericTypographic);
            laneRight[lane] = x + width;
        }
    }

    internal static string ExtractFtxCallsign(string? message)
    {
        var parts = SplitMessage(message);
        if (parts.Length == 0) return string.Empty;
        if (parts[0].Equals("CQ", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("QRZ", StringComparison.OrdinalIgnoreCase))
        {
            for (var index = 1; index < parts.Length; index++)
                if (LooksLikeCallsign(parts[index])) return parts[index];
            return parts.Length > 1 ? parts[1] : string.Empty;
        }
        return parts.Length > 1 ? parts[1] : parts[0];
    }

    internal static string ExtractCalledCallsign(string? message)
    {
        var parts = SplitMessage(message);
        if (parts.Length == 0) return string.Empty;
        if (IsBroadcastToken(parts[0])) return string.Empty;
        return LooksLikeCallsign(parts[0]) ? parts[0] : string.Empty;
    }

    internal static bool IsCqMessage(string? message)
    {
        var parts = SplitMessage(message);
        return parts.Length > 0 && IsBroadcastToken(parts[0]);
    }

    private static string[] SplitMessage(string? message) => (message ?? string.Empty).Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsBroadcastToken(string token) =>
        token.Equals("CQ", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("QRZ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// FT8/FT4 unresolved hash placeholder (e.g. &lt;...&gt;) — not a real callsign for QSO linking.
    /// </summary>
    internal static bool IsUnresolvedCallsign(string? token) =>
        !string.IsNullOrEmpty(token) &&
        token.Length >= 2 &&
        token[0] == '<' &&
        token[^1] == '>';

    private static bool LooksLikeCallsign(string token)
    {
        if (token.Length is < 3 or > 10) return false;
        if (IsUnresolvedCallsign(token)) return true;
        var hasLetter = false;
        var hasDigit = false;
        foreach (var ch in token)
        {
            if (char.IsLetter(ch)) hasLetter = true;
            else if (char.IsDigit(ch)) hasDigit = true;
            else if (ch is not '/' and not '-') return false;
        }
        return hasLetter && hasDigit;
    }

    private void DrawSpectrum(Graphics graphics, Rectangle bounds)
    {
        var state = graphics.Save();
        graphics.SetClip(bounds);
        using var gridPen = new Pen(Color.FromArgb(42, 84, 106));
        using var labelBrush = new SolidBrush(Color.FromArgb(180, ForeColor));
        using var font = new Font("Segoe UI", 7f);
        for (var tick = 0; tick <= 8; tick++)
        {
            var x = bounds.Width * tick / 8f;
            graphics.DrawLine(gridPen, x, bounds.Top, x, bounds.Bottom);
            graphics.DrawString($"{_maximumFrequency * tick / 8 / 1_000d:0.###}k", font, labelBrush, x + 2, bounds.Top + 2);
        }
        for (var db = -120; db <= 0; db += 30)
        {
            var y = PowerToY(db, bounds);
            graphics.DrawLine(gridPen, bounds.Left, y, bounds.Right, y);
        }

        float[]? spectrum;
        lock (_sync) spectrum = _spectrum;
        if (spectrum is null || bounds.Width < 2)
        {
            graphics.Restore(state);
            return;
        }
        var points = new PointF[bounds.Width];
        for (var x = 0; x < bounds.Width; x++)
        {
            var frequency = x * (double)_maximumFrequency / Math.Max(1, bounds.Width - 1);
            var index = Math.Clamp((int)Math.Round(frequency * spectrum.Length / (AudioDemodulator.AudioSampleRate / 2d)), 0, spectrum.Length - 1);
            points[x] = new PointF(bounds.Left + x, PowerToY(spectrum[index], bounds));
        }
        using var fill = new System.Drawing.Drawing2D.GraphicsPath();
        fill.AddLines(points);
        // Close on the last visible pixel of the spectrum pane (Bottom-1) so the
        // fill/baseline is never clipped by the clip rect edge.
        var floorY = bounds.Bottom - 1;
        fill.AddLine(bounds.Right - 1, floorY, bounds.Left, floorY);
        fill.CloseFigure();
        using var fillBrush = new SolidBrush(Color.FromArgb(54, 77, 116, 189));
        using var trace = new Pen(Color.FromArgb(140, 205, 242), 1.1f);
        using var baseline = new Pen(Color.FromArgb(90, 100, 130, 150), 1f);
        graphics.FillPath(fillBrush, fill);
        graphics.DrawLines(trace, points);
        graphics.DrawLine(baseline, bounds.Left, floorY, bounds.Right, floorY);
        graphics.Restore(state);
    }

    private void DrawFilter(Graphics graphics)
    {
        using var font = new Font("Segoe UI Semibold", 7.5f);
        if (_cwPassbandEnabled)
        {
            DrawCwPassband(graphics, font);
            return;
        }
        if (!_filterEnabled)
        {
            using var offBrush = new SolidBrush(Color.FromArgb(116, 126, 139));
            graphics.DrawString($"{DisplayVfoName}  ·  AF FILTER OFF", font, offBrush, 6, 36);
            return;
        }

        var lowX = FrequencyToX(_filterLowFrequency);
        var highX = FrequencyToX(_filterHighFrequency);
        var edgeColor = Color.FromArgb(255, 193, 56);
        using var region = new SolidBrush(Color.FromArgb(30, 68, 139, 222));
        using var edgePen = new Pen(edgeColor, 1.7f);
        graphics.FillRectangle(region, lowX, 0, Math.Max(1, highX - lowX), Height);
        graphics.DrawLine(edgePen, lowX, 0, lowX, Height);
        graphics.DrawLine(edgePen, highX, 0, highX, Height);
        using var brush = new SolidBrush(edgeColor);
        graphics.DrawString(
            $"{DisplayVfoName}  ·  AF FILTER  {_filterLowFrequency:N0} - {_filterHighFrequency:N0} Hz",
            font, brush, 6, 36);
    }

    private void DrawCwPassband(Graphics graphics, Font font)
    {
        var half = Math.Max(10, _cwBandwidthHz / 2);
        var low = Math.Max(0, _cwCenterHz - half);
        var high = Math.Min(_maximumFrequency, _cwCenterHz + half);
        var lowX = FrequencyToX(low);
        var highX = FrequencyToX(high);
        var centerX = FrequencyToX(_cwCenterHz);
        using var fill = new SolidBrush(Color.FromArgb(32, 0, 210, 255));
        using var edgePen = new Pen(Color.FromArgb(245, 255, 193, 56), 1.5f);
        using var centerPen = new Pen(Color.FromArgb(245, 255, 193, 56), 1.5f);
        graphics.FillRectangle(fill, lowX, 0, Math.Max(1, highX - lowX), Height);
        graphics.DrawLine(edgePen, lowX, 0, lowX, Height);
        graphics.DrawLine(edgePen, highX, 0, highX, Height);
        graphics.DrawLine(centerPen, centerX, 0, centerX, Height);
        using var brush = new SolidBrush(Color.FromArgb(255, 215, 70));
        graphics.DrawString(
            $"{DisplayVfoName}  ·  CW FILTER  {_cwCenterHz:N0} Hz  ·  BW {_cwBandwidthHz} Hz",
            font, brush, 6, 36);
    }

    private void DrawFskMarkers(Graphics graphics)
    {
        if (!_fskMarkersEnabled || Width < 4) return;
        var lowHz = Math.Clamp(_fskCenterHz - _fskDeviationHz, 0, _maximumFrequency);
        var highHz = Math.Clamp(_fskCenterHz + _fskDeviationHz, 0, _maximumFrequency);
        var lowX = FrequencyToX(lowHz);
        var centerX = FrequencyToX(_fskCenterHz);
        var highX = FrequencyToX(highHz);

        using var band = new SolidBrush(Color.FromArgb(36, 48, 190, 140));
        graphics.FillRectangle(band, Math.Min(lowX, highX), 0, Math.Max(2, Math.Abs(highX - lowX)), Height);

        using var tonePen = new Pen(Color.FromArgb(230, 72, 220, 170), 1.6f);
        using var centerPen = new Pen(Color.FromArgb(245, 255, 210, 90), 1.8f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        graphics.DrawLine(tonePen, lowX, 0, lowX, Height);
        graphics.DrawLine(tonePen, highX, 0, highX, Height);
        graphics.DrawLine(centerPen, centerX, 0, centerX, Height);

        using var font = new Font("Segoe UI Semibold", 7.2f);
        using var toneBrush = new SolidBrush(Color.FromArgb(230, 140, 235, 200));
        using var centerBrush = new SolidBrush(Color.FromArgb(245, 255, 220, 120));
        using var shadow = new SolidBrush(Color.FromArgb(180, 4, 10, 14));
        void Label(string text, int x, int y, Brush brush)
        {
            graphics.DrawString(text, font, shadow, x + 1, y + 1);
            graphics.DrawString(text, font, brush, x, y);
        }
        Label($"LO {lowHz}", Math.Clamp(lowX + 3, 2, Width - 70), 34, toneBrush);
        Label($"CTR {_fskCenterHz}", Math.Clamp(centerX + 3, 2, Width - 80), 48, centerBrush);
        Label($"HI {highHz}", Math.Clamp(highX + 3, 2, Width - 70), 62, toneBrush);
        Label($"FSK  ±{_fskDeviationHz} Hz", 6, 34, centerBrush);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        if (_fskMarkersEnabled && TryHitFsk(e.X, out var fskDrag))
        {
            _draggingFsk = fskDrag;
            _draggingEdge = FilterEdge.None;
            Capture = true;
            SetFskFromX(e.X);
            return;
        }
        _draggingFsk = FskDrag.None;
        if (_cwPassbandEnabled)
        {
            _draggingCwCenter = true;
            _draggingEdge = FilterEdge.None;
            Capture = true;
            SetCwCenterFromX(e.X);
            return;
        }
        if (!_filterEnabled) return;
        var lowDistance = Math.Abs(e.X - FrequencyToX(_filterLowFrequency));
        var highDistance = Math.Abs(e.X - FrequencyToX(_filterHighFrequency));
        _draggingEdge = lowDistance <= highDistance ? FilterEdge.Low : FilterEdge.High;
        Capture = true;
        SetFilterFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingFsk != FskDrag.None)
        {
            Cursor = Cursors.SizeWE;
            SetFskFromX(e.X);
            return;
        }
        if (_draggingCwCenter)
        {
            Cursor = Cursors.SizeWE;
            SetCwCenterFromX(e.X);
            return;
        }
        if (_draggingEdge != FilterEdge.None)
        {
            Cursor = Cursors.SizeWE;
            SetFilterFromX(e.X);
            return;
        }
        Cursor = _fskMarkersEnabled && TryHitFsk(e.X, out _)
            ? Cursors.SizeWE
            : _cwPassbandEnabled || _filterEnabled ? Cursors.SizeWE : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _draggingEdge = FilterEdge.None;
        _draggingFsk = FskDrag.None;
        _draggingCwCenter = false;
        Capture = false;
    }

    private bool TryHitFsk(int x, out FskDrag drag)
    {
        drag = FskDrag.None;
        const int hit = 7;
        var lowX = FrequencyToX(_fskCenterHz - _fskDeviationHz);
        var centerX = FrequencyToX(_fskCenterHz);
        var highX = FrequencyToX(_fskCenterHz + _fskDeviationHz);
        var dCenter = Math.Abs(x - centerX);
        var dLow = Math.Abs(x - lowX);
        var dHigh = Math.Abs(x - highX);
        if (dCenter <= hit && dCenter <= dLow && dCenter <= dHigh)
        {
            drag = FskDrag.Center;
            return true;
        }
        if (dLow <= hit || dHigh <= hit)
        {
            drag = FskDrag.Deviation;
            return true;
        }
        return false;
    }

    private void SetFskFromX(int x)
    {
        var frequency = (int)Math.Round(Math.Clamp(x, 0, Width) * (double)_maximumFrequency / Math.Max(1, Width));
        if (_draggingFsk == FskDrag.Center)
        {
            _fskCenterHz = Math.Clamp(frequency, _fskDeviationHz + 20, _maximumFrequency - _fskDeviationHz - 20);
        }
        else if (_draggingFsk == FskDrag.Deviation)
        {
            _fskDeviationHz = Math.Clamp(Math.Abs(frequency - _fskCenterHz), 10, Math.Max(10, _maximumFrequency / 2));
            ClampFskToScale();
        }
        else return;
        FskMarkersChanged?.Invoke(_fskCenterHz, _fskDeviationHz);
        Invalidate();
    }

    private void ClampFskToScale()
    {
        _fskCenterHz = Math.Clamp(_fskCenterHz, 50, Math.Max(50, _maximumFrequency - 50));
        var maxDev = Math.Max(10, Math.Min(_fskCenterHz - 20, _maximumFrequency - _fskCenterHz - 20));
        _fskDeviationHz = Math.Clamp(_fskDeviationHz, 10, maxDev);
    }

    private void SetCwCenterFromX(int x)
    {
        var half = Math.Max(10, _cwBandwidthHz / 2);
        var frequency = XToFrequency(x);
        _cwCenterHz = Math.Clamp(frequency, half + 20, Math.Max(half + 20, _maximumFrequency - half - 20));
        CwCenterChanged?.Invoke(_cwCenterHz);
        Invalidate();
    }

    private int XToFrequency(int x) =>
        (int)Math.Round(Math.Clamp(x, 0, Width) * (double)_maximumFrequency / Math.Max(1, Width));

    private void SetFilterFromX(int x)
    {
        var frequency = (int)Math.Round(Math.Clamp(x, 0, Width) * (double)_maximumFrequency / Math.Max(1, Width));
        if (_draggingEdge == FilterEdge.Low)
            _filterLowFrequency = Math.Clamp(frequency, 0, _filterHighFrequency - 50);
        else if (_draggingEdge == FilterEdge.High)
            _filterHighFrequency = Math.Clamp(frequency, _filterLowFrequency + 50, _maximumFrequency);
        else
            return;
        FilterRangeChanged?.Invoke(_filterLowFrequency, _filterHighFrequency);
        Invalidate();
    }

    internal void SetFilterFromXForVerification(bool lowEdge, int x)
    {
        _draggingEdge = lowEdge ? FilterEdge.Low : FilterEdge.High;
        SetFilterFromX(x);
        _draggingEdge = FilterEdge.None;
    }
    internal int PendingFtxMessageCountForVerification { get { lock (_sync) return _pendingFtxCallsigns.Count; } }

    private int FrequencyToX(int frequency) => Math.Clamp((int)Math.Round(frequency * Width / (double)Math.Max(1, _maximumFrequency)), 0, Width);
    /// <summary>
    /// Map dBFS to Y inside bounds. Floor (-130) lands on Bottom-1 so the trace
    /// is never clipped by the exclusive bottom edge of the clip region.
    /// </summary>
    private static float PowerToY(float power, Rectangle bounds)
    {
        var usable = Math.Max(1, bounds.Height - 1);
        return bounds.Top + (1f - Math.Clamp((power + 130) / 140f, 0f, 1f)) * usable;
    }

    private void EnsureWaterfall()
    {
        var spectrumHeight = Math.Max(65, Height * 48 / 100);
        var height = Math.Max(2, Height - spectrumHeight);
        if (Width < 2 || (_waterfall?.Width == Width && _waterfall.Height == height)) return;
        _waterfall?.Dispose();
        _waterfall = new Bitmap(Math.Max(2, Width), height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(_waterfall);
        graphics.Clear(BackColor);
    }

    private void ClearWaterfall()
    {
        lock (_sync)
        {
            if (_waterfall is null) return;
            using var graphics = Graphics.FromImage(_waterfall);
            graphics.Clear(BackColor);
        }
    }

    private static Color PowerColor(float power)
    {
        var value = Math.Clamp((power + 120) / 100f, 0, 1);
        if (value < .3f) return Color.FromArgb(0, 0, (int)(30 + value * 420));
        if (value < .65f) return Color.FromArgb((int)((value - .3f) * 350), 35, 190);
        return Color.FromArgb((int)(122 + (value - .65f) * 380), (int)(45 + (value - .65f) * 410), 28);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _waterfall?.Dispose();
        base.Dispose(disposing);
    }

    private sealed record PendingCaptionCredit(string Line1, string Line2);
    private sealed record PendingFtxCallsign(float FrequencyHz, string Callsign, string CalledCallsign, bool IsCq, long Slot);
    private sealed record PendingCwCharacter(float FrequencyHz, string Text);
    private sealed record FtxPlacement(string Callsign, string CalledCallsign, bool IsCq, int X, int Y, int Width, int Height);
}
