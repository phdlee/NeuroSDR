namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// UHSDR / KiwiSDR CW decoder port (uhsdr_cw_decoder.cpp).
/// Operates on mono PCM16; intended sample rate is 12 kHz with block size 88.
/// </summary>
internal sealed class KiwiCwDecoder
{
    public const int NominalSampleRate = 12_000;
    public const int DefaultBlockSize = 88;
    public const float DefaultPitchHz = 700f;
    public const float DefaultThresholdDb = 47f;

    private const float SignalTau = 0.1f;
    private const float OneMinusSignalTau = 1f - SignalTau;
    private const uint AutoWeightLinear = 32_000;
    private const uint AutoThresholdLinear = 15_849;
    private const int TrainingStable = 32;
    private const int CwTimeoutSec = 3;
    private const int ErrorTimeoutSec = 8;
    private const int SpikeCancelMaxDuration = 8;
    private const int SigBufSize = 256;
    private const int DataBufSize = 40;
    private const int BlockSizeMax = 128;
    private const int Dit = 2;
    private const int Dah = 3;
    private const int SpikeCancelOff = 0;
    private const int SpikeCancelSpike = 1;
    private const int SpikeCancelShort = 2;
    private const int ParisElems = 50;

    private readonly object _sync = new();
    private readonly float[] _raw = new float[BlockSizeMax];
    private readonly SigBuf[] _sig = new SigBuf[SigBufSize];
    private readonly SigBuf[] _data = new SigBuf[DataBufSize];
    private Goertzel _goertzel;
    private BFlags _b;
    private CwTimes _times;

    private float _samplingFreq = NominalSampleRate;
    private float _targetFreq = DefaultPitchHz;
    private byte _blocksize = DefaultBlockSize;
    private bool _isAutoThreshold;
    private uint _weightLinear;
    private uint _thresholdLinear = DbToLinear(DefaultThresholdDb);
    private byte _noiseCancelEnable = 1;
    private byte _spikeCancel;
    private int _wsc = 1;
    private int _trainingInterval = 100;
    private int _wpmFixed;
    private int _errCnt;
    private long _errTimeoutSec;
    private long _wpmUpdateBucket = -1;

    private int _sigLastRx;
    private int _sigInCount;
    private int _sigOutCount;
    private int _sigTimer;
    private byte _dataLen;
    private uint _code;
    private bool _state;
    private int _timerStepSize = 1;
    private int _curTime;
    private int _curOutCount;
    private int _lastOutCount;

    private float _cwEnv;
    private float _cwMag;
    private float _cwNoise;
    private float _oldSigLevel = 0.001f;
    private float _speedWpmAvg;
    private bool _prevState;
    private bool _noiseCancelChange;
    private ushort _sampleCounter;
    private short _startPos, _progress;
    private bool _initializing;
    private bool _spike;
    private bool _processed;
    private bool _processSamples;
    private bool _configured;
    private string _status = "Idle";

    public int SpeedWpm => (int)_speedWpmAvg;
    public string Status => _status;
    public float PitchHz => _targetFreq;
    public bool AutoWpm => _b.Track;

    public event Action<string>? CharacterDecoded;
    public event Action<string>? StatusChanged;
    public event Action<int>? WpmChanged;

