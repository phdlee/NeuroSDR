using NeuroSDR.Core;

namespace NeuroSDR.Dsp;

public sealed unsafe class AudioDemodulator
{
    public const int AudioSampleRate = 48_000;
    private const int WideProcessingRate = 250_000;
    private const int NarrowDecimation = 5;
    private const int NarrowSampleRate = 50_000;
    private readonly object _sync = new();
    private readonly ComplexFirDecimator _narrowDecimator = new(NarrowDecimation, 127, 20_000, 250_000);
    private readonly ComplexCascadedBoxcarDecimator _inputDecimator = new();
    private readonly ComplexFirFilter _channelFilter = new();
    private readonly FmStereoDecoder _stereoDecoder = new();
    private double _oscillatorI = 1, _oscillatorQ, _audioPhase;
    private double _samPhase, _samFrequency, _cwPhase;
    private float _previousPhase, _amDc, _deemphasis, _audioLowpass;
    private float _discPrev;
    private long _discInCount;
    private double _discOutAt;
    private int _discRate;
    private bool _discHasPrev;
    private float _afcErrorHz, _afcPhasePrev;
    private int _afcEnabledFlag, _stereoEnabledFlag;
    private int _oscillatorNormalizationCounter;
    private int _inputDecimation = 8;
    private int _inputDecimatorStages;
    private int _configuredMode = -1, _configuredBandwidth = -1, _configuredSsb = -1;
    private int _mode = (int)RadioMode.WFM, _bandwidth = 180_000;
    private int _ssbLowerFlag = 1;
    private long _frequencyOffset;
    private float[] _audioScratch = new float[2_048];
    private float[] _audioPublished = new float[2_048];
    private float[] _stereoScratch = new float[4_096];
    private float[] _stereoPublished = [];
    private int _audioCount, _stereoCount;
    private int _stereoLockedFlag;

    public RadioMode Mode { get => (RadioMode)Volatile.Read(ref _mode); set => Volatile.Write(ref _mode, (int)value); }
    /// <summary>When <see cref="Mode"/> is FreeDV, true = LSB (typical below 10 MHz).</summary>
    public bool SsbLower
    {
        get => Volatile.Read(ref _ssbLowerFlag) != 0;
        set => Volatile.Write(ref _ssbLowerFlag, value ? 1 : 0);
    }
    public int Bandwidth { get => Volatile.Read(ref _bandwidth); set => Volatile.Write(ref _bandwidth, Math.Max(100, value)); }
    public long FrequencyOffset { get => Interlocked.Read(ref _frequencyOffset); set => Interlocked.Exchange(ref _frequencyOffset, value); }
    public int CwPitchHz { get; set; } = 700;

    public bool AfcEnabled
    {
        get => Volatile.Read(ref _afcEnabledFlag) != 0;
        set => Volatile.Write(ref _afcEnabledFlag, value ? 1 : 0);
    }

    public bool StereoEnabled
    {
        get => Volatile.Read(ref _stereoEnabledFlag) != 0;
        set
        {
            Volatile.Write(ref _stereoEnabledFlag, value ? 1 : 0);
            if (!value) Volatile.Write(ref _stereoLockedFlag, 0);
        }
    }

    public float AfcErrorHz => Volatile.Read(ref _afcErrorHz);
    public bool StereoLocked => Volatile.Read(ref _stereoLockedFlag) != 0;
    public float StereoLockPercent { get; private set; }

    /// <summary>RF channel peak (dB) used to gate WFM stereo lock.</summary>
    public float StereoCarrierDb
    {
        get => BitConverter.Int32BitsToSingle(Volatile.Read(ref _stereoCarrierDbBits));
        set => Volatile.Write(ref _stereoCarrierDbBits, BitConverter.SingleToInt32Bits(value));
    }

    private int _stereoCarrierDbBits = BitConverter.SingleToInt32Bits(-140f);

