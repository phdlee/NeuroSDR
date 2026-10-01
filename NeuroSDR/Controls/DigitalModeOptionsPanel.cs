using NeuroSDR.Audio;
using NeuroSDR.Core;
using NeuroSDR.Plugins.DigitalVoice;

namespace NeuroSDR.Controls;

/// <summary>
/// EXTENDS AREA options for PCM-fed DMR / D-STAR / C4FM (dsdfme.dll).
/// Feed can be RF discriminator, AF WAV file, or Line In (VB-Cable etc.).
/// </summary>
internal sealed class DigitalModeOptionsPanel : ExtendsSectionPanel
{
    private readonly Label _status = new();
    private readonly ComboBox _output = new();
    private readonly CheckBox _agc = new();
    private readonly CheckBox _digitalAgc = new();
    private readonly TrackBar _feedVolume = new();
    private readonly Label _feedVolumeLabel = new();
    private readonly ComboBox _feedSource = new();
    private readonly ComboBox _lineInDevice = new();
    private readonly Label _wavPath = new();
    private readonly Button _wavBrowse = new();
    private readonly Button _wavPlay = new();
    private readonly Label _hint = new();
    private readonly ComboBox _fdvModem = new();
    private readonly ComboBox _fdvSide = new();
    private readonly Label _fdvModemLabel = new();
    private readonly Label _fdvSideLabel = new();
    private DigitalModeEngine? _engine;
    private bool _suppress;
    private string _selectedWavPath = "";
    private bool _wavPlaying;

    public event Action? OptionsChanged;
    public event Action? FeedSourceChanged;
    public event Action? LineInDeviceChanged;
    public event Action? WavBrowseRequested;
    public event Action? WavPlayStopRequested;

