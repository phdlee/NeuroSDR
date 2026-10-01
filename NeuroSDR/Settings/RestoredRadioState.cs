using NeuroSDR.Core;
using NeuroSDR.Dsp;

namespace NeuroSDR.Settings;

internal readonly record struct RestoredRadioState(
    long TunedFrequency,
    long RfCenterFrequency,
    long ViewCenterFrequency,
    int ViewBandwidth,
    RadioMode Mode,
    int FilterBandwidth,
    int CwPitchHz)
{
    public static RestoredRadioState From(AppSettings settings, int sampleRate)
    {
        var tuned = Math.Clamp(settings.TunedFrequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        var rfCenter = Math.Clamp(settings.RfCenterFrequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        var viewCenter = Math.Clamp(settings.ViewCenterFrequency, RadioLimits.MinimumFrequency, RadioLimits.MaximumFrequency);
        var viewBandwidth = Math.Clamp(settings.ViewBandwidth, 5_000, Math.Max(5_000, sampleRate));
        var mode = Enum.IsDefined(settings.Mode) ? settings.Mode : RadioMode.WFM;
        var filterBandwidth = Math.Clamp(settings.FilterBandwidth, 500, 250_000);
        return new RestoredRadioState(tuned, rfCenter, viewCenter, viewBandwidth, mode,
            filterBandwidth, settings.CwLowerSide ? -700 : 700);
    }

    public void ApplyTo(AudioDemodulator demodulator)
    {
        demodulator.Mode = Mode;
        demodulator.Bandwidth = FilterBandwidth;
        demodulator.CwPitchHz = CwPitchHz;
        demodulator.FrequencyOffset = TunedFrequency - RfCenterFrequency;
    }
}
