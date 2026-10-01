using System.Globalization;
using System.Net.NetworkInformation;

namespace NeuroSDR.Controls;

internal sealed class SatelliteMapPickerDialog : Form
{
    private readonly WebBrowser _browser = new() { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true };
    private bool _picked;
    private double _latitude;
    private double _longitude;

    private SatelliteMapPickerDialog(double latitude, double longitude)
    {
        Text = "Pick location (OpenStreetMap)";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 640);
        Controls.Add(_browser);
        Shown += (_, _) => LoadMap(latitude, longitude);
    }

    public static bool TryPick(IWin32Window owner, double latitude, double longitude, out double lat, out double lon)
    {
        lat = latitude;
        lon = longitude;
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            MessageBox.Show(owner,
                "Internet connection is required to open the map picker.",
                "Map Picker", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        using var dialog = new SatelliteMapPickerDialog(latitude, longitude);
        if (dialog.ShowDialog(owner) != DialogResult.OK || !dialog._picked) return false;
        lat = dialog._latitude;
        lon = dialog._longitude;
        return true;
    }

    private void LoadMap(double latitude, double longitude)
    {
        var lat = latitude.ToString(CultureInfo.InvariantCulture);
        var lon = longitude.ToString(CultureInfo.InvariantCulture);
        var html = $$"""
<!DOCTYPE html>
<html><head>
<meta charset="utf-8"/>
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"/>
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
<style>html,body,#map{margin:0;height:100%;}</style>
</head><body>
<div id="map"></div>
<script>
const map = L.map('map').setView([{{lat}}, {{lon}}], 6);
L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '&copy; OpenStreetMap' }).addTo(map);
let marker = L.marker([{{lat}}, {{lon}}], { draggable: true }).addTo(map);
function send() {
  const p = marker.getLatLng();
  window.external.NotifyPick(p.lat, p.lng);
}
marker.on('dragend', send);
map.on('click', e => { marker.setLatLng(e.latlng); send(); });
</script>
</body></html>
""";
        _browser.ObjectForScripting = new ScriptBridge(this);
        _browser.DocumentText = html;
    }

    internal void NotifyPick(double latitude, double longitude)
    {
        _picked = true;
        _latitude = latitude;
        _longitude = longitude;
        DialogResult = DialogResult.OK;
        Close();
    }

    [System.Runtime.InteropServices.ComVisible(true)]
    private sealed class ScriptBridge(SatelliteMapPickerDialog owner)
    {
        public void NotifyPick(double latitude, double longitude) => owner.NotifyPick(latitude, longitude);
    }
}
