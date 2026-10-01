using NeuroSatellite.Controls;
using NeuroSatellite.Satellite;
using NeuroSDR.Hardware;
using System.Collections.Concurrent;
using System.Globalization;

namespace NeuroSDR.Controls;

internal sealed record SatelliteCatalogPick(SatelliteInfo Satellite, RemoteSdrEntry? Site);

internal sealed class SatelliteCatalogDialog : Form
{
    private readonly SatelliteManagerControl _manager;
    private readonly IReadOnlyList<SatelliteInfo> _satellites;
    private readonly IReadOnlyList<RemoteSdrEntry> _sites;
    private readonly Dictionary<string, (RemoteSdrEntry Site, double Elevation)> _matches = new(StringComparer.Ordinal);
    private bool _matched;
    private bool _busy;
    private int _sortColumn = 0;
    private bool _sortAsc = true;

    private readonly TextBox _search = new()
    {
        Width = 200, BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = "Name, NORAD, mode…"
    };
    private readonly ComboBox _band = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _state = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FlatStyle = FlatStyle.Flat };
    private readonly Button _findSdr;
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, HeaderStyle = ColumnHeaderStyle.Clickable
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0)
    };

    public SatelliteCatalogPick? Pick { get; private set; }

    private SatelliteCatalogDialog(
        IReadOnlyList<SatelliteInfo> satellites,
        SatelliteManagerControl manager,
        IReadOnlyList<RemoteSdrEntry> sites)
    {
        _satellites = satellites;
        _manager = manager;
        _sites = sites.Where(item => item.HasCoordinates && item.Bands.Count > 0).ToList();
        Text = "Satellite catalog";
        Size = new Size(1100, 620);
        MinimumSize = new Size(820, 420);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 22, 30);
        ForeColor = Color.FromArgb(228, 238, 246);
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;

        _band.Items.AddRange(["All bands", "Has frequency", "HF", "VHF", "UHF"]);
        _band.SelectedIndex = 1;
        _state.Items.AddRange(["All status", "Active", "Heard", "Above horizon"]);
        _state.SelectedIndex = 0;

        _findSdr = Action("Find receivable SDR");
        _findSdr.Width = 168;
        _findSdr.Click += async (_, _) => await FindReceivableSitesAsync();

        _list.BackColor = Color.FromArgb(8, 16, 24);
        _list.ForeColor = Color.FromArgb(228, 238, 246);
        _list.Columns.Add("Satellite", 160);
        _list.Columns.Add("Status", 88);
        _list.Columns.Add("Active", 52);
        _list.Columns.Add("Heard", 52);
        _list.Columns.Add("RX", 120);
        _list.Columns.Add("Mode", 72);
        _list.Columns.Add("El°", 46, HorizontalAlignment.Right);
        _list.Columns.Add("Next", 92);
        _list.Columns.Add("NORAD", 64, HorizontalAlignment.Right);
        _list.Columns.Add("Receivable SDR", 240);

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 8, 10, 4), WrapContents = false,
            BackColor = Color.FromArgb(12, 24, 34)
        };
        filters.Controls.Add(Hint("FIND"));
        filters.Controls.Add(_search);
        filters.Controls.Add(Hint("BAND"));
        filters.Controls.Add(_band);
        filters.Controls.Add(Hint("STATUS"));
        filters.Controls.Add(_state);
        filters.Controls.Add(_findSdr);

        var go = Action("OPEN");
        var close = Action("CLOSE");
        close.BackColor = Color.FromArgb(32, 46, 58);
        go.Click += (_, _) => AcceptCurrent();
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _list.DoubleClick += (_, _) => AcceptCurrent();
        _list.ColumnClick += OnColumnClick;
        _search.TextChanged += (_, _) => Fill();
        _band.SelectedIndexChanged += (_, _) => Fill();
        _state.SelectedIndexChanged += (_, _) => Fill();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(12, 20, 28) };
        footer.Controls.Add(_status);
        footer.Controls.Add(close);
        footer.Controls.Add(go);
        footer.Resize += (_, _) =>
        {
            go.Left = footer.ClientSize.Width - go.Width - 12;
            close.Left = go.Left - close.Width - 8;
            close.Top = go.Top = 8;
            _status.Width = Math.Max(80, close.Left - 8);
        };

        Controls.Add(_list);
        Controls.Add(footer);
        Controls.Add(filters);
        AcceptButton = go;
        CancelButton = close;
        Fill();
        _status.Text = $"{_satellites.Count} satellites · {_sites.Count} mapped SDR sites  ·  Find receivable SDR matches frequency + footprint";
    }

    public static bool TryPick(
        IWin32Window owner,
        IReadOnlyList<SatelliteInfo> satellites,
        SatelliteManagerControl manager,
        IReadOnlyList<RemoteSdrEntry> sites,
        out SatelliteCatalogPick pick)
    {
        pick = null!;
        if (satellites.Count == 0) return false;
        using var dialog = new SatelliteCatalogDialog(satellites, manager, sites);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Pick is null) return false;
        pick = dialog.Pick;
        return true;
    }

    private void Fill()
    {
        var query = _search.Text.Trim();
        var rows = _satellites.Where(MatchesFilter).ToList();
        rows.Sort(CompareRows);
        var selected = CurrentSatellite()?.NoradCatalogId;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var satellite in rows)
        {
            var item = new ListViewItem(satellite.Name) { Tag = satellite };
            item.SubItems.Add(string.IsNullOrWhiteSpace(satellite.Status) ? "-" : satellite.Status);
            item.SubItems.Add(satellite.IsActive ? "Yes" : "");
            item.SubItems.Add(satellite.RecentlyHeard ? "Yes" : "");
            item.SubItems.Add(satellite.ReceiveFrequency);
            item.SubItems.Add(satellite.Radio?.DownlinkMode ?? "-");
            item.SubItems.Add(satellite.IsAboveHorizon ? $"{satellite.ElevationDegrees:F0}" : "-");
            item.SubItems.Add(satellite.NextVisibilityDisplay);
            item.SubItems.Add(satellite.NoradCatalogId?.ToString() ?? "-");
            item.SubItems.Add(FormatMatch(satellite));
            if (satellite.IsAboveHorizon)
                item.ForeColor = Color.FromArgb(160, 230, 190);
            else if (satellite.RecentlyHeard)
                item.ForeColor = Color.FromArgb(210, 200, 140);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        if (selected is int norad)
        {
            foreach (ListViewItem row in _list.Items)
            {
                if (row.Tag is SatelliteInfo info && info.NoradCatalogId == norad)
                {
                    row.Selected = true;
                    row.EnsureVisible();
                    break;
                }
            }
        }
        var matched = _matched ? _list.Items.Cast<ListViewItem>().Count(item => item.SubItems[9].Text.Length > 0) : 0;
        _status.Text = _busy
            ? _status.Text
            : $"{rows.Count} shown  ·  {_satellites.Count} total" +
              (_matched ? $"  ·  {matched} with a receivable SDR" : "  ·  SDR column empty until you press Find receivable SDR");
    }

    private bool MatchesFilter(SatelliteInfo satellite)
    {
        var query = _search.Text.Trim();
        if (query.Length > 0 &&
            !Contains(satellite.Name, query) &&
            !Contains(satellite.Status, query) &&
            !Contains(satellite.ReceiveFrequency, query) &&
            !Contains(satellite.Radio?.DownlinkMode, query) &&
            !(satellite.NoradCatalogId?.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;
        var hz = satellite.Radio?.GetReceiveFrequencyHz() ?? 0;
        switch (_band.SelectedIndex)
        {
            case 1 when !satellite.HasFrequencyInfo: return false;
            case 2 when hz is not > 0 || hz > 30_000_000: return false;
            case 3 when hz is not > 30_000_000 || hz > 300_000_000: return false;
            case 4 when hz is not > 300_000_000: return false;
        }
        return _state.SelectedIndex switch
        {
            1 => satellite.IsActive,
            2 => satellite.RecentlyHeard,
            3 => satellite.IsAboveHorizon,
            _ => true
        };
    }

    private int CompareRows(SatelliteInfo a, SatelliteInfo b)
    {
        var cmp = _sortColumn switch
        {
            1 => string.Compare(a.Status, b.Status, StringComparison.OrdinalIgnoreCase),
            2 => a.IsActive.CompareTo(b.IsActive),
            3 => a.RecentlyHeard.CompareTo(b.RecentlyHeard),
            4 => (a.Radio?.GetReceiveFrequencyHz() ?? 0).CompareTo(b.Radio?.GetReceiveFrequencyHz() ?? 0),
            5 => string.Compare(a.Radio?.DownlinkMode, b.Radio?.DownlinkMode, StringComparison.OrdinalIgnoreCase),
            6 => (a.ElevationDegrees ?? -90).CompareTo(b.ElevationDegrees ?? -90),
            7 => (a.NextVisibilityAt ?? DateTimeOffset.MaxValue).CompareTo(b.NextVisibilityAt ?? DateTimeOffset.MaxValue),
            8 => (a.NoradCatalogId ?? 0).CompareTo(b.NoradCatalogId ?? 0),
            9 => string.Compare(FormatMatch(a), FormatMatch(b), StringComparison.OrdinalIgnoreCase),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        };
        return _sortAsc ? cmp : -cmp;
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (_sortColumn == e.Column) _sortAsc = !_sortAsc;
        else
        {
            _sortColumn = e.Column;
            _sortAsc = e.Column is 4 or 6 or 7;
        }
        Fill();
    }

    private string FormatMatch(SatelliteInfo satellite)
    {
        if (!_matches.TryGetValue(Key(satellite), out var match)) return "";
        var proto = siteLabel(match.Site.Protocol);
        var name = match.Site.Name;
        if (name.Length > 28) name = name[..26] + "…";
        return $"{proto} · {name}  el {match.Elevation:0}°";

        static string siteLabel(string protocol) => protocol switch
        {
            "KiwiSDR" => "Kiwi",
            "OpenWebRX" => "OWRX",
            "WebSDR" => "Web",
            _ => protocol
        };
    }

    private async Task FindReceivableSitesAsync()
    {
        if (_busy) return;
        _busy = true;
        _findSdr.Enabled = false;
        _status.Text = "Matching satellites to mapped WebSDR / Kiwi sites…";
        try
        {
            var sites = _sites;
            var manager = _manager;
            var found = await Task.Run(() =>
            {
                var bag = new ConcurrentDictionary<string, (RemoteSdrEntry Site, double Elevation)>(StringComparer.Ordinal);
                Parallel.ForEach(_satellites, satellite =>
                {
                    if (!satellite.HasTle) return;
                    var hz = satellite.Radio?.GetReceiveFrequencyHz() ?? 0;
                    if (hz <= 0) return;
                    RemoteSdrEntry? best = null;
                    var bestEl = 0d;
                    foreach (var site in sites)
                    {
                        if (!RemoteSdrBands.ContainsHz(hz, site.Bands, 50_000)) continue;
                        try
                        {
                            var look = manager.GetLookAngle(satellite, new ObserverLocation(
                                site.Latitude!.Value, site.Longitude!.Value, site.AltitudeMeters ?? 50));
                            if (look.ElevationDegrees < 1) continue;
                            if (look.ElevationDegrees <= bestEl) continue;
                            bestEl = look.ElevationDegrees;
                            best = site;
                        }
                        catch
                        {
                            // TLE/site geometry failure
                        }
                    }
                    if (best is not null)
                        bag[Key(satellite)] = (best, bestEl);
                });
                return bag;
            }).ConfigureAwait(true);
            _matches.Clear();
            foreach (var pair in found)
                _matches[pair.Key] = pair.Value;
            _matched = true;
        }
        catch (Exception exception)
        {
            _status.Text = $"SDR match failed: {exception.Message}";
        }
        finally
        {
            _busy = false;
            _findSdr.Enabled = true;
            Fill();
        }
    }

    private void AcceptCurrent()
    {
        if (CurrentSatellite() is not { } satellite) return;
        RemoteSdrEntry? site = null;
        if (_matches.TryGetValue(Key(satellite), out var match))
            site = match.Site;
        Pick = new SatelliteCatalogPick(satellite, site);
        DialogResult = DialogResult.OK;
        Close();
    }

    private SatelliteInfo? CurrentSatellite()
        => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as SatelliteInfo : null;

    private static string Key(SatelliteInfo satellite)
        => satellite.NoradCatalogId is int norad ? norad.ToString(CultureInfo.InvariantCulture) : satellite.Name;

    private static bool Contains(string? text, string query) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(8, 6, 4, 0),
        ForeColor = Color.FromArgb(130, 165, 182), Font = new Font("Segoe UI Semibold", 7.5f)
    };

    private static Button Action(string text) => new()
    {
        Text = text, Width = 96, Height = 28, FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 66, 83), ForeColor = Color.FromArgb(222, 233, 240)
    };
}
