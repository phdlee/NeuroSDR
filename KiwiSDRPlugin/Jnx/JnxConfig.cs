namespace KiwiSDRPlugin.Jnx;

public enum JnxEncoding
{
    Ita2,
    Ascii,
    Ccir476,
    Dsc,
    Selcall
}

/// <summary>
/// Configuration for <see cref="JnxDecoder"/>.
/// Designed for KiwiSDR AF rate of <b>12000</b> Hz (also works at 48000 with correct bit_sample_count).
/// <see cref="ShiftHz"/> is the FULL mark-space shift (Kiwi shift), not half-deviation.
/// For framing ending in ".5" (e.g. 5N1.5), baud is doubled internally like Kiwi FSK.js.
/// </summary>
public sealed class JnxConfig
{
    /// <summary>PCM sample rate. Prefer 12000 (Kiwi AF); 48000 is also valid.</summary>
    public int SampleRate { get; set; } = 12000;

    public double CenterFrequencyHz { get; set; } = 1000;

    /// <summary>Full mark-space shift in Hz (Kiwi "shift"), NOT half-deviation.</summary>
    public double ShiftHz { get; set; } = 170;

    public double BaudRate { get; set; } = 45.45;

    /// <summary>
    /// Framing string: 5N1, 5N1.5, 5N2, 7N1, 8N1, 4/7, 7/3, EFR, EFR2, CHU, 5N1V, etc.
    /// </summary>
    public string Framing { get; set; } = "5N1.5";

    public bool Inverted { get; set; }

    public JnxEncoding Encoding { get; set; } = JnxEncoding.Ita2;

    public bool ShowRaw { get; set; }

    public bool ShowErrs { get; set; }

    /// <summary>Injectable RF frequency (Hz) for DSC/Selcall status lines. Default 0.</summary>
    public Func<double>? GetFrequencyHz { get; set; }

    /// <summary>Optional extension name (replaces ext_get_name). Unused features skipped.</summary>
    public Func<string>? GetExtensionName { get; set; }
}
