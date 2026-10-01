namespace NeuroSDR.Plugins.Lte;

/// <summary>Map downlink center frequency ↔ EARFCN (3GPP TS 36.101 table 5.7.3-1 subset).</summary>
internal static class LteEarfcn
{
    private readonly record struct Band(int Number, int EarfcnStart, int EarfcnEnd, double FdlLowMhz);

    // Common FDD DL bands (incl. Korea PS-LTE / commercial).
    private static readonly Band[] Bands =
    [
        new(1, 0, 599, 2110.0),
        new(2, 600, 1199, 1930.0),
        new(3, 1200, 1949, 1805.0),
        new(4, 1950, 2399, 2110.0),
        new(5, 2400, 2649, 869.0),
        new(7, 2750, 3449, 2620.0),
        new(8, 3450, 3799, 925.0),
        new(12, 5010, 5179, 729.0),
        new(13, 5180, 5279, 746.0),
        new(17, 5730, 5849, 734.0),
        new(20, 6150, 6449, 791.0),
        new(28, 9210, 9659, 758.0),
        new(66, 66436, 57335, 2110.0), // placeholder end fixed below
    ];

    static LteEarfcn()
    {
        // Band 66: N_DL 66436–57335 is wrong in draft — actual is 66436–67335
        Bands[^1] = new Band(66, 66436, 67335, 2110.0);
    }

    public static int FromFrequencyMhz(double freqMhz)
    {
        var bestEarfcn = -1;
        var bestErr = double.MaxValue;
        foreach (var band in Bands)
        {
            var n = (int)Math.Round((freqMhz - band.FdlLowMhz) / 0.1) + band.EarfcnStart;
            if (n < band.EarfcnStart || n > band.EarfcnEnd) continue;
            var f = band.FdlLowMhz + 0.1 * (n - band.EarfcnStart);
            var err = Math.Abs(f - freqMhz);
            if (err >= bestErr) continue;
            bestErr = err;
            bestEarfcn = n;
        }
        return bestErr <= 0.15 ? bestEarfcn : -1;
    }

    public static double ToFrequencyMhz(int earfcn)
    {
        foreach (var band in Bands)
        {
            if (earfcn < band.EarfcnStart || earfcn > band.EarfcnEnd) continue;
            return band.FdlLowMhz + 0.1 * (earfcn - band.EarfcnStart);
        }
        return 0;
    }

    public static int? BandOf(int earfcn)
    {
        foreach (var band in Bands)
            if (earfcn >= band.EarfcnStart && earfcn <= band.EarfcnEnd) return band.Number;
        return null;
    }
}