    public DigitalModeOptionsPanel() : base("DIGITAL MODE · PCM")
    {
        var outLabel = MakeLabel("VOICE OUT", 0, 0);
        _output.DropDownStyle = ComboBoxStyle.DropDownList;
        _output.Location = new Point(0, 16);
        _output.Size = new Size(214, 24);
        StyleCombo(_output);
        _output.Items.AddRange(["OUT 1", "OUT 2"]);
        _output.SelectedIndex = 0;
        _output.SelectedIndexChanged += (_, _) => RaiseOptions();

        _agc.Text = "PCM AGC (gentle)";
        _agc.AutoSize = false;
        _agc.Location = new Point(0, 44);
        _agc.Size = new Size(214, 20);
        _agc.ForeColor = Color.FromArgb(216, 225, 235);
        _agc.Checked = false;
        _agc.CheckedChanged += (_, _) => RaiseOptions();

        _digitalAgc.Text = "AGC for Digital";
        _digitalAgc.AutoSize = false;
        _digitalAgc.Location = new Point(0, 66);
        _digitalAgc.Size = new Size(214, 20);
        _digitalAgc.ForeColor = Color.FromArgb(216, 225, 235);
        _digitalAgc.Checked = false;
        _digitalAgc.CheckedChanged += (_, _) => RaiseOptions();

        _feedVolumeLabel.Location = new Point(0, 90);
        _feedVolumeLabel.Size = new Size(214, 16);
        _feedVolumeLabel.ForeColor = Color.FromArgb(145, 181, 198);
        _feedVolumeLabel.Font = new Font("Segoe UI Semibold", 7.5f);
        _feedVolumeLabel.Text = "PCM VOL → DLL  70%";

        ConfigureTrack(_feedVolume, 108);
        _feedVolume.Value = 70;
        _feedVolume.ValueChanged += (_, _) =>
        {
            _feedVolumeLabel.Text = $"PCM VOL → DLL  {_feedVolume.Value}%";
            RaiseOptions();
        };

        var srcLabel = MakeLabel("AF FEED (DSD-FME style)", 0, 140);
        _feedSource.DropDownStyle = ComboBoxStyle.DropDownList;
        _feedSource.Location = new Point(0, 156);
        _feedSource.Size = new Size(214, 24);
        StyleCombo(_feedSource);
        _feedSource.Items.AddRange(["RF (SDR)", "WAV file (NFM AF)", "Line In"]);
        _feedSource.SelectedIndex = 0;
        _feedSource.SelectedIndexChanged += (_, _) =>
        {
            UpdateFeedChrome();
            if (!_suppress)
            {
                RaiseOptions();
                FeedSourceChanged?.Invoke();
            }
        };

        var lineLabel = MakeLabel("LINE IN DEVICE", 0, 184);
        _lineInDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        _lineInDevice.Location = new Point(0, 200);
        _lineInDevice.Size = new Size(214, 24);
        StyleCombo(_lineInDevice);
        _lineInDevice.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppress)
            {
                RaiseOptions();
                LineInDeviceChanged?.Invoke();
            }
        };

        _wavBrowse.Text = "Browse…";
        _wavBrowse.Location = new Point(0, 230);
        _wavBrowse.Size = new Size(104, 24);
        StyleButton(_wavBrowse);
        _wavBrowse.Click += (_, _) => WavBrowseRequested?.Invoke();

        _wavPlay.Text = "Play";
        _wavPlay.Location = new Point(110, 230);
        _wavPlay.Size = new Size(104, 24);
        StyleButton(_wavPlay);
        _wavPlay.Click += (_, _) => WavPlayStopRequested?.Invoke();

        _wavPath.Location = new Point(0, 258);
        _wavPath.Size = new Size(214, 28);
        _wavPath.ForeColor = Color.FromArgb(150, 175, 190);
        _wavPath.Font = new Font("Consolas", 7f);
        _wavPath.Text = "No WAV selected";

        _status.Location = new Point(0, 290);
        _status.Size = new Size(214, 48);
        _status.ForeColor = Color.FromArgb(180, 198, 210);
        _status.Font = new Font("Consolas", 7.2f);
        _status.Text = "Idle";

        _hint.Location = new Point(0, 340);
        _hint.Size = new Size(214, 32);
        _hint.ForeColor = Color.FromArgb(120, 150, 165);
        _hint.Font = new Font("Segoe UI", 7f);
        _hint.Text = "WAV / Line In = NFM discriminator AF into dsdfme (other radio / SDR → VB-Cable)";

        _fdvModemLabel.Text = "FREEDV MODE";
        _fdvModemLabel.AutoSize = false;
        _fdvModemLabel.Location = new Point(0, 374);
        _fdvModemLabel.Size = new Size(214, 16);
        _fdvModemLabel.ForeColor = Color.FromArgb(145, 181, 198);
        _fdvModemLabel.Font = new Font("Segoe UI Semibold", 7.5f);
        _fdvModem.DropDownStyle = ComboBoxStyle.DropDownList;
        _fdvModem.Location = new Point(0, 390);
        _fdvModem.Size = new Size(214, 24);
        StyleCombo(_fdvModem);
        _fdvModem.Items.AddRange(["Auto", "700D", "700E", "1600", "700C"]);
        _fdvModem.SelectedIndex = 0;
        _fdvModem.SelectedIndexChanged += (_, _) => RaiseOptions();

        _fdvSideLabel.Text = "SIDEBAND";
        _fdvSideLabel.AutoSize = false;
        _fdvSideLabel.Location = new Point(0, 418);
        _fdvSideLabel.Size = new Size(214, 16);
        _fdvSideLabel.ForeColor = Color.FromArgb(145, 181, 198);
        _fdvSideLabel.Font = new Font("Segoe UI Semibold", 7.5f);
        _fdvSide.DropDownStyle = ComboBoxStyle.DropDownList;
        _fdvSide.Location = new Point(0, 434);
        _fdvSide.Size = new Size(214, 24);
        StyleCombo(_fdvSide);
        _fdvSide.Items.AddRange(["Auto (<10 MHz LSB)", "LSB", "USB"]);
        _fdvSide.SelectedIndex = 0;
        _fdvSide.SelectedIndexChanged += (_, _) => RaiseOptions();

        Place(outLabel);
        Place(_output);
        Place(_agc);
        Place(_digitalAgc);
        Place(_feedVolumeLabel);
        Place(_feedVolume);
        Place(srcLabel);
        Place(_feedSource);
        Place(lineLabel);
        Place(_lineInDevice);
        Place(_wavBrowse);
        Place(_wavPlay);
        Place(_wavPath);
        Place(_status);
        Place(_hint);
        Place(_fdvModemLabel);
        Place(_fdvModem);
        Place(_fdvSideLabel);
        Place(_fdvSide);
        RefreshLineInDevices();
        UpdateFeedChrome();
        SetBodyHeight(372);
    }

    public int OutputChannel => _output.SelectedIndex + 1;
    public bool FeedAgc => _agc.Checked;
    public bool DigitalModeAgc => _digitalAgc.Checked;

    public void SetDigitalModeAgc(bool enabled)
    {
        if (_digitalAgc.Checked == enabled) return;
        _suppress = true;
        _digitalAgc.Checked = enabled;
        _suppress = false;
    }

    public void SetDigitalAgcReduction(int pointsBelowSlider)
    {
        var text = pointsBelowSlider > 0 ? $"AGC for Digital  −{pointsBelowSlider}" : "AGC for Digital";
        if (_digitalAgc.Text != text) _digitalAgc.Text = text;
    }
    public int FeedVolumePercent => _feedVolume.Value;
    public DigitalVoiceFeedSource FeedSource => _feedSource.SelectedIndex switch
    {
        1 => DigitalVoiceFeedSource.WavFile,
        2 => DigitalVoiceFeedSource.LineIn,
        _ => DigitalVoiceFeedSource.Rf
    };
    public int LineInDeviceId => _lineInDevice.SelectedItem is DeviceItem item ? item.Id : -1;
    public string FreeDvModem => _fdvModem.SelectedItem?.ToString() ?? "Auto";
    public string FreeDvSideband => _fdvSide.SelectedIndex switch
    {
        1 => "LSB",
        2 => "USB",
        _ => "Auto"
    };
    public string SelectedWavPath => _selectedWavPath;
    public bool IsWavPlaying => _wavPlaying;

    public void Bind(DigitalModeEngine engine)
    {
        if (ReferenceEquals(_engine, engine)) return;
        if (_engine is not null) _engine.StatusChanged -= OnStatusChanged;
        _engine = engine;
        engine.StatusChanged += OnStatusChanged;
        SyncFromEngine();
    }

    public void ShowForMode(RadioMode mode)
    {
        Visible = RadioModes.IsDigitalVoice(mode);
        if (!Visible) return;
        SetCaption(mode switch
        {
            RadioMode.DMR => "DMR · PCM OPTIONS",
            RadioMode.DSTAR => "D-STAR · PCM OPTIONS",
            RadioMode.C4FM => "C4FM · PCM OPTIONS",
            RadioMode.FREEDV => "FREEDV · CODEC2",
            _ => "DIGITAL · PCM OPTIONS"
        });
        var freedv = mode == RadioMode.FREEDV;
        _fdvModemLabel.Visible = _fdvModem.Visible = _fdvSideLabel.Visible = _fdvSide.Visible = freedv;
        _hint.Text = freedv
            ? "Tune LSB/USB on the FreeDV tone. WAV/Line In = SSB AF (not NFM)."
            : "WAV / Line In = NFM discriminator AF into dsdfme (other radio / SDR → VB-Cable)";
        SetBodyHeight(freedv ? 462 : 372);
        SyncFromEngine();
    }

    public void LoadOptions(int outputChannel, bool agc, int feedVolume,
        DigitalVoiceFeedSource feedSource, int lineInDeviceId, string? wavPath,
        string? freeDvModem = null, string? freeDvSideband = null)
    {
        _suppress = true;
        _output.SelectedIndex = Math.Clamp(outputChannel, 1, 2) - 1;
        _agc.Checked = agc;
        _feedVolume.Value = Math.Clamp(feedVolume, 0, 100);
        _feedVolumeLabel.Text = $"PCM VOL → DLL  {_feedVolume.Value}%";
        _feedSource.SelectedIndex = feedSource switch
        {
            DigitalVoiceFeedSource.WavFile => 1,
            DigitalVoiceFeedSource.LineIn => 2,
            _ => 0
        };
        RefreshLineInDevices();
        SelectLineInDevice(lineInDeviceId);
        SetWavPath(wavPath ?? "");
        SelectComboText(_fdvModem, NormalizeModem(freeDvModem));
        _fdvSide.SelectedIndex = (freeDvSideband ?? "Auto").Trim().ToUpperInvariant() switch
        {
            "LSB" => 1,
            "USB" => 2,
            _ => 0
        };
        UpdateFeedChrome();
        _suppress = false;
    }

    public void SetWavPath(string path)
    {
        _selectedWavPath = path?.Trim() ?? "";
        _wavPath.Text = string.IsNullOrWhiteSpace(_selectedWavPath)
            ? "No WAV selected"
            : TruncatePath(_selectedWavPath, 42);
    }

    public void SetWavPlaying(bool playing)
    {
        _wavPlaying = playing;
        _wavPlay.Text = playing ? "Stop" : "Play";
        UpdateFeedChrome();
    }

    public void SetFeedActivity(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetFeedActivity(text));
            return;
        }
        if (!string.IsNullOrWhiteSpace(text))
            _status.Text = text;
        else
            SyncFromEngine();
    }

    public void RefreshLineInDevices()
    {
        var selected = LineInDeviceId;
        _suppress = true;
        try
        {
            _lineInDevice.Items.Clear();
            foreach (var device in WaveInDevices.EnumerateForCapture())
                _lineInDevice.Items.Add(new DeviceItem(device.Id, device.Name));
            if (_lineInDevice.Items.Count == 0)
                _lineInDevice.Items.Add(new DeviceItem(-1, "Windows Default Input"));
            SelectLineInDevice(selected);
        }
        finally
        {
            _suppress = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _engine is not null)
            _engine.StatusChanged -= OnStatusChanged;
        base.Dispose(disposing);
    }

    private static string NormalizeModem(string? value) =>
        (value ?? "Auto").Trim().ToUpperInvariant() switch
        {
            "700D" => "700D",
            "700E" => "700E",
            "1600" => "1600",
            "700C" => "700C",
            _ => "Auto"
        };

    private static void SelectComboText(ComboBox box, string text)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i]?.ToString()?.Equals(text, StringComparison.OrdinalIgnoreCase) == true)
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private void SelectLineInDevice(int id)
    {
        void SelectIndex(int index)
        {
            if (_lineInDevice.SelectedIndex == index) return;
            var was = _suppress;
            _suppress = true;
            try { _lineInDevice.SelectedIndex = index; }
            finally { _suppress = was; }
        }

        for (var i = 0; i < _lineInDevice.Items.Count; i++)
        {
            if (_lineInDevice.Items[i] is DeviceItem item && item.Id == id)
            {
                SelectIndex(i);
                return;
            }
        }
        // Prefer VB-Cable when nothing saved.
        for (var i = 0; i < _lineInDevice.Items.Count; i++)
        {
            if (_lineInDevice.Items[i] is DeviceItem item &&
                item.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
            {
                SelectIndex(i);
                return;
            }
        }
        if (_lineInDevice.Items.Count > 0) SelectIndex(0);
    }

    private void UpdateFeedChrome()
    {
        var src = FeedSource;
        _lineInDevice.Enabled = src == DigitalVoiceFeedSource.LineIn;
        _wavBrowse.Enabled = src == DigitalVoiceFeedSource.WavFile && !_wavPlaying;
        _wavPlay.Enabled = src == DigitalVoiceFeedSource.WavFile &&
                           (_wavPlaying || !string.IsNullOrWhiteSpace(_selectedWavPath));
    }

    private void RaiseOptions()
    {
        if (_suppress) return;
        OptionsChanged?.Invoke();
    }

    private void OnStatusChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(SyncFromEngine);
            return;
        }
        SyncFromEngine();
    }

    private void SyncFromEngine()
    {
        if (_engine is null) return;
        _status.Text = _engine.Status;
    }

    private static string TruncatePath(string path, int max)
    {
        if (path.Length <= max) return path;
        return "…" + path[^(max - 1)..];
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = false,
        Location = new Point(x, y),
        Size = new Size(214, 16),
        ForeColor = Color.FromArgb(145, 181, 198),
        Font = new Font("Segoe UI Semibold", 7.5f)
    };

    private static void ConfigureTrack(TrackBar bar, int y)
    {
        bar.Location = new Point(-4, y);
        bar.Size = new Size(220, 28);
        bar.Minimum = 0;
        bar.Maximum = 100;
        bar.TickStyle = TickStyle.None;
        bar.BackColor = Color.FromArgb(10, 20, 28);
    }

    private static void StyleCombo(ComboBox box)
    {
        box.BackColor = Color.FromArgb(5, 17, 24);
        box.ForeColor = Color.FromArgb(207, 224, 235);
        box.FlatStyle = FlatStyle.Flat;
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(38, 61, 75);
        button.ForeColor = Color.FromArgb(211, 226, 235);
        button.Font = new Font("Segoe UI Semibold", 7.5f);
        button.FlatAppearance.BorderColor = Color.FromArgb(72, 103, 119);
    }

    private sealed record DeviceItem(int Id, string Name)
    {
        public override string ToString() => Name;
    }
}

internal enum DigitalVoiceFeedSource
{
    Rf = 0,
    WavFile = 1,
    LineIn = 2
}
