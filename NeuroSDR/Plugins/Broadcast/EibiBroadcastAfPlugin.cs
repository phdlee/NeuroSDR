using NeuroSDR.Controls;

namespace NeuroSDR.Plugins.Broadcast;

internal sealed class EibiBroadcastAfPlugin : IAfPlugin, IAfVisualPlugin
{
    public const string PluginId = "builtin.af.eibi";

    private EibiBroadcastPluginView? _view;

    public AfPluginInfo Info { get; } = new(
        PluginId,
        "Shortwave Schedule",
        "EiBi HF broadcast schedule: download, filter, tune, and mark the main waterfall.",
        AfPluginCapabilities.Display | AfPluginCapabilities.HostResults,
        IsBuiltIn: true);

    public event Action<AfPluginResult>? ResultAvailable;

    public Control CreateView(IAfPluginUiHost ui)
    {
        _view = new EibiBroadcastPluginView();
        _view.OptionsChanged += () =>
        {
            var opts = _view.BuildOptions();
            ui.SaveOptions(opts);
            ui.Configure(opts);
            PublishOverlay(opts);
        };
        _view.TuneRequested += entry =>
        {
            ResultAvailable?.Invoke(new AfPluginResult(
                PluginId, "TUNE", entry.Station, DateTime.UtcNow,
                Fields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["frequencyHz"] = entry.FrequencyHz.ToString(),
                    ["mode"] = "AM",
                    ["bandwidth"] = "10000"
                }));
        };
        var loaded = ui.LoadOptions();
        _view.LoadOptions(loaded);
        Configure(loaded);
        return _view;
    }

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        // Do not reload the grid here — host Configure ticks would jump the list to row 0.
        if (_view is null) return;
        PublishOverlay(_view.BuildOptions());
    }

    public AfPluginResult? Process(AfAudioBlock block) => null;

    public void Dispose() => _view = null;

    private void PublishOverlay(IReadOnlyDictionary<string, string> options)
    {
        var show = options.TryGetValue("showOnMainWaterfall", out var flag) &&
                   bool.TryParse(flag, out var on) && on;
        ResultAvailable?.Invoke(new AfPluginResult(
            PluginId, "OVERLAY", show ? "on" : "off", DateTime.UtcNow,
            Fields: new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase)));
    }
}