    /// <param name="pitchHz">Passband offset (tone) frequency for Goertzel.</param>
    /// <param name="fixedWpm">0 = auto-train WPM; otherwise fixed speed.</param>
    /// <param name="trainingInterval">Mark/space states used while training (default 100).</param>
    /// <param name="wordSpaceCorrection">English word-space correction (WSC).</param>
    /// <param name="autoThreshold">Adaptive envelope threshold.</param>
    /// <param name="thresholdDb">Fixed threshold in dB (linear = 10^(dB/10)).</param>
    /// <param name="sampleRateHz">Decoder sample rate (prefer 12000).</param>
    public void Configure(
        float pitchHz = DefaultPitchHz,
        int fixedWpm = 0,
        int trainingInterval = 100,
        bool wordSpaceCorrection = true,
        bool autoThreshold = false,
        float thresholdDb = DefaultThresholdDb,
        float sampleRateHz = NominalSampleRate)
    {
        lock (_sync)
        {
            InitCore(fixedWpm, trainingInterval, sampleRateHz);
            _wsc = wordSpaceCorrection ? 1 : 0;
            SetThreshold(autoThreshold, thresholdDb);
            SetPitch(pitchHz);
            _configured = true;
            SetStatus(fixedWpm == 0
                ? $"KiwiCW train · pitch {pitchHz:0} Hz"
                : $"KiwiCW {fixedWpm} WPM · pitch {pitchHz:0} Hz");
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            var pitch = _targetFreq;
            var wpm = _wpmFixed;
            var train = _trainingInterval;
            var wsc = _wsc != 0;
            var autoTh = _isAutoThreshold;
            var thDb = LinearToDb(_thresholdLinear);
            var sr = _samplingFreq;
            InitCore(wpm, train, sr);
            _wsc = wsc ? 1 : 0;
            SetThreshold(autoTh, thDb);
            SetPitch(pitch);
            _configured = true;
            SetStatus("Restarted");
        }
    }

    public void ProcessPcm16(ReadOnlySpan<short> samples)
    {
        lock (_sync)
        {
            if (!_configured || !_processSamples || samples.IsEmpty) return;
            for (var i = 0; i < samples.Length; i++)
            {
                // Original: samps[idx] / 4
                _raw[_sampleCounter] = samples[i] / 4f;
                _sampleCounter++;
                if (_sampleCounter >= _blocksize)
                {
                    DecodeExe();
                    _sampleCounter = 0;
                }
            }
        }
    }

    private void InitCore(int wpm, int trainingInterval, float sampleRateHz)
    {
        CwCodeTable.EnsureInitialized();

        Array.Clear(_sig);
        Array.Clear(_data);
        Array.Clear(_raw);
        _goertzel = default;
        _b = default;
        _times = default;
        _sigLastRx = _sigInCount = _sigOutCount = _sigTimer = 0;
        _dataLen = 0;
        _code = 0;
        _state = false;
        _curTime = _curOutCount = _lastOutCount = 0;
        _cwEnv = _cwMag = _cwNoise = 0;
        _oldSigLevel = 0.001f;
        _speedWpmAvg = 0;
        _prevState = false;
        _noiseCancelChange = false;
        _sampleCounter = 0;
        _startPos = _progress = 0;
        _initializing = false;
        _spike = false;
        _processed = false;
        _errCnt = 0;
        _errTimeoutSec = 0;
        _wpmUpdateBucket = -1;
        _processSamples = false;

        _samplingFreq = sampleRateHz > 0 ? sampleRateHz : NominalSampleRate;
        _blocksize = DefaultBlockSize;
        _noiseCancelEnable = 1;
        _spikeCancel = SpikeCancelOff;
        _timerStepSize = 1;
        _wpmFixed = Math.Max(0, wpm);
        _trainingInterval = Math.Clamp(trainingInterval, 20, 500);
        _wsc = 1;

        if (wpm != 0)
        {
            _speedWpmAvg = wpm;
            var blkPerMs = _samplingFreq / 1e3f / DefaultBlockSize;
            var msPerElem = 60_000f / (wpm * ParisElems);
            _times.DotAvg = blkPerMs * msPerElem;
            _times.DashAvg = _times.DotAvg * 3f;
            _times.CwSpaceAvg = _times.DotAvg * 4.2f;
            _times.SymSpaceAvg = _times.DotAvg * 0.93f;
            _times.PulseAvg = (_times.DotAvg / 4f + _times.DashAvg) / 2f;
            _b.Track = false;
            _b.Initialized = true;
            _initializing = false;
        }
        else
        {
            _b.Track = true;
        }
    }

    private void SetPitch(float pitchHz)
    {
        _targetFreq = Math.Clamp(pitchHz, 200f, 3_000f);
        CalcGoertzel(ref _goertzel, _targetFreq, _blocksize, 1f, _samplingFreq);
        _processSamples = true;
    }

