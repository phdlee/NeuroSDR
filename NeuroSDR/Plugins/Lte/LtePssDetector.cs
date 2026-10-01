using System.Numerics;

namespace NeuroSDR.Plugins.Lte;

/// <summary>LTE PSS (3GPP TS 36.211 §6.11.1) — Zadoff–Chu roots and time-domain matched filters.</summary>
internal sealed class LtePssDetector
{
    public const int PssLen = 62;
    public const int FftSize = 128;
    public const int FrameSamples = 9_600; // 5 ms @ 1.92 MS/s
    public const int SearchRate = 1_920_000;

    private static readonly double[] Roots = [25.0, 29.0, 34.0];
    private readonly Complex[][] _time = new Complex[3][];
    private readonly float[] _corr = new float[FrameSamples];

    public LtePssDetector()
    {
        for (var id2 = 0; id2 < 3; id2++)
            _time[id2] = BuildMatchedFilter(id2);
    }

    public static Complex[] GenerateFreqDomain(int nId2)
    {
        if ((uint)nId2 > 2) throw new ArgumentOutOfRangeException(nameof(nId2));
        var signal = new Complex[PssLen];
        var root = Roots[nId2];
        for (var i = 0; i < PssLen / 2; i++)
        {
            var arg = -Math.PI * root * (i * (i + 1.0)) / 63.0;
            signal[i] = Complex.FromPolarCoordinates(1, arg);
        }
        for (var i = PssLen / 2; i < PssLen; i++)
        {
            var arg = -Math.PI * root * ((i + 2.0) * (i + 1.0)) / 63.0;
            signal[i] = Complex.FromPolarCoordinates(1, arg);
        }
        return signal;
    }

    /// <summary>Time-domain PSS (TX) for <paramref name="nId2"/>, length <see cref="FftSize"/>.</summary>
    public static Complex[] BuildTimeDomain(int nId2)
    {
        var freq = GenerateFreqDomain(nId2);
        var centered = new Complex[FftSize];
        var start = (FftSize - PssLen) / 2;
        for (var i = 0; i < PssLen; i++) centered[start + i] = freq[i];
        var time = new Complex[FftSize];
        LteDft.InverseFromCentered(centered, time);
        return time;
    }

    public static Complex[] BuildMatchedFilter(int nId2)
    {
        var time = BuildTimeDomain(nId2);
        for (var i = 0; i < time.Length; i++)
            time[i] = Complex.Conjugate(time[i]) / PssLen;
        return time;
    }

    /// <summary>Obsolete alias — use <see cref="BuildMatchedFilter"/>.</summary>
    public static Complex[] BuildTimeTemplate(int nId2) => BuildMatchedFilter(nId2);

    public bool Find(ReadOnlySpan<Complex> frame, out int nId2, out int peakPos, out float psr, out float peakAbs)
    {
        nId2 = -1;
        peakPos = -1;
        psr = 0;
        peakAbs = 0;
        if (frame.Length < FrameSamples) return false;

        float bestPsr = 0;
        var bestId = -1;
        var bestPos = -1;
        float bestAbs = 0;

        for (var id = 0; id < 3; id++)
        {
            CorrelateTime(frame, _time[id], out var pos, out var peak, out var ratio);
            if (ratio > bestPsr)
            {
                bestPsr = ratio;
                bestId = id;
                bestPos = pos;
                bestAbs = peak;
            }
        }

        if (bestId < 0 || bestPsr < 1.8f) return false;
        nId2 = bestId;
        peakPos = bestPos;
        psr = bestPsr;
        peakAbs = bestAbs;
        return true;
    }

    public float EstimateCfoHz(ReadOnlySpan<Complex> frame, int peakPos, int nId2)
    {
        // peakPos = lag where template[0] aligns with frame[peakPos]
        if (peakPos < 0 || peakPos + FftSize > frame.Length) return 0;
        var tmpl = _time[nId2];
        Complex y0 = Complex.Zero, y1 = Complex.Zero;
        for (var i = 0; i < FftSize / 2; i++)
        {
            y0 += tmpl[i] * frame[peakPos + i];
            y1 += tmpl[FftSize / 2 + i] * frame[peakPos + FftSize / 2 + i];
        }
        var phase = (Complex.Conjugate(y0) * y1).Phase;
        return (float)(phase / Math.PI * (SearchRate / (double)FftSize));
    }

    private void CorrelateTime(ReadOnlySpan<Complex> frame, Complex[] tmpl, out int peakPos, out float peak, out float psr)
    {
        var limit = FrameSamples - FftSize;
        // Stride-2 coarse search, then refine ±2 — ~2× faster, still sample-accurate.
        var coarseBest = 0;
        var coarsePeak = 0f;
        for (var lag = 0; lag <= limit; lag += 2)
        {
            Complex sum = Complex.Zero;
            for (var i = 0; i < FftSize; i++)
                sum += tmpl[i] * frame[lag + i];
            var mag = (float)(sum.Real * sum.Real + sum.Imaginary * sum.Imaginary);
            _corr[lag] = mag;
            if (mag > coarsePeak)
            {
                coarsePeak = mag;
                coarseBest = lag;
            }
        }

        peakPos = coarseBest;
        peak = coarsePeak;
        var refineLo = Math.Max(0, coarseBest - 2);
        var refineHi = Math.Min(limit, coarseBest + 2);
        for (var lag = refineLo; lag <= refineHi; lag++)
        {
            if ((lag & 1) == 0 && lag == coarseBest) continue;
            Complex sum = Complex.Zero;
            for (var i = 0; i < FftSize; i++)
                sum += tmpl[i] * frame[lag + i];
            var mag = (float)(sum.Real * sum.Real + sum.Imaginary * sum.Imaginary);
            _corr[lag] = mag;
            if (mag <= peak) continue;
            peak = mag;
            peakPos = lag;
        }

        var guard = FftSize / 2;
        float side = 1e-20f;
        for (var i = 0; i <= limit; i += 2)
        {
            if (Math.Abs(i - peakPos) < guard) continue;
            var v = _corr[i];
            if (v > side) side = v;
        }
        psr = peak / side;
    }
}
