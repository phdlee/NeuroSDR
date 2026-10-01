using NeuroSDR.Core;
using NeuroSDR.Settings;

namespace NeuroSDR.Dsp;

internal sealed class SubVfoReceiver
{
    public string Id { get; }
    public string Name { get; }
    public long Frequency { get; set; }
    public RadioMode Mode { get; }
    public int Bandwidth { get; }
    public int OutputChannel { get; }
    public AudioDemodulator Demodulator { get; } = new();
    public AudioPostProcessor Processor { get; } = new();
    public float SignalLevelDb = -140;
    public long LastAudioTick;

    public SubVfoReceiver(SubVfoSettings settings)
    {
        Id = settings.Id;
        Name = settings.Name;
        Frequency = settings.Frequency;
        Mode = settings.Mode;
        Bandwidth = settings.Bandwidth;
        OutputChannel = settings.OutputChannel;
        Demodulator.Mode = Mode;
        Demodulator.Bandwidth = Bandwidth;
        Processor.SquelchEnabled = settings.SquelchEnabled;
        Processor.SquelchThresholdDb = settings.SquelchLevel;
    }

    public bool IsInside(long center, int sampleRate)
    {
        var half = Mode switch
        {
            RadioMode.USB or RadioMode.LSB or RadioMode.FREEDV => Bandwidth,
            _ => Math.Max(50, Bandwidth / 2)
        };
        return Frequency - half >= center - sampleRate / 2L &&
               Frequency + half <= center + sampleRate / 2L;
    }
}
