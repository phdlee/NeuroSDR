using NeuroSDR.Core;
using NeuroSDR.Plugins;

namespace NeuroSDR.Controls;

public class SpectrumWaterfallControl : Control
{
    public event Action<long>? SubVfoRequested;
    private enum DragMode { None, PendingClick, Broadcast, Tune, Center, Pan, SpectrumLevel, WaterfallLevel }

    private const int DepthRows = 64;
    // History ridges: lower than live front spectrum (classic-res front is drawn separately).
    private const int DepthBins = 256;
    // ~0.35s/row at ~7–15 FPS RF → ~20–25s window (2× previous stride-5 pace).
    private const int DepthPushStride = 2;
    private readonly object _sync = new();
    private float[]? _spectrum;
    private readonly float[] _depthHistory = new float[DepthRows * DepthBins];
    private readonly float[] _depthPaintCopy = new float[DepthRows * DepthBins];
    private readonly long[] _depthTickMs = new long[DepthRows];
    private int _depthWrite;
    private int _depthCount;
    private int _depthPushCounter;
    private RfDisplayMode _displayMode = RfDisplayMode.SpectrumWaterfall;
    private ISpectrumRendererPlugin _spectrumRenderer = new NeonLineSpectrumPlugin();
    private IWaterfallRendererPlugin _waterfallRenderer = new NightWaterfallPlugin();
    private long _centerFrequency = 100_000_000;
    private long _tunedFrequency = 100_000_000;
    private long _viewCenterFrequency = 100_000_000;
    private int _sampleRate = 2_048_000;
    private int _viewBandwidth = 2_048_000;
    private int _maximumViewBandwidth = 2_048_000;
    private int _filterBandwidth = 12_000;
    private RadioMode _mode = RadioMode.AM;
    private int _cwPitchHz = 700;
    private int _dragStartX = -1;
    private long _dragStartFrequency, _dragStartViewCenter, _dragStartCenter;
    private DragMode _dragMode;
    private int _dragVisualOffsetPixels;
    private float _spectrumLevelOffsetDb, _waterfallLevelOffsetDb;
    private (long Frequency, string Name)[] _subVfoMarkers = [];
    private (long Frequency, string Name)[] _wfmStationMarkers = [];
    private (long Frequency, string Name)[] _broadcastMarkers = [];
    private long _broadcastPresenceAt;
    private long _broadcastPresenceViewCenter;
    private int _broadcastPresenceViewSpan;
    private readonly Dictionary<long, bool> _broadcastPresence = new();
    private AutoTuneOverlay[] _autoTuneOverlays = [];
    private readonly List<(Rectangle Bounds, long Frequency, string Name)> _wfmStationHits = [];
    private readonly List<(Rectangle Bounds, long Frequency, string Name)> _broadcastHits = [];
    /// <summary>FT8/FT4 labels that scroll with classic waterfall (not used on 3D).</summary>
    private readonly List<FtxScrollStamp> _ftxStamps = [];
    private bool _showFtxOnMain;
    private const int FtxLaneCount = 6;
    private const int FtxLaneHeight = 13;
    private int _hoveredSubMarkerIndex = -1;
    private int _hoveredAutoTuneIndex = -1;
    private Rectangle _subRecallButtonBounds;
    private long _hoveredSubFrequency;
    private long _pendingBroadcastHz;
    private string _pendingBroadcastName = "";
    private Point _subRecallAnchor;
    private readonly System.Windows.Forms.Timer _subRecallHoldTimer = new() { Interval = 3_000 };
    private const int SubRecallHoldMs = 3_000;
    private const int SubRecallHitPixels = 8;
    private const int AutoTuneHitPixels = 14;
    private Rectangle _overlayChromeReserve;

    public readonly record struct AutoTuneOverlay(
        long MinFrequency, long MaxFrequency, float TriggerLevelDb, bool IsMain, string Name, long AnchorFrequency);

    public event Action<long>? TunedFrequencyChanged;
    public event Action<long, int>? ViewChanged;
    public event Action<long>? CenterFrequencyChanged;
    public event Action? CenterDragStarted;
    public event Action<long>? CenterDragCompleted;
    public event Action<float, float>? DisplayLevelsChanged;
    public event Action<bool, bool>? AutoLevelsChanged;
    private bool _spectrumAutoLevel;
    private bool _waterfallAutoLevel;
    private long _spectrumAutoTick;
    private long _waterfallAutoTick;
    public event Action<long, string>? WfmStationClicked;
    public event Action<long, string>? BroadcastStationClicked;

