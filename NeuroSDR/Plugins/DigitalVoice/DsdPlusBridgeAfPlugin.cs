using System.Diagnostics;
using System.Globalization;
using NeuroSDR.Audio;
using NeuroSDR.Controls;
using NeuroSDR.Plugins;

namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// Launch DSD+ and route host OUT (default OUT2) to VB-Cable via the normal
/// NeuroSDR WaveOut path — identical to setting OUT2→CABLE Input by hand.
/// Does not open a second WaveOut or re-buffer AF.
/// </summary>
internal sealed class DsdPlusBridgeAfPlugin : IAfPlugin, IAfVisualPlugin, IAfFrequencyPresetPlugin
{
    public const string PluginId = "builtin.af.dsdplus";

    private readonly object _sync = new();
    private Process? _process;
    private IAfPluginUiHost? _ui;
    private bool _enabled;
    private bool _autoLaunch = true;
    private bool _bindOut = true;
    private bool _muteMain = true;
    private int _outputChannel = 2;
    private int _dsdPlusInputDevice;
    private int _feedVolume = 45;
    private string _cableOutHint = "CABLE Input";
    private string _dsdPlusDir = DefaultDsdPlusDir();
    private string _extraArgs = "-fr";
    private Control? _view;
    private string _bindStatus = "";

    public AfPluginInfo Info { get; } = new(
        PluginId,
        "DSD+ Bridge",
        "Bind host OUT→VB-Cable (same as manual) and launch DSDPlus.exe. No second WaveOut.",
        AfPluginCapabilities.Display | AfPluginCapabilities.HostResults);

    public string PresetGroup => "DMR";
    public event Action<AfPluginResult>? ResultAvailable;

    public Control CreateView(IAfPluginUiHost ui)
    {
        _ui = ui;
        if (_view is { IsDisposed: false }) return _view;
        var view = new DsdPlusBridgePluginView();
        view.LoadOptions(ui.LoadOptions());
        void Push(string? command = null)
        {
            var options = new Dictionary<string, string>(view.Snapshot(), StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = ui.IsRunning.ToString()
            };
            if (!string.IsNullOrEmpty(command)) options["command"] = command;
            ui.SaveOptions(options);
            ui.Configure(options);
        }
        view.OptionsChanged += () => Push();
        view.CommandRequested += command => Push(command);
        Push();
        return _view = view;
    }

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            _autoLaunch = Bool(options, "autoLaunch", _autoLaunch);
            _bindOut = Bool(options, "bindOut", _bindOut);
            _muteMain = Bool(options, "muteMain", _muteMain);
            _outputChannel = Int(options, "output", _outputChannel, 1, 2);
            _dsdPlusInputDevice = Int(options, "dsdPlusInput", _dsdPlusInputDevice, 0, 255);
            _feedVolume = Int(options, "feedVolume", _feedVolume, 5, 100);
            _cableOutHint = Text(options, "cableOut", _cableOutHint);
            _dsdPlusDir = Text(options, "dsdPlusDir", _dsdPlusDir);
            _extraArgs = Text(options, "extraArgs", _extraArgs);
            if (string.IsNullOrWhiteSpace(_dsdPlusDir)) _dsdPlusDir = DefaultDsdPlusDir();
            if (string.IsNullOrWhiteSpace(_cableOutHint)) _cableOutHint = "CABLE Input";
            if (_dsdPlusInputDevice <= 0)
                _dsdPlusInputDevice = WaveInDevices.FindPreferred("CABLE Output");

            var command = Text(options, "command", "");
            if (command.Equals("launch", StringComparison.OrdinalIgnoreCase))
            {
                if (_bindOut) BindHostOutLocked();
                LaunchDsdPlusLocked();
            }
            else if (command.Equals("stop-dsdplus", StringComparison.OrdinalIgnoreCase))
                StopDsdPlusLocked();
            else if (command.Equals("bind", StringComparison.OrdinalIgnoreCase))
                BindHostOutLocked();

            if (_enabled)
            {
                if (_bindOut) BindHostOutLocked();
                MuteMainLocked(_muteMain);
                if (_autoLaunch && (_process is null || _process.HasExited))
                    LaunchDsdPlusLocked();
                PublishStatus(StatusLine(live: _process is { HasExited: false }));
            }
            else
            {
                StopDsdPlusLocked();
                MuteMainLocked(false);
                PublishStatus("idle — enable plugin to bind OUT→VB and launch DSD+");
            }
        }
    }

    public AfPluginResult? Process(AfAudioBlock block) => null;

    public void Dispose()
    {
        lock (_sync)
        {
            _enabled = false;
            StopDsdPlusLocked();
            MuteMainLocked(false);
        }
    }

    private void BindHostOutLocked()
    {
        if (_ui is null)
        {
            _bindStatus = "UI host missing";
            return;
        }
        _bindStatus = _ui.BindHostWaveOut(_outputChannel, _cableOutHint, _feedVolume);
    }

    private void MuteMainLocked(bool muted)
    {
        if (_ui is null) return;
        var text = _ui.SetHostMainMuted(muted);
        if (!string.IsNullOrWhiteSpace(text))
            _bindStatus = string.IsNullOrWhiteSpace(_bindStatus) ? text : $"{_bindStatus} · {text}";
    }

    private void LaunchDsdPlusLocked()
    {
        StopDsdPlusLocked();
        var exe = Path.Combine(_dsdPlusDir, "DSDPlus.exe");
        if (!File.Exists(exe))
        {
            PublishStatus("DSDPlus.exe not found: " + exe);
            return;
        }
        try
        {
            var args = $"-i{_dsdPlusInputDevice} {_extraArgs}".Trim();
            _process = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = _dsdPlusDir,
                UseShellExecute = false
            });
            PublishStatus($"launched DSDPlus {args} · {_bindStatus}");
        }
        catch (Exception exception)
        {
            PublishStatus("launch failed: " + exception.GetBaseException().Message);
        }
    }

    private void StopDsdPlusLocked()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit(1500)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try { _process.Dispose(); } catch { }
        _process = null;
    }

    private string StatusLine(bool live) =>
        $"{_bindStatus} · DSD+ {(live ? "running" : "stopped")} · -i{_dsdPlusInputDevice} · OUT{_outputChannel} vol={_feedVolume}%";

    private void PublishStatus(string text) =>
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", text, DateTime.UtcNow));

    private static string DefaultDsdPlusDir()
    {
        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "DSDPlusFull"));
        if (Directory.Exists(candidate)) return candidate;
        candidate = @"J:\codex\sdr\samples\DSDPlusFull";
        return Directory.Exists(candidate) ? candidate : AppContext.BaseDirectory;
    }

    private static int Int(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max)
    {
        if (!options.TryGetValue(key, out var text) ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;
        return Math.Clamp(value, min, max);
    }

    private static bool Bool(IReadOnlyDictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static string Text(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var text) ? text : fallback;
}
