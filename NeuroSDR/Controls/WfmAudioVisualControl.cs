using NeuroSDR.Dsp;

namespace NeuroSDR.Controls;

/// <summary>
/// WFM-only AF visual: LEFT/RIGHT LED analyzers on top, matching L/R band waterfalls below.
/// </summary>
internal sealed class WfmAudioVisualControl : Control
{
    private const int BandCount = 28;
    private const int HistoryRows = 96;
    private readonly object _sync = new();
    private readonly float[] _levelsL = new float[BandCount];
    private readonly float[] _levelsR = new float[BandCount];
    private readonly float[] _peaksL = new float[BandCount];
    private readonly float[] _peaksR = new float[BandCount];
    private readonly float[] _historyL = new float[BandCount * HistoryRows];
    private readonly float[] _historyR = new float[BandCount * HistoryRows];
    private int _historyWrite;
    private int _historyCount;
    private float _maxHz = 4_000;
    /// <summary>Absolute LED spectrum height (px). When set, stays stable as the station strip shrinks.</summary>
    private int _spectrumHeightHint;

    public WfmAudioVisualControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(6, 10, 16);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void Configure(float maxFrequencyHz) =>
        _maxHz = Math.Clamp(maxFrequencyHz, 2_000, 8_000);

    /// <summary>
    /// Keep LED spectrum at a stable absolute height (typically ~1/4 of the AF graph).
    /// Extra height from a shorter station strip goes to the waterfall only.
    /// </summary>
    public void SetSpectrumHeightHint(int pixels)
    {
        var next = Math.Max(0, pixels);
        if (_spectrumHeightHint == next) return;
        _spectrumHeightHint = next;
        Invalidate();
    }

    public void PushSpectrum(float[] spectrum) =>
        PushStereoSpectrum(spectrum, spectrum);

    public void PushStereoSpectrum(float[] left, float[] right)
    {
        if (left.Length < 8 || right.Length < 8) return;
        var nyquist = AudioDemodulator.AudioSampleRate / 2d;
        var usable = Math.Min(Math.Min(left.Length, right.Length), (int)(_maxHz * left.Length / nyquist));
        usable = Math.Max(usable, BandCount);
        lock (_sync)
        {
            FillBands(left, usable, _levelsL, _peaksL);
            FillBands(right, usable, _levelsR, _peaksR);
            for (var band = 0; band < BandCount; band++)
            {
                _historyL[_historyWrite * BandCount + band] = _levelsL[band];
                _historyR[_historyWrite * BandCount + band] = _levelsR[band];
            }
            _historyWrite = (_historyWrite + 1) % HistoryRows;
            if (_historyCount < HistoryRows) _historyCount++;
        }
        Invalidate();
    }