    public float[] Process(Complex32[] samples, int inputSampleRate)
    {
        lock (_sync)
        {
            var mode = Mode;
            var bandwidth = Bandwidth;
            var stereo = mode == RadioMode.WFM && StereoEnabled;
            var inputDecimation = Math.Max(1, (int)Math.Round(inputSampleRate / (double)WideProcessingRate));
            var inputDecimatorStages = mode == RadioMode.WFM ? 1 : 2;
            if (_inputDecimation != inputDecimation || _inputDecimatorStages != inputDecimatorStages)
            {
                _inputDecimation = inputDecimation;
                _inputDecimatorStages = inputDecimatorStages;
                _inputDecimator.Configure(inputDecimation, inputDecimatorStages);
                _narrowDecimator.Reset();
            }
            var wideProcessingRate = Math.Max(1, inputSampleRate / _inputDecimation);
            var narrowProcessingRate = Math.Max(1, wideProcessingRate / NarrowDecimation);
            EnsureChannelFilter(mode, bandwidth);
            _audioCount = 0;
            _stereoCount = 0;
            var offset = FrequencyOffset;
            var oscillatorStep = -2 * Math.PI * offset / inputSampleRate;
            var stepI = (float)Math.Cos(oscillatorStep);
            var stepQ = (float)Math.Sin(oscillatorStep);
            var needsMixing = offset != 0;
            var processingRate = mode == RadioMode.WFM ? wideProcessingRate : narrowProcessingRate;
            var audioAlpha = CalculateAudioAlpha(mode, bandwidth, processingRate, stereo);
            if (stereo) _stereoDecoder.CarrierDb = StereoCarrierDb;

            fixed (Complex32* input = samples)
            {
                if (!needsMixing)
                {
                    for (var index = 0; index < samples.Length; index++)
                    {
                        if (!_inputDecimator.TryProcess(input[index].I, input[index].Q, out var decimatedI, out var decimatedQ))
                            continue;
                        ProcessIntermediate(decimatedI, decimatedQ,
                            mode, wideProcessingRate, narrowProcessingRate, audioAlpha, stereo);
                    }
                }
                else
                {
                    var oscillatorI = (float)_oscillatorI;
                    var oscillatorQ = (float)_oscillatorQ;
                    var normalizationCounter = _oscillatorNormalizationCounter;
                    for (var index = 0; index < samples.Length; index++)
                    {
                        var sample = input[index];
                        var mixedI = sample.I * oscillatorI - sample.Q * oscillatorQ;
                        var mixedQ = sample.I * oscillatorQ + sample.Q * oscillatorI;
                        var nextI = oscillatorI * stepI - oscillatorQ * stepQ;
                        oscillatorQ = oscillatorI * stepQ + oscillatorQ * stepI;
                        oscillatorI = nextI;
                        if (++normalizationCounter == 4096)
                        {
                            var inverseMagnitude = 1f / MathF.Sqrt(oscillatorI * oscillatorI + oscillatorQ * oscillatorQ);
                            oscillatorI *= inverseMagnitude;
                            oscillatorQ *= inverseMagnitude;
                            normalizationCounter = 0;
                        }
                        if (!_inputDecimator.TryProcess(mixedI, mixedQ, out var decimatedI, out var decimatedQ))
                            continue;
                        ProcessIntermediate(decimatedI, decimatedQ,
                            mode, wideProcessingRate, narrowProcessingRate, audioAlpha, stereo);
                    }
                    _oscillatorI = oscillatorI;
                    _oscillatorQ = oscillatorQ;
                    _oscillatorNormalizationCounter = normalizationCounter;
                }
            }

            StereoLockPercent = stereo ? _stereoDecoder.LockPercent : 0;
            Volatile.Write(ref _stereoLockedFlag, stereo && _stereoDecoder.Locked ? 1 : 0);
            return PublishAudio();
        }
    }

    /// <summary>Interleaved L/R samples from the last <see cref="Process"/> call (empty when mono).</summary>
    public float[] TakeStereoAudio()
    {
        lock (_sync)
        {
            if (_stereoCount == 0) return [];
            if (_stereoPublished.Length != _stereoCount)
                _stereoPublished = new float[_stereoCount];
            Array.Copy(_stereoScratch, _stereoPublished, _stereoCount);
            var count = _stereoCount;
            _stereoCount = 0;
            return count == 0 ? [] : _stereoPublished;
        }
    }

