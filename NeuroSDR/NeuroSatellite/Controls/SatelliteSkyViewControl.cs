using System.ComponentModel;
using NeuroSatellite.Satellite;

namespace NeuroSatellite.Controls;

/// <summary>
/// Polar satellite display, similar to a GPS sky view.
/// The timer asks for a position update and stays separate from the TLE download interval.
/// </summary>
public sealed class SatelliteSkyViewControl : UserControl
{
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly Label _summaryLabel = new() { Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleCenter };
    private readonly SkyCanvas _canvas = new() { Dock = DockStyle.Fill };
    private IReadOnlyList<SatelliteInfo> _satellites = [];
    private int? _selectedNorad;
    private int? _soloNorad;
    private double _headingDegrees;
    private int? _gnssUsedSatelliteCount;
    private bool _showBelowHorizonCandidates = true;
    private double _belowHorizonLimitDegrees = 15;

    public SatelliteSkyViewControl()
    {
        BackColor = Color.FromArgb(15, 23, 42);
        MinimumSize = new Size(160, 160);
        Padding = new Padding(0);
        _summaryLabel.ForeColor = Color.FromArgb(191, 219, 254);
        Controls.Add(_canvas);
        Controls.Add(_summaryLabel);
        _refreshTimer.Tick += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _canvas.SatelliteDoubleClicked += (_, satellite) => SatelliteDoubleClicked?.Invoke(this, satellite);
        UpdateSummary();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSummary
    {
        get => _summaryLabel.Visible;
        set
        {
            _summaryLabel.Visible = value;
            if (value)
            {
                _summaryLabel.Dock = DockStyle.Bottom;
                _summaryLabel.Height = 30;
            }
            else
            {
                _summaryLabel.Dock = DockStyle.None;
                _summaryLabel.Height = 0;
            }
        }
    }

    /// <summary>Screen top bearing (0-360 degrees).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double HeadingDegrees
    {
        get => _headingDegrees;
        set
        {
            _headingDegrees = ((value % 360d) + 360d) % 360d;
            _canvas.HeadingDegrees = _headingDegrees;
            _canvas.Invalidate();
            UpdateSummary();
        }
    }

    /// <summary>How often to request a position update. Default is 5 seconds.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TimeSpan RefreshInterval
    {
        get => TimeSpan.FromMilliseconds(_refreshTimer.Interval);
        set
        {
            if (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromMinutes(10))
                throw new ArgumentOutOfRangeException(nameof(value), "Refresh interval must be 1 second to 10 minutes.");
            _refreshTimer.Interval = (int)value.TotalMilliseconds;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoRefreshEnabled
    {
        get => _refreshTimer.Enabled;
        set => _refreshTimer.Enabled = value;
    }

    /// <summary>
    /// Number of satellites used in the fix, from a GNSS receiver (NMEA GSA/GSV).
    /// When unset, the view shows only the TLE count above the horizon.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int? GnssUsedSatelliteCount
    {
        get => _gnssUsedSatelliteCount;
        set
        {
            _gnssUsedSatelliteCount = value is < 0 ? null : value;
            UpdateSummary();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowBelowHorizonCandidates
    {
        get => _showBelowHorizonCandidates;
        set
        {
            _showBelowHorizonCandidates = value;
            _canvas.ShowBelowHorizonCandidates = value;
            _canvas.Invalidate();
        }
    }

    /// <summary>How many degrees below the horizon still appear as candidates outside the circle.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double BelowHorizonLimitDegrees
    {
        get => _belowHorizonLimitDegrees;
        set
        {
            _belowHorizonLimitDegrees = Math.Clamp(value, 0, 45);
            _canvas.BelowHorizonLimitDegrees = _belowHorizonLimitDegrees;
            _canvas.Invalidate();
        }
    }

    /// <summary>The host recomputes TLE positions in this event, then calls SetSatellites.</summary>
    public event EventHandler? RefreshRequested;
    public event EventHandler<SatelliteInfo>? SatelliteDoubleClicked;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int? SelectedNorad
    {
        get => _selectedNorad;
        set
        {
            if (_selectedNorad == value) return;
            _selectedNorad = value;
            _canvas.SelectedNorad = value;
            _canvas.Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int? SoloNorad
    {
        get => _soloNorad;
        set
        {
            if (_soloNorad == value) return;
            _soloNorad = value;
            _canvas.SoloNorad = value;
            _canvas.Invalidate();
        }
    }

    public void SetSatellites(IEnumerable<SatelliteInfo> satellites)
    {
        _satellites = satellites.Where(satellite => satellite.CurrentLookAngle is not null).ToList();
        _canvas.Satellites = _satellites;
        _canvas.SelectedNorad = _selectedNorad;
        _canvas.SoloNorad = _soloNorad;
        _canvas.Invalidate();
        UpdateSummary();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refreshTimer.Dispose();
        base.Dispose(disposing);
    }

    private void UpdateSummary()
    {
        var visibleCount = _satellites.Count(satellite => satellite.IsAboveHorizon);
        var gnssText = GnssUsedSatelliteCount.HasValue ? $"  ·  GPS Fix {GnssUsedSatelliteCount:N0}" : string.Empty;
        _summaryLabel.Text = $"Tracking {_satellites.Count:N0} · Above horizon {visibleCount:N0}{gnssText} · Heading {HeadingDegrees:F0}°";
    }

    private sealed class SkyCanvas : Control
    {
        public event EventHandler<SatelliteInfo>? SatelliteDoubleClicked;

        private readonly List<SkyHitRegion> _hitRegions = [];

        private sealed class SkyHitRegion
        {
            public required SatelliteInfo Satellite { get; init; }
            public RectangleF IconBounds { get; init; }
            public RectangleF? LabelBounds { get; init; }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double HeadingDegrees { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public IReadOnlyList<SatelliteInfo> Satellites { get; set; } = [];
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int? SelectedNorad { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int? SoloNorad { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowBelowHorizonCandidates { get; set; } = true;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double BelowHorizonLimitDegrees { get; set; } = 15;

        public SkyCanvas()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(15, 23, 42);
            MouseDoubleClick += (_, e) =>
            {
                if (HitTestSatellites(e.Location) is { } satellite)
                    SatelliteDoubleClicked?.Invoke(this, satellite);
            };
        }

        private SatelliteInfo? HitTestSatellites(Point location)
        {
            SatelliteInfo? best = null;
            var bestDistance = float.MaxValue;
            foreach (var region in _hitRegions)
            {
                if (!region.IconBounds.Contains(location)) continue;
                var center = region.IconBounds;
                var dx = location.X - (center.Left + center.Width / 2f);
                var dy = location.Y - (center.Top + center.Height / 2f);
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = region.Satellite;
            }

            if (best is not null) return best;

            foreach (var region in _hitRegions)
            {
                if (region.LabelBounds is { } label && label.Contains(location))
                    return region.Satellite;
            }

            return null;
        }

        private (PointF Center, float Radius)? ComputeLayout()
        {
            const float labelPad = 14f;
            var diameter = Math.Min(ClientSize.Width, ClientSize.Height) - labelPad * 2;
            if (diameter < 48) return null;
            var radius = diameter / 2f;
            return (new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f), radius);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            var graphics = eventArgs.Graphics;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var layout = ComputeLayout();
            if (layout is null) return;
            var center = layout.Value.Center;
            var radius = layout.Value.Radius;
            using var ringPen = new Pen(Color.FromArgb(55, 148, 163, 184), 1);
            using var horizonPen = new Pen(Color.FromArgb(140, 56, 189, 248), 2);
            using var textBrush = new SolidBrush(Color.FromArgb(226, 232, 240));
            using var centerBrush = new SolidBrush(Color.FromArgb(250, 204, 21));

            _hitRegions.Clear();
            graphics.DrawEllipse(horizonPen, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            foreach (var ratio in new[] { 0.25f, 0.5f, 0.75f })
                graphics.DrawEllipse(ringPen, center.X - radius * ratio, center.Y - radius * ratio, radius * ratio * 2, radius * ratio * 2);

            for (var bearing = 0; bearing < 360; bearing += 30)
            {
                var angle = ToRadians(bearing - HeadingDegrees - 90);
                var outer = PointOnCircle(center, radius, angle);
                var inner = PointOnCircle(center, radius - (bearing % 90 == 0 ? 12 : 7), angle);
                graphics.DrawLine(ringPen, inner, outer);
                if (bearing % 90 == 0)
                {
                    var label = bearing switch { 0 => "N", 90 => "E", 180 => "S", _ => "W" };
                    var labelPoint = PointOnCircle(center, radius + 8, angle);
                    var labelSize = graphics.MeasureString(label, Font);
                    graphics.DrawString(label, Font, textBrush, labelPoint.X - labelSize.Width / 2, labelPoint.Y - labelSize.Height / 2);
                }
            }

            foreach (var satellite in Satellites)
            {
                if (satellite.NoradCatalogId is int norad &&
                    (norad == SelectedNorad || norad == SoloNorad))
                    continue;
                DrawSkySatellite(graphics, center, radius, satellite, selected: false, solo: false);
            }
            var selectedSat = Satellites.FirstOrDefault(item =>
                item.NoradCatalogId == SelectedNorad && item.NoradCatalogId != SoloNorad);
            if (selectedSat is not null)
                DrawSkySatellite(graphics, center, radius, selectedSat, selected: true, solo: false);
            var soloSat = Satellites.FirstOrDefault(item => item.NoradCatalogId == SoloNorad);
            if (soloSat is not null)
                DrawSkySatellite(graphics, center, radius, soloSat, selected: soloSat.NoradCatalogId == SelectedNorad, solo: true);

            graphics.FillEllipse(centerBrush, center.X - 5, center.Y - 5, 10, 10);
            graphics.DrawString("ME", Font, centerBrush, center.X + 7, center.Y + 3);
        }

        private void DrawSkySatellite(Graphics graphics, PointF center, float radius, SatelliteInfo satellite, bool selected, bool solo)
        {
            var lookAngle = satellite.CurrentLookAngle!;
            var belowHorizonCandidate = lookAngle.ElevationDegrees < 0
                                        && lookAngle.ElevationDegrees >= -BelowHorizonLimitDegrees;
            if (!lookAngle.IsAboveHorizon && (!ShowBelowHorizonCandidates || !belowHorizonCandidate) && !selected && !solo)
                return;
            var point = GetSkyPoint(center, radius, lookAngle.AzimuthDegrees, lookAngle.ElevationDegrees, BelowHorizonLimitDegrees);
            var color = solo
                ? Color.FromArgb(255, 220, 90, 255)
                : selected
                    ? Color.FromArgb(255, 255, 145, 40)
                    : lookAngle.IsAboveHorizon
                        ? DopplerColor(lookAngle)
                        : Color.FromArgb(160, 148, 163, 184);
            var marker = solo || selected ? 8f : 5f;
            using var satelliteBrush = new SolidBrush(color);
            graphics.FillEllipse(satelliteBrush, point.X - marker, point.Y - marker, marker * 2, marker * 2);
            if (solo || selected)
            {
                using var ring = new Pen(solo ? Color.FromArgb(255, 235, 140, 255) : Color.FromArgb(255, 255, 210, 90), 2f);
                graphics.DrawEllipse(ring, point.X - marker - 4, point.Y - marker - 4, (marker + 4) * 2, (marker + 4) * 2);
            }
            var iconBounds = new RectangleF(point.X - marker - 3, point.Y - marker - 3, (marker + 3) * 2, (marker + 3) * 2);
            using var labelFont = new Font(Font, solo || selected ? FontStyle.Bold : FontStyle.Regular);
            var caption = solo ? $"SOLO · {satellite.Name}" : satellite.Name;
            var nameSize = graphics.MeasureString(caption, labelFont);
            var labelX = point.X + 8;
            if (labelX + nameSize.Width > ClientSize.Width - 4)
                labelX = point.X - nameSize.Width - 8;
            var labelY = point.Y - nameSize.Height / 2;
            labelY = Math.Clamp(labelY, 2, ClientSize.Height - nameSize.Height - 2);
            var labelBounds = new RectangleF(labelX, labelY, nameSize.Width, nameSize.Height);
            graphics.DrawString(caption, labelFont, satelliteBrush, labelX, labelY);
            _hitRegions.Add(new SkyHitRegion
            {
                Satellite = satellite,
                IconBounds = iconBounds,
                LabelBounds = labelBounds
            });
        }

        private static Color DopplerColor(SatelliteLookAngle lookAngle)
        {
            var rate = lookAngle.RangeRateKilometersPerSecond;
            if (rate < -0.05)
                return Color.FromArgb(255, 72, 220, 120);
            if (rate > 0.05)
                return Color.FromArgb(255, 96, 165, 250);
            return lookAngle.ElevationDegrees >= 30 ? Color.LimeGreen : Color.Orange;
        }

        private PointF GetSkyPoint(PointF center, float radius, double azimuth, double elevation, double belowHorizonLimit)
        {
            var relativeAngle = ToRadians(azimuth - HeadingDegrees - 90);
            var boundedElevation = Math.Clamp(elevation, -belowHorizonLimit, 90d);
            var distance = radius * (float)((90d - boundedElevation) / 90d);
            return PointOnCircle(center, distance, relativeAngle);
        }

        private static PointF PointOnCircle(PointF center, float radius, double radians)
            => new(center.X + radius * (float)Math.Cos(radians), center.Y + radius * (float)Math.Sin(radians));

        private static double ToRadians(double degrees) => degrees * Math.PI / 180d;
    }
}
