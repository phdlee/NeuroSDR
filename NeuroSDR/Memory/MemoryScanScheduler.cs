namespace NeuroSDR.Memory;

internal sealed class MemoryScanScheduler
{
    private IReadOnlyList<MemoryChannel> _channels = [];
    private int _index = -1;
    private long _nextDue;

    public bool IsRunning { get; private set; }
    public bool IsHolding { get; private set; }
    public int DwellMilliseconds { get; set; } = 800;
    public bool HoldOnSignal { get; set; } = true;
    public float SignalThresholdDb { get; set; } = -75;

    public void Start(IReadOnlyList<MemoryChannel> channels, long nowMilliseconds)
    {
        _channels = channels.ToArray();
        _index = -1;
        _nextDue = nowMilliseconds;
        IsRunning = _channels.Count > 0;
        IsHolding = false;
    }

    public void Stop()
    {
        IsRunning = false;
        _channels = [];
        _index = -1;
        IsHolding = false;
    }

    public MemoryChannel? Poll(long nowMilliseconds, float signalDb)
    {
        if (!IsRunning || nowMilliseconds < _nextDue)
            return null;
        if (_index >= 0 && HoldOnSignal && signalDb >= SignalThresholdDb)
        {
            IsHolding = true;
            _nextDue = nowMilliseconds + 200;
            return null;
        }
        IsHolding = false;
        _index = (_index + 1) % _channels.Count;
        _nextDue = nowMilliseconds + Math.Clamp(DwellMilliseconds, 100, 60_000);
        return _channels[_index];
    }
}
