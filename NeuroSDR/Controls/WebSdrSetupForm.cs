using NeuroSDR.Hardware;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class WebSdrSetupForm : Form
{
    private readonly ComboBox _protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _check = new() { Text = "Check Start", AutoSize = false, Size = new Size(120, 28) };
    private readonly Button _remaining = new() { Text = "Check Remaining", AutoSize = false, Size = new Size(140, 28) };
    private readonly Button _stop = new() { Text = "Stop", AutoSize = false, Size = new Size(120, 28), Enabled = false };
    private readonly Button _options = new() { Text = "Options", AutoSize = false, Size = new Size(100, 28) };
    private readonly AppSettings _settings;
    private readonly Label _progress = new() { AutoSize = false, Size = new Size(520, 20) };
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false
    };
    private List<RemoteSdrEntry> _entries = [];
    private CancellationTokenSource? _checkCancel;
    private bool _checking;

    public bool Changed { get; private set; }

    public WebSdrSetupForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Web SDR Sites";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 560);
        MinimumSize = new Size(760, 420);
        BackColor = Color.FromArgb(18, 31, 42);
        ForeColor = Color.FromArgb(220, 231, 239);
        Font = new Font("Segoe UI", 9f);

        _protocol.Items.AddRange(["KiwiSDR", "WebSDR", "OpenWebRX"]);
        _protocol.SelectedIndex = 0;
        _protocol.Location = new Point(12, 12);
        _protocol.Size = new Size(140, 24);
        _check.Location = new Point(168, 10);
        _remaining.Location = new Point(296, 10);
        _stop.Location = new Point(444, 10);
        _options.Location = new Point(572, 10);
        _progress.Location = new Point(12, 44);
        _progress.ForeColor = Color.FromArgb(151, 181, 198);
        _list.Location = new Point(12, 70);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.Size = new Size(896, 478);
        _list.BackColor = Color.FromArgb(8, 20, 28);
        _list.ForeColor = Color.FromArgb(210, 228, 240);
        _list.Columns.Add("Name", 220);
        _list.Columns.Add("Location", 180);
        _list.Columns.Add("Bands", 200);
        _list.Columns.Add("Result", 200);
        _list.Columns.Add("Checked", 110);
        _list.Columns.Add("Reception", 90);

        Controls.AddRange([_protocol, _check, _remaining, _stop, _options, _progress, _list]);
        _protocol.SelectedIndexChanged += (_, _) => { if (!_checking) Reload(); };
        _check.Click += async (_, _) => await StartCheckAsync(remaining: false);
        _remaining.Click += async (_, _) => await StartCheckAsync(remaining: true);
        _stop.Click += (_, _) => _checkCancel?.Cancel();
        _options.Click += (_, _) =>
        {
            using var options = new WebSdrSiteListOptionsForm(_settings);
            if (options.ShowDialog(this) == DialogResult.OK)
                Changed = true;
        };
        FormClosing += (_, e) =>
        {
            if (!_checking) return;
            _checkCancel?.Cancel();
        };
        Reload();
    }

    private string Protocol => _protocol.SelectedItem?.ToString() ?? "KiwiSDR";

    private void Reload()
    {
        _entries = RemoteSdrCatalog.LoadDirectory().Where(item => item.Protocol == Protocol).ToList();
        Fill();
    }

    private void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in _entries)
        {
            var check = RemoteSdrReachability.Find(entry.Url);
            var item = new ListViewItem(entry.Name) { Tag = entry };
            item.SubItems.Add(entry.LocationSummary);
            item.SubItems.Add(entry.BandSummary);
            item.SubItems.Add(ResultText(check));
            item.SubItems.Add(CheckedText(check));
            item.SubItems.Add(ScoreText(check));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        if (!_checking)
            _progress.Text = $"{Protocol}  {_entries.Count} sites";
    }

    private static string ResultText(RemoteSdrSiteCheck? check)
    {
        if (check is null) return "";
        return check.Reachable ? "Reachable" : $"Unreachable · {check.Detail}";
    }

    private static string CheckedText(RemoteSdrSiteCheck? check) =>
        check is null ? "" : check.CheckedUtc.LocalDateTime.ToString("yyyy-MM-dd");

    private static string ScoreText(RemoteSdrSiteCheck? check) =>
        check is { ReceiveScore: > 0 } ? check.ReceiveScore.ToString() : "";

    /// <summary>
    /// Continue the newest check date. Sites already stamped that day are skipped,
    /// and newly checked sites keep that same day so the run stays one set.
    /// </summary>
    private bool TryRemainingStart(out int start, out DateTimeOffset stamp)
    {
        start = 0;
        stamp = DateTimeOffset.Now;
        DateTimeOffset? latest = null;
        foreach (var entry in _entries)
        {
            var check = RemoteSdrReachability.Find(entry.Url);
            if (check is null) continue;
            if (latest is null || check.CheckedUtc > latest.Value)
                latest = check.CheckedUtc;
        }
        if (latest is null)
            return true;

        var batchDay = DateOnly.FromDateTime(latest.Value.LocalDateTime);
        stamp = latest.Value;
        for (var i = 0; i < _entries.Count; i++)
        {
            var check = RemoteSdrReachability.Find(_entries[i].Url);
            var day = check is null ? (DateOnly?)null : DateOnly.FromDateTime(check.CheckedUtc.LocalDateTime);
            if (day != batchDay)
            {
                start = i;
                return true;
            }
        }
        _progress.Text = $"{Protocol}  every site was checked on {batchDay:yyyy-MM-dd}";
        return false;
    }

    private async Task StartCheckAsync(bool remaining)
    {
        if (_checking) return;
        var stamp = DateTimeOffset.Now;
        var start = 0;
        if (remaining && !TryRemainingStart(out start, out stamp))
            return;

        _checking = true;
        _check.Enabled = false;
        _remaining.Enabled = false;
        _stop.Enabled = true;
        _protocol.Enabled = false;
        _checkCancel = new CancellationTokenSource();
        var token = _checkCancel.Token;
        var targets = _entries.ToList();
        var reachable = 0;
        try
        {
            for (var index = start; index < targets.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = targets[index];
                Mark(entry, "Checking");
                _progress.Text = $"{Protocol}  {index + 1} / {targets.Count}    reachable {reachable}    set {stamp.LocalDateTime:yyyy-MM-dd}";
                RemoteSdrProbeResult result;
                try
                {
                    result = await RemoteSdrSiteProbe.CheckAsync(entry, token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    Mark(entry, ResultText(RemoteSdrReachability.Find(entry.Url)));
                    break;
                }
                catch (Exception exception)
                {
                    result = new RemoteSdrProbeResult { Reachable = false, Detail = exception.GetBaseException().Message };
                }

                RemoteSdrReachability.Save(entry.Url, result.Reachable, result.Detail, stamp, result.ReceiveScore);
                Changed = true;
                if (result.Reachable)
                {
                    reachable++;
                    entry = ApplyLiveDetails(entry, result);
                    var at = _entries.FindIndex(item => RemoteSdrCatalog.SameSite(item.Url, entry.Url));
                    if (at >= 0) _entries[at] = entry;
                }
                Mark(entry, result.Reachable ? "Reachable" : $"Unreachable · {result.Detail}");
            }
        }
        finally
        {
            _checking = false;
            _check.Enabled = true;
            _remaining.Enabled = true;
            _stop.Enabled = false;
            _protocol.Enabled = true;
            _checkCancel.Dispose();
            _checkCancel = null;
            Fill();
        }
    }

    private RemoteSdrEntry ApplyLiveDetails(RemoteSdrEntry entry, RemoteSdrProbeResult result)
    {
        var bands = result.Bands is { Count: > 0 } ? result.Bands : entry.Bands;
        var lat = result.Latitude ?? entry.Latitude;
        var lon = result.Longitude ?? entry.Longitude;
        var alt = result.AltitudeMeters ?? entry.AltitudeMeters;
        var location = string.IsNullOrWhiteSpace(result.Location) ? entry.Location : result.Location;
        if (bands == entry.Bands && lat == entry.Latitude && lon == entry.Longitude &&
            alt == entry.AltitudeMeters && location == entry.Location)
            return entry;

        var updated = new RemoteSdrEntry
        {
            Name = entry.Name,
            Protocol = entry.Protocol,
            Url = entry.Url,
            Location = location,
            Country = entry.Country,
            City = entry.City,
            Grid = entry.Grid,
            Latitude = lat,
            Longitude = lon,
            AltitudeMeters = alt,
            Bands = bands,
            DirectoryOrder = entry.DirectoryOrder,
            SortScore = entry.SortScore
        };
        RemoteSdrCatalog.Upsert(updated);
        return RemoteSdrCatalog.FindByUrl(updated.Url) ?? updated;
    }

    private void Mark(RemoteSdrEntry entry, string result)
    {
        var row = FindRow(entry);
        if (row is null)
        {
            row = new ListViewItem(entry.Name) { Tag = entry };
            row.SubItems.Add(entry.LocationSummary);
            row.SubItems.Add(entry.BandSummary);
            row.SubItems.Add(result);
            row.SubItems.Add(CheckedText(RemoteSdrReachability.Find(entry.Url)));
            row.SubItems.Add(ScoreText(RemoteSdrReachability.Find(entry.Url)));
            _list.Items.Add(row);
        }
        else
        {
            row.Tag = entry;
            row.SubItems[1].Text = entry.LocationSummary;
            row.SubItems[2].Text = entry.BandSummary;
            row.SubItems[3].Text = result;
            var check = RemoteSdrReachability.Find(entry.Url);
            row.SubItems[4].Text = CheckedText(check);
            row.SubItems[5].Text = ScoreText(check);
        }
        row.EnsureVisible();
    }

    private ListViewItem? FindRow(RemoteSdrEntry entry)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is RemoteSdrEntry row && RemoteSdrCatalog.SameSite(row.Url, entry.Url))
                return item;
        }
        return null;
    }
}