    private void ProcessIntermediate(float i, float q, RadioMode mode, int wideProcessingRate,
        int narrowProcessingRate, float audioAlpha, bool stereo)
    {
        if (mode == RadioMode.WFM)
        {
            var mpx = DemodulateFm(i, q, wideProcessingRate, true, 0, applyDeemphasis: !stereo);
            ProcessAudioSample(mpx, wideProcessingRate, audioAlpha, stereo);
            return;
        }
        if (!_narrowDecimator.TryProcess(i, q, out var narrowI, out var narrowQ)) return;
        _channelFilter.Process(narrowI, narrowQ, out var filteredI, out var filteredQ);
        ObserveAfc(filteredI, filteredQ, mode, narrowProcessingRate);
        var audio = DemodulateNarrow(filteredI, filteredQ, mode, narrowProcessingRate);
        if (RadioModes.IsFmDigitalVoice(mode))
            EmitDigitalDiscriminator(audio, narrowProcessingRate);
        else
            ProcessAudioSample(audio, narrowProcessingRate, audioAlpha, false);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _oscillatorI = 1;
            _oscillatorQ = _audioPhase = _samPhase = _samFrequency = _cwPhase = 0;
            _previousPhase = _amDc = _deemphasis = _audioLowpass = _afcErrorHz = _afcPhasePrev = 0;
            _discPrev = 0;
            _discInCount = 0;
            _discOutAt = 0;
            _discRate = 0;
            _discHasPrev = false;
            _oscillatorNormalizationCounter = 0;
            _stereoCount = 0;
            _stereoDecoder.Reset();
            Volatile.Write(ref _stereoLockedFlag, 0);
            _inputDecimator.Reset();
            _narrowDecimator.Reset();
            _channelFilter.Reset();
        }
    }

    private void EnsureChannelFilter(RadioMode mode, int bandwidth)
    {
        var ssbLower = SsbLower ? 1 : 0;
        if (_configuredMode == (int)mode && _configuredBandwidth == bandwidth && _configuredSsb == ssbLower) return;
        _configuredMode = (int)mode;
        _configuredBandwidth = bandwidth;
        _configuredSsb = ssbLower;
        switch (mode)
        {
            case RadioMode.USB:
                _channelFilter.ConfigureBandPass(255, 150, Math.Clamp(bandwidth, 500, 12_000), NarrowSampleRate);
                break;
            case RadioMode.LSB:
                _channelFilter.ConfigureBandPass(255, -Math.Clamp(bandwidth, 500, 12_000), -150, NarrowSampleRate);
                break;
            case RadioMode.FREEDV:
                if (SsbLower)
                    _channelFilter.ConfigureBandPass(255, -Math.Clamp(bandwidth, 500, 12_000), -150, NarrowSampleRate);
                else
                    _channelFilter.ConfigureBandPass(255, 150, Math.Clamp(bandwidth, 500, 12_000), NarrowSampleRate);
                break;
            case RadioMode.CW:
                _channelFilter.ConfigureLowPass(255, Math.Clamp(bandwidth / 2d, 150, 1_500), NarrowSampleRate);
                break;
            default:
                _channelFilter.ConfigureLowPass(191, Math.Clamp(bandwidth / 2d, 500, 18_000), NarrowSampleRate);
                break;
        }
    }

    private void ObserveAfc(float i, float q, RadioMode mode, float sampleRate)
    {
        if (!AfcEnabled)
        {
            if (_afcErrorHz != 0) Volatile.Write(ref _afcErrorHz, 0);
            return;
        }
        if (mode is not (RadioMode.AM or RadioMode.SAM or RadioMode.NFM)) return;

        float errorHz;
        if (mode == RadioMode.SAM)
        {
            errorHz = (float)(_samFrequency * sampleRate / (2 * Math.PI));
        }
        else
        {
            var phase = MathF.Atan2(q, i);
            var difference = phase - _afcPhasePrev;
            _afcPhasePrev = phase;
            if (difference > MathF.PI) difference -= 2 * MathF.PI;
            else if (difference < -MathF.PI) difference += 2 * MathF.PI;
            errorHz = difference * sampleRate / (2 * MathF.PI);
        }

        // Soft low-pass; clamp so noise bursts cannot yank the VFO.
        errorHz = Math.Clamp(errorHz, -2_500f, 2_500f);
        var smoothed = _afcErrorHz + 0.02f * (errorHz - _afcErrorHz);
        _afcErrorHz = smoothed;
        Volatile.Write(ref _afcErrorHz, smoothed);
    }

    private float DemodulateNarrow(float i, float q, RadioMode mode, int sampleRate)
    {
        switch (mode)
        {
            case RadioMode.AM:
                return RemoveAmplitudeDc(MathF.Sqrt(i * i + q * q));
            case RadioMode.SAM:
            {
                var cosine = Math.Cos(_samPhase);
                var sine = Math.Sin(_samPhase);
                var lockedI = (float)(i * cosine + q * sine);
                var lockedQ = (float)(q * cosine - i * sine);
                var error = MathF.Atan2(lockedQ, MathF.Abs(lockedI) + 1e-9f);
                _samFrequency = Math.Clamp(_samFrequency + .00012 * error, -2 * Math.PI * 3_000 / sampleRate, 2 * Math.PI * 3_000 / sampleRate);
                _samPhase += _samFrequency + .025 * error;
                if (_samPhase > Math.PI) _samPhase -= 2 * Math.PI;
                else if (_samPhase < -Math.PI) _samPhase += 2 * Math.PI;
                return RemoveAmplitudeDc(lockedI);
            }
            case RadioMode.NFM:
                // ±5 kHz scaling — matches typical NFM so CTCSS (~±0.5 kHz) is not vanishingly small.
                return DemodulateFm(i, q, sampleRate, false, 5_000f);
            case RadioMode.DMR:
            case RadioMode.DSTAR:
            case RadioMode.C4FM:
                return DemodulateFm(i, q, sampleRate, false, 12_500f);
            case RadioMode.USB:
            case RadioMode.LSB:
            case RadioMode.FREEDV:
                return i * 2.2f;
            case RadioMode.CW:
            {
                var output = (float)(i * Math.Cos(_cwPhase) - q * Math.Sin(_cwPhase)) * 2f;
                _cwPhase += 2 * Math.PI * CwPitchHz / sampleRate;
                if (_cwPhase > 2 * Math.PI) _cwPhase -= 2 * Math.PI;
                return output;
            }
            default:
                return i;
        }
    }

    private float RemoveAmplitudeDc(float value)
    {
        _amDc += .002f * (value - _amDc);
        return (value - _amDc) * 4f;
    }

    private float DemodulateFm(float i, float q, float sampleRate, bool wide, float deviationHz = 0,
        bool applyDeemphasis = true)
    {
        var phase = MathF.Atan2(q, i);
        var difference = phase - _previousPhase;
        _previousPhase = phase;
        if (difference > MathF.PI) difference -= 2 * MathF.PI;
        else if (difference < -MathF.PI) difference += 2 * MathF.PI;
        var deviation = deviationHz > 0 ? deviationHz : (wide ? 75_000f : 5_000f);
        var audio = difference * sampleRate / (2 * MathF.PI * deviation);
        if (!wide || !applyDeemphasis) return audio;
        _deemphasis += .052f * (audio - _deemphasis);
        return _deemphasis;
    }

    private static float CalculateAudioAlpha(RadioMode mode, int bandwidth, int processingRate, bool stereo)
    {
        var cutoff = mode switch
        {
            // Keep 19 kHz pilot when stereo decoding.
            RadioMode.WFM => stereo ? 16_500f : 15_000f,
            RadioMode.NFM => bandwidth >= 10_000 ? 7_500f : 4_000f,
            RadioMode.AM or RadioMode.SAM => Math.Min(8_000f, bandwidth * .45f),
            RadioMode.CW => 1_200f,
            RadioMode.USB or RadioMode.LSB or RadioMode.FREEDV => Math.Min(12_000f, Math.Max(3_800f, bandwidth * .95f)),
            _ => Math.Min(3_200f, bandwidth * .48f)
        };
        return 1f - MathF.Exp(-2 * MathF.PI * cutoff / processingRate);
    }

    private void ProcessAudioSample(float audio, int processingRate, float alpha, bool stereo)
    {
        _audioLowpass += alpha * (audio - _audioLowpass);
        _audioPhase += AudioSampleRate;
        if (_audioPhase < processingRate) return;
        _audioPhase -= processingRate;

        float mono;
        if (stereo)
        {
            _stereoDecoder.Process(_audioLowpass, out var left, out var right);
            mono = Math.Clamp(0.5f * (left + right), -1f, 1f);
            if (_stereoCount + 2 > _stereoScratch.Length)
                Array.Resize(ref _stereoScratch, _stereoScratch.Length * 2);
            _stereoScratch[_stereoCount++] = left;
            _stereoScratch[_stereoCount++] = right;
        }
        else
        {
            mono = Math.Clamp(_audioLowpass, -1f, 1f);
        }

        AppendAudio(mono);
    }

    /// <summary>
    /// DMR/D-STAR/C4FM discriminator for DSD-FME: no voice lowpass, uniform 48 kHz.
    /// The narrow demod is 50 kHz; dropping samples to 48 kHz walks the symbol clock.
    /// </summary>
    private void EmitDigitalDiscriminator(float sample, int processingRate)
    {
        if (processingRate != _discRate)
        {
            _discRate = processingRate;
            _discHasPrev = false;
            _discInCount = 0;
            _discOutAt = 0;
        }
        var step = processingRate / (double)AudioSampleRate;
        var n = _discInCount;
        if (!_discHasPrev)
        {
            _discPrev = sample;
            _discHasPrev = true;
            _discInCount = 1;
            _discOutAt = step;
            AppendAudio(Math.Clamp(sample, -1f, 1f));
            return;
        }
        while (_discOutAt <= n)
        {
            var t = (float)(_discOutAt - (n - 1));
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            AppendAudio(Math.Clamp(_discPrev + (sample - _discPrev) * t, -1f, 1f));
            _discOutAt += step;
        }
        _discPrev = sample;
        _discInCount = n + 1;
        if (_discInCount > 1_000_000)
        {
            _discOutAt -= _discInCount - 1;
            _discInCount = 1;
        }
    }

    private void AppendAudio(float mono)
    {
        if (_audioCount == _audioScratch.Length)
            Array.Resize(ref _audioScratch, _audioScratch.Length * 2);
        _audioScratch[_audioCount++] = mono;
    }

    private float[] PublishAudio()
    {
        if (_audioPublished.Length != _audioCount)
            _audioPublished = new float[_audioCount];
        Array.Copy(_audioScratch, _audioPublished, _audioCount);
        return _audioPublished;
    }
}
