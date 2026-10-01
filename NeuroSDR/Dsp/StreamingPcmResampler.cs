namespace NeuroSDR.Dsp;

/// <summary>
/// Small stateful mono resampler for slow-mode AF decoders. The SDR host keeps
/// its full 48 kHz stream; each adapter chooses the rate required by its engine.
/// </summary>
internal sealed class StreamingPcmResampler(int sourceRate, int targetRate)
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

    public void Reset() => _phase = 0;
}
