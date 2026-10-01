// Port of KiwiSDR JNX.js — Copyright (C) 2011 Paul Lutus (GPL-2.0+)
// Sample rate: designed for 12000 Hz (Kiwi AF). Also works at 48000.

namespace NeuroSDR.Plugins.Kiwi.Jnx;

/// <summary>
/// Multi-encoding FSK demodulator (ITA2, ASCII, CCIR476, DSC, Selcall).
/// Feed mono PCM16 via <see cref="ProcessPcm16"/>. Prefer <see cref="JnxConfig.SampleRate"/> = 12000.
/// </summary>
public sealed class JnxDecoder : IDisposable
{
    enum State { Nosignal, Sync1, Sync2, ReadData, FixedLength }
    enum FrontPorch { Off, Wait, Wait2, Start, Pass, Sync, SyncStart }

    static readonly double InvSqrt2 = 1.0 / Math.Sqrt(2);

    readonly BiQuadraticFilter _biquadMark = new();
    readonly BiQuadraticFilter _biquadSpace = new();
    readonly BiQuadraticFilter _biquadLowpass = new();
    readonly List<int> _syncChars = new();

    JnxConfig _cfg = new();
    IJnxEncoding? _encoding;
    State _state = State.Nosignal;
    FrontPorch _fp = FrontPorch.Off;

    double _sampleRate = 12000;
    double _centerFrequency = 1000;
    double _deviationF;
    double _lowpassFilterF = 140;
    double _audioAverageTc;
    double _audioAverage;
    double _audioMinimum = 64;
    const double AudioMinimumFloor = 16;
    double _markSpaceFilterQ;
    double _markF, _spaceF;

    int _bitSampleCount, _halfBitSampleCount;
    long _sampleCount, _nextEventCount;
    int _bitDuration;
    int _signalAccumulator;
    bool _oldMarkState, _averagedMarkState, _pulseEdgeEvent;
    bool _inverted;
    bool _stopVariable;
    bool _syncSetup;
    bool _waiting;
    bool _showRaw, _showErrs;
    int _bitCount, _codeBits, _errorCount, _validCount;
    int _msb, _nbits;
    int _succeedTally, _failTally;
    int _fpCount, _fpMarkBits, _fpPassBits;
    int _fixedStart;
    double _syncDelta, _baudError;
    int _zeroCrossingCount;
    int[]? _zeroCrossings;
    const int ZeroCrossingSamples = 16;
    const int ZeroCrossingsDivisor = 4;
    bool _init = true;
    bool _disposed;

    public event Action<string>? TextReceived;
    public event Action<string>? StatusChanged;

    /// <summary>Apply configuration and rebuild filters / encoder. Resets demod state.</summary>
    public void Configure(JnxConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cfg = config ?? new JnxConfig();
        SetupValues();
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetupValues();
    }

