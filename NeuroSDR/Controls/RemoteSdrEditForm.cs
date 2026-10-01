using NeuroSDR.Hardware;
using System.Globalization;

namespace NeuroSDR.Controls;

internal sealed class RemoteSdrEditForm : Form
{
    private readonly TextBox _name = Field();
    private readonly ComboBox _protocol = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat
    };
    private readonly TextBox _url = Field();
    private readonly TextBox _country = Field();
    private readonly TextBox _city = Field();
    private readonly TextBox _grid = Field();
    private readonly TextBox _lat = Field();
    private readonly TextBox _lon = Field();
    private readonly TextBox _bands = Field();
    private readonly string _originalUrl;

    public RemoteSdrEntry Result { get; private set; }

    public RemoteSdrEditForm(RemoteSdrEntry entry)
    {
        Result = entry;
        _originalUrl = entry.Url;
        Text = string.IsNullOrWhiteSpace(entry.Name) ? "Edit SDR site" : $"Edit {entry.Name}";
        ClientSize = new Size(520, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 22, 30);
        ForeColor = Color.FromArgb(228, 238, 246);
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 12),
            ColumnCount = 2,
            RowCount = 10,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _protocol.Items.AddRange(["WebSDR", "KiwiSDR"]);
        _protocol.SelectedItem = entry.Protocol is "KiwiSDR" ? "KiwiSDR" : "WebSDR";
        _name.Text = entry.Name;
        _url.Text = entry.Url;
        _country.Text = entry.Country;
        _city.Text = entry.City;
        _grid.Text = entry.Grid;
        _lat.Text = entry.Latitude?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "";
        _lon.Text = entry.Longitude?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "";
        _bands.Text = RemoteSdrBands.FormatUserText(entry.Bands);

        AddRow(layout, 0, "TITLE", _name);
        AddRow(layout, 1, "SITE", _protocol);
        AddRow(layout, 2, "URL", _url);
        AddRow(layout, 3, "COUNTRY", _country);
        AddRow(layout, 4, "CITY", _city);
        AddRow(layout, 5, "GRID", _grid);
        AddRow(layout, 6, "LAT", _lat);
        AddRow(layout, 7, "LON", _lon);
        AddRow(layout, 8, "BANDS", _bands);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0)
        };
        var ok = MakeButton("SAVE", DialogResult.None, Color.FromArgb(62, 118, 164));
        var cancel = MakeButton("CANCEL", DialogResult.Cancel, Color.FromArgb(32, 46, 58));
        ok.Click += (_, _) => Commit();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 9);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string OriginalUrl => _originalUrl;

    private void Commit()
    {
        var url = _url.Text.Trim();
        if (url.Length == 0)
        {
            MessageBox.Show(this, "URL is required.", "Edit SDR site", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result = new RemoteSdrEntry
        {
            Name = string.IsNullOrWhiteSpace(_name.Text) ? url : _name.Text.Trim(),
            Protocol = _protocol.SelectedItem?.ToString() ?? "WebSDR",
            Url = url,
            Location = string.Join(" / ", new[] { _country.Text.Trim(), _city.Text.Trim() }.Where(part => part.Length > 0)),
            Country = _country.Text.Trim(),
            City = _city.Text.Trim(),
            Grid = _grid.Text.Trim(),
            Latitude = ParseCoord(_lat.Text),
            Longitude = ParseCoord(_lon.Text),
            Bands = RemoteSdrBands.ParseUserText(_bands.Text)
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private static double? ParseCoord(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static TextBox Field() => new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(8, 16, 24),
        ForeColor = Color.FromArgb(228, 238, 246)
    };

    private static void AddRow(TableLayoutPanel layout, int row, string caption, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(120, 168, 190)
        }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private static Button MakeButton(string text, DialogResult result, Color back)
    {
        var button = new Button
        {
            Text = text, Width = 96, Height = 30, DialogResult = result, FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = Color.White, UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
