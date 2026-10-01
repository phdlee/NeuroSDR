namespace NeuroSDR.Dsp;

/// <summary>
/// OS-CFAR style occupancy: compare the VFO channel peak to the median of nearby FFT bins.
/// Flat noise raises both the channel and neighbors together; a real signal is a localized bump.
/// </summary>
internal static class SpectrumOccupancy
{
    public readonly record struct Result(float SignalDb, float NoiseDb, float SnrDb, bool Occupied);

    public static Result Evaluate(float[] spectrum, int channelFirst, int channelLast, float snrThresholdDb)
    {
        if (spectrum.Length == 0) return new(-140f, -140f, 0f, false);
        channelFirst = Math.Clamp(channelFirst, 0, spectrum.Length - 1);
        channelLast = Math.Clamp(channelLast, channelFirst, spectrum.Length - 1);
        var signal = Peak(spectrum, channelFirst, channelLast);

        var width = Math.Max(1, channelLast - channelFirst + 1);
        var guard = Math.Max(2, width / 4);
        var refWidth = Math.Max(8, width * 3);
        var noise = MedianNeighbors(spectrum, channelFirst, channelLast, guard, refWidth);
        var snr = signal - noise;
        var occupied = snr >= snrThresholdDb && signal > noise + 2.5f;
        return new(signal, noise, snr, occupied);
    }

    /// <summary>
    /// Evaluates a broadcast channel against reference bands that start outside the receive
    /// bandwidth. A narrow carrier peak and broad modulation energy can both establish occupancy.
    /// </summary>
    public static Result EvaluateBroadcast(
        float[] spectrum, long spectrumCenterHz, int spectrumSpanHz, long frequencyHz, int bandwidthHz)
    {
        if (spectrum.Length < 8 || spectrumSpanHz <= 0) return new(-140f, -140f, 0f, false);
        var binHz = spectrumSpanHz / (double)spectrum.Length;
        var captureLeft = spectrumCenterHz - spectrumSpanHz / 2d;
        int Bin(long hz) =>
            Math.Clamp((int)Math.Round((hz - captureLeft) / binHz), 0, spectrum.Length - 1);

        bandwidthHz = Math.Max(2_000, bandwidthHz);
        var halfBw = bandwidthHz / 2d;
        var guardHz = Math.Max(1_000d, bandwidthHz * .15);
        var referenceHz = Math.Max(20_000d, bandwidthHz * 2d);
        var requiredHalfSpan = halfBw + guardHz + referenceHz;
        if (frequencyHz - requiredHalfSpan < captureLeft ||
            frequencyHz + requiredHalfSpan > captureLeft + spectrumSpanHz)
            return new(-140f, -140f, 0f, false);

        // The carrier is expected near channel center. Keeping this window narrow prevents
        // a strong station on an adjacent 5 kHz channel from being credited to this row.
        var coreHalfHz = Math.Min(2_000d, Math.Max(600d, bandwidthHz * .2));
        var coreFirst = Bin((long)Math.Round(frequencyHz - coreHalfHz));
        var coreLast = Bin((long)Math.Round(frequencyHz + coreHalfHz));
        var channelFirst = Bin((long)Math.Round(frequencyHz - halfBw));
        var channelLast = Bin((long)Math.Round(frequencyHz + halfBw));
        var signal = Math.Max(
            Peak(spectrum, coreFirst, coreLast),
            Percentile(spectrum, channelFirst, channelLast, .85));

        var referenceFirstLeft = Bin((long)Math.Round(frequencyHz - halfBw - guardHz - referenceHz));
        var referenceLastLeft = Bin((long)Math.Round(frequencyHz - halfBw - guardHz));
        var referenceFirstRight = Bin((long)Math.Round(frequencyHz + halfBw + guardHz));
        var referenceLastRight = Bin((long)Math.Round(frequencyHz + halfBw + guardHz + referenceHz));
        var noise = MedianRanges(spectrum,
            referenceFirstLeft, referenceLastLeft, referenceFirstRight, referenceLastRight);
        var snr = signal - noise;

        // A carrier can be a single FFT bin. A modulated/weak fading station may instead
        // raise several bins across the channel, so retain a second robust channel test.
        var channelLevel = Percentile(spectrum, channelFirst, channelLast, .65);
        var occupied = snr >= 4f || (signal - noise >= 2.5f && channelLevel - noise >= 1.5f);
        return new(signal, noise, snr, occupied);
    }

    private static float MedianNeighbors(float[] spectrum, int first, int last, int guard, int refWidth)
    {
        var leftEnd = Math.Max(0, first - guard);
        var leftStart = Math.Max(0, leftEnd - refWidth);
        var rightStart = Math.Min(spectrum.Length, last + 1 + guard);
        var rightEnd = Math.Min(spectrum.Length, rightStart + refWidth);
        var count = (leftEnd - leftStart) + (rightEnd - rightStart);
        if (count < 4)
        {
            var fallback = 0f;
            foreach (var value in spectrum) fallback += value;
            return fallback / spectrum.Length;
        }

        Span<float> scratch = count <= 256 ? stackalloc float[count] : new float[count];
        var n = 0;
        for (var i = leftStart; i < leftEnd; i++) scratch[n++] = spectrum[i];
        for (var i = rightStart; i < rightEnd; i++) scratch[n++] = spectrum[i];
        scratch[..n].Sort();
        return scratch[n / 2];
    }

    private static float Peak(float[] spectrum, int first, int last)
    {
        var peak = -140f;
        for (var i = first; i <= last; i++)
            peak = Math.Max(peak, spectrum[i]);
        return peak;
    }

    private static float Percentile(float[] spectrum, int first, int last, double percentile)
    {
        if (last < first) (first, last) = (last, first);
        var values = new float[last - first + 1];
        Array.Copy(spectrum, first, values, 0, values.Length);
        Array.Sort(values);
        var index = Math.Clamp((int)Math.Round((values.Length - 1) * percentile), 0, values.Length - 1);
        return values[index];
    }

    private static float MedianRanges(float[] spectrum, int leftFirst, int leftLast, int rightFirst, int rightLast)
    {
        if (leftLast < leftFirst) (leftFirst, leftLast) = (leftLast, leftFirst);
        if (rightLast < rightFirst) (rightFirst, rightLast) = (rightLast, rightFirst);
        var values = new float[leftLast - leftFirst + 1 + rightLast - rightFirst + 1];
        var n = 0;
        for (var i = leftFirst; i <= leftLast; i++) values[n++] = spectrum[i];
        for (var i = rightFirst; i <= rightLast; i++) values[n++] = spectrum[i];
        Array.Sort(values, 0, n);
        return values[n / 2];
    }
}
