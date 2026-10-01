using NeuroSDR.Core;
using System.Runtime.CompilerServices;

namespace NeuroSDR.Dsp;

internal static class FirDesigner
{
    public static float[] LowPass(int length, double cutoffHz, double sampleRate)
    {
        if ((length & 1) == 0) throw new ArgumentException("FIR length must be odd.", nameof(length));
        cutoffHz = Math.Clamp(cutoffHz, 10, sampleRate * .495);
        var taps = new float[length];
        var middle = (length - 1) / 2;
        double sum = 0;
        for (var n = 0; n < length; n++)
        {
            var x = n - middle;
            var sinc = x == 0 ? 2 * cutoffHz / sampleRate : Math.Sin(2 * Math.PI * cutoffHz * x / sampleRate) / (Math.PI * x);
            var window = .42 - .5 * Math.Cos(2 * Math.PI * n / (length - 1)) + .08 * Math.Cos(4 * Math.PI * n / (length - 1));
            taps[n] = (float)(sinc * window);
            sum += taps[n];
        }
        for (var n = 0; n < taps.Length; n++) taps[n] /= (float)sum;
        return taps;
    }

    public static Complex32[] ComplexBandPass(int length, double lowHz, double highHz, double sampleRate)
    {
        if (highHz <= lowHz) throw new ArgumentOutOfRangeException(nameof(highHz));
        var center = (lowHz + highHz) / 2;
        var prototype = LowPass(length, (highHz - lowHz) / 2, sampleRate);
        var middle = (length - 1) / 2;
        var taps = new Complex32[length];
        for (var n = 0; n < length; n++)
        {
            var phase = 2 * Math.PI * center * (n - middle) / sampleRate;
            taps[n] = new Complex32((float)(prototype[n] * Math.Cos(phase)), (float)(prototype[n] * Math.Sin(phase)));
        }
        return taps;
    }
}

internal sealed class ComplexFirFilter
{
    private float[] _tapI = [1f];
    private float[] _tapQ = [0f];
    private float[] _bufferI = [0f];
    private float[] _bufferQ = [0f];
    private int _position;
    private int _length = 1;

    public void ConfigureLowPass(int length, double cutoffHz, double sampleRate)
    {
        var real = FirDesigner.LowPass(length, cutoffHz, sampleRate);
        _length = real.Length;
        _tapI = real;
        _tapQ = new float[real.Length];
        _bufferI = new float[real.Length];
        _bufferQ = new float[real.Length];
        _position = 0;
    }

    public void ConfigureBandPass(int length, double lowHz, double highHz, double sampleRate)
    {
        var taps = FirDesigner.ComplexBandPass(length, lowHz, highHz, sampleRate);
        _length = taps.Length;
        _tapI = new float[taps.Length];
        _tapQ = new float[taps.Length];
        for (var n = 0; n < taps.Length; n++)
        {
            _tapI[n] = taps[n].I;
            _tapQ[n] = taps[n].Q;
        }
        _bufferI = new float[taps.Length];
        _bufferQ = new float[taps.Length];
        _position = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(float i, float q, out float outI, out float outQ)
    {
        var length = _length;
        var position = _position;
        _bufferI[position] = i;
        _bufferQ[position] = q;
        float sumI = 0, sumQ = 0;
        var bufferIndex = position;
        var tapI = _tapI;
        var tapQ = _tapQ;
        var bufferI = _bufferI;
        var bufferQ = _bufferQ;
        for (var tap = 0; tap < length; tap++)
        {
            var sampleI = bufferI[bufferIndex];
            var sampleQ = bufferQ[bufferIndex];
            var coefficientI = tapI[tap];
            var coefficientQ = tapQ[tap];
            sumI += sampleI * coefficientI - sampleQ * coefficientQ;
            sumQ += sampleI * coefficientQ + sampleQ * coefficientI;
            if (--bufferIndex < 0) bufferIndex = length - 1;
        }
        _position = position + 1 == length ? 0 : position + 1;
        outI = sumI;
        outQ = sumQ;
    }

    public void Reset()
    {
        Array.Clear(_bufferI);
        Array.Clear(_bufferQ);
        _position = 0;
    }
}

internal sealed class ComplexFirDecimator
{
    private readonly float[] _taps;
    private readonly float[] _bufferI;
    private readonly float[] _bufferQ;
    private readonly int _factor;
    private readonly int _length;
    private int _position, _counter;

