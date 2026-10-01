using System.ComponentModel;
using NeuroSDR.Core;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class SmartRecordForm : Form
{
    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyList<(string Id, string Name)>> _listVfos;
    private readonly BindingList<SmartRecordJob> _jobs;
    private readonly BindingSource _binding = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _name = new();
    private readonly CheckBox _enabled = new();
    private readonly ComboBox _kind = new();
    private readonly ComboBox _vfo = new();
    private readonly DateTimePicker _start = new();
    private readonly DateTimePicker _end = new();
    private readonly CheckedListBox _days = new();
    private readonly NumericUpDown _tuneHz = new();
    private readonly ComboBox _tuneMode = new();
    private readonly NumericUpDown _tuneBw = new();
    private readonly CheckBox _pileup = new();
    private readonly NumericUpDown _pileupRange = new();
    private readonly NumericUpDown _pileupTrig = new();
    private readonly TextBox _callContains = new();
    private readonly TextBox _prefix = new();
    private readonly TextBox _msgContains = new();
    private readonly CheckBox _cqOnly = new();
    private readonly TextBox _outputRoot = new();
    private bool _suppress;
    private bool _committed;

    public SmartRecordForm(AppSettings settings, Func<IReadOnlyList<(string Id, string Name)>> listVfos)
    {
        _settings = settings;
        _listVfos = listVfos;
        Text = "Smart Record";
        Width = 980;
        Height = 680;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 27, 37);
        ForeColor = Color.FromArgb(216, 226, 236);
        Font = new Font("Segoe UI", 9f);
        MinimizeBox = false;

        settings.SmartRecordJobs ??= [];
        if (string.IsNullOrWhiteSpace(settings.SmartRecordOutputFolder))
            settings.SmartRecordOutputFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeuroSDR", "SmartRecord");

        _jobs = new BindingList<SmartRecordJob>(settings.SmartRecordJobs.Select(j => j.Clone()).ToList());
        _binding.DataSource = _jobs;
        BuildUi();
        LoadVfos();
        SetClock(_start, "00:00", TimeSpan.Zero);
        SetClock(_end, "01:00", TimeSpan.FromHours(1));
        if (_jobs.Count > 0) _binding.Position = 0;
        ShowSelected();
        _binding.CurrentChanged += (_, _) => ShowSelected();
    }

    public List<SmartRecordJob> ResultJobs => _jobs.Select(j => j.Clone()).ToList();
    public string ResultOutputFolder => _outputRoot.Text.Trim();
    public bool Committed => _committed;

    public event Action? JobsChanged;

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12),
            BackColor = BackColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.FromArgb(8, 20, 28);
        _grid.ForeColor = ForeColor;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 52, 66);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(255, 193, 69);
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(8, 20, 28);
        _grid.DefaultCellStyle.ForeColor = ForeColor;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(50, 80, 60);
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.DataSource = _binding;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Enabled", HeaderText = "On", Width = 36 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Kind", HeaderText = "Kind", Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "VfoId", HeaderText = "VFO", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "StartTime", HeaderText = "Start", Width = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "EndTime", HeaderText = "End", Width = 55 });

        var editor = BuildEditor();
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var add = MakeButton("ADD", Color.FromArgb(35, 66, 83));
        var del = MakeButton("DEL", Color.FromArgb(70, 48, 58));
        add.Click += (_, _) => AddJob();
        del.Click += (_, _) => DeleteJob();
        leftButtons.Controls.Add(add);
        leftButtons.Controls.Add(del);

        var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = MakeButton("OK", Color.FromArgb(196, 112, 39));
        var apply = MakeButton("APPLY", Color.FromArgb(35, 90, 70));
        var cancel = MakeButton("CANCEL", Color.FromArgb(47, 66, 78));
        ok.Click += (_, _) =>
        {
            CommitSelected();
            _committed = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        apply.Click += (_, _) =>
        {
            CommitSelected();
            _committed = true;
            RaiseJobsChanged();
        };
        cancel.Click += (_, _) =>
        {
            _committed = false;
            DialogResult = DialogResult.Cancel;
            Close();
        };
        rightButtons.Controls.Add(ok);
        rightButtons.Controls.Add(apply);
        rightButtons.Controls.Add(cancel);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        bottom.Controls.Add(leftButtons, 0, 0);
        bottom.Controls.Add(rightButtons, 1, 0);

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_grid);
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(editor, 1, 0);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);
        CancelButton = cancel;
    }

    private Control BuildEditor()
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 0, 0, 0) };
        var y = 0;
        void Label(string text)
        {
            panel.Controls.Add(new Label
            {
                Text = text, Location = new Point(0, y), AutoSize = true,
                ForeColor = Color.FromArgb(145, 181, 198)
            });
            y += 18;
        }
        void Place(Control c, int height = 26)
        {
            c.Location = new Point(0, y);
            c.Width = 480;
            panel.Controls.Add(c);
            y += height + 8;
        }

        Label("NAME");
        StyleBox(_name); Place(_name);
        _enabled.Text = "Enabled"; _enabled.ForeColor = ForeColor; _enabled.AutoSize = true; Place(_enabled, 22);

        Label("KIND");
        _kind.DropDownStyle = ComboBoxStyle.DropDownList;
        _kind.Items.AddRange(["Audio", "FtxReport"]);
        StyleCombo(_kind); Place(_kind);
        _kind.SelectedIndexChanged += (_, _) => UpdateKindVisibility();

        Label("VFO TARGET");
        _vfo.DropDownStyle = ComboBoxStyle.DropDownList;
        StyleCombo(_vfo); Place(_vfo);

        Label("SCHEDULE (local HH:mm)");
        var timeRow = new FlowLayoutPanel { Height = 30, Width = 480, FlowDirection = FlowDirection.LeftToRight };
        StyleClock(_start);
        StyleClock(_end);
        timeRow.Controls.Add(new Label { Text = "Start", AutoSize = true, ForeColor = ForeColor, Margin = new Padding(0, 6, 6, 0) });
        timeRow.Controls.Add(_start);
        timeRow.Controls.Add(new Label { Text = "End", AutoSize = true, ForeColor = ForeColor, Margin = new Padding(12, 6, 6, 0) });
        timeRow.Controls.Add(_end);
        Place(timeRow, 32);

        Label("DAYS (none = every day)");
        _days.Items.AddRange(["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]);
        _days.Height = 110; _days.Width = 200;
        _days.BackColor = Color.FromArgb(5, 17, 24);
        _days.ForeColor = ForeColor;
        _days.CheckOnClick = true;
        Place(_days, 118);

        Label("OUTPUT FOLDER");
        var folderRow = new FlowLayoutPanel { Height = 30, Width = 480, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        StyleBox(_outputRoot);
        _outputRoot.Width = 400;
        _outputRoot.Margin = new Padding(0, 0, 8, 0);
        var browse = MakeButton("…", Color.FromArgb(35, 66, 83));
        browse.Size = new Size(40, 26);
        browse.Margin = Padding.Empty;
        browse.Click += (_, _) => BrowseOutputFolder();
        folderRow.Controls.Add(_outputRoot);
        folderRow.Controls.Add(browse);
        Place(folderRow, 32);
        _outputRoot.Text = _settings.SmartRecordOutputFolder;

        Label("AUDIO · TUNE ON START (0 = keep current)");
        ConfigureHz(_tuneHz); Place(_tuneHz);
        Label("AUDIO · MODE (blank = keep)");
        _tuneMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _tuneMode.Items.Add("(keep)");
        foreach (var mode in Enum.GetValues<RadioMode>()) _tuneMode.Items.Add(mode);
        StyleCombo(_tuneMode); Place(_tuneMode);
        Label("AUDIO · BANDWIDTH Hz (0 = keep)");
        ConfigureNum(_tuneBw, 0, 500_000, 100, 0); Place(_tuneBw);

        _pileup.Text = "Pile-up follow near tune (auto move to strong signals)";
        _pileup.ForeColor = ForeColor; _pileup.AutoSize = true; Place(_pileup, 22);
        Label("PILE-UP RANGE ±Hz");
        ConfigureNum(_pileupRange, 1_000, 500_000, 1_000, 25_000); Place(_pileupRange);
        Label("PILE-UP TRIGGER dB");
        ConfigureNum(_pileupTrig, -140, 0, 1, -70); Place(_pileupTrig);

        Label("FT8 REPORT · CALLSIGN CONTAINS");
        StyleBox(_callContains); Place(_callContains);
        Label("FT8 REPORT · PREFIX (e.g. HL, JA)");
        StyleBox(_prefix); Place(_prefix);
        Label("FT8 REPORT · MESSAGE CONTAINS");
        StyleBox(_msgContains); Place(_msgContains);
        _cqOnly.Text = "CQ / QRZ only"; _cqOnly.ForeColor = ForeColor; _cqOnly.AutoSize = true; Place(_cqOnly, 22);

        foreach (Control c in new Control[]
                 {
                     _name, _enabled, _kind, _vfo, _start, _end, _days, _tuneHz, _tuneMode, _tuneBw,
                     _pileup, _pileupRange, _pileupTrig, _callContains, _prefix, _msgContains, _cqOnly, _outputRoot
                 })
        {
            c.Leave += (_, _) => CommitSelected();
            if (c is CheckBox cb) cb.CheckedChanged += (_, _) => { if (!_suppress) CommitSelected(); };
            if (c is ComboBox combo) combo.SelectedIndexChanged += (_, _) => { if (!_suppress) CommitSelected(); };
            if (c is DateTimePicker picker) picker.ValueChanged += (_, _) => { if (!_suppress) CommitSelected(); };
            if (c is NumericUpDown num) num.ValueChanged += (_, _) => { if (!_suppress) CommitSelected(); };
        }
        _days.ItemCheck += (_, _) => BeginInvoke(CommitSelected);
        return panel;
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Smart Record output folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_outputRoot.Text.Trim())
                ? _outputRoot.Text.Trim()
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _outputRoot.Text = dialog.SelectedPath;
        RaiseJobsChanged();
    }

    private void LoadVfos()
    {
        _vfo.Items.Clear();
        foreach (var (id, name) in _listVfos())
            _vfo.Items.Add(new VfoItem(id, name));
        if (_vfo.Items.Count > 0) _vfo.SelectedIndex = 0;
    }

    private void AddJob()
    {
        CommitSelected();
        var job = ReadEditor();
        job.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(job.Name) || job.Name.StartsWith("Job ", StringComparison.Ordinal))
            job.Name = $"Job {_jobs.Count + 1}";
        _jobs.Add(job);
        _binding.Position = _jobs.Count - 1;
        ShowSelected();
        RaiseJobsChanged();
    }

    private void DeleteJob()
    {
        if (_binding.Current is not SmartRecordJob) return;
        _jobs.RemoveAt(_binding.Position);
        ShowSelected();
        RaiseJobsChanged();
    }

    private void ShowSelected()
    {
        if (_binding.Current is not SmartRecordJob job)
            return;
        _suppress = true;
        _name.Text = job.Name;
        _enabled.Checked = job.Enabled;
        _kind.SelectedIndex = job.Kind == SmartRecordKind.FtxReport ? 1 : 0;
        SelectVfo(job.VfoId);
        SetClock(_start, job.StartTime, TimeSpan.Zero);
        SetClock(_end, job.EndTime, TimeSpan.FromHours(1));
        for (var i = 0; i < _days.Items.Count; i++)
            _days.SetItemChecked(i, job.DaysOfWeek.Contains(i));
        _tuneHz.Value = Math.Clamp(job.TuneFrequencyHz, _tuneHz.Minimum, _tuneHz.Maximum);
        if (job.TuneMode is RadioMode mode)
        {
            var idx = _tuneMode.Items.IndexOf(mode);
            _tuneMode.SelectedIndex = idx >= 0 ? idx : 0;
        }
        else _tuneMode.SelectedIndex = 0;
        _tuneBw.Value = Math.Clamp(job.TuneBandwidthHz, _tuneBw.Minimum, _tuneBw.Maximum);
        _pileup.Checked = job.PileupFollowEnabled;
        _pileupRange.Value = Math.Clamp(job.PileupRangeHz, _pileupRange.Minimum, _pileupRange.Maximum);
        _pileupTrig.Value = (decimal)Math.Clamp(job.PileupTriggerLevelDb, (float)_pileupTrig.Minimum, (float)_pileupTrig.Maximum);
        _callContains.Text = job.CallsignContains;
        _prefix.Text = job.Prefix;
        _msgContains.Text = job.MessageContains;
        _cqOnly.Checked = job.CqOnly;
        UpdateKindVisibility();
        _suppress = false;
    }

    private void CommitSelected()
    {
        if (_suppress || _binding.Current is not SmartRecordJob job) return;
        WriteEditor(job);
        _binding.ResetCurrentItem();
        RaiseJobsChanged();
    }

    private SmartRecordJob ReadEditor()
    {
        var job = new SmartRecordJob();
        WriteEditor(job);
        return job;
    }

    private void WriteEditor(SmartRecordJob job)
    {
        job.Name = string.IsNullOrWhiteSpace(_name.Text) ? job.Name : _name.Text.Trim();
        job.Enabled = _enabled.Checked;
        job.Kind = _kind.SelectedIndex == 1 ? SmartRecordKind.FtxReport : SmartRecordKind.Audio;
        job.VfoId = _vfo.SelectedItem is VfoItem item ? item.Id : "main";
        job.StartTime = SmartRecordJob.FormatClock(_start.Value.TimeOfDay);
        job.EndTime = SmartRecordJob.FormatClock(_end.Value.TimeOfDay);
        job.DaysOfWeek = [];
        for (var i = 0; i < _days.Items.Count; i++)
            if (_days.GetItemChecked(i)) job.DaysOfWeek.Add(i);
        job.TuneFrequencyHz = decimal.ToInt64(_tuneHz.Value);
        job.TuneMode = _tuneMode.SelectedItem is RadioMode mode ? mode : null;
        job.TuneBandwidthHz = decimal.ToInt32(_tuneBw.Value);
        job.PileupFollowEnabled = _pileup.Checked;
        job.PileupRangeHz = decimal.ToInt64(_pileupRange.Value);
        job.PileupTriggerLevelDb = (float)_pileupTrig.Value;
        job.PileupReleaseLevelDb = job.PileupTriggerLevelDb - 15f;
        job.CallsignContains = _callContains.Text.Trim();
        job.Prefix = _prefix.Text.Trim();
        job.MessageContains = _msgContains.Text.Trim();
        job.CqOnly = _cqOnly.Checked;
    }

    private void RaiseJobsChanged()
    {
        if (_suppress) return;
        JobsChanged?.Invoke();
    }

    private void UpdateKindVisibility()
    {
        var audio = _kind.SelectedIndex != 1;
        foreach (Control c in new Control[] { _tuneHz, _tuneMode, _tuneBw, _pileup, _pileupRange, _pileupTrig })
            c.Enabled = audio;
        foreach (Control c in new Control[] { _callContains, _prefix, _msgContains, _cqOnly })
            c.Enabled = !audio;
    }

    private void SelectVfo(string id)
    {
        for (var i = 0; i < _vfo.Items.Count; i++)
            if (_vfo.Items[i] is VfoItem item && item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                _vfo.SelectedIndex = i;
                return;
            }
        if (_vfo.Items.Count > 0) _vfo.SelectedIndex = 0;
    }

    private static void SetClock(DateTimePicker picker, string text, TimeSpan fallback)
    {
        var span = SmartRecordJob.TryParseClock(text, out var parsed) ? parsed : fallback;
        var today = DateTime.Today;
        picker.Value = today.Add(span);
    }

    private static void StyleClock(DateTimePicker picker)
    {
        picker.Format = DateTimePickerFormat.Custom;
        picker.CustomFormat = "HH:mm";
        picker.ShowUpDown = true;
        picker.Width = 80;
        picker.Margin = new Padding(0, 2, 0, 0);
    }

    private static void StyleBox(TextBox box)
    {
        box.BackColor = Color.FromArgb(5, 17, 24);
        box.ForeColor = Color.FromArgb(226, 235, 242);
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleCombo(ComboBox box)
    {
        box.BackColor = Color.FromArgb(5, 17, 24);
        box.ForeColor = Color.FromArgb(226, 235, 242);
        box.FlatStyle = FlatStyle.Flat;
    }

    private static void ConfigureHz(NumericUpDown box) => ConfigureNum(box, 0, 7_500_000_000m, 1, 0);

    private static void ConfigureNum(NumericUpDown box, decimal min, decimal max, decimal inc, decimal value)
    {
        box.Minimum = min;
        box.Maximum = max;
        box.Increment = inc;
        box.Value = Math.Clamp(value, min, max);
        box.BackColor = Color.FromArgb(5, 17, 24);
        box.ForeColor = Color.FromArgb(226, 235, 242);
        box.BorderStyle = BorderStyle.FixedSingle;
        if (max > 1_000) box.ThousandsSeparator = true;
    }

    private static Button MakeButton(string text, Color color) => new()
    {
        Text = text, Size = new Size(90, 30), FlatStyle = FlatStyle.Flat,
        BackColor = color, ForeColor = Color.White, Margin = new Padding(0, 8, 8, 0)
    };

    private sealed record VfoItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
