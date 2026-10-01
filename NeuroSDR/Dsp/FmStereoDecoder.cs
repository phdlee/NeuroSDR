namespace NeuroSDR.Dsp;

/// <summary>
/// Broadcast FM MPX stereo @ 48 kHz (pilot PLL + L−R recovery).
/// L−R is band-limited harder than L+R so mixing products around ~8 kHz stay quiet.
/// </summary>
internal sealed class FmStereoDecoder
{
    private const float SampleRate = AudioDemodulator.AudioSampleRate;
    private const float DeemphasisAlpha = 0.052f; // 75 µs @ 48 kHz
    private const float CarrierMinDb = -78f;
    private const float PilotSnrOn = 0.085f;
    private const float PilotSnrOff = 0.045f;
    private const float LockLevelOn = 0.72f;
    private const float LockLevelOff = 0.40f;

    private double _pilotPhase;
    private double _pilotFrequency = 2 * Math.PI * 19_000 / SampleRate;
    private float _pilotI, _pilotQ;
    private float _mpxPower;
    private float _lockLevel;
    private float _stereoWidth;
    private float _carrierDb = -140;
    private bool _locked;
    // Mono keeps musical air (~15 kHz). Difference is tighter — grit lives in L−R HF.
    private readonly BiquadLpf _monoLpf = BiquadLpf.Create(15_000f, SampleRate);
    private readonly BiquadLpf _diffLpf1 = BiquadLpf.Create(9_500f, SampleRate);
    private readonly BiquadLpf _diffLpf2 = BiquadLpf.Create(9_500f, SampleRate);
    private readonly BiquadLpf _outLLpf = BiquadLpf.Create(14_000f, SampleRate);
    private readonly BiquadLpf _outRLpf = BiquadLpf.Create(14_000f, SampleRate);
    private readonly PeakingCut _hfSoftL = PeakingCut.Create(8_000f, -3.5f, SampleRate);
    private readonly PeakingCut _hfSoftR = PeakingCut.Create(8_000f, -3.5f, SampleRate);
    private float _leftDeemph, _rightDeemph;

    public bool Locked => _locked;
    public float LockPercent => Math.Clamp(_lockLevel * 100f, 0f, 100f);

    public float CarrierDb
    {
        get => _carrierDb;
        set => _carrierDb = value;
    }

    public void Reset()
    {
        _pilotPhase = 0;
        _pilotFrequency = 2 * Math.PI * 19_000 / SampleRate;
        _pilotI = _pilotQ = _mpxPower = _lockLevel = _stereoWidth = 0;
        _leftDeemph = _rightDeemph = 0;
        _carrierDb = -140;
        _locked = false;
        _monoLpf.Reset();
        _diffLpf1.Reset();
        _diffLpf2.Reset();
        _outLLpf.Reset();
        _outRLpf.Reset();
        _hfSoftL.Reset();
        _hfSoftR.Reset();
    }

