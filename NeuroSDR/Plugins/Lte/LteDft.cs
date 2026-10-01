using System.Numerics;
using NeuroSDR.Dsp;

namespace NeuroSDR.Plugins.Lte;

/// <summary>Complex FFT helpers matching srsRAN mirror+DC (centered spectrum) conventions.</summary>
internal static class LteDft
{
    public static void ForwardDc0(Complex[] data) => SpectrumProcessor.ForwardFft(data);

    public static void InverseDc0(Complex[] data)
    {
        var n = data.Length;
        for (var i = 0; i < n; i++) data[i] = Complex.Conjugate(data[i]);
        SpectrumProcessor.ForwardFft(data);
        var scale = 1.0 / n;
        for (var i = 0; i < n; i++) data[i] = Complex.Conjugate(data[i]) * scale;
    }

    /// <summary>DC @ 0 → DC @ N/2 (fftshift).</summary>
    public static void FftShift(Complex[] data)
    {
        var n = data.Length;
        var half = n / 2;
        var tmp = new Complex[half];
        Array.Copy(data, 0, tmp, 0, half);
        Array.Copy(data, half, data, 0, n - half);
        Array.Copy(tmp, 0, data, n - half, half);
    }

    /// <summary>DC @ N/2 → DC @ 0 (ifftshift).</summary>
    public static void IfftShift(Complex[] data)
    {
        var n = data.Length;
        var half = n / 2;
        var tmp = new Complex[n - half];
        Array.Copy(data, half, tmp, 0, n - half);
        Array.Copy(data, 0, data, n - half, half);
        Array.Copy(tmp, 0, data, 0, n - half);
    }

    /// <summary>Forward FFT then shift so DC sits at N/2 (srsRAN forward+mirror+dc output layout).</summary>
    public static void ForwardCentered(Complex[] timeDomain, Complex[] centeredOut)
    {
        Array.Copy(timeDomain, centeredOut, timeDomain.Length);
        ForwardDc0(centeredOut);
        FftShift(centeredOut);
    }

    /// <summary>IFFT from centered spectrum (srsRAN backward+mirror+dc).</summary>
    public static void InverseFromCentered(Complex[] centered, Complex[] timeOut)
    {
        Array.Copy(centered, timeOut, centered.Length);
        IfftShift(timeOut);
        InverseDc0(timeOut);
    }
}
