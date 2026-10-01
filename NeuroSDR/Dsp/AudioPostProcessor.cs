namespace NeuroSDR.Dsp;

internal sealed class AudioPostProcessor
{
    private float _agcEnvelope = .05f, _agcGain = 1f;
    private float _noiseFloor = .01f;
    private float _squelchGate = 1f;
    private float _notchX1, _notchX2, _notchY1, _notchY2;
    private int _configuredNotchFrequency;
    private float _notchCosine, _notchRadiusSquared;
    private readonly Biquad _afHighPass = new(), _afLowPass = new();
    private int _configuredAfLow = -1, _configuredAfHigh = -1;
    private int _agcEnabled, _extendedAgcRange, _noiseReductionEnabled, _notchEnabled, _squelchEnabled, _afFilterEnabled;
    private int _noiseReductionStrength = 55, _notchFrequency = 1_000;
    private int _afLowCutHz, _afHighCutHz = 20_000;
    private int _squelchThresholdBits = BitConverter.SingleToInt32Bits(-75f);
    private int _squelchOpen = 1;

    public bool AgcEnabled { get => Volatile.Read(ref _agcEnabled) != 0; set => Volatile.Write(ref _agcEnabled, value ? 1 : 0); }
    public bool ExtendedAgcRange { get => Volatile.Read(ref _extendedAgcRange) != 0; set => Volatile.Write(ref _extendedAgcRange, value ? 1 : 0); }
    public bool NoiseReductionEnabled { get => Volatile.Read(ref _noiseReductionEnabled) != 0; set => Volatile.Write(ref _noiseReductionEnabled, value ? 1 : 0); }
    public bool NotchEnabled { get => Volatile.Read(ref _notchEnabled) != 0; set => Volatile.Write(ref _notchEnabled, value ? 1 : 0); }
    public bool SquelchEnabled { get => Volatile.Read(ref _squelchEnabled) != 0; set => Volatile.Write(ref _squelchEnabled, value ? 1 : 0); }
    public bool AfFilterEnabled { get => Volatile.Read(ref _afFilterEnabled) != 0; set => Volatile.Write(ref _afFilterEnabled, value ? 1 : 0); }
    public int AfLowCutHz { get => Volatile.Read(ref _afLowCutHz); set => Volatile.Write(ref _afLowCutHz, Math.Clamp(value, 0, 19_950)); }
    public int AfHighCutHz { get => Volatile.Read(ref _afHighCutHz); set => Volatile.Write(ref _afHighCutHz, Math.Clamp(value, 50, 20_000)); }
    public int NoiseReductionStrength { get => Volatile.Read(ref _noiseReductionStrength); set => Volatile.Write(ref _noiseReductionStrength, Math.Clamp(value, 0, 100)); }
    public int NotchFrequency { get => Volatile.Read(ref _notchFrequency); set => Volatile.Write(ref _notchFrequency, Math.Clamp(value, 50, 20_000)); }
    public float SquelchThresholdDb
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _squelchThresholdBits));
        set => Volatile.Write(ref _squelchThresholdBits, BitConverter.SingleToInt32Bits(Math.Clamp(value, -140, 0)));
    }
    public bool SquelchOpen => Volatile.Read(ref _squelchOpen) != 0;

    public float[] Process(float[] samples, float signalDb)
    {
        if (samples.Length == 0) return samples;
        var agc = AgcEnabled;
        var noiseReduction = NoiseReductionEnabled;
        var notch = NotchEnabled;
        var squelch = SquelchEnabled;
        var afFilter = AfFilterEnabled;
        var strength = NoiseReductionStrength / 100f;
        var squelchOpen = !squelch || signalDb >= SquelchThresholdDb;
        Volatile.Write(ref _squelchOpen, squelchOpen ? 1 : 0);
        if (notch) ConfigureNotch();
        if (afFilter) ConfigureAfFilter();

        for (var index = 0; index < samples.Length; index++)
        {
            var value = samples[index];
            if (afFilter)
            {
                if (_configuredAfLow > 0) value = _afHighPass.Process(value);
                if (_configuredAfHigh < 20_000) value = _afLowPass.Process(value);
            }
            if (noiseReduction) value = ReduceNoise(value, strength);
            if (notch) value = ApplyNotch(value);
            if (agc) value = ApplyAgc(value);
            var gateTarget = squelchOpen ? 1f : 0f;
            _squelchGate += (gateTarget - _squelchGate) * (gateTarget > _squelchGate ? .035f : .0025f);
            samples[index] = Math.Clamp(value * _squelchGate, -1f, 1f);
        }
        return samples;
    }

    public void Reset()
    {
        _agcEnvelope = .05f;
        _agcGain = _squelchGate = 1f;
        _noiseFloor = .01f;
        _notchX1 = _notchX2 = _notchY1 = _notchY2 = 0;
        _configuredNotchFrequency = 0;
        _configuredAfLow = _configuredAfHigh = -1;
        _afHighPass.Reset();
        _afLowPass.Reset();
        Volatile.Write(ref _squelchOpen, 1);
    }

    private float ApplyAgc(float value)
    {
        var amplitude = MathF.Abs(value);
        _agcEnvelope += (amplitude - _agcEnvelope) * (amplitude > _agcEnvelope ? .025f : .00015f);
        var extended = ExtendedAgcRange;
        var minimumEnvelope = extended ? .0005f : .004f;
        var maximumGain = extended ? 128f : 18f;
        var target = Math.Clamp(.24f / Math.Max(_agcEnvelope, minimumEnvelope), .15f, maximumGain);
        _agcGain += (target - _agcGain) * (target < _agcGain ? .012f : .00025f);
        return value * _agcGain;
    }

    private float ReduceNoise(float value, float strength)
    {
        var amplitude = MathF.Abs(value);
        _noiseFloor += (amplitude - _noiseFloor) * (amplitude < _noiseFloor ? .01f : .00002f);
        var threshold = Math.Max(1e-5f, _noiseFloor * 2.4f);
        if (amplitude >= threshold) return value;
        var ratio = amplitude / threshold;
        var floorGain = 1f - .92f * strength;
        var gain = floorGain + (1f - floorGain) * ratio * ratio;
        return value * gain;
    }

    private void ConfigureNotch()
    {
        var frequency = NotchFrequency;
        if (frequency == _configuredNotchFrequency) return;
        _configuredNotchFrequency = frequency;
        const float radius = .985f;
        _notchCosine = 2f * MathF.Cos(2 * MathF.PI * frequency / AudioDemodulator.AudioSampleRate);
        _notchRadiusSquared = radius * radius;
        _notchX1 = _notchX2 = _notchY1 = _notchY2 = 0;
    }

    private float ApplyNotch(float value)
    {
        const float radius = .985f;
        var output = value - _notchCosine * _notchX1 + _notchX2 + radius * _notchCosine * _notchY1 - _notchRadiusSquared * _notchY2;
        _notchX2 = _notchX1;
        _notchX1 = value;
        _notchY2 = _notchY1;
        _notchY1 = output;
        return output;
    }

    private void ConfigureAfFilter()
    {
        var low = Math.Clamp(AfLowCutHz, 0, 19_950);
        var high = Math.Clamp(AfHighCutHz, low + 50, 20_000);
        if (low == _configuredAfLow && high == _configuredAfHigh) return;
        _configuredAfLow = low;
        _configuredAfHigh = high;
        if (low > 0) _afHighPass.ConfigureHighPass(low, AudioDemodulator.AudioSampleRate);
        else _afHighPass.Reset();
        if (high < 20_000) _afLowPass.ConfigureLowPass(high, AudioDemodulator.AudioSampleRate);
        else _afLowPass.Reset();
    }

    private sealed class Biquad
    {
        private float _b0 = 1, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public void ConfigureLowPass(float frequency, float sampleRate) => Configure(frequency, sampleRate, false);
        public void ConfigureHighPass(float frequency, float sampleRate) => Configure(frequency, sampleRate, true);

        private void Configure(float frequency, float sampleRate, bool highPass)
        {
            frequency = Math.Clamp(frequency, 10, sampleRate * .49f);
            var omega = 2f * MathF.PI * frequency / sampleRate;
            var cosine = MathF.Cos(omega);
            var alpha = MathF.Sin(omega) / (2f * .70710678f);
            var a0 = 1f + alpha;
            if (highPass)
            {
                _b0 = (1f + cosine) * .5f / a0;
                _b1 = -(1f + cosine) / a0;
                _b2 = _b0;
            }
            else
            {
                _b0 = (1f - cosine) * .5f / a0;
                _b1 = (1f - cosine) / a0;
                _b2 = _b0;
            }
            _a1 = -2f * cosine / a0;
            _a2 = (1f - alpha) / a0;
            _x1 = _x2 = _y1 = _y2 = 0;
        }

        public float Process(float value)
        {
            var output = _b0 * value + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = value;
            _y2 = _y1;
            _y1 = output;
            return output;
        }

        public void Reset()
        {
            _b0 = 1;
            _b1 = _b2 = _a1 = _a2 = _x1 = _x2 = _y1 = _y2 = 0;
        }
    }
}
