namespace NeuroSDR.Core;

public enum RadioMode
{
    AM,
    SAM,
    DMR,
    DSTAR,
    C4FM,
    NFM,
    WFM,
    USB,
    LSB,
    CW,
    RAW,
    FREEDV
}

public static class RadioModes
{
    public static bool IsFmDigitalVoice(RadioMode mode) =>
        mode is RadioMode.DMR or RadioMode.DSTAR or RadioMode.C4FM;

    public static bool IsDigitalVoice(RadioMode mode) =>
        IsFmDigitalVoice(mode) || mode == RadioMode.FREEDV;

    /// <summary>Kiwi/WebSDR analog demod. FreeDV is SSB (not NFM).</summary>
    public static RadioMode DemodMode(RadioMode mode, bool freedvLower = true) =>
        mode == RadioMode.FREEDV
            ? (freedvLower ? RadioMode.LSB : RadioMode.USB)
            : IsFmDigitalVoice(mode) ? RadioMode.NFM : mode;

    public static (long Low, long High) FilterRange(RadioMode mode, long tuned, int bandwidth, bool freedvLower = true)
    {
        if (mode == RadioMode.USB || (mode == RadioMode.FREEDV && !freedvLower))
            return (tuned, tuned + bandwidth);
        if (mode == RadioMode.LSB || (mode == RadioMode.FREEDV && freedvLower))
            return (tuned - bandwidth, tuned);
        return (tuned - bandwidth / 2L, tuned + bandwidth / 2L);
    }

    public static int DefaultBandwidth(RadioMode mode) => mode switch
    {
        RadioMode.WFM => 180_000,
        RadioMode.NFM or RadioMode.DMR or RadioMode.DSTAR => 12_500,
        RadioMode.C4FM => 16_000,
        RadioMode.AM or RadioMode.SAM => 10_000,
        RadioMode.USB or RadioMode.LSB or RadioMode.FREEDV => 2_700,
        RadioMode.CW => 500,
        _ => 48_000
    };
}