    private void SetThreshold(bool autoThreshold, float thresholdDb)
    {
        _isAutoThreshold = autoThreshold;
        if (autoThreshold)
        {
            _weightLinear = AutoWeightLinear;
            _thresholdLinear = AutoThresholdLinear;
        }
        else
        {
            var db = thresholdDb > 0 ? thresholdDb : DefaultThresholdDb;
            _thresholdLinear = DbToLinear(db);
        }
    }

    private int OneSecond => (int)(_samplingFreq / _blocksize);

    private void DecodeExe()
    {
        for (var index = 0; index < _blocksize; index++)
            GoertzelInput(ref _goertzel, _raw[index]);

        var magnitudeSquared = GoertzelEnergy(ref _goertzel);
        float sigLevel = magnitudeSquared;
        bool newState;

        if (_isAutoThreshold)
        {
            _cwMag = sigLevel;
            _cwEnv = DecayAvg(_cwEnv, _cwMag,
                _cwMag > _cwEnv ? _weightLinear / 1000f / 4f : _weightLinear / 1000f * 16f);
            _cwNoise = DecayAvg(_cwNoise, _cwMag,
                _cwMag < _cwNoise ? _weightLinear / 1000f / 4f : _weightLinear / 1000f * 48f);

            var clipped = Math.Clamp(_cwEnv, _cwNoise, _cwMag);
            var envToNoise = clipped - _cwNoise;
            var v1 = (clipped - _cwNoise) * envToNoise - 0.8f * (envToNoise * envToNoise);
            v1 = MathF.Sqrt(MathF.Abs(v1)) * (v1 < 0 ? -1f : 1f);
            sigLevel = v1 * SignalTau + OneMinusSignalTau * _oldSigLevel;
            _oldSigLevel = v1;
            newState = sigLevel >= _thresholdLinear;
        }
        else
        {
            sigLevel = sigLevel * SignalTau + OneMinusSignalTau * _oldSigLevel;
            _oldSigLevel = magnitudeSquared;
            newState = sigLevel >= _thresholdLinear;
        }

        if (_noiseCancelEnable != 0)
        {
            if (_noiseCancelChange)
            {
                _state = newState;
                _noiseCancelChange = false;
            }
            else if (newState != _state)
            {
                _noiseCancelChange = true;
            }
        }
        else
        {
            _state = newState;
        }

        if (_state != _prevState)
        {
            _sig[_sigLastRx] = new SigBuf { State = _prevState, Time = (uint)_sigTimer };
            _sigLastRx = RingInc(_sigLastRx, SigBufSize);
            _sigTimer = 0;
            _prevState = _state;
        }

        _sigTimer += _timerStepSize;
        var maxTime = OneSecond * CwTimeoutSec;
        if (_sigTimer > maxTime) _sigTimer = maxTime;

        _sigInCount = _sigLastRx;
        _curTime = _sigTimer;
        Decode();

        var spdCalc = 10f * _times.DotAvg + 4f * _times.DashAvg + 9f * _times.SymSpaceAvg + 5f * _times.CwSpaceAvg;
        if (_b.Initialized && spdCalc > 0)
        {
            var msPerWord = spdCalc * 1000f / (_samplingFreq / _blocksize);
            var wpmRaw = 0.5f + 60_000f / msPerWord;
            _speedWpmAvg = wpmRaw * 0.3f + 0.7f * _speedWpmAvg;
        }
        else
        {
            _speedWpmAvg = 0;
        }
    }

    private void Decode()
    {
        if (!_b.Initialized)
            Train();

        if (_b.Initialized || _curTime >= OneSecond * CwTimeoutSec)
        {
            DataRecognition(out var received);
            if (received && _dataLen > 0)
            {
                CodeGen();
                var decoded = CharacterId(_code);
                if (decoded < 0xfe)
                {
                    PrintChar(decoded);
                    WordSpace(decoded);
                }
                else if (decoded == 0xff)
                {
                    if (!ErrorCorrection() && _b.Track)
                    {
                        _errCnt++;
                        _errTimeoutSec = NowSec() + ErrorTimeoutSec;
                        SetStatus($"train err -{_errCnt}");
                        if (_errCnt > 3)
                        {
                            _b.Initialized = false;
                            _errCnt = 0;
                            _errTimeoutSec = 0;
                            SetStatus("re-train");
                        }
                    }
                }

                if (_b.Track && _errTimeoutSec != 0 && NowSec() >= _errTimeoutSec)
                {
                    _errCnt = 0;
                    _errTimeoutSec = 0;
                    SetStatus(_b.Initialized ? $"KiwiCW {(int)_speedWpmAvg} WPM" : "training");
                }
            }
        }
    }

