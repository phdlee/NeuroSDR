using NeuroSatellite.Controls;
using NeuroSatellite.Satellite;
using NeuroSDR.Hardware;
using System.Globalization;

namespace NeuroSDR.Controls;

internal sealed class RemoteSdrFindForm : Form
{
    private readonly ComboBox _protocol = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, FlatStyle = FlatStyle.Flat
    };
    private readonly TextBox _search = new()
    {
        Width = 220, BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = "Title, city, grid, URL…"
    };
    private readonly CheckBox _hf = BandCheck("HF");
    private readonly CheckBox _vhf = BandCheck("VHF");
    private readonly CheckBox _uhf = BandCheck("UHF");
    private readonly ComboBox _sort = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, FlatStyle = FlatStyle.Flat
    };
    private readonly Button _viewToggle;
    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle
    };
    private readonly OfflineWorldMapControl _map = new()
    {
        Dock = DockStyle.Fill, AllowNetworkDownload = false, ShowLegend = false
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0)
    };
    private readonly double _observerLat;
    private readonly double _observerLon;
    private readonly bool _haveObserver;
    private bool _showMap;
    private bool _syncingSelection;

    public RemoteSdrEntry? Selected { get; private set; }
    public bool DirectoryChanged { get; private set; }

    public RemoteSdrFindForm(
        IReadOnlyList<RemoteSdrEntry> entries,
        string protocol,
        string? currentUrl,
        double observerLat,
        double observerLon,
        bool haveObserver)
    {
        _ = entries;
        _observerLat = observerLat;
        _observerLon = observerLon;
        _haveObserver = haveObserver;
        Text = "Find SDR site";
        Size = new Size(960, 580);
        MinimumSize = new Size(720, 420);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 22, 30);
        ForeColor = Color.FromArgb(228, 238, 246);
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false;

        _viewToggle = Action("MAP");
        _viewToggle.Width = 72;
        _viewToggle.BackColor = Color.FromArgb(32, 46, 58);

        _protocol.Items.AddRange(["WebSDR", "KiwiSDR"]);
        _protocol.SelectedItem = protocol is "KiwiSDR" ? "KiwiSDR" : "WebSDR";
        _sort.Items.AddRange(["Website", "Location", "Distance", "Title"]);
        _sort.SelectedIndex = 0;

        _list.BackColor = Color.FromArgb(8, 16, 24);
        _list.ForeColor = Color.FromArgb(228, 238, 246);
        _list.Columns.Add("Title", 280);
        _list.Columns.Add("Bands", 260);
        _list.Columns.Add("Location", 180);
        _list.Columns.Add("km", 70);
        _list.Columns.Add("URL", 220);

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, Padding = new Padding(10, 8, 10, 4), WrapContents = false,
            BackColor = Color.FromArgb(12, 24, 34)
        };
        filters.Controls.Add(Hint("SITE"));
        filters.Controls.Add(_protocol);
        filters.Controls.Add(Hint("FIND"));
        filters.Controls.Add(_search);
        filters.Controls.Add(_hf);
        filters.Controls.Add(_vhf);
        filters.Controls.Add(_uhf);
        filters.Controls.Add(Hint("SORT"));
        filters.Controls.Add(_sort);
        filters.Controls.Add(_viewToggle);

        var ok = Action("SELECT");
        var cancel = Action("CANCEL");
        cancel.BackColor = Color.FromArgb(32, 46, 58);
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _list.DoubleClick += (_, _) => Accept();
        _list.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _list.HitTest(e.Location);
            if (hit.Item is null) return;
            hit.Item.Selected = true;
            hit.Item.Focused = true;
            PushMapMarkers();
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Edit…", null, (_, _) => EditSelected());
        menu.Items.Add("Delete", null, (_, _) => DeleteSelected());
        menu.Opening += (_, e) =>
        {
            if (CurrentEntry() is null) e.Cancel = true;
        };
        _list.ContextMenuStrip = menu;
        _map.ContextMenuStrip = menu;
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingSelection) return;
            PushMapMarkers();
        };
        _viewToggle.Click += (_, _) => ToggleView();
        _map.SiteSelected += (_, site) => SelectByUrl(site.Id, ensureVisible: false);
        _map.SiteDoubleClicked += (_, site) =>
        {
            SelectByUrl(site.Id, ensureVisible: false);
            Accept();
        };
        if (_haveObserver)
            _map.SetObserver(new ObserverLocation(_observerLat, _observerLon));

        var host = new Panel { Dock = DockStyle.Fill };
        _map.EnableZoomControls();
        _map.Visible = false;
        host.Controls.Add(_map);
        host.Controls.Add(_list);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(12, 20, 28) };
        footer.Controls.Add(_status);
        footer.Controls.Add(cancel);
        footer.Controls.Add(ok);
        footer.Resize += (_, _) =>
        {
            ok.Left = footer.ClientSize.Width - ok.Width - 12;
            cancel.Left = ok.Left - cancel.Width - 8;
            cancel.Top = ok.Top = 8;
            _status.Width = Math.Max(80, cancel.Left - 8);
        };

        Controls.Add(host);
        Controls.Add(footer);
        Controls.Add(filters);

        void Relist(object? _, EventArgs e) => Fill();
        _protocol.SelectedIndexChanged += Relist;
        _search.TextChanged += Relist;
        _hf.CheckedChanged += Relist;
        _vhf.CheckedChanged += Relist;
        _uhf.CheckedChanged += Relist;
        _sort.SelectedIndexChanged += Relist;
        Fill();
        SelectByUrl(currentUrl, ensureVisible: true);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void ToggleView()
    {
        _showMap = !_showMap;
        _list.Visible = !_showMap;
        _map.Visible = _showMap;
        _viewToggle.Text = _showMap ? "LIST" : "MAP";
        PushMapMarkers();
    }

    private void Fill()
    {
        var protocol = _protocol.SelectedItem?.ToString() ?? "WebSDR";
        var wanted = RemoteSdrSpectrum.None;
        if (_hf.Checked) wanted |= RemoteSdrSpectrum.Hf;
        if (_vhf.Checked) wanted |= RemoteSdrSpectrum.Vhf;
        if (_uhf.Checked) wanted |= RemoteSdrSpectrum.Uhf;
        var query = _search.Text.Trim();
        IEnumerable<RemoteSdrEntry> rows = RemoteSdrCatalog.LoadDirectory().Where(item => item.Protocol == protocol);
        if (wanted != RemoteSdrSpectrum.None)
            rows = rows.Where(item => RemoteSdrBands.Covers(item.Bands, wanted));
        if (query.Length > 0)
        {
            rows = rows.Where(item =>
                Contains(item.Name, query) || Contains(item.LocationSummary, query) ||
                Contains(item.BandSummary, query) || Contains(item.Url, query) ||
                Contains(item.Grid, query) || Contains(item.Country, query) || Contains(item.City, query));
        }
        var list = rows.ToList();
        list.Sort(CompareRows);
        var keep = CurrentUrl();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in list)
        {
            var km = Distance(entry);
            var item = new ListViewItem(entry.Name) { Tag = entry };
            item.SubItems.Add(entry.BandSummary);
            item.SubItems.Add(entry.LocationSummary);
            item.SubItems.Add(km is null ? "" : km.Value.ToString("0", CultureInfo.InvariantCulture));
            item.SubItems.Add(entry.Url);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        if (!string.IsNullOrWhiteSpace(keep))
            SelectByUrl(keep, ensureVisible: false);
        var mapped = list.Count(item => item.HasCoordinates);
        _status.Text = $"{protocol} · {list.Count} sites  ·  local directory" +
                       (_showMap ? $"  ·  {mapped} on map" : "");
        PushMapMarkers();
    }

    private int CompareRows(RemoteSdrEntry a, RemoteSdrEntry b)
    {
        switch (_sort.SelectedIndex)
        {
            case 1:
            {
                var loc = string.Compare(a.LocationSummary, b.LocationSummary, StringComparison.OrdinalIgnoreCase);
                if (loc != 0) return loc;
                var country = string.Compare(a.Country, b.Country, StringComparison.OrdinalIgnoreCase);
                if (country != 0) return country;
                break;
            }
            case 2 when _haveObserver:
            {
                var da = Distance(a);
                var db = Distance(b);
                if (da is null && db is null) break;
                if (da is null) return 1;
                if (db is null) return -1;
                var cmp = da.Value.CompareTo(db.Value);
                if (cmp != 0) return cmp;
                break;
            }
            case 3:
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
        var order = a.DirectoryOrder.CompareTo(b.DirectoryOrder);
        return order != 0 ? order : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private void PushMapMarkers()
    {
        var selected = CurrentUrl();
        var markers = new List<SiteMapMarker>();
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not RemoteSdrEntry entry || !entry.HasCoordinates) continue;
            markers.Add(new SiteMapMarker(
                entry.Url,
                entry.Name,
                $"{entry.LocationSummary}\n{entry.BandSummary}",
                entry.Latitude!.Value,
                entry.Longitude!.Value));
        }
        _map.SetSiteMarkers(markers, selected);
    }

    private string? CurrentUrl() => CurrentEntry()?.Url;

    private RemoteSdrEntry? CurrentEntry() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as RemoteSdrEntry : null;

    private void EditSelected()
    {
        if (CurrentEntry() is not { } entry) return;
        using var editor = new RemoteSdrEditForm(entry);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        RemoteSdrCatalog.Upsert(editor.Result, editor.OriginalUrl);
        DirectoryChanged = true;
        Fill();
        SelectByUrl(editor.Result.Url, ensureVisible: true);
    }

    private void DeleteSelected()
    {
        if (CurrentEntry() is not { } entry) return;
        var ask = MessageBox.Show(
            this,
            $"Delete {entry.Name} from the local directory?",
            "Delete SDR site",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (ask != DialogResult.Yes) return;
        RemoteSdrCatalog.RemoveByUrl(entry.Url);
        DirectoryChanged = true;
        Fill();
    }

    private void SelectByUrl(string? url, bool ensureVisible)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var key = url.Trim().TrimEnd('/');
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not RemoteSdrEntry entry ||
                !entry.Url.Trim().TrimEnd('/').Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            _syncingSelection = true;
            item.Selected = true;
            item.Focused = true;
            if (ensureVisible) item.EnsureVisible();
            _syncingSelection = false;
            PushMapMarkers();
            return;
        }
    }

    private void Accept()
    {
        if (_list.SelectedItems.Count == 0) return;
        Selected = _list.SelectedItems[0].Tag as RemoteSdrEntry;
        if (Selected is null) return;
        DialogResult = DialogResult.OK;
        Close();
    }

    private double? Distance(RemoteSdrEntry entry)
    {
        if (!_haveObserver || !entry.HasCoordinates) return null;
        return RemoteSdrGeo.DistanceKm(_observerLat, _observerLon, entry.Latitude!.Value, entry.Longitude!.Value);
    }

    private static bool Contains(string? hay, string needle) =>
        !string.IsNullOrEmpty(hay) && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static CheckBox BandCheck(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(200, 220, 232),
        FlatStyle = FlatStyle.Flat, Margin = new Padding(10, 4, 0, 0)
    };

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(120, 168, 190),
        Font = new Font("Segoe UI Semibold", 8f), Margin = new Padding(8, 8, 4, 0)
    };

    private static Button Action(string text)
    {
        var button = new Button
        {
            Text = text, Width = 96, Height = 30, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(62, 118, 164), ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 8.5f), UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
