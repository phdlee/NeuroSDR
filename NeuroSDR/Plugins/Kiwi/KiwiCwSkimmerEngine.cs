namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// C# port of KiwiSDR <c>extensions/CW_skimmer/cw_skimmer.hpp</c> CwSkimmer.
/// Uses managed DFT (no fftw3) + per-channel <see cref="KiwiCsdrCwDecoder"/>.
/// Channel spacing is always 100 Hz across 0 … sampleRate/2.
/// </summary>
internal sealed class KiwiCwSkimmerEngine
{
    public const int NominalSampleRate = 12_000;
    public const int PowerCalcAvgRatio = 0;
    public const int PowerCalcAvgBottom = 1;
    public const int PowerCalcThreshold = 2;

    private const int MaxScales = 16;
    private const float NeighWeight = 0.5f;
    private const float ThresWeight = 6.0f;
    private const int AvgSeconds = 3;
    private const int PrintChars = 8;

    private readonly int _sampleRate;
    private readonly int _maxChannels;
    private readonly int _maxInput;
    private readonly float[] _dataBuf;
    private readonly float[] _fftIn;
    private readonly float[] _mag;
    private readonly float[] _magScratch;
    private readonly KiwiCsdrCwDecoder[] _decoders;
    private readonly Queue<byte>[] _outQueues;
    private readonly uint[] _outState;
    private readonly float[] _channelScratch;
    private int _remains;
    private float _avgPower = 4f;
    private int _pwrCalc = PowerCalcAvgRatio;
    private bool _filterNeighbors;

    public event Action<int, char, int>? CharacterDecoded; // freqHz, char, wpm

    public int ChannelCount => _maxChannels;
    public int SampleRate => _sampleRate;

    public KiwiCwSkimmerEngine(int sampleRate = NominalSampleRate)
    {
        _sampleRate = Math.Clamp(sampleRate, 4_000, 48_000);
        _maxChannels = Math.Max(1, _sampleRate / 2 / 100);
        _maxInput = _maxChannels * 2;
        _dataBuf = new float[_maxInput];
        _fftIn = new float[_maxInput];
        _mag = new float[_maxInput / 2];
        _magScratch = new float[_maxInput / 2];
        _channelScratch = new float[_maxInput];
        _decoders = new KiwiCsdrCwDecoder[_maxChannels];
        _outQueues = new Queue<byte>[_maxChannels];
        _outState = new uint[_maxChannels];
        for (var i = 0; i < _maxChannels; i++)
        {
            _decoders[i] = new KiwiCsdrCwDecoder(_sampleRate);
            _outQueues[i] = new Queue<byte>(PrintChars * 4);
            _outState[i] = ' ';
        }
    }

    public void SetParams(int pwrCalc, bool filterNeighbors)
    {
        _pwrCalc = Math.Clamp(pwrCalc, 0, 2);
        _filterNeighbors = filterNeighbors;
    }

    public void Reset()
    {
        for (var i = 0; i < _maxChannels; i++)
        {
            PrintOutput(i, i * _sampleRate / 2 / _maxChannels, 1);
            _outState[i] = ' ';
            _decoders[i].Reset();
            _outQueues[i].Clear();
        }
        _remains = 0;
        _avgPower = 4f;
    }

    public void ProcessPcm16(ReadOnlySpan<short> samples)
    {
        var i = 0;
        while (i < samples.Length)
        {
            var avail = _maxInput - _remains;
            var take = Math.Min(avail, samples.Length - i);
            for (var n = 0; n < take; n++)
                _dataBuf[_remains + n] = samples[i + n] / 32768f;
            _remains += take;
            i += take;
            if (_remains >= _maxInput)
            {
                ProcessFrame();
                _remains = 0;
            }
        }
    }

    public void ProcessFloat(ReadOnlySpan<float> samples)
    {
        var i = 0;
        while (i < samples.Length)
        {
            var avail = _maxInput - _remains;
            var take = Math.Min(avail, samples.Length - i);
            samples.Slice(i, take).CopyTo(_dataBuf.AsSpan(_remains, take));
            _remains += take;
            i += take;
            if (_remains >= _maxInput)
            {
                ProcessFrame();
                _remains = 0;
            }
        }
    }