    private void Train()
    {
        if (!_b.Track) return;

        if (!_initializing)
        {
            _startPos = (short)_sigOutCount;
            _progress = (short)_sigOutCount;
            _initializing = true;
            _times = default;
        }

        var processed = RingDistance(_startPos, _progress);
        if (processed >= _trainingInterval)
        {
            _b.Initialized = true;
            _initializing = false;
            SetStatus($"trained · {(int)_speedWpmAvg} WPM");
        }
        else
        {
            SetStatus($"train {processed}/{_trainingInterval}");
        }

        if (_progress == _sigInCount) return;

        var t = (float)_sig[_progress].Time;
        if (_sig[_progress].State)
        {
            if (processed > TrainingStable)
            {
                if (t > _times.PulseAvg)
                    _times.DashAvg += (t - _times.DashAvg) / 4f;
                else
                    _times.DotAvg += (t - _times.DotAvg) / 4f;
            }
            else
            {
                if (t > _times.PulseAvg)
                    _times.DashAvg = (t + _times.DashAvg) / 2f;
                else
                    _times.DotAvg = (t + _times.DotAvg) / 2f;
            }
            _times.PulseAvg = (_times.DotAvg / 4f + _times.DashAvg) / 2f;
        }
        else if (t > _times.PulseAvg)
        {
            if (processed > TrainingStable)
                _times.CwSpaceAvg += (t - _times.CwSpaceAvg) / 4f;
        }
        else if (processed > TrainingStable)
        {
            _times.SymSpaceAvg += (t - _times.SymSpaceAvg) / 4f;
        }

        _progress = (short)RingInc(_progress, SigBufSize);
    }

    private bool IsSpike(uint t)
    {
        if (_spikeCancel == SpikeCancelSpike)
            return t <= SpikeCancelMaxDuration;
        if (_spikeCancel == SpikeCancelShort)
            return 3 * t < _times.DotAvg && _b.Initialized;
        return false;
    }

    private float SpikeCancel(float t)
    {
        if (_spikeCancel == SpikeCancelOff) return t;

        if (IsSpike((uint)t))
        {
            _spike = true;
            _sigOutCount = RingInc(_sigOutCount, SigBufSize);
            return 0f;
        }

        if (_spike)
        {
            _spike = false;
            t = t
                + _sig[RingChange(_sigOutCount, -1, SigBufSize)].Time
                + _sig[RingChange(_sigOutCount, -2, SigBufSize)].Time;
        }
        return t;
    }

