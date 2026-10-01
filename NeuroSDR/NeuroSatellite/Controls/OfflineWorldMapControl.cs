using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using NeuroSatellite.Satellite;

namespace NeuroSatellite.Controls;

/// <summary>
/// GeoJSON world map with automatic download and offline local storage.
/// </summary>
public sealed class OfflineWorldMapControl : Control
{
    // Saved next to the executable.
    private const string DefaultMapFileName = "world_map.geojson";

    // jsDelivr CDN for Natural Earth 1:110m (~200KB). A browser User-Agent avoids HTTP 429.
    private const string DefaultDownloadUrl = "https://cdn.jsdelivr.net/gh/nvkelso/natural-earth-vector@master/geojson/ne_110m_admin_0_countries.geojson";

    private List<PointF[]> _countryPolygons = new();
    private ObserverLocation? _observer;
    private IReadOnlyList<SatelliteMapMarker> _satellites = Array.Empty<SatelliteMapMarker>();
    private IReadOnlyList<SatelliteInfo> _trackedSatellites = Array.Empty<SatelliteInfo>();
    private IReadOnlyList<SiteMapMarker> _sites = Array.Empty<SiteMapMarker>();
    private string? _selectedSiteId;
    private readonly List<(SiteMapMarker Site, RectangleF Bounds)> _siteHits = [];
    private SiteMapMarker? _hoveredSite;

    private const float MapPad = 10f;
    private const float MinZoom = 1f;
    private const float MaxZoom = 24f;
    private float _zoom = 1f;
    private double _viewLat;
    private double _viewLon;
    private bool _panning;
    private bool _didPan;
    private Point _panStart;
    private FlowLayoutPanel? _zoomBar;
    private bool _zoomEnabled;
    private bool _isDownloading;
    private string _statusMessage = string.Empty;
    private readonly ToolTip _toolTip = new();
    private readonly List<SatelliteHitRegion> _hitRegions = [];
    private SatelliteInfo? _hoveredSatellite;