    private static void FillBands(float[] spectrum, int usable, float[] levels, float[] peaks)
    {
        for (var band = 0; band < BandCount; band++)
        {
            var first = band * usable / BandCount;
            var last = Math.Max(first + 1, (band + 1) * usable / BandCount);
            var peak = -140f;
            for (var i = first; i < last && i < spectrum.Length; i++)
                peak = Math.Max(peak, spectrum[i]);
            var target = Math.Clamp((peak + 100f) / 100f, 0f, 1f);
            levels[band] += (target - levels[band]) * (target > levels[band] ? 0.55f : 0.12f);
            peaks[band] = Math.Max(levels[band], peaks[band] - 0.012f);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_levelsL);
            Array.Clear(_levelsR);
            Array.Clear(_peaksL);
            Array.Clear(_peaksR);
            Array.Clear(_historyL);
            Array.Clear(_historyR);
            _historyWrite = _historyCount = 0;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                   ClientRectangle,
                   Color.FromArgb(8, 14, 22),
                   Color.FromArgb(4, 8, 12),
                   90f))
            g.FillRectangle(bg, ClientRectangle);

        var pad = 8;
        var gap = 8;
        var topChrome = 18;
        var scaleH = 14;
        // Keep bottomPad tiny — a larger pad repeatedly looked like the AF spectrum
        // being clipped / missing its baseline.
        var bottomPad = 2;
        var mid = Width / 2;
        var colW = Math.Max(40, mid - pad - gap / 2);
        // Spectrum keeps prior absolute size (~1/4 of AF graph); waterfall fills the rest.
        var bodyH = Math.Max(48, Height - topChrome - scaleH - bottomPad - 2);
        var spectrumH = _spectrumHeightHint > 0
            ? Math.Clamp(_spectrumHeightHint, 40, Math.Max(40, bodyH - 36))
            : Math.Max(40, (int)(bodyH * 0.38));
        var wfH = Math.Max(28, bodyH - spectrumH);
        var leftSpec = new Rectangle(pad, topChrome, colW, spectrumH);
        var rightSpec = new Rectangle(mid + gap / 2, topChrome, colW, spectrumH);
        var scaleTop = topChrome + spectrumH + 2;
        var leftWf = new Rectangle(pad, scaleTop + scaleH, colW, wfH);
        var rightWf = new Rectangle(mid + gap / 2, scaleTop + scaleH, colW, wfH);

        using var titleFont = new Font("Segoe UI Semibold", 8.5f);
        using var labelBrush = new SolidBrush(Color.FromArgb(160, 205, 220));
        g.DrawString("LEFT", titleFont, labelBrush, leftSpec.Left, 2);
        g.DrawString("RIGHT", titleFont, labelBrush, rightSpec.Left, 2);
        DrawFrequencyScale(g, new Rectangle(pad, scaleTop, colW, scaleH), _maxHz);
        DrawFrequencyScale(g, new Rectangle(mid + gap / 2, scaleTop, colW, scaleH), _maxHz);

        float[] levelsL, levelsR, peaksL, peaksR, historyL, historyR;
        int write, count;
        lock (_sync)
        {
            levelsL = (float[])_levelsL.Clone();
            levelsR = (float[])_levelsR.Clone();
            peaksL = (float[])_peaksL.Clone();
            peaksR = (float[])_peaksR.Clone();
            historyL = (float[])_historyL.Clone();
            historyR = (float[])_historyR.Clone();
            write = _historyWrite;
            count = _historyCount;
        }

        DrawLedSpectrum(g, leftSpec, levelsL, peaksL);
        DrawLedSpectrum(g, rightSpec, levelsR, peaksR);
        DrawBandWaterfall(g, leftWf, historyL, write, count);
        DrawBandWaterfall(g, rightWf, historyR, write, count);
        DrawChrome(g);
    }

    private static void DrawFrequencyScale(Graphics g, Rectangle bounds, float maxHz)
    {
        using var font = new Font("Segoe UI Semibold", 7f);
        using var brush = new SolidBrush(Color.FromArgb(155, 205, 220));
        using var dim = new SolidBrush(Color.FromArgb(90, 130, 150));
        using var tick = new Pen(Color.FromArgb(80, 140, 165), 1f);
        using var rule = new Pen(Color.FromArgb(45, 80, 100), 1f);
        g.DrawLine(rule, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);

        var stepHz = maxHz <= 3_500 ? 500f : 1_000f;
        for (var hz = 0f; hz <= maxHz + 0.1f; hz += stepHz)
        {
            var x = bounds.Left + hz / Math.Max(1f, maxHz) * (bounds.Width - 1);
            var major = hz % 1_000f < 0.1f || Math.Abs(hz % 1_000f - 1_000f) < 0.1f || hz < 0.1f;
            g.DrawLine(tick, x, bounds.Bottom - 1, x, bounds.Bottom - (major ? 5 : 3));
            if (!major && stepHz < 1_000f) continue;
            var label = hz < 0.1f ? "0" : $"{hz / 1000f:0.#}K";
            var size = g.MeasureString(label, font);
            var lx = Math.Clamp(x - size.Width / 2f, bounds.Left, bounds.Right - size.Width);
            g.DrawString(label, font, major ? brush : dim, lx, bounds.Top);
        }
    }

    private void DrawLedSpectrum(Graphics g, Rectangle bounds, float[] levels, float[] peaks)
    {
        using var face = new SolidBrush(Color.FromArgb(10, 16, 24));
        using var rim = new Pen(Color.FromArgb(50, 90, 110), 1f);
        g.FillRectangle(face, bounds);
        g.DrawRectangle(rim, bounds);

        // Inner plot: LEDs must reach the true baseline (bottom) — no empty dead band.
        var inner = Rectangle.Inflate(bounds, -3, -2);
        if (inner.Width < 8 || inner.Height < 16) return;

        var gap = 3f;
        var barW = (inner.Width - gap * (BandCount + 1)) / BandCount;
        if (barW < 2) barW = 2;

        var segments = 16;
        var segPitch = inner.Height / (float)segments;
        var segH = Math.Max(1f, segPitch - 1f);

        for (var i = 0; i < BandCount; i++)
        {
            var x = inner.Left + gap + i * (barW + gap);
            var level = Math.Clamp(levels[i], 0, 1);
            var peak = Math.Clamp(peaks[i], 0, 1);

            for (var s = 0; s < segments; s++)
            {
                var frac = (s + 1) / (float)segments;
                // s=0 sits on the baseline (inner.Bottom).
                var sy = inner.Bottom - (s + 1) * segPitch;
                var lit = frac <= level + 0.001f;
                using var brush = new SolidBrush(lit ? LedColor(frac) : Color.FromArgb(22, 32, 42));
                g.FillRectangle(brush, x, sy + (segPitch - segH) * 0.5f, barW, segH);
            }

            var py = inner.Bottom - peak * inner.Height;
            using var peakPen = new Pen(Color.FromArgb(220, 235, 255), 1.4f);
            g.DrawLine(peakPen, x, py, x + barW, py);
        }
    }

    private void DrawBandWaterfall(Graphics g, Rectangle bounds, float[] history, int write, int count)
    {
        using var face = new SolidBrush(Color.FromArgb(6, 10, 16));
        using var rim = new Pen(Color.FromArgb(50, 90, 110), 1f);
        g.FillRectangle(face, bounds);
        g.DrawRectangle(rim, bounds);
        if (count < 2) return;

        var cellW = bounds.Width / (float)BandCount;
        var cellH = bounds.Height / (float)Math.Max(1, count);
        for (var row = 0; row < count; row++)
        {
            var histIndex = (write - 1 - row + HistoryRows * 4) % HistoryRows;
            var y = bounds.Top + row * cellH;
            var age = row / (float)count;
            for (var band = 0; band < BandCount; band++)
            {
                var v = history[histIndex * BandCount + band];
                using var brush = new SolidBrush(WaterfallColor(v, age));
                g.FillRectangle(brush, bounds.Left + band * cellW, y, cellW + 0.6f, cellH + 0.6f);
            }
        }

        using var edge = new Pen(Color.FromArgb(90, 180, 230, 255), 1.5f);
        g.DrawLine(edge, bounds.Left + 1, bounds.Top + 1, bounds.Right - 1, bounds.Top + 1);
    }

    private void DrawChrome(Graphics g)
    {
        using var accent = new Pen(Color.FromArgb(70, 150, 180), 1f);
        g.DrawLine(accent, 0, 0, Width, 0);
    }

    private static Color LedColor(float frac) => frac switch
    {
        // Cool slate → ice cyan → soft violet (no lime/mustard arcade look).
        < 0.45f => Color.FromArgb(70, 120, 155),
        < 0.70f => Color.FromArgb(95, 175, 210),
        < 0.88f => Color.FromArgb(140, 165, 220),
        _ => Color.FromArgb(190, 140, 200)
    };

    private static Color WaterfallColor(float level, float age)
    {
        var t = Math.Clamp(level, 0, 1);
        var a = Math.Clamp(age, 0, 1);
        var r = (int)(18 + t * (90 + a * 40));
        var green = (int)(28 + t * (110 - a * 30));
        var b = (int)(48 + t * (160 - a * 40));
        var alpha = (int)(255 * (0.28 + t * 0.65) * (1 - a * 0.42));
        return Color.FromArgb(Math.Clamp(alpha, 30, 255), Math.Clamp(r, 0, 255), Math.Clamp(green, 0, 255), Math.Clamp(b, 0, 255));
    }
}