    private bool DataRecognition(out bool newChar)
    {
        var notDone = false;
        newChar = false;

        if (_sigOutCount != _sigInCount)
        {
            notDone = true;
            _b.Timeout = false;
            var t = SpikeCancel(_sig[_sigOutCount].Time);
            if (t > 0)
            {
                var isMark = _sig[_sigOutCount].State;
                _sigOutCount = RingInc(_sigOutCount, SigBufSize);

                if (isMark)
                {
                    _processed = false;
                    if (_times.PulseAvg - t >= 0)
                    {
                        _b.Dash = false;
                        _data[_dataLen].State = false;
                        if (_b.Track)
                            _times.DotAvg += (t - _times.DotAvg) / 8f;
                    }
                    else
                    {
                        _b.Dash = true;
                        _data[_dataLen].State = true;
                        if (t <= 5 * _times.DashAvg && _b.Track)
                            _times.DashAvg += (t - _times.DashAvg) / 8f;
                    }
                    _data[_dataLen].Time = (uint)t;
                    _dataLen++;
                    if (_b.Track)
                        _times.PulseAvg = (_times.DotAvg / 4f + _times.DashAvg) / 2f;
                }
                else
                {
                    var fullChar = true;
                    if (_b.Dash)
                    {
                        _b.Dash = false;
                        var eq412 = t - (_times.PulseAvg
                            - (_data[_dataLen - 1].Time - _times.PulseAvg) / 4f);
                        if (eq412 < 0)
                        {
                            if (_b.Track)
                                _times.SymSpaceAvg += (t - _times.SymSpaceAvg) / 8f;
                            fullChar = false;
                        }
                        else if (t <= 10 * _times.DashAvg)
                        {
                            var eq414 = t - (_times.CwSpaceAvg
                                - (_data[_dataLen - 1].Time - _times.PulseAvg) / 4f);
                            if (eq414 >= 0)
                            {
                                _times.WSpace = (int)t;
                                _b.WSpace = true;
                            }
                        }
                    }
                    else
                    {
                        if (t - _times.PulseAvg < 0)
                        {
                            if (_b.Track)
                                _times.SymSpaceAvg += (t - _times.SymSpaceAvg) / 8f;
                            fullChar = false;
                        }
                        else if (t <= 10 * _times.DashAvg)
                        {
                            if (_b.Track)
                                _times.CwSpaceAvg += (t - _times.CwSpaceAvg) / 8f;
                            if (t - _times.CwSpaceAvg >= 0)
                            {
                                _times.WSpace = (int)t;
                                _b.WSpace = true;
                            }
                        }
                    }

                    if (fullChar && !_processed)
                        newChar = true;
                }
            }
        }
        else if (_curTime > 10 * _times.DashAvg)
        {
            if (!_sig[_sigInCount].State && !_processed)
            {
                _processed = true;
                _b.WSpace = true;
                _b.Timeout = true;
                newChar = true;
            }
        }

        if (_dataLen > DataBufSize - 2)
            _dataLen = DataBufSize - 2;

        if (newChar)
        {
            _lastOutCount = _curOutCount;
            _curOutCount = _sigOutCount;
        }
        return notDone;
    }

    private void CodeGen()
    {
        _code = 0;
        for (var a = 0; a < _dataLen; a++)
        {
            _code <<= 2;
            _code |= _data[a].State ? (uint)Dah : Dit;
        }
        _dataLen = 0;
    }

    private void PrintChar(byte c)
    {
        string s;
        if (c >= 0x7f)
            s = "[err]";
        else if (c < (byte)' ')
            s = CwCodeTable.Prosign(c);
        else
            s = ((char)c).ToString();

        CharacterDecoded?.Invoke(s);

        var bucket = NowSec() / 4;
        if (_wpmUpdateBucket != bucket)
        {
            _wpmUpdateBucket = bucket;
            var wpm = (int)_speedWpmAvg;
            WpmChanged?.Invoke(wpm);
            if (_b.Initialized)
                SetStatus($"KiwiCW {wpm} WPM · pitch {_targetFreq:0} Hz");
        }
    }

    private void WordSpace(byte c)
    {
        if (!_b.WSpace) return;
        _b.WSpace = false;

        if (_wsc != 0 && c is (byte)'I' or (byte)'J' or (byte)'Q' or (byte)'U' or (byte)'V' or (byte)'Z')
        {
            var x = (_times.CwSpaceAvg + _times.PulseAvg) - _times.WSpace;
            if (x < 0)
                CharacterDecoded?.Invoke(" ");
        }
        else
        {
            CharacterDecoded?.Invoke(" ");
        }
    }

