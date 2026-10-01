using NeuroSDR.Core;
using System.Numerics;

namespace NeuroSDR.Dsp;

public sealed class SpectrumProcessor
{
    private readonly int _size;
    private readonly Complex[] _buffer;
    private readonly Complex[] _work;
    private readonly float[][] _results;
    private readonly double[] _window;
    private readonly int _skippedFrames;
    private int _writeIndex;
    private int _blocksUntilFrame;
    private int _resultSlot;

    public SpectrumProcessor(int size = 2048, int skippedFrames = 7)
    {
        if (!BitOperations.IsPow2((uint)size)) throw new ArgumentException("FFT size must be a power of two.", nameof(size));
        _size = size;
        _skippedFrames = Math.Max(0, skippedFrames);
        _buffer = new Complex[size];
        _work = new Complex[size];
        _results = [new float[size], new float[size], new float[size]];
        _window = Enumerable.Range(0, size)
            .Select(i => 0.35875 - 0.48829 * Math.Cos(2 * Math.PI * i / (size - 1))
                       + 0.14128 * Math.Cos(4 * Math.PI * i / (size - 1))
                       - 0.01168 * Math.Cos(6 * Math.PI * i / (size - 1)))
            .ToArray();
    }

    public float[]? Process(Complex32[] samples) => Process(samples.AsSpan());

    public float[]? Process(ReadOnlySpan<Complex32> samples)
    {
        foreach (var sample in samples)
        {
            _buffer[_writeIndex++] = new Complex(sample.I, sample.Q);
            if (_writeIndex != _size) continue;
            _writeIndex = 0;
            if (_blocksUntilFrame-- > 0) continue;
            _blocksUntilFrame = _skippedFrames;
            return CalculateSpectrum();
        }
        return null;
    }

    private float[] CalculateSpectrum()
    {
        for (var i = 0; i < _size; i++) _work[i] = _buffer[i] * _window[i];
        ForwardFft(_work);

        var result = _results[_resultSlot];
        _resultSlot = (_resultSlot + 1) % _results.Length;
        var normalization = _size * 0.42;
        for (var i = 0; i < _size; i++)
        {
            var shifted = (i + _size / 2) & (_size - 1);
            var magnitude = _work[shifted].Magnitude / normalization;
            result[i] = (float)Math.Max(-140, 20 * Math.Log10(magnitude + 1e-12));
        }
        return result;
    }

    internal static void ForwardFft(Complex[] data)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var offset = 0; offset < n; offset += length)
            {
                var twiddle = Complex.One;
                for (var i = 0; i < length / 2; i++)
                {
                    var even = data[offset + i];
                    var odd = data[offset + i + length / 2] * twiddle;
                    data[offset + i] = even + odd;
                    data[offset + i + length / 2] = even - odd;
                    twiddle *= step;
                }
            }
        }
    }
}
