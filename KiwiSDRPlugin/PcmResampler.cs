namespace KiwiSDRPlugin;

internal sealed class PcmResampler(int sourceRate, int targetRate)
{
    private readonly int _sourceRate = Math.Max(1, sourceRate);
    private readonly int _targetRate = Math.Max(1, targetRate);
    private int _phase;

    public short[] Process(ReadOnlySpan<float> input)
    {
        if (input.IsEmpty) return [];
        var capacity = Math.Max(1, (int)Math.Ceiling(input.Length * (double)_targetRate / _sourceRate) + 2);
        var output = new short[capacity];
        var count = 0;
        foreach (var value in input)
        {
            _phase += _targetRate;
            while (_phase >= _sourceRate)
            {
                _phase -= _sourceRate;
                if (count == output.Length) Array.Resize(ref output, output.Length * 2);
                output[count++] = (short)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f);
            }
        }
        if (count != output.Length) Array.Resize(ref output, count);
        return output;
    }

    public static short[] ToPcm16(ReadOnlySpan<float> input)
    {
        var pcm = new short[input.Length];
        for (var i = 0; i < input.Length; i++)
            pcm[i] = (short)Math.Round(Math.Clamp(input[i], -1f, 1f) * 32767f);
        return pcm;
    }
}