    private bool ErrorCorrection()
    {
        var result = false;

        if (_dataLen >= DataBufSize - 2)
        {
            PrintChar(0xff);
            WordSpace(0xff);
            return false;
        }

        _b.WSpace = false;
        var tempOut = _lastOutCount;
        var sLocation = _lastOutCount;
        var pLocation = _lastOutCount;
        uint pDuration = uint.MaxValue;
        uint sDuration = 0;

        // Faithful to C: last-state test uses raw (cur_outcount - 1), not ring-wrapped.
        while (tempOut != _curOutCount)
        {
            if (_sig[tempOut].State)
            {
                if (_sig[tempOut].Time < pDuration && !IsSpike(_sig[tempOut].Time))
                {
                    pDuration = _sig[tempOut].Time;
                    pLocation = tempOut;
                }
            }

            if (tempOut != _lastOutCount
                && tempOut != _curOutCount - 1
                && !_sig[tempOut].State
                && _sig[tempOut].Time > sDuration)
            {
                sDuration = _sig[tempOut].Time;
                sLocation = tempOut;
            }

            tempOut = RingInc(tempOut, SigBufSize);
        }

        Span<byte> decoded = stackalloc byte[2] { 0xff, 0xff };

        if (pDuration < _times.DotAvg / 2f && pLocation != tempOut)
        {
            _sig[RingChange(pLocation, +1, SigBufSize)].Time =
                _sig[RingChange(pLocation, -1, SigBufSize)].Time
                + _sig[pLocation].Time
                + _sig[RingChange(pLocation, +1, SigBufSize)].Time;

            tempOut = RingChange(pLocation, -2, SigBufSize);
            while (tempOut != _lastOutCount)
            {
                _sig[RingChange(tempOut, +2, SigBufSize)].Time = _sig[tempOut].Time;
                _sig[RingChange(tempOut, +2, SigBufSize)].State = _sig[tempOut].State;
                tempOut = RingDec(tempOut, SigBufSize);
            }

            _sigOutCount = RingChange(_lastOutCount, +2, SigBufSize);
            for (var i = 0; i < 1024 && DataRecognition(out _); i++) { }
            CodeGen();
            decoded[0] = CharacterId(_code);
            if (decoded[0] < 0xfe)
            {
                PrintChar(decoded[0]);
                result = true;
            }
            else
            {
                PrintChar(0xff);
            }
        }
        else
        {
            _sig[sLocation].Time = (uint)Math.Max(1, (int)_times.CwSpaceAvg - 1);
            _sigOutCount = _lastOutCount;
            for (var i = 0; i < 1024 && DataRecognition(out _); i++) { }
            CodeGen();
            decoded[0] = CharacterId(_code);
            for (var i = 0; i < 1024 && DataRecognition(out _); i++) { }
            CodeGen();
            decoded[1] = CharacterId(_code);

            if (decoded[0] < 0xfe && decoded[1] < 0xfe)
            {
                PrintChar(decoded[0]);
                PrintChar(decoded[1]);
                result = true;
            }
            else
            {
                PrintChar(0xff);
            }
        }

        return result;
    }

    private void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private static byte CharacterId(uint code)
    {
        if (code == 0) return 0xfe;
        return CwCodeTable.Lookup(code);
    }

    private static void CalcGoertzel(ref Goertzel g, float freq, uint size, float coeff, float samplerate)
    {
        g.A = (int)(0.5 + (freq * coeff) * size / samplerate);
        g.B = (2 * MathF.PI * g.A) / size;
        g.Sin = MathF.Sin(g.B);
        g.Cos = MathF.Cos(g.B);
        g.R = 2f * g.Cos;
        g.B0 = g.B1 = g.B2 = 0;
    }

    private static void GoertzelInput(ref Goertzel g, float input)
    {
        g.B0 = g.R * g.B1 - g.B2 + input;
        g.B2 = g.B1;
        g.B1 = g.B0;
    }

    private static float GoertzelEnergy(ref Goertzel g)
    {
        var re = g.B1 - g.B2 * g.Cos;
        var im = g.B2 * g.Sin;
        var magSq = re * re + im * im;
        g.B0 = g.B1 = g.B2 = 0;
        return MathF.Sqrt(magSq);
    }

    private static float DecayAvg(float average, float input, float weight)
    {
        if (weight <= 1f) return input;
        return (input - average) / weight + average;
    }

    private static uint DbToLinear(float db) => (uint)Math.Max(1, Math.Round(Math.Pow(10, db / 10.0)));
    private static float LinearToDb(uint linear) => linear <= 0 ? 0 : 10f * MathF.Log10(linear);
    private static long NowSec() => Environment.TickCount64 / 1000;

    private static int RingInc(int value, int size) => value + 1 == size ? 0 : value + 1;
    private static int RingDec(int value, int size) => value == 0 ? size - 1 : value - 1;
    private static int RingChange(int value, int change, int size)
    {
        var n = value + change;
        if (change > 0) return n >= size ? n - size : n;
        return n < 0 ? n + size : n;
    }
    private static int RingDistance(int from, int to) =>
        to < from ? SigBufSize + to - from : to - from;

