using NeuroSDR.Core;
using NeuroSDR.Dsp;
using System.Numerics;

namespace NeuroSDR.Core;

/// <summary>
/// Pre-compiles hot DSP paths on a background thread so the first minutes of RX
/// do not pay tiered-JIT and first-allocation costs on the real-time callback.
/// </summary>
internal static class RuntimeWarmup
{
    private static int _started;

    public static void StartIfNeeded()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(static _ => Run());
    }

    private static void Run()
    {
        try
        {
            var iq = new Complex32[4_096];
            var rng = new Random(17);
            for (var i = 0; i < iq.Length; i++)
                iq[i] = new Complex32((float)(rng.NextDouble() - 0.5), (float)(rng.NextDouble() - 0.5));

            var rf = new SpectrumProcessor(4_096, 2);
            for (var pass = 0; pass < 6; pass++) rf.Process(iq);

            var af = new SpectrumProcessor(4_096, 2);
            var mono = new Complex32[512];
            for (var i = 0; i < mono.Length; i++) mono[i] = new Complex32((float)(rng.NextDouble() - 0.5), 0);
            for (var pass = 0; pass < 6; pass++) af.Process(mono);

            var demod = new AudioDemodulator { Mode = RadioMode.USB, Bandwidth = 2_400 };
            for (var pass = 0; pass < 4; pass++) demod.Process(iq, 2_048_000);
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
        }
    }
}
