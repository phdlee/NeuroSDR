using NeuroSatellite.Controls;
using NeuroSDR.Plugins.Broadcast;
using System.Globalization;

namespace NeuroSDR.Controls;

internal sealed class EibiBroadcastMapForm : Form
{
    private readonly OfflineWorldMapControl _map = new()
    {
        Dock = DockStyle.Fill, AllowNetworkDownload = false, ShowLegend = false
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0)
    };
    private readonly List<(SiteMapMarker Marker, EibiEntry Entry)> _placed = [];
    private string? _selectedId;

    public EibiEntry? Selected { get; private set; }

    public EibiBroadcastMapForm(
        IReadOnlyList<EibiEntry> entries,
        string? selectedKey,
        (double Lat, double Lon, string Label)? receiver = null)
    {
        Text = "Find shortwave station";
        Size = new Size(960, 580);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 22, 30);
        ForeColor = Color.FromArgb(228, 238, 246);
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;

        _selectedId = selectedKey;
        PlaceMarkers(entries);

        _map.EnableZoomControls();
        if (receiver is { } rx)
        {
            _map.EmphasizeObserver = true;
            _map.ObserverLabel = string.IsNullOrWhiteSpace(rx.Label) ? "RX" : $"RX · {rx.Label}";
            _map.SetObserver(new NeuroSatellite.Satellite.ObserverLocation(rx.Lat, rx.Lon));
        }
        _map.SiteSelected += (_, site) =>
        {
            _selectedId = site.Id;
            Selected = EntryFor(site.Id);
            PushMarkers();
        };
        _map.SiteDoubleClicked += (_, site) =>
        {
            Selected = EntryFor(site.Id);
            if (Selected is null) return;
            DialogResult = DialogResult.OK;
            Close();
        };

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0),
            BackColor = Color.FromArgb(12, 24, 34),
            ForeColor = Color.FromArgb(180, 205, 220),
            Text = "Same map as Find SDR / satellite · pan, wheel, + − FIT · double-click a marker to select"
        };

        var select = Action("SELECT");
        var cancel = Action("CANCEL");
        cancel.BackColor = Color.FromArgb(32, 46, 58);
        select.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(12, 20, 28) };
        footer.Controls.Add(_status);
        footer.Controls.Add(cancel);
        footer.Controls.Add(select);
        footer.Resize += (_, _) =>
        {
            select.Left = footer.ClientSize.Width - select.Width - 12;
            cancel.Left = select.Left - cancel.Width - 8;
            cancel.Top = select.Top = 8;
            _status.Width = Math.Max(80, cancel.Left - 8);
        };

        Controls.Add(_map);
        Controls.Add(footer);
        Controls.Add(hint);
        AcceptButton = select;
        CancelButton = cancel;
        PushMarkers();
        _status.Text = receiver is { } shown
            ? $"{_placed.Count:N0} transmitters  ·  RX at {shown.Label}"
            : $"{_placed.Count:N0} transmitters  ·  country center when site coordinates are unknown";
    }

    private void PlaceMarkers(IReadOnlyList<EibiEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var id = EibiTransmitterLocations.RowKey(entry);
            if (!seen.Add(id)) continue;
            if (!EibiTransmitterLocations.TryLocate(entry, out var lat, out var lon)) continue;
            var country = EibiCodes.Country(entry.Itu);
            var khz = (entry.FrequencyHz / 1000d).ToString("0.#", CultureInfo.InvariantCulture);
            var title = string.IsNullOrWhiteSpace(entry.Station) ? $"{khz} kHz" : entry.Station;
            var detail = $"{khz} kHz AM · {country}\n{EibiCodes.Days(entry.Days)} · {Format(entry.StartUtc)}-{Format(entry.EndUtc)} UTC";
            _placed.Add((new SiteMapMarker(id, title, detail, lat, lon), entry));
            if (_placed.Count >= 800) break;
        }
    }

    private void PushMarkers() =>
        _map.SetSiteMarkers(_placed.Select(item => item.Marker).ToArray(), _selectedId);

    private EibiEntry? EntryFor(string id)
    {
        foreach (var item in _placed)
        {
            if (item.Marker.Id.Equals(id, StringComparison.Ordinal))
                return item.Entry;
        }
        return null;
    }

    private void Accept()
    {
        if (Selected is null && _selectedId is not null)
            Selected = EntryFor(_selectedId);
        if (Selected is null) return;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string Format(TimeSpan value) =>
        value >= TimeSpan.FromDays(1) ? "2400" : $"{(int)value.TotalHours:00}{value.Minutes:00}";

    private static Button Action(string text)
    {
        var button = new Button
        {
            Text = text, Width = 88, Height = 28, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 112, 153), ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 8f)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 130, 160);
        return button;
    }
}
