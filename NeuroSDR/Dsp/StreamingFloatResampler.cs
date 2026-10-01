namespace NeuroSDR.Dsp;

internal sealed class StreamingFloatResampler(int inputRate, int outputRate)
{
    private readonly double _step = inputRate / (double)outputRate;
    private double _position;
    private float _previous;
    private bool _hasPrevious;

    public float[] Process(ReadOnlySpan<float> input)
    {
        if (input.Length == 0) return [];
        if (inputRate == outputRate) return input.ToArray();
        var source = new float[input.Length + (_hasPrevious ? 1 : 0)];
        var offset = 0;
        if (_hasPrevious) { source[0] = _previous; offset = 1; }
        input.CopyTo(source.AsSpan(offset));
        var output = new List<float>((int)Math.Ceiling(input.Length * outputRate / (double)inputRate) + 2);
        while (_position + 1 < source.Length)
        {
            var index = (int)_position;
            var fraction = (float)(_position - index);
            output.Add(source[index] + (source[index + 1] - source[index]) * fraction);
            _position += _step;
        }
        _position -= source.Length - 1;
        _previous = source[^1];
        _hasPrevious = true;
        return output.ToArray();
    }
}
