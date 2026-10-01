using System.Numerics;

namespace NeuroSDR.Plugins.Lte;

/// <summary>LTE SSS (3GPP TS 36.211 §6.11.2) — m0/m1 → N_id_1 (srsRAN partial-correlation path).</summary>
internal sealed class LteSssDetector
{
    public const int N = 31;
    public const int FftSize = 128;

    private readonly int[,] _nId1Table = new int[30, 30];
    private readonly float[][,] _s = new float[3][,];
    private readonly float[][,] _z1 = new float[3][,];
    private readonly float[][] _c0 = new float[3][];
    private readonly float[][] _c1 = new float[3][];
    private readonly float[] _corr0 = new float[N];
    private readonly float[] _corr1 = new float[N];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly Complex[] _y0 = new Complex[N];
    private readonly Complex[] _y1 = new Complex[N];

    public LteSssDetector()
    {
        for (var i = 0; i < 30; i++)
        for (var j = 0; j < 30; j++)
            _nId1Table[i, j] = -1;

        for (uint nId1 = 0; nId1 < 168; nId1++)
        {
            GenerateM0M1(nId1, out var m0, out var m1);
            if (m1 >= 1 && m1 <= 30 && m0 < 30)
                _nId1Table[m0, m1 - 1] = (int)nId1;
        }

        var sTilde = new int[N];
        var cTilde = new int[N];
        var zTilde = new int[N];
        GenerateZscTilde(zTilde, sTilde, cTilde);

        for (var nId2 = 0; nId2 < 3; nId2++)
        {
            _s[nId2] = new float[N, N];
            _z1[nId2] = new float[N, N];
            _c0[nId2] = new float[N];
            _c1[nId2] = new float[N];
            for (var m = 0; m < N; m++)
            {
                for (var i = 0; i < N; i++)
                {
                    _s[nId2][m, i] = sTilde[(i + m) % N];
                    _z1[nId2][m, i] = zTilde[(i + (m % 8)) % N];
                }
            }
            for (var i = 0; i < N; i++)
            {
                _c0[nId2][i] = cTilde[(i + nId2) % N];
                _c1[nId2][i] = cTilde[(i + nId2 + 3) % N];
            }
        }
    }

    public static int CpLenSamples(bool extended) =>
        extended
            ? (int)Math.Ceiling(512.0 * FftSize / 2048.0)   // 32
            : (int)Math.Ceiling(144.0 * FftSize / 2048.0);  // 9

    public static int SssOffsetFromPssPeak(bool extended) =>
        2 * (FftSize + CpLenSamples(extended));

    /// <summary>Decode N_id_1 from a length-<see cref="FftSize"/> SSS symbol window.</summary>
    public bool TryDecode(ReadOnlySpan<Complex> sssSymbol, int nId2, out int nId1, out int subframe, out float score)
    {
        nId1 = -1;
        subframe = 0;
        score = 0;
        if (sssSymbol.Length < FftSize || (uint)nId2 > 2) return false;

        ExtractPair(sssSymbol, nId2);

        CorrelatePartial(_y0, _s[nId2], _corr0);
        var m0 = ArgMax(_corr0);
        var m0Val = _corr0[m0];

        for (var i = 0; i < N; i++)
            _y1[i] *= _z1[nId2][m0, i];

        CorrelatePartial(_y1, _s[nId2], _corr1);
        var m1 = ArgMax(_corr1);
        var m1Val = _corr1[m1];
        score = m0Val + m1Val;

        var mean = 0f;
        for (var i = 0; i < N; i++) mean += _corr0[i] + _corr1[i];
        mean /= 2 * N;
        if (score < mean * 3.5f + 1e-6f) return false;

        if (m1 > m0)
        {
            if (m0 >= 30 || m1 - 1 >= 30) return false;
            nId1 = _nId1Table[m0, m1 - 1];
            subframe = 0;
        }
        else
        {
            if (m1 >= 30 || m0 - 1 >= 30) return false;
            nId1 = _nId1Table[m1, m0 - 1];
            subframe = 5;
        }

        return nId1 >= 0;
    }