    private sealed class SatelliteHitRegion
    {
        public required SatelliteInfo Satellite { get; init; }
        public RectangleF IconBounds { get; init; }
        public RectangleF? LabelBounds { get; init; }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowLegend { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string ObserverLabel { get; set; } = "★ Home";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool EmphasizeObserver { get; set; }

    /// <summary>When false, only the already-saved GeoJSON next to the exe is used.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowNetworkDownload { get; set; } = true;

    public event EventHandler<SatelliteInfo>? SatelliteDoubleClicked;
    public event EventHandler<SiteMapMarker>? SiteSelected;
    public event EventHandler<SiteMapMarker>? SiteDoubleClicked;

    public OfflineWorldMapControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(10, 22, 40);
        MinimumSize = new Size(200, 160);
        Font = new Font("Segoe UI", 8.5F, FontStyle.Regular);
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _didPan) return;
            if (HitTestSite(e.Location) is { } site)
                SiteSelected?.Invoke(this, site);
        };
        MouseDoubleClick += (_, e) =>
        {
            if (HitTestSite(e.Location) is { } site)
            {
                SiteDoubleClicked?.Invoke(this, site);
                return;
            }
            if (HitTestSatellites(e.Location) is { } satellite)
                SatelliteDoubleClicked?.Invoke(this, satellite);
        };
        MouseDown += OnMapMouseDown;
        MouseMove += OnMapMouseMove;
        MouseUp += (_, _) => EndPan();
        MouseLeave += (_, _) =>
        {
            EndPan();
            ClearMapHover();
        };
    }

    /// <summary>Find-SDR map only. Satellite tracking map stays a static world view (no overlay flicker).</summary>
    public void EnableZoomControls()
    {
        if (_zoomEnabled) return;
        _zoomEnabled = true;
        TabStop = true;
        MouseEnter += (_, _) =>
        {
            if (!Focused) Focus();
        };
        MouseWheel += OnMapMouseWheel;
        _zoomBar = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(6, 6),
            BackColor = Color.FromArgb(200, 8, 20, 28),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(2),
            Margin = Padding.Empty
        };
        _zoomBar.Controls.Add(MakeZoomButton("+", ZoomIn));
        _zoomBar.Controls.Add(MakeZoomButton("−", ZoomOut));
        _zoomBar.Controls.Add(MakeZoomButton("FIT", ResetView));
        Controls.Add(_zoomBar);
        _zoomBar.BringToFront();
        _zoomBar.PerformLayout();
        // AutoSize on FlowLayoutPanel can keep the designer default (≈200×100).
        var width = _zoomBar.Padding.Horizontal;
        var height = 0;
        foreach (Control child in _zoomBar.Controls)
        {
            width += child.Width + child.Margin.Horizontal;
            height = Math.Max(height, child.Height + child.Margin.Vertical);
        }
        height += _zoomBar.Padding.Vertical;
        _zoomBar.AutoSize = false;
        _zoomBar.Size = new Size(width, height);
    }

    private static Button MakeZoomButton(string text, Action click)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(text.Length > 1 ? 40 : 28, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 70, 88),
            ForeColor = Color.FromArgb(230, 240, 248),
            Font = new Font("Segoe UI Semibold", 8f),
            Margin = new Padding(1),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(90, 130, 150);
        button.Click += (_, _) => click();
        return button;
    }

    public void ZoomIn()
    {
        if (_zoom <= MinZoom + 0.01f && _observer is { } observer)
        {
            _viewLat = observer.LatitudeDegrees;
            _viewLon = observer.LongitudeDegrees;
            _zoom = Math.Clamp(_zoom * 1.7f, MinZoom, MaxZoom);
            Invalidate();
            return;
        }
        ZoomAt(_zoom * 1.7f);
    }

    public void ZoomOut() => ZoomAt(_zoom / 1.7f);

    public void ResetView()
    {
        _zoom = 1f;
        if (_observer is { } observer)
        {
            _viewLat = observer.LatitudeDegrees;
            _viewLon = observer.LongitudeDegrees;
        }
        Invalidate();
    }

    private void ZoomAt(float zoom, Point? anchor = null)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.01f) return;
        if (_zoom <= MinZoom + 0.01f && zoom > _zoom && anchor is null && _observer is { } observer)
        {
            _viewLat = observer.LatitudeDegrees;
            _viewLon = observer.LongitudeDegrees;
            _zoom = zoom;
            Invalidate();
            return;
        }
        var pixel = anchor ?? new Point(ClientSize.Width / 2, ClientSize.Height / 2);
        var (lat, lon) = FromGlobalPoint(pixel);
        _zoom = zoom;
        if (_zoom <= MinZoom + 0.01f)
        {
            _zoom = MinZoom;
            Invalidate();
            return;
        }
        var (lat2, lon2) = FromGlobalPoint(pixel);
        _viewLat = Math.Clamp(_viewLat + lat - lat2, -85d, 85d);
        _viewLon = WrapLon(_viewLon + lon - lon2);
        Invalidate();
    }

    private void OnMapMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _didPan = false;
        if (!_zoomEnabled || _zoom <= MinZoom + 0.01f) return;
        if (HitTestSite(e.Location) is not null || HitTestSatellites(e.Location) is not null) return;
        _panning = true;
        _panStart = e.Location;
        Cursor = Cursors.SizeAll;
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        Cursor = Cursors.Default;
    }

    private void OnMapMouseWheel(object? sender, MouseEventArgs e)
    {
        if (!_zoomEnabled) return;
        ZoomAt(e.Delta > 0 ? _zoom * 1.25f : _zoom / 1.25f, e.Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void OnMapMouseMove(object? sender, MouseEventArgs e)
    {
        if (_zoomEnabled && _panning)
        {
            var (lat0, lon0) = FromGlobalPoint(_panStart);
            var (lat1, lon1) = FromGlobalPoint(e.Location);
            _viewLat = Math.Clamp(_viewLat + lat0 - lat1, -85d, 85d);
            _viewLon = WrapLon(_viewLon + lon0 - lon1);
            _panStart = e.Location;
            _didPan = true;
            Invalidate();
            return;
        }

        if (HitTestSite(e.Location) is { } site)
        {
            if (_hoveredSite?.Id != site.Id)
            {
                _hoveredSite = site;
                _toolTip.SetToolTip(this, string.IsNullOrWhiteSpace(site.Detail) ? site.Title : $"{site.Title}\n{site.Detail}");
            }
            return;
        }

        if (HitTestSatellites(e.Location, iconOnly: true) is { IsAboveHorizon: false } satellite)
        {
            if (!ReferenceEquals(_hoveredSatellite, satellite))
            {
                _hoveredSatellite = satellite;
                _toolTip.SetToolTip(this, satellite.Name);
            }
            return;
        }

        ClearMapHover();
    }

    private void ClearMapHover()
    {
        if (_hoveredSatellite is null && _hoveredSite is null) return;
        _hoveredSatellite = null;
        _hoveredSite = null;
        _toolTip.SetToolTip(this, string.Empty);
    }

    /// <summary>
    /// When the handle is created, load the map beside the executable or download it.
    /// </summary>
    protected override async void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (!DesignMode)
        {
            await EnsureMapLoadedAsync();
        }
    }

    /// <summary>
    /// Load a local map when present; otherwise download and save it.
    /// </summary>
    public async Task EnsureMapLoadedAsync(string fileName = DefaultMapFileName, string downloadUrl = DefaultDownloadUrl)
    {
        // Path beside the executable.
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string localMapPath = Path.Combine(appDir, fileName);

        // Use the local file when it already exists.
        if (File.Exists(localMapPath))
        {
            try
            {
                string jsonText = await File.ReadAllTextAsync(localMapPath);
                LoadGeoJsonContent(jsonText);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read the local map file: {ex.Message}");
            }
        }

        if (!AllowNetworkDownload)
        {
            _statusMessage = "Local world map (world_map.geojson) was not found.";
            Invalidate();
            return;
        }

        // Download and save when the file is missing.
        await DownloadAndSaveMapAsync(localMapPath, downloadUrl);
    }

    /// <summary>
    /// Download the map and store it locally. A browser User-Agent avoids HTTP 429.
    /// </summary>
    private async Task DownloadAndSaveMapAsync(string savePath, string downloadUrl)
    {
        if (_isDownloading) return;

        _isDownloading = true;
        _statusMessage = "Downloading world map data...";
        Invalidate();

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            // Browser User-Agent so the CDN does not return HTTP 429.
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            string jsonContent = await httpClient.GetStringAsync(downloadUrl);

            // Save beside the executable so later runs can stay offline.
            await File.WriteAllTextAsync(savePath, jsonContent);

            // Load the map.
            LoadGeoJsonContent(jsonContent);
            _statusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Map download failed (check the internet connection)\n{ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Map download failed: {ex.Message}");
        }
        finally
        {
            _isDownloading = false;
            Invalidate();
        }
    }

    /// <summary>
    /// Parse a GeoJSON document.
    /// </summary>
    public void LoadGeoJsonContent(string jsonContent)
    {
        var parsedPolygons = new List<PointF[]>();

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateArray())
                {
                    if (!feature.TryGetProperty("geometry", out var geometry)) continue;
                    if (!geometry.TryGetProperty("type", out var typeProp)) continue;

                    string geometryType = typeProp.GetString() ?? "";
                    if (!geometry.TryGetProperty("coordinates", out var coordinates)) continue;

                    if (geometryType == "Polygon")
                    {
                        ParsePolygon(coordinates, parsedPolygons);
                    }
                    else if (geometryType == "MultiPolygon")
                    {
                        foreach (var polyElem in coordinates.EnumerateArray())
                        {
                            ParsePolygon(polyElem, parsedPolygons);
                        }
                    }
                }
            }

            _countryPolygons = parsedPolygons;
            Invalidate();
        }
        catch (Exception ex)
        {
            _statusMessage = "The map could not be parsed.";
            System.Diagnostics.Debug.WriteLine($"GeoJSON parse failed: {ex.Message}");
        }
    }

    private static void ParsePolygon(JsonElement polygonArray, List<PointF[]> output)
    {
        foreach (var ring in polygonArray.EnumerateArray())
        {
            var points = new List<PointF>();
            foreach (var coordPair in ring.EnumerateArray())
            {
                var elements = coordPair.EnumerateArray().ToArray();
                if (elements.Length >= 2)
                {
                    float lon = elements[0].GetSingle();
                    float lat = elements[1].GetSingle();
                    points.Add(new PointF(lon, lat));
                }
            }

            if (points.Count > 2)
            {
                output.Add(points.ToArray());
            }
        }
    }

    private int? _selectedNorad;
    private int? _soloNorad;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? SelectedNorad
    {
        get => _selectedNorad;
        set
        {
            if (_selectedNorad == value) return;
            _selectedNorad = value;
            Invalidate();
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? SoloNorad
    {
        get => _soloNorad;
        set
        {
            if (_soloNorad == value) return;
            _soloNorad = value;
            Invalidate();
        }
    }

    public void SetTrackingData(ObserverLocation observer, IEnumerable<SatelliteInfo> satellites)
    {
        _observer = observer;
        if (_zoom <= MinZoom + 0.01f)
        {
            _viewLat = observer.LatitudeDegrees;
            _viewLon = observer.LongitudeDegrees;
        }
        _trackedSatellites = satellites.Where(item => item.CurrentGeographicPosition is not null).ToList();
        _satellites = _trackedSatellites
            .Select(satellite => new SatelliteMapMarker(
                satellite.Name,
                satellite.CurrentGeographicPosition!.LatitudeDegrees,
                satellite.CurrentGeographicPosition.LongitudeDegrees,
                satellite.IsAboveHorizon))
            .ToList();
        Invalidate();
    }

    public void SetObserver(ObserverLocation? observer)
    {
        _observer = observer;
        if (_zoom <= MinZoom + 0.01f && observer is not null)
        {
            _viewLat = observer.LatitudeDegrees;
            _viewLon = observer.LongitudeDegrees;
        }
        Invalidate();
    }

    public void SetSiteMarkers(IReadOnlyList<SiteMapMarker> sites, string? selectedId = null)
    {
        _sites = sites ?? Array.Empty<SiteMapMarker>();
        _selectedSiteId = selectedId;
        Invalidate();
    }

    private SiteMapMarker? HitTestSite(Point location)
    {
        SiteMapMarker? best = null;
        var bestDistance = float.MaxValue;
        foreach (var (site, bounds) in _siteHits)
        {
            if (!bounds.Contains(location)) continue;
            var dx = location.X - (bounds.Left + bounds.Width / 2f);
            var dy = location.Y - (bounds.Top + bounds.Height / 2f);
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = site;
        }
        return best;
    }

    private SatelliteInfo? HitTestSatellites(Point location, bool iconOnly = false)
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

        if (best is not null || iconOnly) return best;

        foreach (var region in _hitRegions)
        {
            if (region.LabelBounds is { } label && label.Contains(location))
                return region.Satellite;
        }

        return null;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var g = eventArgs.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (ClientSize.Width < 2 || ClientSize.Height < 2)
            return;

        DrawMapGrid(g);
        DrawCountryVectorBorders(g);
        DrawObserver(g);
        DrawSites(g);
        DrawSatellites(g);
        if (ShowLegend) DrawMapOverlay(g);
    }

    private void DrawMapGrid(Graphics g)
    {
        using var gridPen = new Pen(Color.FromArgb(30, 90, 140, 180), 1) { DashStyle = DashStyle.Dot };
        using var fontBrush = new SolidBrush(Color.FromArgb(100, 150, 180));
        GetViewWindow(out var viewLat, out var viewLon, out var lonSpan, out var latSpan);
        var lonStep = _zoom >= 8f ? 5 : _zoom >= 3f ? 10 : 30;
        var latStep = lonStep;
        var lon0 = viewLon - lonSpan / 2d;
        var lon1 = viewLon + lonSpan / 2d;
        var lat0 = Math.Max(-90d, viewLat - latSpan / 2d);
        var lat1 = Math.Min(90d, viewLat + latSpan / 2d);

        for (var lon = Math.Floor(lon0 / lonStep) * lonStep; lon <= lon1 + 0.001; lon += lonStep)
        {
            var p1 = ToGlobalPoint(lat1, lon);
            var p2 = ToGlobalPoint(lat0, lon);
            g.DrawLine(gridPen, p1, p2);
            var wrapped = WrapLon(lon);
            if (Math.Abs(Math.Abs(wrapped) - 180d) < 0.01) continue;
            string label = wrapped == 0 ? "0°" : wrapped > 0 ? $"{wrapped:0}°E" : $"{Math.Abs(wrapped):0}°W";
            var labelY = ClientSize.Height - MapPad - 10;
            if (p1.X > MapPad && p1.X < ClientSize.Width - 36 && labelY > MapPad)
                g.DrawString(label, Font, fontBrush, p1.X - 10, labelY);
        }

        for (var lat = Math.Floor(lat0 / latStep) * latStep; lat <= lat1 + 0.001; lat += latStep)
        {
            if (lat is < -90 or > 90 || lat == 0) continue;
            var p1 = ToGlobalPoint(lat, lon0);
            var p2 = ToGlobalPoint(lat, lon1);
            g.DrawLine(gridPen, p1, p2);
            string label = lat > 0 ? $"{lat:0}°N" : $"{Math.Abs(lat):0}°S";
            var labelY = Math.Clamp(p1.Y - 7, MapPad, ClientSize.Height - MapPad - 12);
            if (labelY > MapPad && labelY < ClientSize.Height - MapPad)
                g.DrawString(label, Font, fontBrush, MapPad + 2, labelY);
        }
    }

    private void DrawCountryVectorBorders(Graphics g)
    {
        // Status text while the map is missing or still downloading.
        if (_countryPolygons.Count == 0 || _isDownloading)
        {
            string message = string.IsNullOrEmpty(_statusMessage) ? "Loading map..." : _statusMessage;
            using var fontBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            using var bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));

            var size = g.MeasureString(message, Font);
            float x = (ClientSize.Width - size.Width) / 2;
            float y = (ClientSize.Height - size.Height) / 2;

            g.FillRectangle(bgBrush, x - 10, y - 5, size.Width + 20, size.Height + 10);
            g.DrawString(message, Font, fontBrush, x, y);
            return;
        }

        using var landBrush = new SolidBrush(Color.FromArgb(28, 55, 82));
        using var borderPen = new Pen(Color.FromArgb(100, 160, 210, 255), 0.8f);
        g.SetClip(ClientRectangle);
        foreach (var poly in _countryPolygons)
            DrawCountryRing(g, poly, landBrush, borderPen);
        g.ResetClip();
    }

    private void DrawCountryRing(Graphics g, PointF[] geoRing, Brush landBrush, Pen borderPen)
    {
        if (geoRing.Length < 3) return;
        GetViewWindow(out var viewLat, out var viewLon, out var lonSpan, out var latSpan);
        var west = viewLon - lonSpan * 0.55d;
        var east = viewLon + lonSpan * 0.55d;
        var south = viewLat - latSpan * 0.55d;
        var north = viewLat + latSpan * 0.55d;

        var unwrapped = new PointF[geoRing.Length];
        for (var i = 0; i < geoRing.Length; i++)
        {
            var lon = geoRing[i].X;
            var lat = geoRing[i].Y;
            unwrapped[i] = new PointF((float)(viewLon + WrapLon(lon - viewLon)), lat);
        }

        foreach (var part in SplitRingOnSeam(unwrapped))
        {
            var clipped = ClipPolygonToRect(part, (float)west, (float)east, (float)south, (float)north);
            if (clipped.Count < 3) continue;
            var screen = new PointF[clipped.Count];
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            for (var i = 0; i < clipped.Count; i++)
            {
                screen[i] = ToGlobalPoint(clipped[i].Y, clipped[i].X);
                if (screen[i].X < minX) minX = screen[i].X;
                if (screen[i].X > maxX) maxX = screen[i].X;
            }
            if (maxX - minX > ClientSize.Width * 1.8f) continue;
            g.FillPolygon(landBrush, screen);
            g.DrawPolygon(borderPen, screen);
        }
    }

    private void GetViewWindow(out double viewLat, out double viewLon, out double lonSpan, out double latSpan)
    {
        if (_zoom <= MinZoom + 0.01f)
        {
            viewLat = 0;
            viewLon = 0;
            lonSpan = 360d;
            latSpan = 180d;
            return;
        }

        viewLat = _viewLat;
        viewLon = _viewLon;
        lonSpan = 360d / _zoom;
        latSpan = 180d / _zoom;
    }

    private static List<List<PointF>> SplitRingOnSeam(PointF[] ring)
    {
        var parts = new List<List<PointF>>();
        if (ring.Length == 0) return parts;
        var current = new List<PointF> { ring[0] };
        for (var i = 1; i < ring.Length; i++)
        {
            if (Math.Abs(ring[i].X - ring[i - 1].X) > 180f)
            {
                if (current.Count >= 3) parts.Add(current);
                current = [ring[i]];
            }
            else
                current.Add(ring[i]);
        }

        if (current.Count >= 3)
            parts.Add(current);
        if (parts.Count >= 2 && Math.Abs(parts[^1][^1].X - parts[0][0].X) <= 180f)
        {
            parts[^1].AddRange(parts[0]);
            parts.RemoveAt(0);
        }

        return parts;
    }

    private static List<PointF> ClipPolygonToRect(
        IReadOnlyList<PointF> input, float xMin, float xMax, float yMin, float yMax)
    {
        var output = ClipEdge(input, p => p.X >= xMin, (a, b) => LerpAtX(a, b, xMin));
        output = ClipEdge(output, p => p.X <= xMax, (a, b) => LerpAtX(a, b, xMax));
        output = ClipEdge(output, p => p.Y >= yMin, (a, b) => LerpAtY(a, b, yMin));
        output = ClipEdge(output, p => p.Y <= yMax, (a, b) => LerpAtY(a, b, yMax));
        return output;
    }

    private static List<PointF> ClipEdge(
        IReadOnlyList<PointF> input,
        Func<PointF, bool> inside,
        Func<PointF, PointF, PointF> intersect)
    {
        var output = new List<PointF>();
        if (input.Count == 0) return output;
        var prev = input[^1];
        var prevIn = inside(prev);
        foreach (var point in input)
        {
            var nowIn = inside(point);
            if (nowIn)
            {
                if (!prevIn) output.Add(intersect(prev, point));
                output.Add(point);
            }
            else if (prevIn)
                output.Add(intersect(prev, point));
            prev = point;
            prevIn = nowIn;
        }
        return output;
    }

    private static PointF LerpAtX(PointF a, PointF b, float x)
    {
        var t = Math.Abs(b.X - a.X) < 0.0001f ? 0f : (x - a.X) / (b.X - a.X);
        return new PointF(x, a.Y + (b.Y - a.Y) * t);
    }

    private static PointF LerpAtY(PointF a, PointF b, float y)
    {
        var t = Math.Abs(b.Y - a.Y) < 0.0001f ? 0f : (y - a.Y) / (b.Y - a.Y);
        return new PointF(a.X + (b.X - a.X) * t, y);
    }

    private void DrawObserver(Graphics g)
    {
        if (_observer is not { } observer) return;

        var center = ToGlobalPoint(observer.LatitudeDegrees, observer.LongitudeDegrees);
        var radius = EmphasizeObserver ? 11 : 6;

        using (var halo = new SolidBrush(Color.FromArgb(EmphasizeObserver ? 90 : 40, 255, 220, 80)))
            g.FillEllipse(halo, center.X - radius - 8, center.Y - radius - 8, (radius + 8) * 2, (radius + 8) * 2);
        using (var fill = new SolidBrush(Color.FromArgb(255, 235, 70)))
        using (var border = new Pen(Color.FromArgb(30, 20, 8), EmphasizeObserver ? 2.4f : 1.2f))
        using (var ring = new Pen(Color.White, EmphasizeObserver ? 2f : 1f))
        {
            g.FillEllipse(fill, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            g.DrawEllipse(ring, center.X - radius + 1, center.Y - radius + 1, (radius - 1) * 2, (radius - 1) * 2);
            g.DrawEllipse(border, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }

        var caption = string.IsNullOrWhiteSpace(ObserverLabel) ? "★ Home" : ObserverLabel;
        using var font = new Font("Segoe UI Semibold", EmphasizeObserver ? 9.5f : 8.5f);
        using var textBrush = new SolidBrush(Color.FromArgb(255, 245, 190));
        using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        var labelX = center.X + radius + 6;
        var labelY = center.Y - (EmphasizeObserver ? 10 : 7);
        g.DrawString(caption, font, shadow, labelX + 1, labelY + 1);
        g.DrawString(caption, font, textBrush, labelX, labelY);
    }

    private void DrawSites(Graphics g)
    {
        _siteHits.Clear();
        var showLabels = _zoom >= 3.2f;
        using var labelBrush = new SolidBrush(Color.FromArgb(220, 210, 230, 240));
        using var labelFont = new Font(Font.FontFamily, 7.5f, FontStyle.Regular);
        foreach (var site in _sites)
        {
            var center = ToGlobalPoint(site.Latitude, site.Longitude);
            if (center.X < -20 || center.Y < -20 || center.X > ClientSize.Width + 20 || center.Y > ClientSize.Height + 20)
                continue;
            var selected = site.Id == _selectedSiteId;
            var radius = selected ? 6f : 4f;
            var fill = selected ? Color.FromArgb(255, 210, 90) : Color.FromArgb(80, 200, 255);
            using (var brush = new SolidBrush(fill))
            using (var border = new Pen(Color.FromArgb(30, 20, 10), 1f))
            {
                g.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2, radius * 2);
                g.DrawEllipse(border, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            }
            if (showLabels)
            {
                var title = site.Title;
                if (title.Length > 28) title = title[..26] + "…";
                g.DrawString(title, labelFont, labelBrush, center.X + radius + 3, center.Y - 7);
                var labelSize = g.MeasureString(title, labelFont);
                _siteHits.Add((site, RectangleF.Union(
                    new RectangleF(center.X - 8, center.Y - 8, 16, 16),
                    new RectangleF(center.X + radius + 3, center.Y - 10, labelSize.Width, Math.Max(16, labelSize.Height)))));
            }
            else
                _siteHits.Add((site, new RectangleF(center.X - 8, center.Y - 8, 16, 16)));
        }
    }

    private void DrawSatellites(Graphics g)
    {
        _hitRegions.Clear();
        SatelliteInfo? selected = null;
        SatelliteInfo? solo = null;
        foreach (var satellite in _trackedSatellites)
        {
            if (satellite.NoradCatalogId is int norad && norad == _soloNorad)
            {
                solo = satellite;
                continue;
            }
            if (satellite.NoradCatalogId is int id && id == _selectedNorad)
            {
                selected = satellite;
                continue;
            }
            DrawSatelliteMarker(g, satellite, selected: false, solo: false);
        }
        if (selected is not null && selected.NoradCatalogId != _soloNorad)
            DrawSatelliteMarker(g, selected, selected: true, solo: false);
        if (solo is not null)
            DrawSatelliteMarker(g, solo, selected: solo.NoradCatalogId == _selectedNorad, solo: true);
    }

    private void DrawSatelliteMarker(Graphics g, SatelliteInfo satellite, bool selected, bool solo)
    {
        if (satellite.CurrentGeographicPosition is not { } geo) return;
        var center = ToGlobalPoint(geo.LatitudeDegrees, geo.LongitudeDegrees);
        var above = satellite.IsAboveHorizon;
        var scale = solo || selected ? 1.5f : above ? 1.15f : 0.85f;
        var color = solo
            ? Color.FromArgb(255, 220, 90, 255)
            : selected
                ? Color.FromArgb(255, 255, 145, 40)
                : above ? Color.LimeGreen : Color.FromArgb(210, 148, 163, 184);
        SatelliteGlyph.Draw(g, center.X, center.Y, color, scale);
        var iconBounds = SatelliteGlyph.IconBounds(center.X, center.Y, scale);
        if (solo || selected)
        {
            using var ring = new Pen(solo ? Color.FromArgb(255, 235, 140, 255) : Color.FromArgb(255, 255, 180, 70), 2.2f);
            g.DrawEllipse(ring, center.X - 12, center.Y - 12, 24, 24);
            if (solo)
                g.DrawRectangle(ring, center.X - 15, center.Y - 15, 30, 30);
        }

        if (!above && !selected && !solo)
        {
            _hitRegions.Add(new SatelliteHitRegion { Satellite = satellite, IconBounds = iconBounds });
            return;
        }

        var caption = solo ? $"SOLO · {satellite.Name}" : satellite.Name;
        using var labelFont = new Font(Font, solo || selected ? FontStyle.Bold : FontStyle.Regular);
        var nameSize = g.MeasureString(caption, labelFont);
        var labelX = center.X + 8;
        if (labelX + nameSize.Width > ClientSize.Width - MapPad)
            labelX = center.X - nameSize.Width - 8;
        var labelY = center.Y - nameSize.Height / 2f;
        labelY = Math.Clamp(labelY, MapPad, ClientSize.Height - MapPad - nameSize.Height);
        var labelBounds = new RectangleF(labelX, labelY, nameSize.Width, nameSize.Height);
        using var shadowBrush = new SolidBrush(Color.FromArgb(solo || selected ? 220 : 180, 0, 0, 0));
        using var textBrush = new SolidBrush(color);
        g.DrawString(caption, labelFont, shadowBrush, labelX + 1, labelY + 1);
        g.DrawString(caption, labelFont, textBrush, labelX, labelY);
        _hitRegions.Add(new SatelliteHitRegion
        {
            Satellite = satellite,
            IconBounds = iconBounds,
            LabelBounds = labelBounds
        });
    }

    private void DrawMapOverlay(Graphics g)
    {
        int x = ClientSize.Width - 150;
        int y = 8;

        using var bgBrush = new SolidBrush(Color.FromArgb(170, 10, 20, 35));
        using var borderPen = new Pen(Color.FromArgb(70, 255, 255, 255), 1);
        g.FillRectangle(bgBrush, x, y, 140, 45);
        g.DrawRectangle(borderPen, x, y, 140, 45);

        using var fontBrush = new SolidBrush(Color.White);
        using var activeBrush = new SolidBrush(Color.LimeGreen);
        using var inactiveBrush = new SolidBrush(Color.LightCoral);

        g.FillEllipse(activeBrush, x + 10, y + 10, 8, 8);
        g.DrawString("In view", Font, fontBrush, x + 24, y + 7);

        g.FillEllipse(inactiveBrush, x + 10, y + 26, 8, 8);
        g.DrawString("Below horizon", Font, fontBrush, x + 24, y + 23);
    }

    private PointF ToGlobalPoint(double latitude, double longitude)
    {
        var innerW = Math.Max(1, ClientSize.Width - MapPad * 2);
        var innerH = Math.Max(1, ClientSize.Height - MapPad * 2);
        if (_zoom <= MinZoom + 0.01f)
        {
            return new(
                MapPad + (float)((longitude + 180d) / 360d * innerW),
                MapPad + (float)((90d - latitude) / 180d * innerH));
        }

        var lonSpan = 360d / _zoom;
        var latSpan = 180d / _zoom;
        var dlon = WrapLon(longitude - _viewLon);
        var dlat = latitude - _viewLat;
        return new(
            MapPad + (float)((dlon / lonSpan + 0.5d) * innerW),
            MapPad + (float)((0.5d - dlat / latSpan) * innerH));
    }

    private (double Lat, double Lon) FromGlobalPoint(Point pixel)
    {
        var innerW = Math.Max(1, ClientSize.Width - MapPad * 2);
        var innerH = Math.Max(1, ClientSize.Height - MapPad * 2);
        var nx = (pixel.X - MapPad) / innerW;
        var ny = (pixel.Y - MapPad) / innerH;
        if (_zoom <= MinZoom + 0.01f)
            return (90d - ny * 180d, nx * 360d - 180d);

        var lonSpan = 360d / _zoom;
        var latSpan = 180d / _zoom;
        return (
            Math.Clamp(_viewLat + (0.5d - ny) * latSpan, -90d, 90d),
            WrapLon(_viewLon + (nx - 0.5d) * lonSpan));
    }

    private static double WrapLon(double longitude)
    {
        longitude %= 360d;
        if (longitude > 180d) longitude -= 360d;
        if (longitude < -180d) longitude += 360d;
        return longitude;
    }
}

// Satellite position marker.
public sealed record SatelliteMapMarker(string Name, double LatitudeDegrees, double LongitudeDegrees, bool IsAboveHorizon);

public sealed record SiteMapMarker(string Id, string Title, string Detail, double Latitude, double Longitude);