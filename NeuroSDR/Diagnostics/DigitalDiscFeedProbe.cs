using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Plugins.DigitalVoice;

namespace NeuroSDR.Diagnostics;

/// <summary>Synthetic check that DMR discriminator audio is uniform 48 kHz and the right level.</summary>
internal static class DigitalDiscFeedProbe
{
    public static int Run()
    {
        const int inputRate = 2_000_000;
        const int samples = 200_000; // 100 ms
        const double offsetHz = 1_944;
        var iq = new Complex32[samples];
        var phase = 0.0;
        var step = 2 * Math.PI * offsetHz / inputRate;
        for (var i = 0; i < samples; i++)
        {
            iq[i] = new Complex32((float)Math.Cos(phase), (float)Math.Sin(phase));
            phase += step;
        }

        var demod = new AudioDemodulator { Mode = RadioMode.DMR, Bandwidth = 12_500 };
        var audio = demod.Process(iq, inputRate);
        var expected = samples * (double)AudioDemodulator.AudioSampleRate / inputRate;
        if (Math.Abs(audio.Length - expected) > 2)
        {
            Console.WriteLine($"FAIL count {audio.Length} expected {expected:0}");
            return 1;
        }

        double sum = 0;
        var start = audio.Length / 5;
        var n = 0;
        for (var i = start; i < audio.Length; i++)
        {
            sum += audio[i];
            n++;
        }
        var mean = n == 0 ? 0 : sum / n;
        var want = offsetHz / 12_500d;
        var counts = mean * DigitalModeEngine.DiscriminatorFeedGain * 32767d;
        Console.WriteLine($"count={audio.Length} mean={mean:0.0000} want={want:0.0000} int16≈{counts:0}");
        if (mean < want * 0.75 || mean > want * 1.25)
        {
            Console.WriteLine("FAIL level");
            return 2;
        }
        if (counts < 12_000 || counts > 16_000)
        {
            Console.WriteLine("FAIL int16 window");
            return 3;
        }
        Console.WriteLine("PASS");
        return 0;
    }
}