    private struct Goertzel
    {
        public int A;
        public float B, Sin, Cos, R, B0, B1, B2;
    }

    private struct SigBuf
    {
        public bool State;
        public uint Time;
    }

    private struct BFlags
    {
        public bool Initialized, Dash, WSpace, Timeout, Track;
    }

    private struct CwTimes
    {
        public float PulseAvg, DotAvg, DashAvg, SymSpaceAvg, CwSpaceAvg;
        public int WSpace;
    }

    private static class CwCodeTable
    {
        private static readonly Entry[] Entries =
        [
            new(0, ".-.-", "<aa>"),
            new(1, ".-.-.", "<ar>"),
            new(2, ".-...", "<as>"),
            new(3, "-...-.-", "<bk>"),
            new(4, "-...-", "<bt>"),
            new(5, "-.-..-..", "<cl>"),
            new(6, "-.-.-", "<ct>"),
            new(7, "........", "<hh>"),
            new(8, "-.--.", "<kn>"),
            new(9, "-..---", "<nj>"),
            new(10, "...-.-", "<sk>"),
            new(11, "...-.", "<sn>"),
            new((byte)'E', "."),
            new((byte)'T', "-"),
            new((byte)'I', ".."),
            new((byte)'A', ".-"),
            new((byte)'N', "-."),
            new((byte)'M', "--"),
            new((byte)'S', "..."),
            new((byte)'U', "..-"),
            new((byte)'R', ".-."),
            new((byte)'W', ".--"),
            new((byte)'D', "-.."),
            new((byte)'K', "-.-"),
            new((byte)'G', "--."),
            new((byte)'O', "---"),
            new((byte)'H', "...."),
            new((byte)'V', "...-"),
            new((byte)'F', "..-."),
            new((byte)'L', ".-.."),
            new((byte)'P', ".--."),
            new((byte)'J', ".---"),
            new((byte)'B', "-..."),
            new((byte)'X', "-..-"),
            new((byte)'C', "-.-."),
            new((byte)'Y', "-.--"),
            new((byte)'Z', "--.."),
            new((byte)'Q', "--.-"),
            new((byte)'5', "....."),
            new((byte)'4', "....-"),
            new((byte)'3', "...--"),
            new((byte)'2', "..---"),
            new((byte)'1', ".----"),
            new((byte)'6', "-...."),
            new((byte)'=', "-...-"),
            new((byte)'/', "-..-."),
            new((byte)'7', "--..."),
            new((byte)'8', "---.."),
            new((byte)'9', "----."),
            new((byte)'0', "-----"),
            new((byte)'?', "..--.."),
            new((byte)'"', ".-..-."),
            new((byte)'.', ".-.-.-"),
            new((byte)'@', ".--.-."),
            new((byte)'\'', ".----."),
            new((byte)'-', "-....-"),
            new((byte)',', "--..--"),
            new((byte)':', "---..."),
        ];

        private static bool _init;

        public static void EnsureInitialized()
        {
            if (_init) return;
            for (var i = 0; i < Entries.Length; i++)
            {
                uint elems = 0;
                foreach (var ch in Entries[i].Elems)
                {
                    if (ch == '\0') break;
                    elems = (elems << 2) | (ch == '.' ? (uint)Dit : Dah);
                }
                var e = Entries[i];
                e.Code = elems;
                Entries[i] = e;
            }
            _init = true;
        }

        public static byte Lookup(uint code)
        {
            EnsureInitialized();
            for (var i = 0; i < Entries.Length; i++)
            {
                if (Entries[i].Code == code)
                    return Entries[i].C;
            }
            return 0xff;
        }

        public static string Prosign(byte id)
        {
            EnsureInitialized();
            if (id < Entries.Length && Entries[id].C == id && Entries[id].Prosign is { } p)
                return p;
            return "[?]";
        }

        private struct Entry
        {
            public byte C;
            public string Elems;
            public string? Prosign;
            public uint Code;
            public Entry(byte c, string elems, string? prosign = null)
            {
                C = c;
                Elems = elems;
                Prosign = prosign;
                Code = 0;
            }
        }
    }
}
