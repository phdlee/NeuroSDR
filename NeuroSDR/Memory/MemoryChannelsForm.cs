using NeuroSDR.Core;

namespace NeuroSDR.Memory;

internal sealed class MemoryChannelsForm : Form
{
    private readonly MemoryChannelStore _store;
    private readonly Func<(long Frequency, RadioMode Mode, int Bandwidth)> _captureCurrent;
    private readonly Action<MemoryChannel> _recall;
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly Button _scanButton = new();
    private readonly NumericUpDown _dwell = new();
    private readonly NumericUpDown _threshold = new();
    private readonly CheckBox _hold = new();
    private readonly Button _rangeScanButton = new();
    private readonly NumericUpDown _rangeStart = new();
    private readonly NumericUpDown _rangeEnd = new();
    private readonly NumericUpDown _rangeStep = new();
    private readonly NumericUpDown _rangeDwell = new();

    public MemoryChannelsForm(MemoryChannelStore store,
        Func<(long Frequency, RadioMode Mode, int Bandwidth)> captureCurrent,
        Action<MemoryChannel> recall)
    {
        _store = store;
        _captureCurrent = captureCurrent;
        _recall = recall;
        Text = "NeuroSDR · MEMORY / SCAN";
        Size = new Size(820, 440);
        MinimumSize = new Size(720, 340);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(20, 38, 50);
        ForeColor = Color.FromArgb(220, 230, 237);
        Font = new Font("Segoe UI", 9f);
        BuildInterface();
        ReloadRows();
    }

    public event Action<IReadOnlyList<MemoryChannel>, int, bool, float>? ScanRequested;
    public event Action<long, long, long, int, bool, float>? RangeScanRequested;
    public event Action? ScanStopRequested;

