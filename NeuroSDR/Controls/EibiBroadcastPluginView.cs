using System.Globalization;
using NeuroSDR.Plugins;
using NeuroSDR.Plugins.Broadcast;

namespace NeuroSDR.Controls;

internal sealed class EibiBroadcastPluginView : UserControl, IAfResultView
{
    private readonly ComboBox _language = new();
    private readonly ComboBox _country = new();
    private readonly ComboBox _band = new();
    private readonly TextBox _search = new();
    private readonly CheckBox _onAir = new();
    private readonly CheckBox _mainWf = new();
    private readonly Button _mapFind = new();
    private readonly Button _aiScan = new();
    private readonly Button _download = new();
    private readonly Label _status = new();
    private readonly DataGridView _grid = new();
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 15_000 };
    private bool _suppress;
    private IReadOnlyList<EibiEntry> _rows = [];
    private string? _selectedKey;
    private int _scrollRow = -1;
    private int _lastOnAirMinute = -1;
    private readonly Dictionary<string, ScanMark> _scanMarks = new(StringComparer.Ordinal);
    private string? _languageChoice;
    private string? _countryChoice;
    private bool _aiScanning;

    public event Action? OptionsChanged;
    public event Action<EibiEntry>? TuneRequested;
    public event Action<bool>? AiScanRequested;

    public EibiBroadcastPluginView()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(12, 24, 34);
        ForeColor = Color.FromArgb(220, 230, 238);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 64,
            WrapContents = true,
            Padding = new Padding(6, 4, 6, 0),
            BackColor = Color.FromArgb(13, 31, 41)
        };
        StyleCombo(_language);
        StyleCombo(_country);
        StyleCombo(_band);
        _language.Width = 120;
        _country.Width = 120;
        _band.Width = 100;
        _band.Items.AddRange(["Shortwave", "Medium wave", "Longwave", "VLF", "All"]);
        _band.SelectedIndex = 0;
        StyleBox(_search);
        _search.Width = 140;
        _search.PlaceholderText = "Station / kHz";
        _onAir.Text = "On air now";
        _onAir.AutoSize = true;
        _onAir.ForeColor = ForeColor;
        _onAir.Checked = true;
        _mainWf.Text = "MAIN WF";
        _mainWf.AutoSize = true;
        _mainWf.ForeColor = ForeColor;
        StyleButton(_mapFind, "MAP", Color.FromArgb(35, 112, 153));
        StyleButton(_aiScan, "AI Scan", Color.FromArgb(42, 92, 58));
        _aiScan.Enabled = false;
        StyleButton(_download, "Download", Color.FromArgb(196, 112, 39));
        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(145, 181, 198);
        _status.Margin = new Padding(8, 8, 0, 0);
        bar.Controls.Add(Label("Band"));
        bar.Controls.Add(_band);
        bar.Controls.Add(Label("Lang"));
        bar.Controls.Add(_language);
        bar.Controls.Add(Label("Country"));
        bar.Controls.Add(_country);
        bar.Controls.Add(_search);
        bar.Controls.Add(_onAir);
        bar.Controls.Add(_mainWf);
        bar.Controls.Add(_mapFind);
        bar.Controls.Add(_aiScan);
        bar.Controls.Add(_status);

        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.FromArgb(8, 20, 28);
        _grid.ForeColor = ForeColor;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 52, 66);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(255, 193, 69);
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(8, 20, 28);
        _grid.DefaultCellStyle.ForeColor = ForeColor;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 80, 60);
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToResizeColumns = true;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 22;
        _grid.RowTemplate.Height = 22;
        _grid.RowTemplate.Resizable = DataGridViewTriState.False;
        _grid.RowHeadersVisible = false;
        _grid.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
        _grid.ReadOnly = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoGenerateColumns = false;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.DefaultCellStyle.Padding = new Padding(2, 0, 2, 0);
        typeof(DataGridView).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(_grid, true);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "kHz", HeaderText = "kHz", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Station", HeaderText = "Station", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Utc", HeaderText = "UTC", Width = 62 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Days", HeaderText = "Days", Width = 56 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Lang", HeaderText = "Lang", Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Country", HeaderText = "Country", Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = "Target", Width = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Scan", HeaderText = "Scan", Width = 48 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Site", HeaderText = "Site", Width = 110 });
        _grid.CellDoubleClick += (_, _) => TuneSelected();

        Controls.Add(_grid);
        Controls.Add(bar);

        _download.Click += async (_, _) => await DownloadAsync();
        _mapFind.Click += (_, _) => OpenMap();
        _aiScan.Click += (_, _) => ToggleAiScan();
        _language.SelectedIndexChanged += (_, _) => { if (!_suppress) Fire(); };
        _country.SelectedIndexChanged += (_, _) => { if (!_suppress) Fire(); };
        _band.SelectedIndexChanged += (_, _) => { if (!_suppress) Fire(); };
        _search.TextChanged += (_, _) => { if (!_suppress) RefreshGrid(); };
        _onAir.CheckedChanged += (_, _) => { if (!_suppress) Fire(); };
        _mainWf.CheckedChanged += (_, _) => { if (!_suppress) OptionsChanged?.Invoke(); };
        _clock.Tick += (_, _) =>
        {
            var minute = DateTime.UtcNow.Hour * 60 + DateTime.UtcNow.Minute;
            _status.Text = EibiScheduleStore.Status + $" · UTC {DateTime.UtcNow:HH:mm}";
            if (!_aiScanning && _onAir.Checked && minute != _lastOnAirMinute)
            {
                _lastOnAirMinute = minute;
                RefreshGrid();
            }
        };
        HandleCreated += (_, _) =>
        {
            EibiScheduleStore.LoadCacheIfPresent();
            RebuildFilters();
            ApplyFilterChoices();
            _clock.Start();
            _status.Text = EibiScheduleStore.Status;
            BeginInvoke(() =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                RefreshGrid();
            });
        };
        Disposed += (_, _) => _clock.Stop();
    }

    public Control AttachHostBar(ComboBox vfoBox)
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(13, 31, 41) };
        vfoBox.Location = new Point(3, 2);
        vfoBox.Size = new Size(112, 25);
        var title = new Label
        {
            Text = "Shortwave Schedule",
            Location = new Point(123, 3),
            AutoSize = false,
            Height = 21,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(104, 193, 222),
            Font = new Font("Segoe UI Semibold", 8f),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _download.AutoSize = false;
        _download.Size = new Size(86, 24);
        _download.Margin = Padding.Empty;
        _download.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        void PlaceDownload()
        {
            _download.Location = new Point(Math.Max(280, bar.ClientSize.Width - _download.Width - 4), 2);
            title.Width = Math.Max(80, _download.Left - title.Left - 8);
        }
        bar.Resize += (_, _) => PlaceDownload();
        bar.Controls.Add(vfoBox);
        bar.Controls.Add(title);
        bar.Controls.Add(_download);
        _download.BringToFront();
        PlaceDownload();
        return bar;
    }

    public Dictionary<string, string> BuildOptions() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["language"] = ComboText(_language),
        ["country"] = ComboText(_country),
        ["band"] = ComboText(_band),
        ["search"] = _search.Text.Trim(),
        ["onAirOnly"] = _onAir.Checked.ToString(),
        ["showOnMainWaterfall"] = _mainWf.Checked.ToString()
    };

    public void LoadOptions(IReadOnlyDictionary<string, string> options)
    {
        _suppress = true;
        if (options.TryGetValue("search", out var search)) _search.Text = search;
        if (options.TryGetValue("onAirOnly", out var air) && bool.TryParse(air, out var onAir))
            _onAir.Checked = onAir;
        if (options.TryGetValue("showOnMainWaterfall", out var wf) && bool.TryParse(wf, out var show))
            _mainWf.Checked = show;
        _languageChoice = options.GetValueOrDefault("language");
        _countryChoice = options.GetValueOrDefault("country");
        RebuildFilters();
        ApplyFilterChoices();
        SelectCombo(_band, options.GetValueOrDefault("band") ?? "Shortwave");
        _suppress = false;
        RefreshGrid();
    }

    public void ApplyResult(AfPluginResult result)
    {
        if (result.Kind.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            _status.Text = result.Text;
    }

    private async Task DownloadAsync()
    {
        _download.Enabled = false;
        _status.Text = "Downloading EiBi schedule…";
        try
        {
            var ok = await EibiScheduleStore.DownloadAsync().ConfigureAwait(true);
            RebuildFilters();
            RefreshGrid();
            _status.Text = EibiScheduleStore.Status;
            if (ok) OptionsChanged?.Invoke();
        }
        finally
        {
            _download.Enabled = true;
        }
    }

    private void Fire()
    {
        _languageChoice = ComboText(_language);
        _countryChoice = ComboText(_country);
        RefreshGrid();
        OptionsChanged?.Invoke();
    }

    private void RebuildFilters()
    {
        var keep = _suppress;
        _suppress = true;
        var entries = EibiScheduleStore.Entries;
        FillCombo(_language, entries.Select(e => string.IsNullOrEmpty(e.LanguageName) ? e.Language : e.LanguageName)
            .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        FillCombo(_country, entries.Select(e => EibiCodes.Country(e.Itu)).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        _suppress = keep;
    }

    private void ApplyFilterChoices()
    {
        var keep = _suppress;
        _suppress = true;
        SelectCombo(_language, _languageChoice);
        SelectCombo(_country, _countryChoice);
        _suppress = keep;
    }

    private void RefreshGrid()
    {
        CaptureNav();
        var utc = DateTime.UtcNow;
        BandRange(ComboText(_band), out var minHz, out var maxHz);
        _rows = EibiScheduleStore.Filter(ComboText(_language), ComboText(_country), _search.Text, _onAir.Checked, utc,
            minHz, maxHz);
        _grid.Rows.Clear();
        var limit = Math.Min(_rows.Count, 2_000);
        _grid.SuspendLayout();
        for (var i = 0; i < limit; i++)
        {
            var e = _rows[i];
            var on = EibiScheduleParser.IsOnAir(e, utc);
            var idx = _grid.Rows.Add(
                (e.FrequencyHz / 1000d).ToString("0.#", CultureInfo.InvariantCulture),
                e.Station,
                $"{Format(e.StartUtc)}-{Format(e.EndUtc)}",
                EibiCodes.Days(e.Days),
                string.IsNullOrEmpty(e.LanguageName) ? e.Language : e.LanguageName,
                EibiCodes.Country(e.Itu),
                e.Target,
                "",
                EibiCodes.Site(e.Site));
            _grid.Rows[idx].Tag = RowKey(e);
            _grid.Rows[idx].Height = 22;
            _grid.Rows[idx].Resizable = DataGridViewTriState.False;
            if (on) _grid.Rows[idx].DefaultCellStyle.ForeColor = Color.FromArgb(120, 230, 170);
            ApplyScanMark(idx, RowKey(e));
        }
        _grid.ResumeLayout();
        RestoreNav();
        if (_status.IsHandleCreated)
            _status.Text = $"{EibiScheduleStore.Status} · showing {limit:N0}" + (_rows.Count > limit ? "+" : "");
    }

    private void CaptureNav()
    {
        if (_grid.CurrentRow?.Index is >= 0 and var index && index < _rows.Count)
            _selectedKey = RowKey(_rows[index]);
        if (_grid.RowCount > 0 && _grid.FirstDisplayedScrollingRowIndex >= 0)
            _scrollRow = _grid.FirstDisplayedScrollingRowIndex;
    }

    private void RestoreNav()
    {
        if (_grid.RowCount == 0 || _grid.Columns.Count == 0) return;
        var row = 0;
        if (_selectedKey is not null)
        {
            for (var i = 0; i < _grid.RowCount; i++)
            {
                if (!string.Equals(_grid.Rows[i].Tag as string, _selectedKey, StringComparison.Ordinal)) continue;
                row = i;
                break;
            }
        }
        try
        {
            if (row >= 0 && row < _grid.RowCount && _grid.Rows[row].Visible)
                _grid.CurrentCell = _grid[0, row];
            if (!CanScrollGrid()) return;
            var scroll = _scrollRow >= 0 ? Math.Min(_scrollRow, _grid.RowCount - 1) : row;
            if (scroll >= 0 && scroll < _grid.RowCount)
                _grid.FirstDisplayedScrollingRowIndex = scroll;
        }
        catch (InvalidOperationException)
        {
            // Grid is still 0-height (tab just created / AF pane collapsed).
        }
        catch (ArgumentOutOfRangeException) { }
    }

    private bool CanScrollGrid()
    {
        if (!_grid.IsHandleCreated || !_grid.Visible) return false;
        var body = _grid.ClientSize.Height;
        if (_grid.ColumnHeadersVisible) body -= _grid.ColumnHeadersHeight;
        return body >= Math.Max(8, _grid.RowTemplate.Height);
    }

    private static string RowKey(EibiEntry e) =>
        $"{e.FrequencyHz}|{e.StartUtc.Ticks}|{e.Station}";

    internal static void BandRange(string band, out long minHz, out long maxHz)
    {
        minHz = 0;
        maxHz = 0;
        if (band.Equals("Shortwave", StringComparison.OrdinalIgnoreCase))
        {
            minHz = 2_300_000;
            maxHz = 30_000_000;
        }
        else if (band.Equals("Medium wave", StringComparison.OrdinalIgnoreCase))
        {
            minHz = 520_000;
            maxHz = 1_800_000;
        }
        else if (band.Equals("Longwave", StringComparison.OrdinalIgnoreCase))
        {
            minHz = 150_000;
            maxHz = 519_000;
        }
        else if (band.Equals("VLF", StringComparison.OrdinalIgnoreCase))
        {
            minHz = 10_000;
            maxHz = 149_000;
        }
    }

    private void OpenMap()
    {
        (double Lat, double Lon, string Label)? receiver = null;
        if (FindForm() is frmNeuroSDR host)
        {
            host.PrepareShortwaveMapReceiver();
            receiver = host.TryGetMapReceiverLocation();
        }
        using var form = new EibiBroadcastMapForm(_rows, _selectedKey, receiver);
        if (form.ShowDialog(FindForm()) != DialogResult.OK || form.Selected is not { } entry)
            return;
        _selectedKey = RowKey(entry);
        RestoreNav();
        TuneRequested?.Invoke(entry);
    }

    private void TuneSelected()
    {
        if (_grid.CurrentRow is null) return;
        var index = _grid.CurrentRow.Index;
        if (index < 0 || index >= _rows.Count) return;
        TuneRequested?.Invoke(_rows[index]);
    }

    private void FillCombo(ComboBox box, IEnumerable<string> items)
    {
        var selected = ComboText(box);
        box.BeginUpdate();
        box.Items.Clear();
        box.Items.Add("All");
        foreach (var item in items) box.Items.Add(item);
        box.EndUpdate();
        SelectCombo(box, selected);
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private static void SelectCombo(ComboBox box, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (box.Items.Count > 0) box.SelectedIndex = 0;
            return;
        }
        for (var i = 0; i < box.Items.Count; i++)
            if (string.Equals(box.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string ComboText(ComboBox box) =>
        box.SelectedItem?.ToString() ?? box.Text;

    public void SetAiScanAvailable(bool available)
    {
        if (_aiScanning) return;
        _aiScan.Enabled = available;
        PaintAiScan();
    }

    public void SetAiScanning(bool scanning)
    {
        _aiScanning = scanning;
        _aiScan.Enabled = true;
        _aiScan.Text = scanning ? "Stop scan" : "AI Scan";
        PaintAiScan();
    }

    public int SelectedRowIndex => _grid.CurrentRow?.Index ?? 0;

    public IReadOnlyList<EibiEntry> RowsFrom(int startIndex)
    {
        if (_rows.Count == 0) return [];
        startIndex = Math.Clamp(startIndex, 0, _rows.Count - 1);
        return _rows.Skip(startIndex).ToArray();
    }

    public void HighlightKey(string key)
    {
        SelectAndShow(key);
    }

    public bool SelectStation(long frequencyHz, string? station)
    {
        var name = (station ?? "").Trim();
        var named = -1;
        var any = -1;
        for (var i = 0; i < _rows.Count && i < _grid.RowCount; i++)
        {
            if (_rows[i].FrequencyHz != frequencyHz) continue;
            if (any < 0) any = i;
            if (name.Length > 0 &&
                _rows[i].Station.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                named = i;
                break;
            }
        }
        var row = named >= 0 ? named : any;
        if (row < 0) return false;
        SelectAndShow(RowKey(_rows[row]));
        return true;
    }

    public void WriteScan(string key, string text, Color color)
    {
        _scanMarks[key] = new ScanMark(text, color);
        for (var i = 0; i < _grid.RowCount; i++)
        {
            if (!string.Equals(_grid.Rows[i].Tag as string, key, StringComparison.Ordinal)) continue;
            ApplyScanMark(i, key);
            return;
        }
    }

    public void WriteScanForFrequency(long frequencyHz, string text, Color color)
    {
        var limit = Math.Min(_rows.Count, _grid.RowCount);
        for (var i = 0; i < limit; i++)
        {
            if (_rows[i].FrequencyHz != frequencyHz) continue;
            WriteScan(RowKey(_rows[i]), text, color);
        }
    }

    public void ClearScanFrom(int startIndex)
    {
        startIndex = Math.Max(0, startIndex);
        for (var i = startIndex; i < _rows.Count; i++)
            _scanMarks.Remove(RowKey(_rows[i]));
        for (var i = startIndex; i < _grid.RowCount; i++)
        {
            var key = _grid.Rows[i].Tag as string;
            if (key is null) continue;
            ApplyScanMark(i, key);
        }
    }

    private void SelectAndShow(string key)
    {
        _selectedKey = key;
        if (_grid.RowCount == 0) return;
        var row = -1;
        for (var i = 0; i < _grid.RowCount; i++)
        {
            if (!string.Equals(_grid.Rows[i].Tag as string, key, StringComparison.Ordinal)) continue;
            row = i;
            break;
        }
        if (row < 0) return;
        try
        {
            _grid.ClearSelection();
            if (_grid.Columns.Count > 0)
                _grid.CurrentCell = _grid[0, row];
            _grid.Rows[row].Selected = true;
            var visible = Math.Max(1, _grid.DisplayedRowCount(false));
            var top = Math.Clamp(row - visible / 3, 0, Math.Max(0, _grid.RowCount - 1));
            _grid.FirstDisplayedScrollingRowIndex = top;
            _scrollRow = top;
        }
        catch (InvalidOperationException) { }
        catch (ArgumentOutOfRangeException) { }
    }

    internal static string EntryKey(EibiEntry e) => $"{e.FrequencyHz}|{e.StartUtc.Ticks}|{e.Station}";

    private void ToggleAiScan()
    {
        AiScanRequested?.Invoke(!_aiScanning);
    }

    private void PaintAiScan()
    {
        _aiScan.BackColor = _aiScanning
            ? Color.FromArgb(154, 52, 42)
            : _aiScan.Enabled
                ? Color.FromArgb(42, 92, 58)
                : Color.FromArgb(40, 52, 58);
        _aiScan.ForeColor = _aiScan.Enabled || _aiScanning ? Color.White : Color.FromArgb(140, 156, 166);
    }

    private void ApplyScanMark(int rowIndex, string key)
    {
        if (rowIndex < 0 || rowIndex >= _grid.RowCount) return;
        var cell = _grid.Rows[rowIndex].Cells["Scan"];
        if (!_scanMarks.TryGetValue(key, out var mark))
        {
            cell.Value = "";
            cell.Style.ForeColor = _grid.DefaultCellStyle.ForeColor;
            return;
        }
        cell.Value = mark.Text;
        cell.Style.ForeColor = mark.Color;
        cell.Style.Font = new Font("Segoe UI Semibold", 8f);
    }

    private static string Format(TimeSpan value)
    {
        if (value >= TimeSpan.FromDays(1)) return "2400";
        return $"{(int)value.TotalHours:00}{value.Minutes:00}";
    }

    private static Label Label(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(145, 181, 198),
        Margin = new Padding(4, 8, 4, 0)
    };

    private static void StyleCombo(ComboBox box)
    {
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.BackColor = Color.FromArgb(5, 17, 24);
        box.ForeColor = Color.FromArgb(226, 235, 242);
        box.FlatStyle = FlatStyle.Flat;
        box.Margin = new Padding(0, 4, 8, 0);
    }

    private static void StyleBox(TextBox box)
    {
        box.BackColor = Color.FromArgb(5, 17, 24);
        box.ForeColor = Color.FromArgb(226, 235, 242);
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Margin = new Padding(0, 4, 8, 0);
    }

    private static void StyleButton(Button button, string text, Color color)
    {
        button.Text = text;
        button.AutoSize = true;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Margin = new Padding(4, 2, 4, 0);
    }

    private readonly record struct ScanMark(string Text, Color Color);
}
