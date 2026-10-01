using System.Threading.Channels;
using NeuroSDR.Core;

namespace NeuroSDR.Plugins;

internal sealed class IqPluginHost : IDisposable
{
    private readonly Dictionary<string, Runner> _runners;

    public IqPluginHost(IqPluginCatalog catalog, IEnumerable<string> enabledIds)
    {
        var enabled = enabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _runners = catalog.Plugins.ToDictionary(
            registration => registration.Info.Id,
            registration => new Runner(registration.Create(), enabled.Contains(registration.Info.Id), Publish),
            StringComparer.OrdinalIgnoreCase);
    }

    public event Action<IqPluginResult>? ResultAvailable;
    public IReadOnlyList<IqPluginInfo> Plugins => _runners.Values.Select(runner => runner.Plugin.Info).ToArray();
    internal bool IsEnabled(string id) => _runners.TryGetValue(id, out var runner) && runner.Enabled;
    internal bool HasEnabled => _runners.Values.Any(runner => runner.Enabled);

    public void Configure(string id, IReadOnlyDictionary<string, string> options)
    {
        if (!_runners.TryGetValue(id, out var runner)) return;
        try { runner.Plugin.Configure(options); }
        catch (Exception exception) { PublishError(id, exception); }
    }

    public void Apply(IEnumerable<string> enabledIds)
    {
        var enabled = enabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var runner in _runners.Values)
        {
            var shouldRun = enabled.Contains(runner.Plugin.Info.Id);
            runner.SetEnabled(shouldRun);
            runner.Plugin.Configure(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = shouldRun.ToString()
            });
        }
    }

    /// <summary>
    /// Feeds IQ through enabled plugins. Returns the (possibly replaced) sample array for demod / spectrum.
    /// </summary>
    public Complex32[] Process(Complex32[] input, int sampleRate, long centerFrequencyHz, DateTime timestampUtc)
    {
        var current = input;
        foreach (var runner in _runners.Values)
        {
            if (!runner.Enabled) continue;
            var synchronousOutput = runner.Plugin.Info.Capabilities.HasFlag(IqPluginCapabilities.IqOutput) &&
                (runner.Plugin is not IConditionalIqOutputPlugin conditional || conditional.ProducesIqOutput);
            if (synchronousOutput)
            {
                try
                {
                    var result = runner.Plugin.Process(new IqSampleBlock(current, sampleRate, centerFrequencyHz, timestampUtc));
                    if (result is not null) Publish(result);
                    if (result?.OutputSamples is { Length: > 0 } output) current = output;
                }
                catch (Exception exception) { PublishError(runner.Plugin.Info.Id, exception); }
            }
            else
            {
                runner.Submit(current, sampleRate, centerFrequencyHz, timestampUtc);
            }
        }
        return current;
    }

    private void Publish(IqPluginResult result)
    {
        try { ResultAvailable?.Invoke(result); } catch { }
    }

    private void PublishError(string id, Exception exception) =>
        Publish(new IqPluginResult(id, "ERROR", exception.GetBaseException().Message, DateTime.UtcNow));

    public void Dispose()
    {
        foreach (var runner in _runners.Values) runner.Dispose();
    }

    private sealed class Runner : IDisposable
    {
        private readonly Channel<IqSampleBlock> _queue = Channel.CreateBounded<IqSampleBlock>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        private readonly Action<IqPluginResult> _publish;
        private readonly Action<IqPluginResult> _pluginResultHandler;
        private readonly Task _worker;
        private int _queuedOrProcessing;
        private Complex32[]? _reusableInput;
        public IIqPlugin Plugin { get; }
        public volatile bool Enabled;

        public Runner(IIqPlugin plugin, bool enabled, Action<IqPluginResult> publish)
        {
            Plugin = plugin;
            Enabled = enabled;
            _publish = publish;
            _pluginResultHandler = result => { if (Enabled) _publish(result); };
            Plugin.ResultAvailable += _pluginResultHandler;
            _worker = Task.Run(ProcessLoop);
        }

        public void Submit(Complex32[] samples, int sampleRate, long centerFrequencyHz, DateTime timestampUtc)
        {
            if (!Enabled || Interlocked.CompareExchange(ref _queuedOrProcessing, 1, 0) != 0) return;
            var copy = _reusableInput;
            if (copy is null || copy.Length != samples.Length)
                copy = _reusableInput = new Complex32[samples.Length];
            samples.AsSpan().CopyTo(copy);
            var block = new IqSampleBlock(copy, sampleRate, centerFrequencyHz, timestampUtc);
            if (_queue.Writer.TryWrite(block)) return;
            Interlocked.Exchange(ref _queuedOrProcessing, 0);
        }

        public void SetEnabled(bool enabled)
        {
            Enabled = enabled;
            if (!enabled)
            {
                while (_queue.Reader.TryRead(out _)) { }
                Interlocked.Exchange(ref _queuedOrProcessing, 0);
            }
        }

        private async Task ProcessLoop()
        {
            await foreach (var block in _queue.Reader.ReadAllAsync())
            {
                if (!Enabled)
                {
                    Interlocked.Exchange(ref _queuedOrProcessing, 0);
                    continue;
                }
                try
                {
                    var result = Plugin.Process(block);
                    if (Enabled && result is not null) _publish(result);
                }
                catch (Exception exception)
                {
                    if (Enabled)
                        _publish(new IqPluginResult(Plugin.Info.Id, "ERROR", exception.GetBaseException().Message, DateTime.UtcNow));
                }
                finally { Interlocked.Exchange(ref _queuedOrProcessing, 0); }
            }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            try { _worker.Wait(2_000); } catch { }
            Plugin.ResultAvailable -= _pluginResultHandler;
            Plugin.Dispose();
        }
    }
}