    /// <summary>Process mono PCM16 samples at <see cref="JnxConfig.SampleRate"/> (prefer 12000 Hz).</summary>
    public void ProcessPcm16(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_encoding == null) return;
        for (int i = 0; i < samples.Length; i++)
            ProcessOne(samples[i]);
    }

    void SetupValues()
    {
        _sampleRate = _cfg.SampleRate < 1000 ? 48000 : _cfg.SampleRate;
        _centerFrequency = _cfg.CenterFrequencyHz;
        double baud = _cfg.BaudRate < 10 ? 10 : _cfg.BaudRate;
        // Kiwi FSK.js doubles baud when framing ends with ".5" (half-bit sampling).
        string framing = _cfg.Framing ?? "5N1.5";
        if (framing.EndsWith(".5", StringComparison.Ordinal))
            baud *= 2;

        double shift = _cfg.ShiftHz <= 0 ? 170 : _cfg.ShiftHz;
        _deviationF = shift / 2.0;
        _lowpassFilterF = 140;
        _audioAverageTc = 1000.0 / _sampleRate;
        // 16-bit AF: kiwi used 256; lower floor so weak host AF still leaves NOSIGNAL.
        _audioMinimum = Math.Max(AudioMinimumFloor, 64 * (_sampleRate / 12000.0));
        _bitSampleCount = Math.Max(1, (int)(_sampleRate * (1.0 / baud) + 0.5));
        _halfBitSampleCount = Math.Max(1, _bitSampleCount / 2);
        _stopVariable = framing.EndsWith('V') || framing.Contains("EFR", StringComparison.Ordinal);

        if (framing == "CHU")
        {
            _fp = FrontPorch.Wait;
            _fpMarkBits = 32;
            _fpPassBits = 11 * 10;
        }
        else if (_cfg.Encoding is JnxEncoding.Dsc or JnxEncoding.Selcall)
            _fp = FrontPorch.Sync;
        else
            _fp = FrontPorch.Off;

        _inverted = _cfg.Inverted;
        _showRaw = _cfg.ShowRaw;
        _showErrs = _cfg.ShowErrs;

        Action<string> outCb = EmitText;
        _encoding = _cfg.Encoding switch
        {
            JnxEncoding.Ascii => new FskAsyncEncoding(framing, "ASCII"),
            JnxEncoding.Ccir476 => new Ccir476Encoding(),
            JnxEncoding.Dsc => new DscEncoding(_init, outCb, _cfg.GetFrequencyHz),
            JnxEncoding.Selcall => new SelcallEncoding(_cfg.GetFrequencyHz),
            _ => new FskAsyncEncoding(framing, "ITA2")
        };
        if (_cfg.Encoding is JnxEncoding.Dsc or JnxEncoding.Selcall)
            _init = false;

        _msb = _encoding.GetMsb();
        _nbits = _encoding.GetNbits();
        _pulseEdgeEvent = false;
        _errorCount = 0;
        _validCount = 0;
        _sampleCount = 0;
        _nextEventCount = 0;
        _averagedMarkState = false;
        _zeroCrossingCount = 0;
        _zeroCrossings = new int[Math.Max(1, _bitSampleCount / ZeroCrossingsDivisor)];
        _syncDelta = 0;
        _audioAverage = 0;
        _signalAccumulator = 0;
        _bitDuration = 0;
        _syncChars.Clear();
        _syncSetup = false;
        _waiting = false;
        _bitCount = 0;
        _codeBits = 0;
        UpdateFilters();
        SetState(State.Nosignal);
    }

    void UpdateFilters()
    {
        _markSpaceFilterQ = 6 * _centerFrequency / 1000.0;
        double qv = _centerFrequency + 4.0 * 1000 / _centerFrequency;
        _markF = qv + _deviationF;
        _spaceF = qv - _deviationF;
        _biquadMark.Configure(BiQuadraticFilter.FilterType.Bandpass, _markF, _sampleRate, _markSpaceFilterQ);
        _biquadSpace.Configure(BiQuadraticFilter.FilterType.Bandpass, _spaceF, _sampleRate, _markSpaceFilterQ);
        _biquadLowpass.Configure(BiQuadraticFilter.FilterType.Lowpass, _lowpassFilterF, _sampleRate, InvSqrt2);
    }

    void SetState(State s)
    {
        if (s == _state) return;
        _state = s;
        StatusChanged?.Invoke(s.ToString().ToUpperInvariant());
    }

    void EmitText(string s)
    {
        if (!string.IsNullOrEmpty(s))
            TextReceived?.Invoke(s);
    }

    void ProcessOne(short sample)
    {
        double dv = sample;
        double markLevel = _biquadMark.Filter(dv);
        double spaceLevel = _biquadSpace.Filter(dv);
        double markAbs = Math.Abs(markLevel);
        double spaceAbs = Math.Abs(spaceLevel);

        _audioAverage += (Math.Max(markAbs, spaceAbs) - _audioAverage) * _audioAverageTc;
        _audioAverage = Math.Max(0.1, _audioAverage);

        double diffAbs = (markAbs - spaceAbs) / _audioAverage;
        double logicLevel = _biquadLowpass.Filter(diffAbs);
        bool markState = logicLevel > 0;
        _signalAccumulator += markState ? 1 : -1;
        _bitDuration++;

        if (_zeroCrossings != null)
        {
            if (markState != _oldMarkState)
            {
                if ((_bitDuration % _bitSampleCount) > _halfBitSampleCount)
                {
                    int index = (int)((_sampleCount - _nextEventCount + _bitSampleCount * 8L) % _bitSampleCount);
                    int zi = index / ZeroCrossingsDivisor;
                    if ((uint)zi < (uint)_zeroCrossings.Length)
                        _zeroCrossings[zi]++;
                }
                _bitDuration = 0;
            }
            _oldMarkState = markState;

            if (_sampleCount % _bitSampleCount == 0)
            {
                _zeroCrossingCount++;
                if (_zeroCrossingCount >= ZeroCrossingSamples)
                {
                    int best = 0, index = 0;
                    for (int j = 0; j < _zeroCrossings.Length; j++)
                    {
                        int q = _zeroCrossings[j];
                        _zeroCrossings[j] = 0;
                        if (q > best) { best = q; index = j; }
                    }
                    if (best > 0)
                    {
                        index *= ZeroCrossingsDivisor;
                        index = ((index + _halfBitSampleCount) % _bitSampleCount) - _halfBitSampleCount;
                        index /= 8;
                        _syncDelta = index;
                        _baudError = index;
                    }
                    _zeroCrossingCount = 0;
                }
            }
        }

        _pulseEdgeEvent = _sampleCount >= _nextEventCount;
        if (_pulseEdgeEvent)
        {
            _averagedMarkState = (_signalAccumulator > 0) ^ _inverted;
            _signalAccumulator = 0;
            _nextEventCount = _sampleCount + _bitSampleCount + (long)Math.Floor(_syncDelta + 0.5);
            _syncDelta = 0;
        }

        if (_audioAverage < _audioMinimum && _state != State.Nosignal)
            SetState(State.Nosignal);
        else if (_audioAverage >= _audioMinimum && _state == State.Nosignal)
            _syncSetup = true;

        if (!_pulseEdgeEvent)
        {
            _sampleCount++;
            return;
        }

        int bit = _averagedMarkState ? 1 : 0;

        if (_fp is FrontPorch.Start or FrontPorch.Pass)
        {
            if (_fp == FrontPorch.Start) _fp = FrontPorch.Pass;
            _fpCount++;
            if (_fpCount > _fpPassBits) _fp = FrontPorch.Wait;
        }

        if (_fp is FrontPorch.Wait or FrontPorch.Wait2)
        {
            if (_fp == FrontPorch.Wait)
            {
                _fpCount = 0;
                _fp = FrontPorch.Wait2;
            }
            if (_fpCount >= _fpMarkBits && bit == 0)
            {
                _fpCount = 0;
                _syncSetup = true;
                _fp = FrontPorch.Start;
            }
            else
            {
                if (bit == 1) _fpCount++; else _fpCount = 0;
                _sampleCount++;
                return;
            }
        }

        if (_fp == FrontPorch.Sync)
        {
            if (_encoding!.SearchSync(bit))
            {
                _syncSetup = true;
                _fp = FrontPorch.SyncStart;
                _sampleCount++;
                return;
            }
        }

        if (_syncSetup)
        {
            _bitCount = 0;
            _codeBits = 0;
            _errorCount = 0;
            _validCount = 0;
            _encoding!.Reset();
            _syncChars.Clear();
            if (_fp is FrontPorch.Start or FrontPorch.SyncStart)
                SetState(State.FixedLength);
            else
                SetState(State.Sync1);
            _syncSetup = false;
        }

        switch (_state)
        {
            case State.Nosignal:
                break;

            case State.Sync1:
                _codeBits = (_codeBits >> 1) | (bit != 0 ? _msb : 0);
                if (_encoding!.CheckBits(_codeBits))
                {
                    _syncChars.Add(_codeBits);
                    _validCount++;
                    _bitCount = 0;
                    _codeBits = 0;
                    SetState(State.Sync2);
                    _waiting = true;
                }
                break;

            case State.Sync2:
                if (_stopVariable && _waiting && bit == 1) break;
                _waiting = false;
                _codeBits = (_codeBits >> 1) | (bit != 0 ? _msb : 0);
                _bitCount++;
                if (_bitCount == _nbits)
                {
                    if (_encoding!.CheckBits(_codeBits))
                    {
                        _syncChars.Add(_codeBits);
                        _codeBits = 0;
                        _bitCount = 0;
                        _validCount++;
                        if (_validCount == 4)
                        {
                            foreach (int sc in _syncChars)
                            {
                                var rv = _encoding.ProcessChar(sc, 0, EmitText, _showRaw, _showErrs);
                                if (rv.Tally == 1) _succeedTally++;
                                else if (rv.Tally == -1) _failTally++;
                            }
                            SetState(State.ReadData);
                        }
                    }
                    else
                    {
                        _codeBits = 0;
                        _bitCount = 0;
                        _syncSetup = true;
                    }
                    _waiting = true;
                }
                break;

            case State.ReadData:
                if (_stopVariable && _waiting && bit == 1) break;
                _waiting = false;
                _codeBits = (_codeBits >> 1) | (bit != 0 ? _msb : 0);
                _bitCount++;
                if (_bitCount == _nbits)
                {
                    var rv = _encoding!.ProcessChar(_codeBits, 0, EmitText, _showRaw, _showErrs);
                    if (rv.Tally == 1) _succeedTally++;
                    else if (rv.Tally == -1) _failTally++;
                    if (rv.Success)
                    {
                        if (_errorCount > 0) _errorCount--;
                    }
                    else
                    {
                        _errorCount++;
                        if (_errorCount > 2) _syncSetup = true;
                    }
                    _bitCount = 0;
                    _codeBits = 0;
                    _waiting = true;
                }
                break;

            case State.FixedLength:
                if (_fp is FrontPorch.Start or FrontPorch.SyncStart) _fixedStart = 1;
                _codeBits = (_codeBits >> 1) | (bit != 0 ? _msb : 0);
                _bitCount++;
                if (_bitCount == _nbits)
                {
                    var rv = _encoding!.ProcessChar(_codeBits, _fixedStart, EmitText, _showRaw, _showErrs);
                    if (rv.Resync)
                    {
                        _fp = FrontPorch.Sync;
                        SetState(State.Nosignal);
                    }
                    _bitCount = 0;
                    _codeBits = 0;
                    _fixedStart = 0;
                }
                break;
        }

        _sampleCount++;
    }

    public void Dispose()
    {
        _disposed = true;
        TextReceived = null;
        StatusChanged = null;
    }
}