    public void Process(float mpx, out float left, out float right)
    {
        _mpxPower += 0.0008f * (mpx * mpx - _mpxPower);
        var mpxRms = MathF.Sqrt(Math.Max(_mpxPower, 1e-12f));
        var carrierOk = _carrierDb >= CarrierMinDb;

        var cos = (float)Math.Cos(_pilotPhase);
        var sin = (float)Math.Sin(_pilotPhase);
        _pilotI += 0.0032f * (mpx * cos - _pilotI);
        _pilotQ += 0.0032f * (mpx * sin - _pilotQ);
        var pilotMag = MathF.Sqrt(_pilotI * _pilotI + _pilotQ * _pilotQ);
        var pilotSnr = pilotMag / (mpxRms + 1e-6f);

        var error = pilotMag > 1e-6f ? _pilotQ / (pilotMag + 1e-6f) : mpx * sin;
        if (carrierOk)
        {
            _pilotFrequency = Math.Clamp(
                _pilotFrequency - 0.0000004 * error,
                2 * Math.PI * 18_800 / SampleRate,
                2 * Math.PI * 19_200 / SampleRate);
            _pilotPhase += _pilotFrequency - 0.0018 * error;
        }
        else
        {
            _pilotPhase += 2 * Math.PI * 19_000 / SampleRate;
            _pilotI *= 0.99f;
            _pilotQ *= 0.99f;
        }
        if (_pilotPhase > Math.PI) _pilotPhase -= 2 * Math.PI;
        else if (_pilotPhase < -Math.PI) _pilotPhase += 2 * Math.PI;

        var lockTarget = 0f;
        if (carrierOk && _pilotI > 0)
            lockTarget = Math.Clamp(pilotSnr / PilotSnrOn, 0f, 1.2f) * Math.Clamp(_pilotI * 30f, 0f, 1f);

        var lockAlpha = lockTarget > _lockLevel ? 0.01f : (carrierOk ? 0.025f : 0.08f);
        _lockLevel += (lockTarget - _lockLevel) * lockAlpha;

        if (_locked)
        {
            if (!carrierOk || pilotSnr < PilotSnrOff || _lockLevel < LockLevelOff)
                _locked = false;
        }
        else if (carrierOk && pilotSnr > PilotSnrOn && _lockLevel > LockLevelOn && _pilotI > 0.01f)
        {
            _locked = true;
        }

        var widthTarget = _locked ? 1f : 0f;
        _stereoWidth += (widthTarget - _stereoWidth) * (_locked ? 0.02f : 0.08f);

        var mono = _monoLpf.Process(mpx);

        float diff = 0;
        if (_stereoWidth > 0.01f)
        {
            var sub = 2f * cos * cos - 1f;
            // Lower L−R gain + dual 9.5 kHz LPF: keeps image, kills ~8 kHz grit.
            diff = _diffLpf2.Process(_diffLpf1.Process(mpx * sub * 0.92f)) * _stereoWidth;
        }
        else
        {
            _diffLpf1.Process(0);
            _diffLpf2.Process(0);
            diff = 0;
        }

        var rawL = _outLLpf.Process(mono + diff);
        var rawR = _outRLpf.Process(mono - diff);
        // Mild fixed 8 kHz dip on stereo path only (scaled by width).
        if (_stereoWidth > 0.01f)
        {
            rawL = Lerp(rawL, _hfSoftL.Process(rawL), _stereoWidth);
            rawR = Lerp(rawR, _hfSoftR.Process(rawR), _stereoWidth);
        }

        _leftDeemph += DeemphasisAlpha * (rawL - _leftDeemph);
        _rightDeemph += DeemphasisAlpha * (rawR - _rightDeemph);
        left = Math.Clamp(_leftDeemph, -1f, 1f);
        right = Math.Clamp(_rightDeemph, -1f, 1f);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private sealed class BiquadLpf
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public static BiquadLpf Create(float cutoffHz, float sampleRate)
        {
            var f = new BiquadLpf();
            var w0 = 2 * MathF.PI * cutoffHz / sampleRate;
            var cos = MathF.Cos(w0);
            var sin = MathF.Sin(w0);
            var alpha = sin / (2f * 0.707f);
            var b0 = (1 - cos) / 2;
            var b1 = 1 - cos;
            var b2 = (1 - cos) / 2;
            var a0 = 1 + alpha;
            var a1 = -2 * cos;
            var a2 = 1 - alpha;
            f._b0 = b0 / a0;
            f._b1 = b1 / a0;
            f._b2 = b2 / a0;
            f._a1 = a1 / a0;
            f._a2 = a2 / a0;
            return f;
        }

        public float Process(float x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;
    }

    private sealed class PeakingCut
    {
        private float _b0 = 1, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public static PeakingCut Create(float frequencyHz, float gainDb, float sampleRate)
        {
            var f = new PeakingCut();
            var a = MathF.Pow(10f, gainDb / 40f);
            var w0 = 2 * MathF.PI * frequencyHz / sampleRate;
            var cos = MathF.Cos(w0);
            var sin = MathF.Sin(w0);
            var q = 1.1f;
            var alpha = sin / (2 * q);
            var b0 = 1 + alpha * a;
            var b1 = -2 * cos;
            var b2 = 1 - alpha * a;
            var a0 = 1 + alpha / a;
            var a1 = -2 * cos;
            var a2 = 1 - alpha / a;
            f._b0 = b0 / a0;
            f._b1 = b1 / a0;
            f._b2 = b2 / a0;
            f._a1 = a1 / a0;
            f._a2 = a2 / a0;
            return f;
        }

        public float Process(float x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;
    }
}