    public ComplexFirDecimator(int factor, int length, double cutoffHz, double sampleRate)
    {
        _factor = factor;
        _taps = FirDesigner.LowPass(length, cutoffHz, sampleRate);
        _length = _taps.Length;
        _bufferI = new float[_length];
        _bufferQ = new float[_length];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryProcess(float i, float q, out float outI, out float outQ)
    {
        var length = _length;
        var position = _position;
        _bufferI[position] = i;
        _bufferQ[position] = q;
        position++;
        if (position == length) position = 0;
        _position = position;
        if (++_counter < _factor)
        {
            outI = outQ = 0;
            return false;
        }
        _counter = 0;
        float sumI = 0, sumQ = 0;
        var bufferIndex = position - 1;
        if (bufferIndex < 0) bufferIndex = length - 1;
        var taps = _taps;
        var bufferI = _bufferI;
        var bufferQ = _bufferQ;
        for (var tap = 0; tap < length; tap++)
        {
            var coefficient = taps[tap];
            sumI += bufferI[bufferIndex] * coefficient;
            sumQ += bufferQ[bufferIndex] * coefficient;
            if (--bufferIndex < 0) bufferIndex = length - 1;
        }
        outI = sumI;
        outQ = sumQ;
        return true;
    }

    public void Reset()
    {
        Array.Clear(_bufferI);
        Array.Clear(_bufferQ);
        _position = _counter = 0;
    }
}

/// <summary>
/// Stable cascaded moving-average decimator. Unlike a single integrate-and-dump
/// stage, several stages provide useful rejection of wide capture-band signals
/// before they can alias into the 250 kHz demodulation domain. Its response is
/// defined relative to the output rate, so 2 and 8 MS/s captures sound alike.
/// </summary>
internal sealed class ComplexCascadedBoxcarDecimator
{
    private float[] _delayI = [];
    private float[] _delayQ = [];
    private float[] _sumI = [];
    private float[] _sumQ = [];
    private int _factor = 1, _stages = 1, _position, _counter;
    private float _outputScale = 1f;

    public void Configure(int factor, int stages)
    {
        factor = Math.Max(1, factor);
        stages = Math.Clamp(stages, 1, 4);
        if (_factor == factor && _stages == stages && _delayI.Length != 0) return;
        _factor = factor;
        _stages = stages;
        _outputScale = 1f / MathF.Pow(factor, stages);
        _delayI = new float[factor * stages];
        _delayQ = new float[factor * stages];
        _sumI = new float[stages];
        _sumQ = new float[stages];
        _position = _counter = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryProcess(float i, float q, out Complex32 output)
    {
        if (!TryProcess(i, q, out var outI, out var outQ))
        {
            output = default;
            return false;
        }
        output = new Complex32(outI, outQ);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryProcess(float i, float q, out float outI, out float outQ)
    {
        var position = _position;
        var sumI0 = _sumI[0] + i - _delayI[position];
        var sumQ0 = _sumQ[0] + q - _delayQ[position];
        _sumI[0] = sumI0;
        _sumQ[0] = sumQ0;
        _delayI[position] = i;
        _delayQ[position] = q;
        var stageI = sumI0;
        var stageQ = sumQ0;

        if (_stages == 2)
        {
            var index = _factor + position;
            var sumI1 = _sumI[1] + stageI - _delayI[index];
            var sumQ1 = _sumQ[1] + stageQ - _delayQ[index];
            _sumI[1] = sumI1;
            _sumQ[1] = sumQ1;
            _delayI[index] = stageI;
            _delayQ[index] = stageQ;
            stageI = sumI1;
            stageQ = sumQ1;
        }
        else if (_stages > 2)
        {
            for (var stage = 1; stage < _stages; stage++)
            {
                var index = stage * _factor + position;
                _sumI[stage] += stageI - _delayI[index];
                _sumQ[stage] += stageQ - _delayQ[index];
                _delayI[index] = stageI;
                _delayQ[index] = stageQ;
                stageI = _sumI[stage];
                stageQ = _sumQ[stage];
            }
        }
        if (++position == _factor) position = 0;
        _position = position;
        if (++_counter < _factor)
        {
            outI = outQ = 0;
            return false;
        }
        _counter = 0;
        outI = stageI * _outputScale;
        outQ = stageQ * _outputScale;
        return true;
    }

    public void Reset()
    {
        Array.Clear(_delayI);
        Array.Clear(_delayQ);
        Array.Clear(_sumI);
        Array.Clear(_sumQ);
        _position = _counter = 0;
    }
}