    private void ProcessFrame()
    {
        var n = _maxInput;
        var hk = 2.0 * Math.PI / (n - 1);
        for (var j = 0; j < n; j++)
            _fftIn[j] = _dataBuf[j] * (float)(0.54 - 0.46 * Math.Cos(j * hk));

        RealDftMagnitude(_fftIn, _mag);

        if (_filterNeighbors)
        {
            var half = n / 2;
            _magScratch[half - 1] = Math.Max(0, _mag[half - 1] - NeighWeight * _mag[half - 2]);
            _magScratch[0] = Math.Max(0, _mag[0] - NeighWeight * _mag[1]);
            for (var j = 1; j < half - 1; j++)
                _magScratch[j] = Math.Max(0, _mag[j] - 0.5f * NeighWeight * (_mag[j - 1] + _mag[j + 1]));
            Array.Copy(_magScratch, _mag, half);
        }

        // Sort buckets into scales for ground power
        Span<(float Power, int Count)> scales = stackalloc (float, int)[MaxScales];
        for (var j = 0; j < n / 2; j++)
        {
            var v = _mag[j];
            var scale = (int)MathF.Floor(MathF.Log(Math.Max(v, 1e-20f)));
            scale = scale < 0 ? 0 : scale + 1 >= MaxScales ? MaxScales - 1 : scale + 1;
            scales[scale].Power += v;
            scales[scale].Count++;
        }

        var buckets = 0;
        float accPower = 0;
        for (var i = 0; i < MaxScales - 1; i++)
        {
            var k = i;
            for (var j = i + 1; j < MaxScales; j++)
                if (scales[j].Count > scales[k].Count) k = j;
            if (k != i)
            {
                var tmp = scales[k];
                scales[k] = scales[i];
                scales[i] = tmp;
            }
            accPower += scales[i].Power;
            buckets += scales[i].Count;
            if (buckets >= n / 2 / 2) break;
        }

        accPower /= Math.Max(1, buckets);
        _avgPower += (accPower - _avgPower) * n / _sampleRate / AvgSeconds;

        // Decode by channel
        var ch = 0;
        var kAcc = 0;
        accPower = 0;
        for (var j = 0; j < n / 2 && ch < _maxChannels; j++)
        {
            if (kAcc >= n / 2)
            {
                var power = _pwrCalc switch
                {
                    PowerCalcAvgBottom => Math.Max(0, accPower - _avgPower),
                    PowerCalcThreshold => accPower >= _avgPower * ThresWeight ? 1f : 0f,
                    _ => Math.Max(1f, accPower / Math.Max(_avgPower, 1e-6f))
                };

                _channelScratch.AsSpan().Fill(power);
                _decoders[ch].Process(_channelScratch);
                DrainDecoder(ch);
                PrintOutput(ch, ch * _sampleRate / 2 / _maxChannels, PrintChars);

                accPower = 0;
                kAcc -= n / 2;
                ch++;
            }

            accPower = Math.Max(accPower, _mag[j]);
            kAcc += _maxChannels;
        }
    }

    private void DrainDecoder(int channel)
    {
        Span<byte> buf = stackalloc byte[64];
        int read;
        while ((read = _decoders[channel].Read(buf)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (_outQueues[channel].Count > 512) _outQueues[channel].Dequeue();
                _outQueues[channel].Enqueue(buf[i]);
            }
        }
    }

    private void PrintOutput(int channel, int freqHz, int printChars)
    {
        var q = _outQueues[channel];
        if (q.Count < printChars) return;
        var n = q.Count;
        var wpm = _decoders[channel].Wpm;
        var chars = new byte[n];
        for (var i = 0; i < n; i++) chars[i] = q.Dequeue();

        for (var j = 0; j < n; j++)
        {
            var ch = (char)chars[j];
            switch (_outState[channel] & 0xFF)
            {
                case 0:
                    CharacterDecoded?.Invoke(freqHz, ch, wpm);
                    if (ch == ' ') _outState[channel] = ch;
                    break;
                case ' ':
                    if ("TEI ".Contains(ch))
                        _outState[channel] = ch;
                    else
                    {
                        CharacterDecoded?.Invoke(freqHz, ch, wpm);
                        _outState[channel] = 0;
                    }
                    break;
                default:
                    if ("TEI ".Contains(ch))
                        _outState[channel] = (_outState[channel] << 8) | ch;
                    else
                    {
                        for (var k = 24; k >= 0; k -= 8)
                        {
                            var c = (char)((_outState[channel] >> k) & 0xFF);
                            if (c != 0) CharacterDecoded?.Invoke(freqHz, c, wpm);
                        }
                        CharacterDecoded?.Invoke(freqHz, ch, wpm);
                        _outState[channel] = 0;
                    }
                    break;
            }
        }
    }

    /// <summary>Real DFT magnitudes for bins 0 .. N/2-1 (N = input.Length).</summary>
    private static void RealDftMagnitude(ReadOnlySpan<float> input, Span<float> magnitudes)
    {
        var n = input.Length;
        var half = magnitudes.Length;
        for (var k = 0; k < half; k++)
        {
            double re = 0, im = 0;
            var phaseStep = -2.0 * Math.PI * k / n;
            for (var t = 0; t < n; t++)
            {
                var ang = phaseStep * t;
                var s = input[t];
                re += s * Math.Cos(ang);
                im += s * Math.Sin(ang);
            }
            magnitudes[k] = (float)Math.Sqrt(re * re + im * im);
        }
    }
}
