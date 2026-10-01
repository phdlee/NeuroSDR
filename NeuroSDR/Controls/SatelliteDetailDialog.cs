using NeuroSatellite.Satellite;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

internal sealed class SatelliteDetailDialog : Form
{
    private readonly CheckBox _priorityCheck = new() { Text = "Priority tracking", AutoSize = true };
    private readonly NumericUpDown _priorityBox = new()
    {
        Minimum = 1,
        Maximum = 99,
        Value = 5,
        Width = 52
    };
    private readonly CheckBox _soloCheck = new() { Text = "Solo tracking (this satellite only)", AutoSize = true };
    private readonly CheckBox _autoSdrCheck = new()
    {
        Text = "Auto SDR Handover (follow this satellite to another site)",
        AutoSize = true
    };
    private readonly NumericUpDown _elBox = new()
    {
        Minimum = 0, Maximum = 45, Value = 12, Width = 48, DecimalPlaces = 0
    };
    private readonly NumericUpDown _snrBox = new()
    {
        Minimum = 0, Maximum = 30, Value = 6, Width = 48, DecimalPlaces = 0
    };

    public SatelliteTrackPreference Preference { get; }
    public bool SoloTracking { get; private set; }
    public bool AutoSdrHandoff { get; private set; }
    public int HandoffMinElevation { get; private set; }
    public int HandoffSnrDb { get; private set; }

    private SatelliteDetailDialog(
        SatelliteInfo satellite,
        SatelliteTrackPreference preference,
        bool thisIsSolo,
        bool autoSdrHandoff,
        int handoffMinElevation,
        int handoffSnrDb)
    {
        Preference = preference;
        Text = satellite.Name;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 438);
        BackColor = Color.FromArgb(12, 28, 38);
        ForeColor = Color.FromArgb(220, 235, 245);
        Font = new Font("Segoe UI", 9f);

