using System.Buffers;
using System.Threading.Channels;
using NeuroSDR.Settings;

namespace NeuroSDR.Plugins;

internal sealed class AfPluginHost : IDisposable
{
    private readonly AfPluginCatalog _catalog;
    private Runner[] _runners = [];
    private IReadOnlyDictionary<string, string> _routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public AfPluginHost(AfPluginCatalog catalog, IEnumerable<AfPluginInstanceSettings> instances)
    {
        _catalog = catalog;
        Rebuild(instances);
    }

    public event Action<AfPluginResult>? ResultAvailable;
    public IReadOnlyList<AfPluginInfo> Plugins => Volatile.Read(ref _runners)
        .Select(runner => runner.Plugin.Info with { Id = runner.InstanceId }).ToArray();

    internal bool IsEnabled(string id) => Matching(id).Any(runner => runner.Enabled);
    internal IAfPlugin? Plugin(string instanceId) => Volatile.Read(ref _runners)
        .FirstOrDefault(runner => runner.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))?.Plugin;
    internal string? PluginTypeId(string instanceId) => Volatile.Read(ref _runners)
        .FirstOrDefault(runner => runner.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))?.PluginId;

    internal string? PluginVariant(string instanceId) => Volatile.Read(ref _runners)
        .FirstOrDefault(runner => runner.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))?.Variant;

    public void Rebuild(IEnumerable<AfPluginInstanceSettings> instances)
    {
        var registrations = _catalog.Plugins.ToDictionary(item => item.Info.Id, StringComparer.OrdinalIgnoreCase);
        var replacement = instances
            .Where(instance => registrations.ContainsKey(instance.PluginId))
            .Select(instance => new Runner(instance.InstanceId, instance.PluginId, instance.Variant,
                instance.VfoId, registrations[instance.PluginId].Create(), Publish))
            .ToArray();
        var previous = Interlocked.Exchange(ref _runners, replacement);
        foreach (var runner in previous) runner.Dispose();
    }

    public void SetRoutes(IReadOnlyDictionary<string, string> routes)
    {
        var copy = new Dictionary<string, string>(routes, StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _routes, copy);
        foreach (var runner in Volatile.Read(ref _runners))
        {
            if (copy.TryGetValue(runner.InstanceId, out var routed) ||
                copy.TryGetValue(runner.PluginId, out routed))
                runner.VfoId = string.IsNullOrWhiteSpace(routed) ? "main" : routed;
        }
    }

    public bool IsVfoInUse(string vfoId)
    {
        return Volatile.Read(ref _runners).Any(runner => runner.Enabled &&
            RouteFor(runner).Equals(vfoId, StringComparison.OrdinalIgnoreCase));
    }

    private string RouteFor(Runner runner)
    {
        var routes = Volatile.Read(ref _routes);
        if (routes.TryGetValue(runner.InstanceId, out var routed) && !string.IsNullOrWhiteSpace(routed))
            return routed;
        if (routes.TryGetValue(runner.PluginId, out var byType) && !string.IsNullOrWhiteSpace(byType))
            return byType;
        return string.IsNullOrWhiteSpace(runner.VfoId) ? "main" : runner.VfoId;
    }

    public void Reset(string id)
    {
        foreach (var runner in Matching(id).Where(runner => runner.Enabled))
            runner.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["command"] = "reset" });
    }

    public void Configure(string id, IReadOnlyDictionary<string, string> options)
    {
        foreach (var runner in Matching(id)) runner.Configure(options);
    }

    public void Apply(IEnumerable<AfPluginInstanceSettings> instances, string ftxMode, double timeAdjustSeconds,
        bool autoTimeAdjust, IEnumerable<string>? activeIds = null)
    {
        var settings = instances.ToDictionary(instance => instance.InstanceId, StringComparer.OrdinalIgnoreCase);
        var active = activeIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var runner in Volatile.Read(ref _runners))
        {
            if (!settings.TryGetValue(runner.InstanceId, out var instance))
            {
                runner.SetEnabled(false);
                continue;
            }
            var display = runner.Plugin.Info.Capabilities.HasFlag(AfPluginCapabilities.Display);
            var shouldRun = !display || active is null || active.Contains(runner.InstanceId) ||
                            active.Contains(runner.PluginId);
            runner.SetEnabled(shouldRun);
            if (!string.IsNullOrWhiteSpace(instance.VfoId))
                runner.VfoId = instance.VfoId;
            runner.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = shouldRun.ToString(),
                ["mode"] = runner.PluginId.Equals("builtin.af.ftx", StringComparison.OrdinalIgnoreCase)
                    ? (string.IsNullOrWhiteSpace(instance.Variant) ? ftxMode : instance.Variant) : ftxMode,
                ["timeAdjustSeconds"] = timeAdjustSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["autoTimeAdjust"] = autoTimeAdjust.ToString()
            });
        }
    }

    public float[] Process(float[] input, int sampleRate, DateTime timestampUtc, string vfoId = "main")
    {
        var current = input;
        foreach (var runner in Volatile.Read(ref _runners))
        {
            if (!runner.Enabled || !RouteFor(runner).Equals(vfoId, StringComparison.OrdinalIgnoreCase)) continue;
            if (runner.Plugin.Info.Capabilities.HasFlag(AfPluginCapabilities.AudioOutput))
            {
                try
                {
                    var result = runner.Plugin.Process(new AfAudioBlock(current, sampleRate, timestampUtc));
                    if (result is not null) Publish(runner.WithInstanceId(result));
                    if (result?.OutputAudio is { Length: > 0 } output) current = output;
                }
                catch (Exception exception) { PublishError(runner.InstanceId, exception); }
            }
            else
            {
                runner.SubmitAsync(current, sampleRate, timestampUtc);
            }
        }
        return current;
    }

    public void PublishRfSpectrum(float[] spectrumDb, long centerHz, int sampleRateHz, long tunedHz, int channelBandwidthHz)
    {
        foreach (var runner in Volatile.Read(ref _runners))
        {
            if (!runner.Enabled || runner.Plugin is not IAfRfSpectrumAware aware) continue;
            try { aware.OnRfSpectrum(spectrumDb, centerHz, sampleRateHz, tunedHz, channelBandwidthHz); }
            catch { }
        }
    }

    private IEnumerable<Runner> Matching(string id) => Volatile.Read(ref _runners).Where(runner =>
        runner.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
        runner.PluginId.Equals(id, StringComparison.OrdinalIgnoreCase));

    private void Publish(AfPluginResult result)
    {
        try { ResultAvailable?.Invoke(result); } catch { }
    }

    private void PublishError(string id, Exception exception) =>
        Publish(new AfPluginResult(id, "ERROR", exception.GetBaseException().Message, DateTime.UtcNow));

    public void Dispose()
    {
        var runners = Interlocked.Exchange(ref _runners, []);
        foreach (var runner in runners) runner.Dispose();
    }

    private sealed class Runner : IDisposable
    {
        private readonly record struct QueuedBlock(float[] Buffer, int Length, int SampleRate, DateTime TimestampUtc);

        private readonly Channel<QueuedBlock> _queue = Channel.CreateBounded<QueuedBlock>(new BoundedChannelOptions(8)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
        });
        private readonly Action<AfPluginResult> _publish;
        private readonly Action<AfPluginResult> _pluginResultHandler;
        private readonly Task _worker;
        public string InstanceId { get; }
        public string PluginId { get; }
        public string Variant { get; }
        public string VfoId;
        public IAfPlugin Plugin { get; }
        public volatile bool Enabled;

        public Runner(string instanceId, string pluginId, string variant, string? vfoId, IAfPlugin plugin, Action<AfPluginResult> publish)
        {
            InstanceId = instanceId;
            PluginId = pluginId;
            Variant = variant;
            VfoId = string.IsNullOrWhiteSpace(vfoId) ? "main" : vfoId;
            Plugin = plugin;
            _publish = publish;
            _pluginResultHandler = result =>
            {
                if (Enabled || result.Kind.Equals("OVERLAY", StringComparison.OrdinalIgnoreCase))
                    _publish(WithInstanceId(result));
            };
            Plugin.ResultAvailable += _pluginResultHandler;
            _worker = Task.Run(ProcessLoop);
        }

        public AfPluginResult WithInstanceId(AfPluginResult result) => result with { PluginId = InstanceId };
        public void Configure(IReadOnlyDictionary<string, string> options)
        {
            try { Plugin.Configure(options); }
            catch (Exception exception)
            {
                _publish(new AfPluginResult(InstanceId, "ERROR", exception.GetBaseException().Message, DateTime.UtcNow));
            }
        }
        public void SubmitAsync(float[] current, int sampleRate, DateTime timestampUtc)
        {
            if (!Enabled || _queue.Reader.Count > 1) return;
            var rented = ArrayPool<float>.Shared.Rent(current.Length);
            current.AsSpan().CopyTo(rented);
            if (!_queue.Writer.TryWrite(new QueuedBlock(rented, current.Length, sampleRate, timestampUtc)))
                ArrayPool<float>.Shared.Return(rented);
        }
        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            if (!enabled)
            {
                while (_queue.Reader.TryRead(out var block))
                    ArrayPool<float>.Shared.Return(block.Buffer);
            }
        }
        private async Task ProcessLoop()
        {
            await foreach (var block in _queue.Reader.ReadAllAsync())
            {
                if (!Enabled)
                {
                    ArrayPool<float>.Shared.Return(block.Buffer);
                    continue;
                }
                try
                {
                    var audio = new AfAudioBlock(block.Buffer.AsMemory(0, block.Length), block.SampleRate, block.TimestampUtc);
                    var result = Plugin.Process(audio);
                    if (Enabled && result is not null) _publish(WithInstanceId(result));
                }
                catch (Exception exception)
                {
                    if (Enabled) _publish(new AfPluginResult(InstanceId, "ERROR",
                        exception.GetBaseException().Message, DateTime.UtcNow));
                }
                finally { ArrayPool<float>.Shared.Return(block.Buffer); }
            }
        }
        public void Dispose()
        {
            Enabled = false;
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var block))
                ArrayPool<float>.Shared.Return(block.Buffer);
            try { _worker.Wait(2_000); } catch { }
            Plugin.ResultAvailable -= _pluginResultHandler;
            Plugin.Dispose();
        }
    }
}