    private void BuildInterface()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 43, Padding = new Padding(6), WrapContents = false,
            BackColor = Color.FromArgb(15, 30, 41)
        };
        var storeButton = MakeButton("STORE", Color.FromArgb(30, 116, 145));
        var recallButton = MakeButton("RECALL", Color.FromArgb(46, 93, 119));
        var deleteButton = MakeButton("DELETE", Color.FromArgb(126, 54, 60));
        _scanButton.Text = "▶ SCAN";
        StyleButton(_scanButton, Color.FromArgb(35, 112, 153));
        _dwell.Minimum = 100;
        _dwell.Maximum = 10_000;
        _dwell.Increment = 100;
        _dwell.Value = 800;
        _dwell.Width = 62;
        _threshold.Minimum = -140;
        _threshold.Maximum = 0;
        _threshold.Value = -75;
        _threshold.Width = 55;
        _hold.Text = "HOLD";
        _hold.Checked = true;
        _hold.AutoSize = true;
        _hold.ForeColor = ForeColor;
        _hold.Padding = new Padding(2, 5, 0, 0);
        toolbar.Controls.AddRange([
            storeButton, recallButton, deleteButton, _scanButton,
            SmallLabel("DWELL"), _dwell, SmallLabel("ms"),
            SmallLabel("THR"), _threshold, SmallLabel("dB"), _hold
        ]);

        var rangeToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 43, Padding = new Padding(6), WrapContents = false,
            BackColor = Color.FromArgb(18, 35, 47)
        };
        ConfigureFrequencyBox(_rangeStart, 7.000000m, 94);
        ConfigureFrequencyBox(_rangeEnd, 7.100000m, 94);
        _rangeStep.DecimalPlaces = 3;
        _rangeStep.Minimum = .001m;
        _rangeStep.Maximum = 100_000;
        _rangeStep.Value = 2.500m;
        _rangeStep.Width = 74;
        _rangeDwell.Minimum = 100;
        _rangeDwell.Maximum = 10_000;
        _rangeDwell.Increment = 100;
        _rangeDwell.Value = 500;
        _rangeDwell.Width = 62;
        _rangeScanButton.Text = "RANGE";
        _rangeScanButton.Width = 72;
        _rangeScanButton.Height = 29;
        StyleButton(_rangeScanButton, Color.FromArgb(93, 76, 156));
        rangeToolbar.Controls.AddRange([
            SmallLabel("START MHz"), _rangeStart, SmallLabel("END MHz"), _rangeEnd,
            SmallLabel("STEP kHz"), _rangeStep, SmallLabel("DWELL"), _rangeDwell, SmallLabel("ms"), _rangeScanButton
        ]);

        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.FromArgb(24, 52, 67);
        _grid.GridColor = Color.FromArgb(84, 121, 139);
        _grid.BorderStyle = BorderStyle.None;
        _grid.RowHeadersVisible = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(17, 38, 51), ForeColor = Color.White, SelectionBackColor = Color.FromArgb(17, 38, 51)
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(28, 62, 79), ForeColor = Color.FromArgb(225, 234, 239),
            SelectionBackColor = Color.FromArgb(35, 111, 140), SelectionForeColor = Color.White
        };
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(24, 55, 70);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Description", FillWeight = 145 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Frequency", HeaderText = "Frequency MHz", FillWeight = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mode", HeaderText = "Mode", FillWeight = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bandwidth", HeaderText = "BW Hz", FillWeight = 65 });

        _status.Dock = DockStyle.Bottom;
        _status.Height = 27;
        _status.Padding = new Padding(8, 5, 0, 0);
        _status.ForeColor = Color.FromArgb(137, 179, 197);
        _status.BackColor = Color.FromArgb(15, 30, 41);

        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(rangeToolbar);
        Controls.Add(toolbar);

        storeButton.Click += (_, _) => StoreCurrent();
        recallButton.Click += (_, _) => RecallSelected();
        deleteButton.Click += (_, _) => DeleteSelected();
        _scanButton.Click += (_, _) => ToggleScan();
        _rangeScanButton.Click += (_, _) => ToggleRangeScan();
        _grid.CellDoubleClick += (_, eventArgs) => { if (eventArgs.RowIndex >= 0) RecallSelected(); };
        _grid.CellEndEdit += (_, eventArgs) => UpdateEditedRow(eventArgs.RowIndex);
        FormClosing += (_, eventArgs) =>
        {
            if (eventArgs.CloseReason != CloseReason.UserClosing) return;
            eventArgs.Cancel = true;
            Hide();
        };
    }

    private void StoreCurrent()
    {
        var current = _captureCurrent();
        var channel = _store.Add($"M{_store.Snapshot().Count + 1:000}", current.Frequency, current.Mode, current.Bandwidth);
        ReloadRows(channel.Id);
        _status.Text = $"Saved · {channel.Frequency / 1_000_000d:0.000000} MHz {channel.Mode}";
    }

    private void RecallSelected()
    {
        if (SelectedChannel() is not { } channel) return;
        _recall(channel);
        _status.Text = $"Recalled · {channel.Name} · {channel.Frequency / 1_000_000d:0.000000} MHz";
    }

    private void DeleteSelected()
    {
        if (SelectedChannel() is not { } channel) return;
        _store.Remove(channel.Id);
        ReloadRows();
        _status.Text = $"Deleted · {channel.Name}";
    }

    private void ToggleScan()
    {
        if (_scanButton.Text.Contains("STOP", StringComparison.Ordinal))
        {
            ScanStopRequested?.Invoke();
            SetScanning(false);
            return;
        }
        var channels = _store.Snapshot();
        if (channels.Count == 0)
        {
            _status.Text = "There are no memory channels to scan.";
            return;
        }
        ScanRequested?.Invoke(channels, (int)_dwell.Value, _hold.Checked, (float)_threshold.Value);
        SetScanning(true);
    }

    public void SetScanning(bool scanning)
    {
        _scanButton.Text = scanning ? "■ STOP" : "▶ SCAN";
        _scanButton.BackColor = scanning ? Color.FromArgb(170, 62, 68) : Color.FromArgb(35, 112, 153);
    }

    public void SetRangeScanning(bool scanning)
    {
        _rangeScanButton.Text = scanning ? "STOP" : "RANGE";
        _rangeScanButton.BackColor = scanning ? Color.FromArgb(170, 62, 68) : Color.FromArgb(93, 76, 156);
    }

    public void UpdateRangeScanStatus(long? frequency, float signalDb, bool holding)
    {
        if (frequency is null) return;
        _status.Text = $"{(holding ? "RANGE HOLD" : "RANGE SCAN")}  ·  {frequency.Value / 1_000_000d:0.000000} MHz  ·  {signalDb:0.0} dB";
    }

    private void ToggleRangeScan()
    {
        if (_rangeScanButton.Text == "STOP")
        {
            ScanStopRequested?.Invoke();
            SetRangeScanning(false);
            return;
        }
        var start = (long)Math.Round(_rangeStart.Value * 1_000_000m);
        var end = (long)Math.Round(_rangeEnd.Value * 1_000_000m);
        var step = (long)Math.Round(_rangeStep.Value * 1_000m);
        RangeScanRequested?.Invoke(start, end, step, (int)_rangeDwell.Value, _hold.Checked, (float)_threshold.Value);
        SetRangeScanning(true);
    }

    public void UpdateScanStatus(MemoryChannel? channel, float signalDb, bool holding)
    {
        if (channel is null) return;
        _status.Text = $"{(holding ? "SCAN HOLD" : "SCAN")} · {channel.Name} · {channel.Frequency / 1_000_000d:0.000000} MHz · {signalDb:0.0} dB";
    }

    private void UpdateEditedRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count || _grid.Rows[rowIndex].Tag is not MemoryChannel original) return;
        var row = _grid.Rows[rowIndex];
        var name = Convert.ToString(row.Cells["Name"].Value)?.Trim();
        var frequencyText = Convert.ToString(row.Cells["Frequency"].Value);
        var modeText = Convert.ToString(row.Cells["Mode"].Value);
        var bandwidthText = Convert.ToString(row.Cells["Bandwidth"].Value);
        if (string.IsNullOrWhiteSpace(name) || !double.TryParse(frequencyText, out var mhz) ||
            !Enum.TryParse<RadioMode>(modeText, true, out var mode) || !int.TryParse(bandwidthText, out var bandwidth) ||
            mhz <= 0 || bandwidth is < 100 or > 500_000)
        {
            _status.Text = "The channel values were invalid and have been restored.";
            ReloadRows(original.Id);
            return;
        }
        var updated = original with { Name = name, Frequency = (long)Math.Round(mhz * 1_000_000), Mode = mode, Bandwidth = bandwidth };
        _store.Update(updated);
        ReloadRows(updated.Id);
    }

    private MemoryChannel? SelectedChannel() => _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag as MemoryChannel;

    private void ReloadRows(Guid? selectId = null)
    {
        _grid.Rows.Clear();
        foreach (var channel in _store.Snapshot())
        {
            var index = _grid.Rows.Add(channel.Name, $"{channel.Frequency / 1_000_000d:0.000000}", channel.Mode, channel.Bandwidth);
            _grid.Rows[index].Tag = channel;
            if (channel.Id == selectId) _grid.Rows[index].Selected = true;
        }
        _status.Text = _store.LastError is null ? $"{_grid.Rows.Count} memory channels" : $"Save error · {_store.LastError}";
    }

    private static Button MakeButton(string text, Color color)
    {
        var button = new Button { Text = text, Width = 66, Height = 29 };
        StyleButton(button, color);
        return button;
    }

    private static void ConfigureFrequencyBox(NumericUpDown box, decimal value, int width)
    {
        box.DecimalPlaces = 6;
        box.Minimum = .001m;
        box.Maximum = 9_000m;
        box.Increment = .001m;
        box.Value = value;
        box.Width = width;
    }

    private static Label SmallLabel(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(150, 181, 195), Padding = new Padding(3, 7, 0, 0)
    };

    private static void StyleButton(Button button, Color color)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 8f);
    }
}