    public void SetOverlayChromeReserve(Rectangle bounds)
    {
        if (_overlayChromeReserve == bounds) return;
        _overlayChromeReserve = bounds;
        Invalidate();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public RfDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (_displayMode == value) return;
            _displayMode = value;
            Invalidate();
        }
    }

    public SpectrumWaterfallControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(5, 9, 15);
        ForeColor = Color.FromArgb(205, 215, 228);
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        _subRecallHoldTimer.Tick += (_, _) => ClearSubRecallHover();
    }

    internal void SetRenderPlugins(ISpectrumRendererPlugin spectrumRenderer, IWaterfallRendererPlugin waterfallRenderer)
    {
        ArgumentNullException.ThrowIfNull(spectrumRenderer);
        ArgumentNullException.ThrowIfNull(waterfallRenderer);
        lock (_sync)
        {
            var previous = _waterfallRenderer;
            _spectrumRenderer = spectrumRenderer;
            _waterfallRenderer = waterfallRenderer;
            EnsureWaterfall();
            _waterfallRenderer.Clear(BackColor);
            previous.Dispose();
        }
        Invalidate();
    }

    public void SetDisplayLevels(float spectrumOffsetDb, float waterfallOffsetDb)
    {
        _spectrumLevelOffsetDb = Math.Clamp(spectrumOffsetDb, -40, 40);
        _waterfallLevelOffsetDb = Math.Clamp(waterfallOffsetDb, -40, 40);
        Invalidate();
    }

    public void ReadDisplayLevels(out float spectrumOffsetDb, out float waterfallOffsetDb, out bool spectrumAuto, out bool waterfallAuto)
    {
        spectrumOffsetDb = _spectrumLevelOffsetDb;
        waterfallOffsetDb = _waterfallLevelOffsetDb;
        spectrumAuto = _spectrumAutoLevel;
        waterfallAuto = _waterfallAutoLevel;
    }

    public void SetAutoLevels(bool spectrum, bool waterfall)
    {
        if (_spectrumAutoLevel == spectrum && _waterfallAutoLevel == waterfall) return;
        _spectrumAutoLevel = spectrum;
        _waterfallAutoLevel = waterfall;
        Invalidate();
    }

    public void SetSubVfoMarkers(IEnumerable<(long Frequency, string Name)> markers)
    {
        var next = markers.ToArray();
        var current = Volatile.Read(ref _subVfoMarkers);
        if (current.SequenceEqual(next)) return;
        Interlocked.Exchange(ref _subVfoMarkers, next);
        Invalidate();
    }

    public void SetWfmStationMarkers(IEnumerable<(long Frequency, string Name)> markers)
    {
        var next = markers.ToArray();
        var current = Volatile.Read(ref _wfmStationMarkers);
        if (current.SequenceEqual(next)) return;
        Interlocked.Exchange(ref _wfmStationMarkers, next);
        Invalidate();
    }

    public void SetBroadcastMarkers(IEnumerable<(long Frequency, string Name)> markers)
    {
        var next = markers.ToArray();
        var current = Volatile.Read(ref _broadcastMarkers);
        if (current.SequenceEqual(next)) return;
        Interlocked.Exchange(ref _broadcastMarkers, next);
        Invalidate();
    }

    public void SetAutoTuneOverlays(IEnumerable<AutoTuneOverlay> overlays)
    {
        var next = overlays.ToArray();
        var current = Volatile.Read(ref _autoTuneOverlays);
        if (current.SequenceEqual(next)) return;
        Interlocked.Exchange(ref _autoTuneOverlays, next);
        Invalidate();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowFtxOnMain
    {
        get => _showFtxOnMain;
        set { if (_showFtxOnMain == value) return; _showFtxOnMain = value; Invalidate(); }
    }

    public void PushFtxOverlay(long rfHz, string message, long slot = -1)
    {
        if (!_showFtxOnMain || string.IsNullOrWhiteSpace(message)) return;
        if (_displayMode == RfDisplayMode.Perspective3D) return; // classic WF only
        var text = message.Length > 18 ? message[..18] : message;
        lock (_sync)
        {
            var lane = PickFtxLane(rfHz);
            _ftxStamps.Add(new FtxScrollStamp(rfHz, text, slot, RowAge: 0, Lane: lane));
            if (_ftxStamps.Count > 64)
                _ftxStamps.RemoveRange(0, _ftxStamps.Count - 64);
        }
        Invalidate();
    }

    public void ClearFtxOverlays()
    {
        lock (_sync) _ftxStamps.Clear();
        Invalidate();
    }

    private int PickFtxLane(long rfHz)
    {
        var x = FrequencyToX(rfHz);
        var occupied = new bool[FtxLaneCount];
        foreach (var stamp in _ftxStamps)
        {
            if (stamp.RowAge > FtxLaneHeight * 2) continue; // only recent collisions
            if (Math.Abs(FrequencyToX(stamp.RfHz) - x) > 70) continue;
            occupied[Math.Clamp(stamp.Lane, 0, FtxLaneCount - 1)] = true;
        }
        for (var i = 0; i < FtxLaneCount; i++)
            if (!occupied[i]) return i;
        return _ftxStamps.Count % FtxLaneCount;
    }

    private readonly record struct FtxScrollStamp(long RfHz, string Text, long Slot, int RowAge, int Lane);

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Focus();
    }

    public void Configure(long centerFrequency, long tunedFrequency, int sampleRate, int filterBandwidth, RadioMode mode, int cwPitchHz,
        long viewCenterFrequency, int viewBandwidth, int maximumViewBandwidth = 0)
    {
        var scaleChanged = _viewBandwidth != viewBandwidth || _sampleRate != sampleRate;
        _centerFrequency = centerFrequency;
        _tunedFrequency = tunedFrequency;
        _sampleRate = sampleRate;
        _maximumViewBandwidth = Math.Max(sampleRate, maximumViewBandwidth);
        _filterBandwidth = filterBandwidth;
        _mode = mode;
        _cwPitchHz = cwPitchHz;
        _viewBandwidth = Math.Clamp(viewBandwidth, MinimumViewBandwidth(), _maximumViewBandwidth);
        _viewCenterFrequency = ClampViewCenter(viewCenterFrequency, _viewBandwidth);
        if (scaleChanged) ClearWaterfall();
        Invalidate();
    }

    public void PushSpectrum(float[] spectrum)
    {
        lock (_sync)
        {
            _spectrum = spectrum;
            // Classic waterfall: every frame. 3D depth history: throttled (~20s window, less CPU).
            if (_displayMode == RfDisplayMode.Perspective3D)
            {
                if (++_depthPushCounter >= DepthPushStride)
                {
                    _depthPushCounter = 0;
                    PushDepthHistory(spectrum);
                }
            }
            else
            {
                _depthPushCounter = 0;
                EnsureWaterfall();
                // Historical rows are physically shifted by SetCenterFromDrag. The new
                // FFT row already belongs to the current hardware center and must not be
                // shifted a second time.
                _waterfallRenderer.Push(spectrum, x => SpectrumIndexForX(x, spectrum.Length, Math.Max(1, Width)), 0, _waterfallLevelOffsetDb, BackColor);
                AgeFtxStamps();
            }
        }
        MaybeAutoLevel(spectrum);
        Invalidate();
    }

    private void MaybeAutoLevel(float[] spectrum)
    {
        if (_dragMode is DragMode.SpectrumLevel or DragMode.WaterfallLevel) return;
        if (!_spectrumAutoLevel && !_waterfallAutoLevel) return;
        var now = Environment.TickCount64;
        var changed = false;
        if (_spectrumAutoLevel && now - _spectrumAutoTick >= 2_500 &&
            TryLevelStep(spectrum, _spectrumLevelOffsetDb, waterfall: false, out var spectrumStep))
        {
            _spectrumLevelOffsetDb = Math.Clamp(_spectrumLevelOffsetDb + spectrumStep, -40, 40);
            _spectrumAutoTick = now;
            changed = true;
        }
        if (_waterfallAutoLevel && now - _waterfallAutoTick >= 2_500 &&
            TryLevelStep(spectrum, _waterfallLevelOffsetDb, waterfall: true, out var waterfallStep))
        {
            _waterfallLevelOffsetDb = Math.Clamp(_waterfallLevelOffsetDb + waterfallStep, -40, 40);
            _waterfallAutoTick = now;
            changed = true;
        }
        if (changed)
            DisplayLevelsChanged?.Invoke(_spectrumLevelOffsetDb, _waterfallLevelOffsetDb);
    }

    private static bool TryLevelStep(float[] spectrum, float offsetDb, bool waterfall, out float step)
    {
        step = 0;
        if (spectrum.Length < 16) return false;
        var stride = Math.Max(1, spectrum.Length / 48);
        Span<float> samples = stackalloc float[48];
        var count = 0;
        for (var i = 0; i < spectrum.Length && count < samples.Length; i += stride)
            samples[count++] = spectrum[i];
        if (count < 8) return false;
        samples[..count].Sort();
        var mid = samples[count / 2];
        var high = samples[count * 9 / 10];
        if (!waterfall)
        {
            if (high + offsetDb < -100f) step = 2f;
            else if (high + offsetDb > 8f || mid + offsetDb > -8f) step = -2f;
        }
        else if (high + offsetDb < -105f) step = 2f;
        else if (mid + offsetDb > -28f) step = -2f;
        return step != 0f;
    }

    private void AgeFtxStamps()
    {
        var waterfallH = Math.Max(2, Height - Math.Max(120, Height * 44 / 100));
        for (var i = _ftxStamps.Count - 1; i >= 0; i--)
        {
            var next = _ftxStamps[i] with { RowAge = _ftxStamps[i].RowAge + 1 };
            if (next.RowAge > waterfallH + FtxLaneCount * FtxLaneHeight + 24)
                _ftxStamps.RemoveAt(i);
            else
                _ftxStamps[i] = next;
        }
    }

    private void PushDepthHistory(float[] spectrum)
    {
        var width = Math.Max(1, Width);
        for (var bin = 0; bin < DepthBins; bin++)
        {
            var x = bin * (width - 1) / Math.Max(1, DepthBins - 1);
            var index = SpectrumIndexForX(x, spectrum.Length, width);
            _depthHistory[_depthWrite * DepthBins + bin] = spectrum[index] + _waterfallLevelOffsetDb;
        }
        _depthTickMs[_depthWrite] = Environment.TickCount64;
        _depthWrite = (_depthWrite + 1) % DepthRows;
        if (_depthCount < DepthRows) _depthCount++;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);
        if (_displayMode == RfDisplayMode.Perspective3D)
        {
            DrawPerspective3D(e.Graphics);
            DrawTuning(e.Graphics, Height * 55 / 100);
            DrawViewStatus(e.Graphics, showCenterSpan: false);
            DrawLevelBars(e.Graphics, spectrumHeight: 0);
            // FT8/FT4 labels are classic-waterfall only (scroll with time).
            return;
        }
        var spectrumHeight = Math.Max(120, Height * 44 / 100);
        DrawSpectrum(e.Graphics, new Rectangle(0, 0, Width, spectrumHeight));
        DrawWaterfall(e.Graphics, new Rectangle(0, spectrumHeight, Width, Height - spectrumHeight));
        DrawTuning(e.Graphics, spectrumHeight);
        DrawViewStatus(e.Graphics, showCenterSpan: true);
        DrawLevelBars(e.Graphics, spectrumHeight);
        FtxScrollStamp[] stamps;
        lock (_sync) stamps = _showFtxOnMain ? _ftxStamps.ToArray() : [];
        DrawFtxScrollStamps(e.Graphics, stamps, spectrumHeight);
    }

    private void DrawPerspective3D(Graphics graphics)
    {
        float[] history;
        int write, count;
        float[]? live;
        lock (_sync)
        {
            Array.Copy(_depthHistory, _depthPaintCopy, _depthHistory.Length);
            history = _depthPaintCopy;
            write = _depthWrite;
            count = _depthCount;
            live = _spectrum;
        }
        if (count < 1 && live is not { Length: > 1 }) return;

        var w = Width;
        var h = Height;
        var vanishY = h * 0.10f;
        var frontY = h * 0.86f;
        var frontLeft = 0f;
        var frontRight = (float)Math.Max(1, w - 1);
        var backLeft = w * 0.28f;
        var backRight = w * 0.72f;

        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        using var ground = new SolidBrush(Color.FromArgb(8, 14, 22));
        graphics.FillRectangle(ground, 0, 0, w, h);

        // Walls fill empty side wedges only — never paint over the waterfall deck.
        using (var deck = new System.Drawing.Drawing2D.GraphicsPath())
        {
            deck.AddPolygon([
                new PointF(frontLeft, frontY),
                new PointF(frontRight, frontY),
                new PointF(backRight, vanishY),
                new PointF(backLeft, vanishY)
            ]);
            graphics.SetClip(deck, System.Drawing.Drawing2D.CombineMode.Exclude);
            DrawPerspectiveStandingWalls(graphics, frontY, vanishY, frontLeft, frontRight, backLeft, backRight);
            graphics.ResetClip();
        }

        var depthScale = Math.Max(1, DepthRows - 1);
        var points = new PointF[DepthBins];
        var strip = new PointF[4];

        for (var age = count - 1; age >= 0; age--)
        {
            var histIndex = (write - 1 - age + DepthRows * 4) % DepthRows;
            var t = age / (float)depthScale;
            var depth = PerspectiveDepth(t);
            var yBase = frontY + (vanishY - frontY) * depth;
            var left = frontLeft + (backLeft - frontLeft) * depth;
            var right = frontRight + (backRight - frontRight) * depth;
            var rowScale = 1f - depth * 0.72f;
            var baseOffset = histIndex * DepthBins;
            var heightScale = h * 0.40f * rowScale;
            // Per-row noise only — never rescale history from the live front spectrum.
            var rowNoise = RowNoiseFloor(history, baseOffset);

            for (var bin = 0; bin < DepthBins; bin++)
            {
                var db = history[baseOffset + bin];
                var rise = Math.Clamp((db - rowNoise) / 48f, 0f, 1.25f);
                var x = left + (right - left) * bin / Math.Max(1, DepthBins - 1);
                points[bin] = new PointF(x, yBase - rise * heightScale);
            }

            if (age <= 14)
            {
                for (var bin = 0; bin < DepthBins - 1; bin++)
                {
                    var db = Math.Max(history[baseOffset + bin], history[baseOffset + bin + 1]);
                    var energy = Math.Clamp((db - rowNoise) / 48f, 0f, 1.25f);
                    if (energy < 0.18f) continue;
                    var color = DepthColor(db, depth, rowNoise, (int)(90 + energy * 130 * (1f - depth * 0.35f)));
                    strip[0] = points[bin];
                    strip[1] = points[bin + 1];
                    strip[2] = new PointF(points[bin + 1].X, yBase);
                    strip[3] = new PointF(points[bin].X, yBase);
                    using var brush = new SolidBrush(color);
                    graphics.FillPolygon(brush, strip);
                }
            }

            var step = age <= 16 ? 1 : 2;
            for (var bin = 0; bin < DepthBins - 1; bin += step)
            {
                var next = Math.Min(bin + step, DepthBins - 1);
                var db = Math.Max(history[baseOffset + bin], history[baseOffset + next]);
                var energy = Math.Clamp((db - rowNoise) / 48f, 0f, 1.25f);
                if (energy < 0.06f && age > 8) continue;
                var alpha = energy < 0.15f
                    ? (int)(30 + (1 - depth) * 40)
                    : (int)(110 + energy * 120 * (1f - depth * 0.4f));
                var color = DepthColor(db, depth, rowNoise, alpha);
                using var pen = new Pen(color, energy < 0.2f ? 0.7f : Math.Max(0.9f, 1.6f - depth * 0.6f));
                graphics.DrawLine(pen, points[bin], points[next]);
            }
        }

        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        if (live is { Length: > 1 })
        {
            var pointCount = Math.Min(w, live.Length);
            if (pointCount >= 2)
            {
                Span<float> liveScratch = stackalloc float[32];
                var ln = 0;
                for (var i = 0; i < pointCount && ln < liveScratch.Length; i += Math.Max(1, pointCount / 32))
                {
                    var idx = SpectrumIndexForX(i * (w - 1) / Math.Max(1, pointCount - 1), live.Length, w);
                    liveScratch[ln++] = live[idx] + _spectrumLevelOffsetDb;
                }
                var liveNoise = RowNoiseFromSamples(liveScratch[..ln]);

                var pts = new PointF[pointCount];
                var dbs = new float[pointCount];
                for (var i = 0; i < pointCount; i++)
                {
                    var xPix = (float)i * (w - 1) / (pointCount - 1);
                    var idx = SpectrumIndexForX((int)Math.Round(xPix), live.Length, w);
                    dbs[i] = live[idx] + _spectrumLevelOffsetDb;
                    var rise = Math.Clamp((dbs[i] - liveNoise) / 48f, 0f, 1.25f);
                    pts[i] = new PointF(xPix, frontY - rise * h * 0.40f);
                }
                for (var i = 0; i < pointCount - 1; i++)
                {
                    var db = Math.Max(dbs[i], dbs[i + 1]);
                    var energy = Math.Clamp((db - liveNoise) / 48f, 0f, 1.25f);
                    if (energy < 0.05f) continue;
                    var color = DepthColor(db, 0f, liveNoise, (int)(160 + energy * 80));
                    using var glow = new Pen(Color.FromArgb(Math.Min(70, (int)(energy * 80)), color.R, color.G, color.B), 3.5f);
                    using var neon = new Pen(color, 1.5f);
                    graphics.DrawLine(glow, pts[i], pts[i + 1]);
                    graphics.DrawLine(neon, pts[i], pts[i + 1]);
                }
            }
        }

        using (var rail = new Pen(Color.FromArgb(70, 100, 140), 1.2f))
        {
            graphics.DrawLine(rail, frontLeft, frontY, backLeft, vanishY);
            graphics.DrawLine(rail, frontRight, frontY, backRight, vanishY);
            graphics.DrawLine(rail, frontLeft, frontY, frontRight, frontY);
        }

        DrawPerspectiveWallAnnotations(graphics, history, write, count, frontY, vanishY, frontLeft, frontRight, backLeft, backRight);

        using var labelFont = new Font("Segoe UI Semibold", 8f);
        using var labelBrush = new SolidBrush(Color.FromArgb(160, 200, 220));
        graphics.DrawString("3D WATERFALL", labelFont, labelBrush, 8, 6);
        DrawPerspectiveFrequencyAxis(graphics, frontY);
    }

    /// <summary>Front spacing wider, rear tighter (camera perspective).</summary>
    private static float PerspectiveDepth(float t) =>
        1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 1.55f);

    private static float RowNoiseFloor(float[] history, int baseOffset)
    {
        Span<float> scratch = stackalloc float[32];
        var n = 0;
        for (var bin = 0; bin < DepthBins && n < scratch.Length; bin += Math.Max(1, DepthBins / 32))
            scratch[n++] = history[baseOffset + bin];
        return RowNoiseFromSamples(scratch[..n]);
    }

    private static float RowNoiseFromSamples(Span<float> samples)
    {
        if (samples.Length == 0) return -100f;
        samples.Sort();
        return samples[Math.Clamp(samples.Length / 5, 0, samples.Length - 1)];
    }

    /// <summary>Yaesu-like vertical walls standing on the left/right floor rails (not outer triangles).</summary>
    private void DrawPerspectiveStandingWalls(
        Graphics graphics,
        float frontY, float vanishY,
        float frontLeft, float frontRight, float backLeft, float backRight)
    {
        var h = Height;
        // ~1.5× taller than before; back shrinks gently so ridges stay under the rim.
        var frontHeight = h * 0.60f;
        var backHeight = frontHeight * 0.78f;
        var wallTopFront = Math.Max(18f, frontY - frontHeight);
        // Allow wall tops above the floor horizon (standing walls), leave a sky gap.
        var wallTopBack = Math.Max(14f, vanishY - backHeight);

        // Left vertical wall (parallelogram along left rail)
        var leftWall = new PointF[]
        {
            new(frontLeft, frontY),
            new(backLeft, vanishY),
            new(backLeft, wallTopBack),
            new(frontLeft, wallTopFront)
        };
        using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new PointF(frontLeft, frontY), new PointF(backLeft + 30, wallTopBack),
                   Color.FromArgb(40, 24, 70, 95),
                   Color.FromArgb(22, 12, 36, 52)))
            graphics.FillPolygon(fill, leftWall);
        using (var edge = new Pen(Color.FromArgb(90, 100, 190, 220), 1.2f))
        {
            graphics.DrawPolygon(edge, leftWall);
            using var post = new Pen(Color.FromArgb(50, 130, 180, 210), 1f);
            for (var i = 1; i <= 4; i++)
            {
                var t = i / 5f;
                var x0 = frontLeft + (backLeft - frontLeft) * t;
                var yFloor = frontY + (vanishY - frontY) * t;
                var yTop = wallTopFront + (wallTopBack - wallTopFront) * t;
                graphics.DrawLine(post, x0, yFloor, x0, yTop);
            }
        }

        // Right vertical wall
        var rightWall = new PointF[]
        {
            new(frontRight, frontY),
            new(backRight, vanishY),
            new(backRight, wallTopBack),
            new(frontRight, wallTopFront)
        };
        using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new PointF(frontRight, frontY), new PointF(backRight - 30, wallTopBack),
                   Color.FromArgb(40, 24, 70, 95),
                   Color.FromArgb(22, 12, 36, 52)))
            graphics.FillPolygon(fill, rightWall);
        using (var edge = new Pen(Color.FromArgb(90, 100, 190, 220), 1.2f))
        {
            graphics.DrawPolygon(edge, rightWall);
            for (var i = 1; i <= 4; i++)
            {
                var t = i / 5f;
                var x0 = frontRight + (backRight - frontRight) * t;
                var yFloor = frontY + (vanishY - frontY) * t;
                var yTop = wallTopFront + (wallTopBack - wallTopFront) * t;
                using var shelf = new Pen(Color.FromArgb(45, 130, 180, 210), 1f);
                graphics.DrawLine(shelf, x0, yFloor, x0, yTop);
            }
        }

        // Thin top rim (optional ceiling hint — small, leaves open sky)
        using (var rim = new Pen(Color.FromArgb(60, 120, 170, 200), 1f))
        {
            graphics.DrawLine(rim, backLeft, wallTopBack, backRight, wallTopBack);
        }
    }

    private void DrawPerspectiveWallAnnotations(
        Graphics graphics, float[] history, int write, int count,
        float frontY, float vanishY,
        float frontLeft, float frontRight, float backLeft, float backRight)
    {
        var w = Width;
        var h = Height;
        var nowMs = Environment.TickCount64;
        var oldestIdx = (write - count + DepthRows) % DepthRows;
        var historySpanSec = count < 2 ? 0f
            : Math.Max(0.05f, (nowMs - _depthTickMs[oldestIdx]) / 1000f);
        var peakDb = -140f;
        var noiseDb = 0f;
        var noiseSamples = 0;
        for (var age = 0; age < count; age++)
        {
            var histIndex = (write - 1 - age + DepthRows * 4) % DepthRows;
            for (var bin = 0; bin < DepthBins; bin++)
            {
                var db = history[histIndex * DepthBins + bin];
                if (db > peakDb) peakDb = db;
                if (db < -40f)
                {
                    noiseDb += db;
                    noiseSamples++;
                }
            }
        }
        var noiseFloor = noiseSamples > 0 ? noiseDb / noiseSamples : -120f;
        var (filterLow, filterHigh) = FilterFrequencyRange();
        var spanText = _viewBandwidth >= 1_000_000
            ? $"{_viewBandwidth / 1_000_000d:0.###} MHz"
            : $"{_viewBandwidth / 1_000d:0.###} kHz";
        var bwText = _filterBandwidth >= 1_000
            ? $"{_filterBandwidth / 1_000d:0.###} kHz"
            : $"{_filterBandwidth} Hz";

        var wallTopFront = Math.Max(18f, frontY - h * 0.60f);
        var wallTopBack = Math.Max(14f, vanishY - (frontY - wallTopFront) * 0.78f);

        using var timeFont = new Font("Segoe UI Semibold", 7.2f);
        using var timeBrush = new SolidBrush(Color.FromArgb(220, 160, 230, 245));
        using var timeDim = new SolidBrush(Color.FromArgb(170, 120, 165, 185));
        using var gridPen = new Pen(Color.FromArgb(35, 120, 170, 200), 1f);
        using var gridStrong = new Pen(Color.FromArgb(65, 160, 210, 230), 1.2f);

        var depthScale = Math.Max(1, DepthRows - 1);
        foreach (var t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            var depth = PerspectiveDepth(t);
            var rowAge = (int)Math.Round(t * depthScale);
            if (rowAge >= count && t > 0.01f) continue;
            var yFloor = frontY + (vanishY - frontY) * depth;
            var left = frontLeft + (backLeft - frontLeft) * depth;
            var right = frontRight + (backRight - frontRight) * depth;
            var yTop = wallTopFront + (wallTopBack - wallTopFront) * depth;
            graphics.DrawLine(t < 0.01f ? gridStrong : gridPen, left, yFloor, left + 18, yFloor);
            // Do not draw grid across the waterfall deck — labels stay on the left wall only.
            string label;
            if (t < 0.01f || count < 1) label = "NOW";
            else
            {
                var histIndex = (write - 1 - Math.Min(rowAge, count - 1) + DepthRows * 4) % DepthRows;
                label = FormatAge(Math.Max(0.05f, (nowMs - _depthTickMs[histIndex]) / 1000f));
            }
            var ly = yFloor + (yTop - yFloor) * 0.35f - 6;
            graphics.DrawString(label, timeFont, t < 0.01f ? timeBrush : timeDim, left + 4, ly);
        }

        using var captionFont = new Font("Segoe UI Semibold", 7.5f);
        using var captionBrush = new SolidBrush(Color.FromArgb(200, 130, 205, 225));
        graphics.DrawString("TIME", captionFont, captionBrush, frontLeft + 6, wallTopFront + 4);
        graphics.DrawString($"{historySpanSec:0.0}s · {count} rows", timeFont, timeDim, frontLeft + 6, wallTopFront + 18);

        using var infoFont = new Font("Segoe UI Semibold", 7.4f);
        using var valueFont = new Font("Consolas", 7.6f);
        using var infoBrush = new SolidBrush(Color.FromArgb(200, 170, 210, 225));
        using var valueBrush = new SolidBrush(Color.FromArgb(230, 220, 235, 245));
        using var accentBrush = new SolidBrush(Color.FromArgb(220, 255, 193, 70));

        var lines = new (string Key, string Val, bool Accent)[]
        {
            ("MODE", _mode.ToString(), true),
            ("VFO", FormatFrequency(_tunedFrequency), true),
            ("CENTER", FormatFrequency(_centerFrequency), false),
            ("SPAN", spanText, false),
            ("FILTER", bwText, false),
            ("PASS", $"{FormatFrequency(filterLow)} – {FormatFrequency(filterHigh)}", false),
            ("RATE", FormatSampleRate(_sampleRate), false),
            ("PEAK", $"{peakDb:0.0} dB", false),
            ("NOISE", $"{noiseFloor:0.0} dB", false),
            ("WF LVL", $"{_waterfallLevelOffsetDb:+0.0;-0.0;0} dB", false),
            ("SUB", $"{Volatile.Read(ref _subVfoMarkers).Length}", false),
        };

        // ~1.7× previous width; keep clear of WF level bar on the far right.
        const float panelW = 201f;
        var panelX = Math.Max(8f, w - 26f - panelW);
        var panelY = 28f;
        using (var panelBg = new SolidBrush(Color.FromArgb(95, 6, 16, 26)))
        using (var panelEdge = new Pen(Color.FromArgb(120, 90, 160, 190), 1f))
        {
            var rect = new RectangleF(panelX - 4, panelY - 2, panelW + 4, lines.Length * 15f + 22);
            graphics.FillRectangle(panelBg, Rectangle.Round(rect));
            graphics.DrawRectangle(panelEdge, Rectangle.Round(rect));
        }
        graphics.DrawString("RF DECK", captionFont, captionBrush, panelX, panelY);
        panelY += 16;
        foreach (var (key, val, accent) in lines)
        {
            graphics.DrawString(key, infoFont, infoBrush, panelX, panelY);
            graphics.DrawString(val, valueFont, accent ? accentBrush : valueBrush, panelX + 52, panelY);
            panelY += 15;
        }

        var legendY = Math.Min(frontY - 40, panelY + 10);
        var legendX = panelX;
        graphics.DrawString("AGE", captionFont, captionBrush, legendX, legendY);
        for (var i = 0; i < 5; i++)
        {
            var t = i / 4f;
            var c = DepthColor(-60f, t, 200);
            using var swatch = new SolidBrush(c);
            graphics.FillRectangle(swatch, legendX + i * 18, legendY + 14, 16, 8);
        }
        graphics.DrawString("new", timeFont, timeDim, legendX, legendY + 24);
        graphics.DrawString("old", timeFont, timeDim, legendX + 58, legendY + 24);
    }

    private static string FormatAge(float seconds) =>
        seconds < 0.05f ? "NOW" :
        seconds < 10f ? $"-{seconds:0.0}s" :
        $"-{seconds:0}s";

    private static string FormatSampleRate(int sampleRate) =>
        sampleRate >= 1_000_000 ? $"{sampleRate / 1_000_000d:0.###} Msps" : $"{sampleRate / 1_000d:0.#} ksps";

    private void DrawPerspectiveFrequencyAxis(Graphics graphics, float frontY)
    {
        using var font = new Font("Segoe UI Semibold", 8f);
        using var brush = new SolidBrush(Color.FromArgb(185, 210, 225));
        using var tickPen = new Pen(Color.FromArgb(100, 150, 175), 1f);
        using var rule = new Pen(Color.FromArgb(70, 110, 135), 1f);
        var axisY = Math.Min(Height - 18f, frontY + 3f);
        graphics.DrawLine(rule, 0, axisY, Width, axisY);
        for (var tick = 0; tick <= 8; tick++)
        {
            var x = Width * tick / 8f;
            var frequency = ViewLeft + _viewBandwidth * tick / 8d;
            graphics.DrawLine(tickPen, x, axisY, x, axisY + 5);
            var text = FormatFrequency(frequency);
            var size = graphics.MeasureString(text, font);
            var lx = tick == 0 ? 2f
                : tick == 8 ? Width - size.Width - 2f
                : x - size.Width / 2f;
            graphics.DrawString(text, font, brush, lx, Math.Min(Height - 15f, axisY + 5f));
        }
    }

    private static float AverageDb(float[] history, int histIndex)
    {
        double sum = 0;
        for (var i = 0; i < DepthBins; i++) sum += history[histIndex * DepthBins + i];
        return (float)(sum / DepthBins);
    }

    private static Color DepthColor(float db, float depth, float noiseFloor, int alpha)
    {
        // Strength relative to noise: floor stays dark; peaks go cyan→yellow→white up front,
        // and wash toward soft crimson with age (Yaesu-style).
        var energy = Math.Clamp((db - noiseFloor) / 48f, 0f, 1.25f);
        var frontR = (int)(18 + energy * 230);
        var frontG = (int)(40 + energy * 170 + (1f - Math.Min(1f, energy)) * 50);
        var frontB = (int)(70 + (1f - Math.Min(1f, energy)) * 140);
        var backR = (int)(55 + energy * 120);
        var backG = (int)(18 + energy * 40);
        var backB = (int)(28 + energy * 35);
        var r = (int)(frontR + (backR - frontR) * depth);
        var g = (int)(frontG + (backG - frontG) * depth);
        var b = (int)(frontB + (backB - frontB) * depth);
        var a = (int)(Math.Clamp(alpha, 0, 255) * (0.55f + energy * 0.45f) * (1f - depth * 0.35f));
        return Color.FromArgb(Math.Clamp(a, 0, 240), Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    private static Color DepthColor(float db, float depth, int alpha) =>
        DepthColor(db, depth, -100f, alpha);

    private void DrawFtxScrollStamps(Graphics graphics, FtxScrollStamp[] stamps, int spectrumHeight)
    {
        if (stamps.Length == 0) return;
        using var font = new Font("Consolas", 7.2f, FontStyle.Bold);
        using var brush = new SolidBrush(Color.FromArgb(235, 255, 210, 90));
        using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        // Start just above the spectrum/waterfall seam so labels enter the waterfall and scroll with time.
        foreach (var stamp in stamps)
        {
            var x = FrequencyToX(stamp.RfHz);
            if (x < -40 || x > Width + 40) continue;
            var lane = Math.Clamp(stamp.Lane, 0, FtxLaneCount - 1);
            var y = spectrumHeight - 14 - lane * FtxLaneHeight + stamp.RowAge;
            if (y < 4 || y > Height - 10) continue;
            var size = graphics.MeasureString(stamp.Text, font);
            var drawX = Math.Clamp(x - size.Width / 2f, 2, Math.Max(2, Width - size.Width - 2));
            graphics.DrawString(stamp.Text, font, shadow, drawX + 1, y + 1);
            graphics.DrawString(stamp.Text, font, brush, drawX, y);
        }
    }


    private void DrawSpectrum(Graphics graphics, Rectangle bounds)
    {
        using var gridPen = new Pen(Color.FromArgb(38, 87, 107, 127));
        using var labelBrush = new SolidBrush(Color.FromArgb(165, ForeColor));
        using var font = new Font("Segoe UI", 8f);
        for (var db = -120; db <= 0; db += 20)
        {
            var y = PowerToY(db, bounds);
            graphics.DrawLine(gridPen, 0, y, Width, y);
            graphics.DrawString($"{db} dB", font, labelBrush, 5, y + 2);
        }
        for (var tick = 0; tick <= 8; tick++)
        {
            var x = Width * tick / 8f;
            graphics.DrawLine(gridPen, x, 0, x, bounds.Bottom);
            var frequency = ViewLeft + _viewBandwidth * tick / 8d;
            graphics.DrawString(FormatFrequency(frequency), font, labelBrush, x + 3, 4);
        }

        float[]? spectrum;
        lock (_sync) spectrum = _spectrum;
        if (spectrum is null || spectrum.Length < 2) return;
        _spectrumRenderer.Render(graphics, bounds, new SpectrumRenderFrame
        {
            Spectrum = spectrum,
            SpectrumIndexForX = x => SpectrumIndexForX(x, spectrum.Length, Math.Max(1, Width)),
            // Live FFT samples use the current RF coordinate system. Applying the
            // accumulated history shift here would separate peaks from received audio.
            VisualOffsetPixels = 0,
            LevelOffsetDb = _spectrumLevelOffsetDb,
            ForeColor = ForeColor
        });
    }

    private void DrawWaterfall(Graphics graphics, Rectangle bounds)
    {
        lock (_sync)
        {
            EnsureWaterfall();
            _waterfallRenderer.Render(graphics, bounds);
        }
        using var separator = new Pen(Color.FromArgb(90, 130, 155));
        graphics.DrawLine(separator, bounds.Left, bounds.Top, bounds.Right, bounds.Top);
    }

    private void DrawTuning(Graphics graphics, int spectrumHeight)
    {
        var rawX = FrequencyToX(_tunedFrequency);
        var x = Math.Clamp(rawX, 0, Math.Max(0, Width - 1));
        var (filterLow, filterHigh) = FilterFrequencyRange();
        var filterLeftX = FrequencyToX(filterLow);
        var filterRightX = FrequencyToX(filterHigh);
        using var filterBrush = new SolidBrush(Color.FromArgb(32, 0, 210, 255));
        using var linePen = new Pen(Color.FromArgb(245, 255, 193, 56), 1.5f);
        var clippedLeft = Math.Max(0, Math.Min(filterLeftX, filterRightX));
        var clippedRight = Math.Min(Width, Math.Max(filterLeftX, filterRightX));
        if (clippedRight > clippedLeft) graphics.FillRectangle(filterBrush, clippedLeft, 0, clippedRight - clippedLeft, Height);
        if (rawX >= 0 && rawX < Width)
        {
            graphics.DrawLine(linePen, x, 0, x, Height);
        }
        else
        {
            PointF[] arrow = rawX < 0
                ? [new(0, spectrumHeight - 12), new(10, spectrumHeight - 18), new(10, spectrumHeight - 6)]
                : [new(Width - 1, spectrumHeight - 12), new(Width - 11, spectrumHeight - 18), new(Width - 11, spectrumHeight - 6)];
            using var arrowBrush = new SolidBrush(linePen.Color);
            graphics.FillPolygon(arrowBrush, arrow);
        }
        using var labelFont = new Font("Consolas", 9f, FontStyle.Bold);
        using var labelBrush = new SolidBrush(Color.FromArgb(255, 215, 70));
        graphics.DrawString($"▼ {FormatFrequency(_tunedFrequency)}", labelFont, labelBrush,
            Math.Clamp(x + 4, 0, Math.Max(0, Width - 125)), spectrumHeight - 20);

        var markerIndex = 0;
        _subRecallButtonBounds = Rectangle.Empty;
        var markers = Volatile.Read(ref _subVfoMarkers);
        for (var i = 0; i < markers.Length; i++)
        {
            var marker = markers[i];
            var markerX = FrequencyToX(marker.Frequency);
            if (markerX < 0 || markerX >= Width) continue;
            using var markerPen = new Pen(Color.FromArgb(225, 104, 193, 222), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var markerBrush = new SolidBrush(Color.FromArgb(104, 193, 222));
            using var markerFont = new Font("Segoe UI Semibold", 7.2f);
            graphics.DrawLine(markerPen, markerX, 0, markerX, Height);
            graphics.DrawString(marker.Name, markerFont, markerBrush,
                Math.Clamp(markerX + 3, 0, Math.Max(0, Width - 48)), 25 + markerIndex++ % 3 * 14);

            if (i == _hoveredSubMarkerIndex)
            {
                const int bw = 88;
                const int bh = 22;
                // Keep the button near the cursor so moving onto it is easy.
                var anchor = _subRecallAnchor.IsEmpty
                    ? new Point(markerX, 48 + (i % 3) * 14)
                    : _subRecallAnchor;
                var bx = Math.Clamp(anchor.X + 14, 2, Math.Max(2, Width - bw - 2));
                var by = Math.Clamp(anchor.Y - bh - 10, 2, Math.Max(2, Height - bh - 2));
                // If that would sit off-screen above, drop just below the cursor instead.
                if (by < 2) by = Math.Clamp(anchor.Y + 12, 2, Math.Max(2, Height - bh - 2));
                _subRecallButtonBounds = new Rectangle(bx, by, bw, bh);
                using var btnFill = new SolidBrush(Color.FromArgb(230, 35, 66, 83));
                using var btnEdge = new Pen(Color.FromArgb(255, 193, 69), 1f);
                using var btnText = new SolidBrush(Color.FromArgb(255, 193, 69));
                using var btnFont = new Font("Segoe UI Semibold", 7.2f);
                graphics.FillRectangle(btnFill, _subRecallButtonBounds);
                graphics.DrawRectangle(btnEdge, _subRecallButtonBounds);
                // Apply Sub VFO frequency to Main VFO.
                var label = "SUB→MAIN";
                var size = graphics.MeasureString(label, btnFont);
                graphics.DrawString(label, btnFont, btnText,
                    bx + (_subRecallButtonBounds.Width - size.Width) / 2f,
                    by + (_subRecallButtonBounds.Height - size.Height) / 2f);
            }
        }

        DrawWfmStationMarkers(graphics, spectrumHeight);
        DrawBroadcastMarkers(graphics, spectrumHeight);
        DrawAutoTuneOverlays(graphics, spectrumHeight);
        DrawAutoLevelOptions(graphics, spectrumHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right or MouseButtons.Middle)) return;
        // Prefer SUB→MAIN button over tune/drag.
        if (e.Button == MouseButtons.Left && !_subRecallButtonBounds.IsEmpty &&
            _subRecallButtonBounds.Contains(e.Location) && _hoveredSubFrequency > 0)
        {
            SetTunedFrequency(_hoveredSubFrequency);
            return;
        }
        if (e.Button == MouseButtons.Left && TryToggleAutoLevel(e.Location))
            return;
        // Station labels sit on top of the VFO/filter strip — take them before Tune.
        if (e.Button == MouseButtons.Left && TryHitBroadcastStation(e.Location, out var bHz, out var bName))
        {
            Focus();
            _dragStartX = e.X;
            _dragMode = DragMode.Broadcast;
            _pendingBroadcastHz = bHz;
            _pendingBroadcastName = bName;
            Capture = true;
            Cursor = Cursors.Hand;
            return;
        }
        Focus();
        _dragStartX = e.X;
        _dragStartFrequency = _tunedFrequency;
        _dragStartViewCenter = _viewCenterFrequency;
        _dragStartCenter = _centerFrequency;
        Capture = true;
        if (e.Button == MouseButtons.Left && e.X >= Width - 24)
        {
            if (_displayMode == RfDisplayMode.Perspective3D)
            {
                _dragMode = DragMode.WaterfallLevel;
                SetLevelFromY(e.Y, spectrumHeight: 0);
            }
            else
            {
                var spectrumHeight = Math.Max(120, Height * 44 / 100);
                _dragMode = e.Y < spectrumHeight ? DragMode.SpectrumLevel : DragMode.WaterfallLevel;
                SetLevelFromY(e.Y, spectrumHeight);
            }
            Cursor = Cursors.SizeNS;
            return;
        }
        if (e.Button is MouseButtons.Right or MouseButtons.Middle)
        {
            _dragMode = DragMode.Pan;
            Cursor = Cursors.Hand;
            return;
        }
        if (IsInFilter(e.X))
        {
            _dragMode = DragMode.Tune;
            Cursor = Cursors.SizeWE;
        }
        else
        {
            _dragMode = DragMode.PendingClick;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStartX < 0)
        {
            UpdateHoveredSubMarker(e.X, e.Y);
            UpdateHoveredAutoTune(e.X);
            if (!_subRecallButtonBounds.IsEmpty && _subRecallButtonBounds.Contains(e.Location))
                Cursor = Cursors.Hand;
            else if (HitsAutoLevel(e.Location) || TryHitBroadcastStation(e.Location, out _, out _))
                Cursor = Cursors.Hand;
            else
                Cursor = e.X >= Width - 24 ? Cursors.SizeNS : IsInFilter(e.X) ? Cursors.SizeWE : Cursors.Default;
            return;
        }
        if (_dragMode == DragMode.Broadcast) return;
        var delta = e.X - _dragStartX;
        if (_dragMode == DragMode.PendingClick && Math.Abs(delta) >= 4)
        {
            _dragMode = DragMode.Center;
            Cursor = Cursors.Hand;
            _dragVisualOffsetPixels = 0;
            CenterDragStarted?.Invoke();
        }
        if (_dragMode == DragMode.Tune)
            SetTunedFrequency(_dragStartFrequency + (long)(delta * (double)_viewBandwidth / Math.Max(1, Width)));
        else if (_dragMode == DragMode.Pan)
            SetView(_dragStartViewCenter - (long)(delta * (double)_viewBandwidth / Math.Max(1, Width)), _viewBandwidth);
        else if (_dragMode == DragMode.Center)
            SetCenterFromDrag(_dragStartCenter - (long)(delta * (double)_viewBandwidth / Math.Max(1, Width)));
        else if (_dragMode is DragMode.SpectrumLevel or DragMode.WaterfallLevel)
            SetLevelFromY(e.Y, _displayMode == RfDisplayMode.Perspective3D
                ? 0
                : Math.Max(120, Height * 44 / 100));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragMode == DragMode.Pan && e.Button == MouseButtons.Right && Math.Abs(e.X - _dragStartX) < 4)
            SubVfoRequested?.Invoke(IsNearMainVfoLine(e.X, e.Y) ? _tunedFrequency : XToFrequency(e.X));
        if (e.Button == MouseButtons.Left && _dragMode == DragMode.Broadcast)
        {
            if (_pendingBroadcastHz > 0)
                BroadcastStationClicked?.Invoke(_pendingBroadcastHz, _pendingBroadcastName);
            _pendingBroadcastHz = 0;
            _pendingBroadcastName = "";
        }
        else if ((_dragMode == DragMode.PendingClick || (_dragMode == DragMode.Tune && Math.Abs(e.X - _dragStartX) < 4)) && e.Button == MouseButtons.Left)
        {
            if (TryHitBroadcastStation(e.Location, out var bHz, out var bName))
                BroadcastStationClicked?.Invoke(bHz, bName);
            else if (TryHitWfmStation(e.Location, out var hz, out var name))
                WfmStationClicked?.Invoke(hz, name);
            else
                SetTunedFrequency(XToFrequency(e.X));
        }
        var endedViewDrag = _dragMode is DragMode.Center or DragMode.Pan;
        if (_dragMode == DragMode.Center) CenterDragCompleted?.Invoke(_centerFrequency);
        _dragStartX = -1;
        _dragMode = DragMode.None;
        Capture = false;
        Cursor = Cursors.Default;
        if (endedViewDrag)
        {
            _broadcastPresenceAt = 0;
            Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (TryHitBroadcastStation(e.Location, out var hz, out var name))
        {
            BroadcastStationClicked?.Invoke(hz, name);
            return;
        }
        SetTunedFrequency(XToFrequency(e.X));
        SetView(_tunedFrequency, _viewBandwidth);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ModifierKeys.HasFlag(Keys.Control) || ModifierKeys.HasFlag(Keys.Shift))
        {
            var step = ModifierKeys.HasFlag(Keys.Control) ? 100 : 1_000;
            SetTunedFrequency(_tunedFrequency + Math.Sign(e.Delta) * step);
            return;
        }
        ZoomAt(e.X, e.Delta);
    }

    internal void BeginAutomatedCenterDrag(int x, int y)
    {
        _dragStartX = x;
        _dragStartFrequency = _tunedFrequency;
        _dragStartViewCenter = _viewCenterFrequency;
        _dragStartCenter = _centerFrequency;
        _dragVisualOffsetPixels = 0;
        _dragMode = DragMode.Center;
        Capture = true;
        CenterDragStarted?.Invoke();
    }
    internal void ContinueAutomatedCenterDrag(int x, int y) => OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, x, y, 0));
    internal void EndAutomatedCenterDrag(int x, int y) => OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
    internal void SetLevelForVerification(bool spectrum, int y)
    {
        var spectrumHeight = Math.Max(120, Height * 44 / 100);
        _dragMode = spectrum ? DragMode.SpectrumLevel : DragMode.WaterfallLevel;
        SetLevelFromY(y, spectrumHeight);
        _dragMode = DragMode.None;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _subRecallHoldTimer.Stop();
            _subRecallHoldTimer.Dispose();
            _waterfallRenderer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredAutoTuneIndex >= 0)
        {
            _hoveredAutoTuneIndex = -1;
            Invalidate();
        }
        // Do not clear immediately — keep SUB→MAIN visible for the hold window.
        if (_hoveredSubMarkerIndex < 0) return;
        ArmSubRecallHold();
    }

    private void UpdateHoveredSubMarker(int mouseX, int mouseY)
    {
        var markers = Volatile.Read(ref _subVfoMarkers);
        var best = -1;
        long bestFreq = 0;
        var bestDist = SubRecallHitPixels;
        for (var i = 0; i < markers.Length; i++)
        {
            var x = FrequencyToX(markers[i].Frequency);
            if (x < 0 || x >= Width) continue;
            var dist = Math.Abs(mouseX - x);
            if (dist > bestDist) continue;
            bestDist = dist;
            best = i;
            bestFreq = markers[i].Frequency;
        }

        if (best >= 0)
        {
            var switched = best != _hoveredSubMarkerIndex;
            if (switched)
            {
                _hoveredSubMarkerIndex = best;
                _hoveredSubFrequency = bestFreq;
                _subRecallAnchor = new Point(mouseX, mouseY);
                ArmSubRecallHold();
                Invalidate();
                return;
            }

            // Same SUB: keep button parked; only refresh the hold window.
            ArmSubRecallHold();
            return;
        }

        // Pointer on the recall button: keep + refresh the 3s hold.
        if (!_subRecallButtonBounds.IsEmpty)
        {
            var cursor = new Point(mouseX, mouseY);
            if (_subRecallButtonBounds.Contains(cursor))
            {
                ArmSubRecallHold();
                return;
            }
        }

        // Away from marker/button: keep showing until the hold timer fires.
        if (_hoveredSubMarkerIndex >= 0 && _subRecallHoldTimer.Enabled)
            return;

        ClearSubRecallHover();
    }

    private void ArmSubRecallHold()
    {
        _subRecallHoldTimer.Stop();
        _subRecallHoldTimer.Interval = SubRecallHoldMs;
        _subRecallHoldTimer.Start();
    }

    private void ClearSubRecallHover()
    {
        _subRecallHoldTimer.Stop();
        if (_hoveredSubMarkerIndex < 0 && _hoveredSubFrequency == 0 && _subRecallButtonBounds.IsEmpty)
            return;
        _hoveredSubMarkerIndex = -1;
        _hoveredSubFrequency = 0;
        _subRecallButtonBounds = Rectangle.Empty;
        _subRecallAnchor = Point.Empty;
        Invalidate();
    }

    private void SetTunedFrequency(long frequency)
    {
        var minimum = _centerFrequency - _sampleRate / 2;
        var maximum = _centerFrequency + _sampleRate / 2;
        _tunedFrequency = Math.Clamp(frequency, minimum, maximum);
        TunedFrequencyChanged?.Invoke(_tunedFrequency);
        Invalidate();
    }

    internal (long Low, long High) FilterFrequencyRange() =>
        RadioModes.FilterRange(_mode, _tunedFrequency, _filterBandwidth, _ssbLower);

    private bool _ssbLower = true;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SsbLower
    {
        get => _ssbLower;
        set => _ssbLower = value;
    }

    private bool IsInFilter(int x)
    {
        var (low, high) = FilterFrequencyRange();
        var left = Math.Min(FrequencyToX(low), FrequencyToX(high)) - 8;
        var right = Math.Max(FrequencyToX(low), FrequencyToX(high)) + 8;
        return x >= left && x <= right;
    }

    private void ZoomAt(int mouseX, int wheelDelta)
    {
        var oldBandwidth = _viewBandwidth;
        var factor = Math.Pow(1.5, Math.Sign(wheelDelta));
        var newBandwidth = (int)Math.Round(oldBandwidth / factor);
        newBandwidth = Math.Clamp(newBandwidth, MinimumViewBandwidth(), _maximumViewBandwidth);
        if (newBandwidth == oldBandwidth) return;
        var anchorRatio = Math.Clamp(mouseX / (double)Math.Max(1, Width), 0, 1);
        var anchorFrequency = ViewLeft + anchorRatio * oldBandwidth;
        var newCenter = (long)Math.Round(anchorFrequency - (anchorRatio - .5) * newBandwidth);
        SetView(newCenter, newBandwidth);
    }

    private void SetView(long center, int bandwidth)
    {
        var bandwidthChanged = _viewBandwidth != bandwidth;
        bandwidth = Math.Clamp(bandwidth, MinimumViewBandwidth(), _maximumViewBandwidth);
        center = ClampViewCenter(center, bandwidth);
        if (_viewCenterFrequency == center && _viewBandwidth == bandwidth) return;
        _viewCenterFrequency = center;
        _viewBandwidth = bandwidth;
        if (bandwidthChanged) ClearWaterfall();
        ViewChanged?.Invoke(center, bandwidth);
        Invalidate();
    }

    private void SetCenterFromDrag(long center)
    {
        var delta = center - _centerFrequency;
        if (delta == 0) return;
        var requestedOffset = (int)Math.Round((_dragStartCenter - center) * Width / (double)Math.Max(1, _viewBandwidth));
        var shift = requestedOffset - _dragVisualOffsetPixels;
        ShiftWaterfall(shift);
        ShiftDepthHistory(shift);
        _dragVisualOffsetPixels = requestedOffset;
        _centerFrequency = center;
        _viewCenterFrequency += delta;
        CenterFrequencyChanged?.Invoke(center);
        Invalidate();
    }

    public void CommitCenterDrag()
    {
        _dragVisualOffsetPixels = 0;
        Invalidate();
    }

    public void CancelCenterDrag()
    {
        ShiftWaterfall(-_dragVisualOffsetPixels);
        ShiftDepthHistory(-_dragVisualOffsetPixels);
        _dragVisualOffsetPixels = 0;
        Invalidate();
    }

    private void ShiftWaterfall(int pixels)
    {
        if (pixels == 0) return;
        lock (_sync)
        {
            _waterfallRenderer.Shift(pixels, BackColor);
        }
    }

    private void ShiftDepthHistory(int pixels)
    {
        if (pixels == 0 || Width < 2) return;
        var binShift = (int)Math.Round(pixels * (double)(DepthBins - 1) / Math.Max(1, Width - 1));
        if (binShift == 0) return;
        lock (_sync)
        {
            var row = new float[DepthBins];
            for (var age = 0; age < _depthCount; age++)
            {
                var idx = (_depthWrite - 1 - age + DepthRows * 4) % DepthRows;
                Array.Copy(_depthHistory, idx * DepthBins, row, 0, DepthBins);
                for (var bin = 0; bin < DepthBins; bin++)
                {
                    var src = bin - binShift;
                    _depthHistory[idx * DepthBins + bin] = src >= 0 && src < DepthBins ? row[src] : -140f;
                }
            }
        }
    }

    private int MinimumViewBandwidth() => Math.Min(_sampleRate, 5_000);

    private void DrawLevelBars(Graphics graphics, int spectrumHeight)
    {
        var waterfall = WaterfallLevelBar(spectrumHeight);
        if (_displayMode == RfDisplayMode.Perspective3D)
        {
            if (!waterfall.IsEmpty)
                DrawLevelBar(graphics, waterfall, _waterfallLevelOffsetDb, "WF");
            return;
        }
        var spectrum = SpectrumLevelBar(spectrumHeight);
        if (!spectrum.IsEmpty)
            DrawLevelBar(graphics, spectrum, _spectrumLevelOffsetDb, "SP");
        if (!waterfall.IsEmpty)
            DrawLevelBar(graphics, waterfall, _waterfallLevelOffsetDb, "WF");
    }

    private static void DrawLevelBar(Graphics graphics, Rectangle bounds, float value, string label)
    {
        using var background = new SolidBrush(Color.FromArgb(185, 7, 15, 22));
        using var border = new Pen(Color.FromArgb(155, 112, 139, 154));
        using var fill = new SolidBrush(Color.FromArgb(205, 190, 119, 42));
        graphics.FillRectangle(background, bounds);
        graphics.DrawRectangle(border, bounds);
        var ratio = (value + 40) / 80f;
        var y = bounds.Bottom - (int)Math.Round(ratio * bounds.Height);
        graphics.FillRectangle(fill, bounds.Left - 2, y - 2, bounds.Width + 4, 5);
        using var font = new Font("Segoe UI Semibold", 6f);
        using var text = new SolidBrush(Color.FromArgb(210, 224, 232));
        graphics.DrawString(label, font, text, bounds.Left - 1, bounds.Top + 2);
    }

    private void SetLevelFromY(int y, int spectrumHeight)
    {
        if (_dragMode == DragMode.SpectrumLevel)
        {
            var bounds = SpectrumLevelBar(spectrumHeight);
            if (!bounds.IsEmpty)
                _spectrumLevelOffsetDb = LevelFromY(y, bounds);
        }
        else if (_dragMode == DragMode.WaterfallLevel)
        {
            var bounds = WaterfallLevelBar(spectrumHeight);
            if (!bounds.IsEmpty)
                _waterfallLevelOffsetDb = LevelFromY(y, bounds);
        }
        DisplayLevelsChanged?.Invoke(_spectrumLevelOffsetDb, _waterfallLevelOffsetDb);
        Invalidate();
    }

    private static float LevelFromY(int y, Rectangle bounds) =>
        Math.Clamp((bounds.Bottom - y) / (float)Math.Max(1, bounds.Height) * 80f - 40f, -40, 40);

    private void DrawViewStatus(Graphics graphics, bool showCenterSpan = true)
    {
        using var font = new Font("Segoe UI Semibold", 8f);
        using var brush = new SolidBrush(Color.FromArgb(205, 188, 204, 220));
        if (showCenterSpan)
        {
            var span = _viewBandwidth >= 1_000_000 ? $"{_viewBandwidth / 1_000_000d:0.###} MHz" : $"{_viewBandwidth / 1_000d:0.###} kHz";
            var status = $"CENTER {_centerFrequency / 1_000_000d:0.000000} MHz  ·  SPAN {span}";
            var size = graphics.MeasureString(status, font);
            graphics.DrawString(status, font, brush, Math.Max(5, Width - size.Width - 8), 22);
        }

        const string help = "Wheel: zoom  ·  Ctrl/Shift+wheel: VFO  ·  Drag background: move view  ·  Right-click drag: pan view";
        using var helpBrush = new SolidBrush(Color.FromArgb(175, 180, 195, 210));
        graphics.DrawString(help, font, helpBrush, 7, Math.Max(5, Height - 19));
    }

    private Rectangle SpectrumLevelBar(int spectrumHeight)
    {
        if (_displayMode == RfDisplayMode.Perspective3D) return Rectangle.Empty;
        return new Rectangle(Width - 20, 8, 14, Math.Max(36, spectrumHeight - 30));
    }

    private Rectangle WaterfallLevelBar(int spectrumHeight)
    {
        if (_displayMode == RfDisplayMode.Perspective3D)
            return new Rectangle(Width - 20, 28, 14, Math.Max(36, Height - 52));
        var top = spectrumHeight + 8;
        return new Rectangle(Width - 20, top, 14, Math.Max(36, Height - top - 22));
    }

    private static Rectangle AutoBoxUnder(Rectangle bar)
    {
        if (bar.IsEmpty) return Rectangle.Empty;
        const int width = 58;
        return new Rectangle(Math.Max(2, bar.Right - width), bar.Bottom + 1, width, 15);
    }

    private Rectangle SpectrumAutoBox(int spectrumHeight) => AutoBoxUnder(SpectrumLevelBar(spectrumHeight));

    private Rectangle WaterfallAutoBox(int spectrumHeight) => AutoBoxUnder(WaterfallLevelBar(spectrumHeight));

    private bool HitsAutoLevel(Point point)
    {
        var spectrumHeight = Math.Max(120, Height * 44 / 100);
        return SpectrumAutoBox(spectrumHeight).Contains(point) || WaterfallAutoBox(spectrumHeight).Contains(point);
    }

    private bool TryToggleAutoLevel(Point point)
    {
        var spectrumHeight = Math.Max(120, Height * 44 / 100);
        if (SpectrumAutoBox(spectrumHeight).Contains(point))
            _spectrumAutoLevel = !_spectrumAutoLevel;
        else if (WaterfallAutoBox(spectrumHeight).Contains(point))
            _waterfallAutoLevel = !_waterfallAutoLevel;
        else
            return false;
        AutoLevelsChanged?.Invoke(_spectrumAutoLevel, _waterfallAutoLevel);
        Invalidate();
        return true;
    }

    private void DrawAutoLevelOptions(Graphics graphics, int spectrumHeight)
    {
        var spectrum = SpectrumAutoBox(spectrumHeight);
        if (!spectrum.IsEmpty)
            DrawAutoCheck(graphics, spectrum, _spectrumAutoLevel);
        var waterfall = WaterfallAutoBox(spectrumHeight);
        if (!waterfall.IsEmpty)
            DrawAutoCheck(graphics, waterfall, _waterfallAutoLevel);
    }

    private static void DrawAutoCheck(Graphics graphics, Rectangle bounds, bool on)
    {
        var box = new Rectangle(bounds.X, bounds.Y + 2, 12, 12);
        using var fill = new SolidBrush(on ? Color.FromArgb(220, 28, 72, 112) : Color.FromArgb(200, 10, 18, 28));
        using var border = new Pen(on ? Color.FromArgb(230, 120, 190, 255) : Color.FromArgb(170, 120, 145, 165));
        using var caption = new SolidBrush(Color.FromArgb(220, 196, 214, 228));
        using var font = new Font("Segoe UI Semibold", 7f);
        graphics.FillRectangle(fill, box);
        graphics.DrawRectangle(border, box);
        if (on)
        {
            using var mark = new Pen(Color.FromArgb(245, 186, 224, 255), 1.6f);
            graphics.DrawLine(mark, box.X + 2, box.Y + 6, box.X + 5, box.Y + 9);
            graphics.DrawLine(mark, box.X + 5, box.Y + 9, box.X + 10, box.Y + 3);
        }
        graphics.DrawString("AUTO", font, caption, box.Right + 3, bounds.Y);
    }

    private long ClampViewCenter(long center, int bandwidth)
    {
        var half = Math.Max(1L, bandwidth / 2L);
        if (_maximumViewBandwidth > _sampleRate)
        {
            // Remote capture span is the current server window. Zoom/pan may request
            // a wider RF range (MaximumSpectrumSpan) outside that window.
            return Math.Clamp(center, half, Math.Max(half, RadioLimits.MaximumFrequency - half));
        }
        var minimum = _centerFrequency - _sampleRate / 2L + half;
        var maximum = _centerFrequency + _sampleRate / 2L - half;
        // Keep scale labels physical — a window starting below 0 Hz is not meaningful RF.
        minimum = Math.Max(minimum, half);
        if (minimum > maximum) return Math.Max(half, _centerFrequency);
        return Math.Clamp(center, minimum, maximum);
    }

    private void EnsureWaterfall()
    {
        var height = Math.Max(2, Height - Math.Max(120, Height * 44 / 100));
        if (Width < 2) return;
        _waterfallRenderer.Resize(Math.Max(2, Width), height, BackColor);
    }

    public void ClearSpectrum()
    {
        lock (_sync) _spectrum = [];
        ClearWaterfall();
        Invalidate();
    }

    private void ClearWaterfall()
    {
        lock (_sync)
        {
            _waterfallRenderer.Clear(BackColor);
            Array.Clear(_depthHistory);
            Array.Clear(_depthTickMs);
            _depthWrite = 0;
            _depthCount = 0;
            _depthPushCounter = 0;
            _ftxStamps.Clear();
        }
    }

    private int SpectrumIndexForX(int x, int spectrumLength, int pixelWidth)
    {
        var frequency = ViewLeft + Math.Clamp(x, 0, pixelWidth) * (double)_viewBandwidth / Math.Max(1, pixelWidth);
        var captureLeft = _centerFrequency - _sampleRate / 2d;
        return Math.Clamp((int)Math.Round((frequency - captureLeft) * (spectrumLength - 1) / _sampleRate), 0, spectrumLength - 1);
    }

    private void DrawWfmStationMarkers(Graphics graphics, int spectrumHeight)
    {
        _wfmStationHits.Clear();
        var markers = Volatile.Read(ref _wfmStationMarkers);
        if (markers.Length == 0) return;

        using var nameFont = new Font("Segoe UI Semibold", 7.5f);
        using var tickPen = new Pen(Color.FromArgb(180, 120, 210, 230), 1f);
        var labelTop = Math.Max(4, Math.Min(18, spectrumHeight / 8));
        var row = 0;
        foreach (var marker in markers)
        {
            var x = FrequencyToX(marker.Frequency);
            if (x < -20 || x >= Width + 20) continue;
            var name = string.IsNullOrWhiteSpace(marker.Name) ? $"{marker.Frequency / 1_000_000d:0.0}" : marker.Name;
            var size = TextRenderer.MeasureText(graphics, name, nameFont);
            var labelW = Math.Min(110, size.Width + 8);
            var labelH = size.Height + 2;
            var labelX = Math.Clamp(x - labelW / 2, 2, Math.Max(2, Width - labelW - 2));
            var labelY = labelTop + row % 3 * (labelH + 2);
            var bounds = new Rectangle(labelX, labelY, labelW, labelH);

            var tickBottom = Math.Min(spectrumHeight - 2, labelY + labelH + 10);
            graphics.DrawLine(tickPen, x, labelY + labelH, x, tickBottom);

            using var bg = new SolidBrush(Color.FromArgb(200, 12, 28, 40));
            using var border = new Pen(Color.FromArgb(160, 90, 180, 205));
            using var textBrush = new SolidBrush(Color.FromArgb(230, 235, 245));
            graphics.FillRectangle(bg, bounds);
            graphics.DrawRectangle(border, bounds);
            graphics.DrawString(name, nameFont, textBrush, bounds.X + 3, bounds.Y + 1);
            _wfmStationHits.Add((bounds, marker.Frequency, marker.Name));
            row++;
        }
    }

    private void DrawBroadcastMarkers(Graphics graphics, int spectrumHeight)
    {
        _broadcastHits.Clear();
        var labels = LayoutBroadcastLabels(spectrumHeight);
        if (labels.Count == 0) return;

        using var nameFont = new Font("Segoe UI Semibold", 7.2f);
        using var idleTick = new Pen(Color.FromArgb(140, 255, 176, 72), 1f);
        using var liveTick = new Pen(Color.FromArgb(230, 80, 170, 255), 1.4f);
        using var idleBg = new SolidBrush(Color.FromArgb(150, 42, 28, 12));
        using var liveBg = new SolidBrush(Color.FromArgb(230, 12, 28, 52));
        using var idleBorder = new Pen(Color.FromArgb(150, 255, 176, 72));
        using var liveBorder = new Pen(Color.FromArgb(240, 90, 175, 255));
        using var idleText = new SolidBrush(Color.FromArgb(210, 255, 220, 170));
        using var liveText = new SolidBrush(Color.FromArgb(245, 214, 232, 255));
        using var liveDot = new SolidBrush(Color.FromArgb(255, 70, 160, 255));

        for (var pass = 0; pass < 2; pass++)
        {
            var livePass = pass == 1;
            foreach (var label in labels)
            {
                if (label.OnAir != livePass) continue;
                var x = FrequencyToX(label.Frequency);
                graphics.DrawLine(livePass ? liveTick : idleTick, x, label.Bounds.Bottom, x,
                    Math.Min(spectrumHeight - 2, label.Bounds.Bottom + 14));
                graphics.FillRectangle(livePass ? liveBg : idleBg, label.Bounds);
                graphics.DrawRectangle(livePass ? liveBorder : idleBorder, label.Bounds);
                var textX = label.Bounds.X + 3;
                if (livePass)
                {
                    var dot = new Rectangle(label.Bounds.X + 3, label.Bounds.Y + Math.Max(2, (label.Bounds.Height - 7) / 2), 7, 7);
                    graphics.FillEllipse(liveDot, dot);
                    textX = dot.Right + 2;
                }
                graphics.DrawString(label.Name, nameFont, livePass ? liveText : idleText, textX, label.Bounds.Y + 1);
                var hit = label.Bounds;
                hit.Inflate(livePass ? 10 : 8, livePass ? 8 : 6);
                _broadcastHits.Add((hit, label.Frequency, label.Name));
            }
        }
    }

    private List<(Rectangle Bounds, long Frequency, string Name)> BuildBroadcastHits()
    {
        var hits = new List<(Rectangle Bounds, long Frequency, string Name)>();
        var spectrumHeight = _displayMode == RfDisplayMode.Perspective3D
            ? Height * 55 / 100
            : Math.Max(120, Height * 44 / 100);
        foreach (var label in LayoutBroadcastLabels(spectrumHeight))
        {
            var bounds = label.Bounds;
            bounds.Inflate(label.OnAir ? 10 : 8, label.OnAir ? 8 : 6);
            hits.Add((bounds, label.Frequency, label.Name));
        }
        hits.Sort((a, b) =>
        {
            var aLive = _broadcastPresence.TryGetValue(a.Frequency, out var onA) && onA;
            var bLive = _broadcastPresence.TryGetValue(b.Frequency, out var onB) && onB;
            return aLive.CompareTo(bLive);
        });
        return hits;
    }

    private List<(Rectangle Bounds, long Frequency, string Name, bool OnAir)> LayoutBroadcastLabels(int spectrumHeight)
    {
        var labels = new List<(Rectangle Bounds, long Frequency, string Name, bool OnAir)>();
        var markers = Volatile.Read(ref _broadcastMarkers);
        if (markers.Length == 0) return labels;

        var visible = new List<(long Frequency, string Name, int X)>(markers.Length);
        foreach (var marker in markers)
        {
            var x = FrequencyToX(marker.Frequency);
            if (x < -20 || x >= Width + 20) continue;
            var name = string.IsNullOrWhiteSpace(marker.Name)
                ? $"{marker.Frequency / 1_000d:0} kHz"
                : marker.Name;
            visible.Add((marker.Frequency, name, x));
        }
        if (visible.Count == 0) return labels;
        EnsureBroadcastPresence(visible);

        var live = new bool[visible.Count];
        for (var i = 0; i < visible.Count; i++)
            live[i] = _broadcastPresence.TryGetValue(visible[i].Frequency, out var on) && on;

        using var nameFont = new Font("Segoe UI Semibold", 7.2f);
        for (var index = 0; index < visible.Count; index++)
        {
            var item = visible[index];
            var size = TextRenderer.MeasureText(item.Name, nameFont);
            size.Width += 12;
            labels.Add((BroadcastLabelBounds(item.X, size, index, spectrumHeight), item.Frequency, item.Name, live[index]));
        }
        return labels;
    }

    private void EnsureBroadcastPresence(List<(long Frequency, string Name, int X)> visible)
    {
        var now = Environment.TickCount64;
        if (_dragMode is DragMode.Center or DragMode.Pan || _dragVisualOffsetPixels != 0)
            return;
        if (_broadcastPresenceViewCenter != _viewCenterFrequency || _broadcastPresenceViewSpan != _viewBandwidth)
            _broadcastPresenceAt = 0;
        if (now - _broadcastPresenceAt < 450 && _broadcastPresence.Count > 0) return;
        _broadcastPresenceAt = now;
        _broadcastPresenceViewCenter = _viewCenterFrequency;
        _broadcastPresenceViewSpan = _viewBandwidth;
        _broadcastPresence.Clear();
        var xs = new int[visible.Count];
        for (var i = 0; i < visible.Count; i++) xs[i] = visible[i].X;
        var flags = new bool[visible.Count];
        if (_displayMode == RfDisplayMode.Perspective3D)
            FillOnAirFromDepth(xs, flags);
        else
        {
            lock (_sync)
            {
                if (_waterfallRenderer is BitmapWaterfallPlugin plugin)
                    plugin.FillOnAirColumns(xs, flags);
            }
        }
        for (var i = 0; i < visible.Count; i++)
            _broadcastPresence[visible[i].Frequency] = flags[i];
    }

    private void FillOnAirFromDepth(int[] xs, bool[] flags)
    {
        lock (_sync)
        {
            if (_depthCount < 8) return;
            var width = Math.Max(2, Width);
            var rows = Math.Min(24, _depthCount);
            Span<float> floorSamples = stackalloc float[64];
            var floorCount = 0;
            var step = Math.Max(1, DepthBins / 48);
            for (var bin = 0; bin < DepthBins && floorCount < floorSamples.Length; bin += step)
                floorSamples[floorCount++] = DepthColumnDb(bin, rows);
            if (floorCount == 0) return;
            floorSamples[..floorCount].Sort();
            var floor = floorSamples[floorCount * 35 / 100];
            for (var i = 0; i < xs.Length && i < flags.Length; i++)
            {
                var bin = (int)Math.Round(xs[i] * (DepthBins - 1) / (double)(width - 1));
                bin = Math.Clamp(bin, 0, DepthBins - 1);
                var score = DepthColumnDb(bin, rows);
                if (bin > 0) score = Math.Max(score, DepthColumnDb(bin - 1, rows));
                if (bin + 1 < DepthBins) score = Math.Max(score, DepthColumnDb(bin + 1, rows));
                flags[i] = score >= floor + 8f;
            }
        }
    }

    private float DepthColumnDb(int bin, int rows)
    {
        double sum = 0;
        for (var age = 0; age < rows; age++)
        {
            var idx = (_depthWrite - 1 - age + DepthRows * 4) % DepthRows;
            sum += _depthHistory[idx * DepthBins + bin];
        }
        return (float)(sum / rows);
    }

    private Rectangle BroadcastLabelBounds(int x, Size size, int row, int spectrumHeight)
    {
        var labelW = Math.Min(148, size.Width + 8);
        var labelH = size.Height + 2;
        var labelX = Math.Clamp(x - labelW / 2, 2, Math.Max(2, Width - labelW - 2));
        var labelTop = Math.Max(22, Math.Min(40, spectrumHeight / 5));
        var chrome = _overlayChromeReserve;
        if (!chrome.IsEmpty && labelX < chrome.Right + 8)
            labelTop = Math.Max(labelTop, chrome.Bottom + 4);
        var labelY = labelTop + row % 4 * (labelH + 2);
        return new Rectangle(labelX, labelY, labelW, labelH);
    }

    private bool TryHitWfmStation(Point pt, out long frequency, out string name) =>
        TryHitMarker(_wfmStationHits, pt, out frequency, out name);

    private bool TryHitBroadcastStation(Point pt, out long frequency, out string name)
    {
        if (TryHitMarker(_broadcastHits, pt, out frequency, out name))
            return true;
        var hits = BuildBroadcastHits();
        for (var i = hits.Count - 1; i >= 0; i--)
        {
            if (!hits[i].Bounds.Contains(pt)) continue;
            frequency = hits[i].Frequency;
            name = hits[i].Name;
            return true;
        }
        frequency = 0;
        name = "";
        return false;
    }

    private static bool TryHitMarker(
        List<(Rectangle Bounds, long Frequency, string Name)> hits, Point pt, out long frequency, out string name)
    {
        for (var i = hits.Count - 1; i >= 0; i--)
        {
            var hit = hits[i];
            if (!hit.Bounds.Contains(pt)) continue;
            frequency = hit.Frequency;
            name = hit.Name;
            return true;
        }
        frequency = 0;
        name = "";
        return false;
    }

    private void DrawAutoTuneOverlays(Graphics graphics, int spectrumHeight)
    {
        var overlays = Volatile.Read(ref _autoTuneOverlays);
        if (overlays.Length == 0 || _hoveredAutoTuneIndex < 0 || _hoveredAutoTuneIndex >= overlays.Length)
            return;

        var o = overlays[_hoveredAutoTuneIndex];
        var minX = FrequencyToX(o.MinFrequency);
        var maxX = FrequencyToX(o.MaxFrequency);
        var left = Math.Clamp(Math.Min(minX, maxX), 0, Width);
        var right = Math.Clamp(Math.Max(minX, maxX), 0, Width);
        if (right <= left)
        {
            left = 0;
            right = Width;
        }

        // Soft band + faint edge ticks near the VFO line (only while hovered).
        var bandTop = 4;
        var bandBottom = Math.Max(bandTop + 8, spectrumHeight - 8);
        using var band = new SolidBrush(Color.FromArgb(o.IsMain ? 28 : 22, 255, 193, 70));
        graphics.FillRectangle(band, left, bandTop, Math.Max(1, right - left), bandBottom - bandTop);

        using var edgePen = new Pen(Color.FromArgb(90, o.IsMain ? 255 : 140, o.IsMain ? 190 : 210, o.IsMain ? 90 : 230), 1f);
        graphics.DrawLine(edgePen, left, bandTop, left, bandBottom);
        graphics.DrawLine(edgePen, right, bandTop, right, bandBottom);

        var spectrumBounds = new Rectangle(0, 0, Width, Math.Max(1, spectrumHeight));
        var y = PowerToY(o.TriggerLevelDb, spectrumBounds);
        using var levelPen = new Pen(Color.FromArgb(110, 255, 140, 90), 1f);
        graphics.DrawLine(levelPen, left, y, right, y);

        using var labelFont = new Font("Consolas", 7.2f);
        using var labelBrush = new SolidBrush(Color.FromArgb(150, 255, 210, 150));
        var minLabel = FormatFrequency(o.MinFrequency);
        var maxLabel = FormatFrequency(o.MaxFrequency);
        var caption = string.IsNullOrWhiteSpace(o.Name)
            ? $"{minLabel} – {maxLabel}"
            : $"{o.Name}  {minLabel} – {maxLabel}";
        var anchorHz = o.IsMain
            ? _tunedFrequency
            : (o.AnchorFrequency > 0 ? o.AnchorFrequency : (o.MinFrequency + o.MaxFrequency) / 2);
        var anchorX = FrequencyToX(anchorHz);
        var textX = Math.Clamp(anchorX + 6, 2, Math.Max(2, Width - 160));
        var textY = Math.Clamp(bandTop + 4, 2, Math.Max(2, spectrumHeight - 18));
        graphics.DrawString(caption, labelFont, labelBrush, textX, textY);
    }

    private void UpdateHoveredAutoTune(int mouseX)
    {
        var overlays = Volatile.Read(ref _autoTuneOverlays);
        var best = -1;
        var bestDist = AutoTuneHitPixels;
        for (var i = 0; i < overlays.Length; i++)
        {
            var o = overlays[i];
            // Hit the VFO vertical line (anchor), not the always-on top min/max ticks.
            // Main uses live tuned frequency so overlays need not be rebuilt every VFO step.
            var anchorHz = o.IsMain
                ? _tunedFrequency
                : (o.AnchorFrequency > 0 ? o.AnchorFrequency : (o.MinFrequency + o.MaxFrequency) / 2);
            var dist = Math.Abs(mouseX - FrequencyToX(anchorHz));
            if (dist > bestDist) continue;
            bestDist = dist;
            best = i;
        }
        if (best == _hoveredAutoTuneIndex) return;
        _hoveredAutoTuneIndex = best;
        Invalidate();
    }

    private double ViewLeft => _viewCenterFrequency - _viewBandwidth / 2d;
    private int FrequencyToX(long frequency) => (int)Math.Round((frequency - ViewLeft) * Width / _viewBandwidth);
    private long XToFrequency(int x) => (long)Math.Round(ViewLeft + Math.Clamp(x, 0, Width) * (double)_viewBandwidth / Math.Max(1, Width));

    private bool IsNearMainVfoLine(int x, int y)
    {
        var vfoX = FrequencyToX(_tunedFrequency);
        if (vfoX < 0 || vfoX >= Width) return false;
        if (Math.Abs(x - vfoX) <= 12) return true;
        var spectrumHeight = _displayMode == RfDisplayMode.Perspective3D
            ? Height
            : Math.Max(120, Height * 44 / 100);
        var labelX = Math.Clamp(vfoX + 4, 0, Math.Max(0, Width - 125));
        var labelY = spectrumHeight - 20;
        return new Rectangle(labelX, labelY - 2, 125, 20).Contains(x, y);
    }
    private static float PowerToY(float power, Rectangle bounds) => bounds.Bottom - Math.Clamp((power + 130) / 140f, 0, 1) * bounds.Height;

    private string FormatFrequency(double hz)
    {
        if (hz < 1_000_000) return $"{hz / 1_000:0.000}k";
        return _viewBandwidth < 200_000 ? $"{hz / 1_000_000:0.000000}M"
            : _viewBandwidth < 1_000_000 ? $"{hz / 1_000_000:0.0000}M"
            : $"{hz / 1_000_000:0.000}M";
    }

}