    private void ExtractPair(ReadOnlySpan<Complex> symbol, int nId2)
    {
        for (var i = 0; i < FftSize; i++) _fft[i] = symbol[i];
        LteDft.ForwardCentered(_fft, _fft);
        var mid = FftSize / 2;
        for (var i = 0; i < N; i++)
        {
            _y0[i] = _fft[mid - N + 2 * i];
            _y1[i] = _fft[mid - N + 2 * i + 1];
        }
        Normalize(_y0);
        Normalize(_y1);
        for (var i = 0; i < N; i++)
        {
            _y0[i] *= _c0[nId2][i];
            _y1[i] *= _c1[nId2][i];
        }
    }

    private static void CorrelatePartial(Complex[] y, float[,] s, float[] output)
    {
        for (var m = 0; m < N; m++)
        {
            Complex sum = Complex.Zero;
            for (var i = 0; i < N; i++)
                sum += y[i] * s[m, i];
            output[m] = (float)(sum.Real * sum.Real + sum.Imaginary * sum.Imaginary);
        }
    }

    private static void Normalize(Complex[] y)
    {
        double power = 0;
        for (var i = 0; i < y.Length; i++)
            power += y[i].Real * y[i].Real + y[i].Imaginary * y[i].Imaginary;
        var rms = Math.Sqrt(power / y.Length);
        if (rms < 1e-12) rms = 1;
        var scale = 1.0 / rms;
        for (var i = 0; i < y.Length; i++) y[i] *= scale;
    }

    private static int ArgMax(float[] v)
    {
        var best = 0;
        for (var i = 1; i < v.Length; i++)
            if (v[i] > v[best]) best = i;
        return best;
    }

    private static void GenerateM0M1(uint nId1, out uint m0, out uint m1)
    {
        var qPrime = nId1 / (N - 1);
        var q = (nId1 + qPrime * (qPrime + 1) / 2) / (N - 1);
        var mPrime = nId1 + q * (q + 1) / 2;
        m0 = mPrime % N;
        m1 = (m0 + mPrime / N + 1) % N;
    }

    private static void GenerateZscTilde(int[] zTilde, int[] sTilde, int[] cTilde)
    {
        // Match srsRAN gen_sss.c: seed x[0..4]=0,0,0,0,1 stays; only x[5..] are rewritten.
        var x = new int[N];
        x[4] = 1;
        for (var i = 0; i < 26; i++) x[i + 5] = (x[i + 2] + x[i]) % 2;
        for (var i = 0; i < N; i++) sTilde[i] = 1 - 2 * x[i];

        for (var i = 0; i < 26; i++) x[i + 5] = (x[i + 3] + x[i]) % 2;
        for (var i = 0; i < N; i++) cTilde[i] = 1 - 2 * x[i];

        for (var i = 0; i < 26; i++) x[i + 5] = (x[i + 4] + x[i + 2] + x[i + 1] + x[i]) % 2;
        for (var i = 0; i < N; i++) zTilde[i] = 1 - 2 * x[i];
    }

    /// <summary>Build frequency-domain SSS (62 REs) for subframe 0 or 5.</summary>
    public static float[] GenerateSssFreq(int cellId, bool subframe5)
    {
        var id1 = (uint)(cellId / 3);
        var id2 = (uint)(cellId % 3);
        GenerateM0M1(id1, out var m0, out var m1);
        var sTilde = new int[N];
        var cTilde = new int[N];
        var zTilde = new int[N];
        GenerateZscTilde(zTilde, sTilde, cTilde);
        var s0 = new int[N];
        var s1 = new int[N];
        var c0 = new int[N];
        var c1 = new int[N];
        var z10 = new int[N];
        var z11 = new int[N];
        for (var i = 0; i < N; i++)
        {
            s0[i] = sTilde[(i + (int)m0) % N];
            s1[i] = sTilde[(i + (int)m1) % N];
            c0[i] = cTilde[(i + (int)id2) % N];
            c1[i] = cTilde[(i + (int)id2 + 3) % N];
            z10[i] = zTilde[(i + ((int)m0 % 8)) % N];
            z11[i] = zTilde[(i + ((int)m1 % 8)) % N];
        }
        var signal = new float[2 * N];
        if (!subframe5)
        {
            for (var i = 0; i < N; i++)
            {
                signal[2 * i] = s0[i] * c0[i];
                signal[2 * i + 1] = s1[i] * c1[i] * z10[i];
            }
        }
        else
        {
            for (var i = 0; i < N; i++)
            {
                signal[2 * i] = s1[i] * c0[i];
                signal[2 * i + 1] = s0[i] * c1[i] * z11[i];
            }
        }
        return signal;
    }
}
