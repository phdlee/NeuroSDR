using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class SatelliteHomeEditorDialog : Form
{
    private readonly ListBox _list = new()
    {
        Location = new Point(12, 12),
        Size = new Size(220, 220),
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(8, 20, 28),
        ForeColor = Color.FromArgb(210, 228, 240)
    };
    private readonly TextBox _nameBox = new() { Location = new Point(248, 28), Width = 160 };
    private readonly NumericUpDown _latBox = CoordBox(-90, 90, 4);
    private readonly NumericUpDown _lonBox = CoordBox(-180, 180, 4);
    private readonly NumericUpDown _altBox = CoordBox(0, 4000, 0);
    private readonly List<SatelliteHomeLocation> _homes;
    private bool _suspend;

    private SatelliteHomeEditorDialog(IEnumerable<SatelliteHomeLocation> homes)
    {
        _homes = homes.Select(item => item.Clone()).ToList();
        Text = "Home Locations";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(424, 300);
        BackColor = Color.FromArgb(12, 28, 38);
        ForeColor = Color.FromArgb(220, 235, 245);
        Font = new Font("Segoe UI", 9f);

        _latBox.Location = new Point(248, 84);
        _lonBox.Location = new Point(248, 118);
        _altBox.Location = new Point(248, 152);
        _latBox.Width = _lonBox.Width = _altBox.Width = 160;

        var addBtn = new Button { Text = "Add", Location = new Point(12, 240), Size = new Size(68, 28) };
        var deleteBtn = new Button { Text = "Delete", Location = new Point(86, 240), Size = new Size(68, 28) };
        var mapBtn = new Button { Text = "Pick on map...", Location = new Point(248, 188), Size = new Size(160, 28) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(252, 240), Size = new Size(74, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(334, 240), Size = new Size(74, 28) };
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange([
            _list, LabelAt("Name", 248, 10), _nameBox,
            LabelAt("Latitude", 248, 66), _latBox,
            LabelAt("Longitude", 248, 100), _lonBox,
            LabelAt("Altitude (m)", 248, 134), _altBox,
            addBtn, deleteBtn, mapBtn, ok, cancel
        ]);

        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        addBtn.Click += (_, _) => AddHome();
        deleteBtn.Click += (_, _) => DeleteHome();
        mapBtn.Click += (_, _) => PickOnMap();
        _nameBox.TextChanged += (_, _) => SaveSelected();
        _latBox.ValueChanged += (_, _) => SaveSelected();
        _lonBox.ValueChanged += (_, _) => SaveSelected();
        _altBox.ValueChanged += (_, _) => SaveSelected();
        RefreshList();
    }

    public static bool Show(IWin32Window owner, List<SatelliteHomeLocation> homes)
    {
        using var dialog = new SatelliteHomeEditorDialog(homes);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        homes.Clear();
        homes.AddRange(dialog._homes.Select(item => item.Clone()));
        return true;
    }

    private void RefreshList(string? selectId = null)
    {
        selectId ??= SelectedHome()?.Id;
        var ordered = _homes.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _suspend = true;
        try
        {
            _list.Items.Clear();
            foreach (var home in ordered)
                _list.Items.Add(home);
            var index = selectId is null
                ? -1
                : ordered.FindIndex(item => item.Id.Equals(selectId, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && ordered.Count > 0) index = 0;
            if (index >= 0 && index < _list.Items.Count)
                _list.SelectedIndex = index;
        }
        finally
        {
            _suspend = false;
        }
        LoadSelected();
    }

    private SatelliteHomeLocation? SelectedHome()
        => _list.SelectedItem as SatelliteHomeLocation;

    private void LoadSelected()
    {
        if (_suspend || SelectedHome() is not { } home) return;
        _suspend = true;
        try
        {
            _nameBox.Text = home.Name;
            _latBox.Value = (decimal)Math.Clamp(home.Latitude, (double)_latBox.Minimum, (double)_latBox.Maximum);
            _lonBox.Value = (decimal)Math.Clamp(home.Longitude, (double)_lonBox.Minimum, (double)_lonBox.Maximum);
            _altBox.Value = (decimal)Math.Clamp(home.AltitudeMeters, (double)_altBox.Minimum, (double)_altBox.Maximum);
        }
        finally
        {
            _suspend = false;
        }
    }

    private void SaveSelected()
    {
        if (_suspend || SelectedHome() is not { } home) return;
        home.Name = string.IsNullOrWhiteSpace(_nameBox.Text) ? "Home" : _nameBox.Text.Trim();
        home.Latitude = (double)_latBox.Value;
        home.Longitude = (double)_lonBox.Value;
        home.AltitudeMeters = (double)_altBox.Value;
        var index = _list.SelectedIndex;
        if (index < 0) return;
        _suspend = true;
        try { _list.Items[index] = home; }
        finally { _suspend = false; }
    }

    private void AddHome()
    {
        var home = new SatelliteHomeLocation { Name = $"Home {_homes.Count + 1}" };
        _homes.Add(home);
        RefreshList(home.Id);
    }

    private void DeleteHome()
    {
        if (SelectedHome() is not { } home) return;
        _homes.RemoveAll(item => item.Id.Equals(home.Id, StringComparison.OrdinalIgnoreCase));
        RefreshList(selectId: null);
    }

    private void PickOnMap()
    {
        if (SelectedHome() is not { } home) return;
        if (!SatelliteMapPickerDialog.TryPick(this, home.Latitude, home.Longitude, out var lat, out var lon)) return;
        _latBox.Value = (decimal)Math.Clamp(lat, (double)_latBox.Minimum, (double)_latBox.Maximum);
        _lonBox.Value = (decimal)Math.Clamp(lon, (double)_lonBox.Minimum, (double)_lonBox.Maximum);
        SaveSelected();
    }

    private static Label LabelAt(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(x, y),
        ForeColor = Color.FromArgb(150, 185, 205)
    };

    private static NumericUpDown CoordBox(decimal min, decimal max, int decimals) => new()
    {
        Minimum = min,
        Maximum = max,
        DecimalPlaces = decimals,
        Increment = decimals == 0 ? 1 : 0.0001m
    };
}
