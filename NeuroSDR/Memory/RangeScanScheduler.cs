namespace NeuroSDR.Memory;

internal sealed class RangeScanScheduler
{
    private long _start, _end, _step, _current, _nextDue;
    private bool _hasCurrent;

    public bool IsRunning { get; private set; }
    public bool IsHolding { get; private set; }
    public int DwellMilliseconds { get; set; } = 500;
    public bool HoldOnSignal { get; set; } = true;
    public float SignalThresholdDb { get; set; } = -75;

    public void Start(long startFrequency, long endFrequency, long stepFrequency, long nowMilliseconds)
    {
        _start = Math.Min(startFrequency, endFrequency);
        _end = Math.Max(startFrequency, endFrequency);
        _step = Math.Max(1, Math.Abs(stepFrequency));
        _current = _start;
        _hasCurrent = false;
        _nextDue = nowMilliseconds;
        IsRunning = _end > 0 && _end >= _start;
        IsHolding = false;
    }

    public void Stop()
    {
        IsRunning = false;
        IsHolding = false;
        _hasCurrent = false;
    }

    public long? Poll(long nowMilliseconds, float signalDb)
    {
        if (!IsRunning || nowMilliseconds < _nextDue) return null;
        if (_hasCurrent && HoldOnSignal && signalDb >= SignalThresholdDb)
        {
            IsHolding = true;
            _nextDue = nowMilliseconds + 200;
            return null;
        }

        IsHolding = false;
        if (!_hasCurrent)
        {
            _current = _start;
            _hasCurrent = true;
        }
        else
        {
            _current = _current > _end - _step ? _start : _current + _step;
        }
        _nextDue = nowMilliseconds + Math.Clamp(DwellMilliseconds, 100, 60_000);
        return _current;
    }
}