internal sealed class WebSdrSiteListOptionsForm : Form
{
    private readonly CheckBox _reachableOnly = new() { AutoSize = true };
    private readonly CheckBox _preferReception = new() { AutoSize = true };
    private readonly AppSettings _settings;

    public WebSdrSiteListOptionsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Web SDR Site List";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 160);
        BackColor = Color.FromArgb(18, 31, 42);
        ForeColor = Color.FromArgb(220, 231, 239);
        Font = new Font("Segoe UI", 9f);

        _reachableOnly.Text = "Main site list shows reachable sites only";
        _reachableOnly.Checked = settings.RemoteSdrReachableOnly;
        _reachableOnly.Location = new Point(16, 16);
        _reachableOnly.ForeColor = ForeColor;

        _preferReception.Text = "Main site list puts better reception first";
        _preferReception.Checked = settings.RemoteSdrPreferReception;
        _preferReception.Location = new Point(16, 48);
        _preferReception.ForeColor = ForeColor;

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(228, 112),
            Size = new Size(80, 28)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(316, 112),
            Size = new Size(80, 28)
        };
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            _settings.RemoteSdrReachableOnly = _reachableOnly.Checked;
            _settings.RemoteSdrPreferReception = _preferReception.Checked;
            AppSettingsStore.Save(_settings);
        };
        Controls.AddRange([_reachableOnly, _preferReception, ok, cancel]);
    }
}