        var body = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(8, 20, 28),
            ForeColor = Color.FromArgb(210, 228, 240),
            Location = new Point(12, 12),
            Size = new Size(396, 168),
            Text = BuildInfoText(satellite)
        };

        _priorityCheck.Checked = preference.PriorityTracking;
        _priorityCheck.Location = new Point(12, 188);
        _priorityCheck.CheckedChanged += (_, _) => _priorityBox.Enabled = _priorityCheck.Checked;

        var priorityLabel = new Label
        {
            Text = "Priority",
            AutoSize = true,
            Location = new Point(168, 191),
            ForeColor = Color.FromArgb(150, 185, 205)
        };
        _priorityBox.Location = new Point(224, 186);
        _priorityBox.Value = Math.Clamp(preference.Priority, 1, 99);
        _priorityBox.Enabled = preference.PriorityTracking;

        var soloGroup = new GroupBox
        {
            Text = "Solo tracking",
            Location = new Point(12, 220),
            Size = new Size(396, 166),
            ForeColor = Color.FromArgb(200, 220, 235)
        };
        _soloCheck.Checked = thisIsSolo;
        _soloCheck.Location = new Point(12, 22);
        _soloCheck.ForeColor = Color.FromArgb(220, 235, 245);
        _autoSdrCheck.Checked = autoSdrHandoff;
        _autoSdrCheck.Location = new Point(12, 48);
        _autoSdrCheck.ForeColor = Color.FromArgb(220, 235, 245);
        var elLabel = new Label
        {
            Text = "Handover EL°",
            AutoSize = true,
            Location = new Point(12, 80),
            ForeColor = Color.FromArgb(150, 185, 205)
        };
        _elBox.Location = new Point(118, 76);
        _elBox.Value = Math.Clamp(handoffMinElevation, 0, 45);
        _elBox.BackColor = Color.FromArgb(5, 17, 25);
        _elBox.ForeColor = Color.FromArgb(207, 224, 235);
        var snrLabel = new Label
        {
            Text = "Handover SNR dB",
            AutoSize = true,
            Location = new Point(188, 80),
            ForeColor = Color.FromArgb(150, 185, 205)
        };
        _snrBox.Location = new Point(310, 76);
        _snrBox.Value = Math.Clamp(handoffSnrDb, 0, 30);
        _snrBox.BackColor = Color.FromArgb(5, 17, 25);
        _snrBox.ForeColor = Color.FromArgb(207, 224, 235);
        var hint = new Label
        {
            Text = "Like a phone changing cell towers: if this SDR cannot hear the satellite (wrong band or below the horizon), switch to a site that can.",
            Location = new Point(12, 108),
            Size = new Size(372, 48),
            ForeColor = Color.FromArgb(130, 165, 185)
        };
        void SyncSoloOptions()
        {
            var on = _soloCheck.Checked;
            _autoSdrCheck.Enabled = on;
            _elBox.Enabled = on && _autoSdrCheck.Checked;
            _snrBox.Enabled = on && _autoSdrCheck.Checked;
        }
        _soloCheck.CheckedChanged += (_, _) => SyncSoloOptions();
        _autoSdrCheck.CheckedChanged += (_, _) => SyncSoloOptions();
        SyncSoloOptions();
        soloGroup.Controls.AddRange([_soloCheck, _autoSdrCheck, elLabel, _elBox, snrLabel, _snrBox, hint]);

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(252, 396),
            Size = new Size(74, 28)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(334, 396),
            Size = new Size(74, 28)
        };
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([body, _priorityCheck, priorityLabel, _priorityBox, soloGroup, ok, cancel]);
    }

    public static bool Show(
        IWin32Window owner,
        SatelliteInfo satellite,
        SatelliteTrackPreference preference,
        bool thisIsSolo,
        bool autoSdrHandoff,
        int handoffMinElevation,
        int handoffSnrDb,
        out bool soloTracking,
        out bool autoSdr,
        out int minElevation,
        out int snrDb)
    {
        soloTracking = thisIsSolo;
        autoSdr = autoSdrHandoff;
        minElevation = handoffMinElevation;
        snrDb = handoffSnrDb;
        using var dialog = new SatelliteDetailDialog(
            satellite, preference, thisIsSolo, autoSdrHandoff, handoffMinElevation, handoffSnrDb);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        preference.PriorityTracking = dialog._priorityCheck.Checked;
        preference.Priority = (int)dialog._priorityBox.Value;
        soloTracking = dialog._soloCheck.Checked;
        autoSdr = dialog._autoSdrCheck.Checked;
        minElevation = (int)dialog._elBox.Value;
        snrDb = (int)dialog._snrBox.Value;
        return true;
    }

    private static string BuildInfoText(SatelliteInfo satellite)
    {
        var look = satellite.CurrentLookAngle;
        var geo = satellite.CurrentGeographicPosition;
        var radio = satellite.Radio;
        return string.Join(Environment.NewLine, new[]
        {
            $"NORAD: {satellite.NoradCatalogId?.ToString() ?? "-"}",
            $"Status: {satellite.Status ?? "-"}",
            $"Above horizon: {(satellite.IsAboveHorizon ? "Yes" : "No")}",
            $"Azimuth: {look?.AzimuthDegrees:F1}°   Elevation: {look?.ElevationDegrees:F1}°",
            $"Range: {look?.RangeKilometers:F0} km   Range rate: {look?.RangeRateKilometersPerSecond:F3} km/s",
            $"Position: {geo?.LatitudeDegrees:F3}°, {geo?.LongitudeDegrees:F3}°  Alt {geo?.AltitudeKilometers:F1} km",
            $"RX: {satellite.ReceiveFrequency}",
            $"TX: {satellite.TransmitFrequency}",
            $"Doppler RX: {(satellite.DopplerCorrectedReceiveHz is long hz ? $"{hz / 1_000_000d:F6} MHz" : "-")}",
            $"Mode: {radio?.DownlinkMode ?? "-"} / {radio?.UplinkMode ?? "-"}",
            $"Callsign: {radio?.Callsign ?? "-"}",
            $"Next pass: {satellite.NextVisibilityDisplay}",
            $"Recently heard: {(satellite.RecentlyHeard ? "Yes" : "No")}",
            "",
            satellite.Details ?? ""
        });
    }
}
