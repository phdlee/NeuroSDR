namespace NeuroSDR.Plugins.DigitalVoice;

internal static class DigitalVoiceBurstFactory
{
    public const string BsVoiceSync = "131111333113313313113313";
    public const string BsDataSync = "313333111331131131331131";
    public const string DstarSync = "313131313133131113313111";

    public static byte[] DmrBurst(string syncPattern, int slot, bool voice, int colorCode = 1, int dataType = 1)
    {
        var burst = new byte[DigitalVoiceDecoder.BurstDibits];
        WriteCach(burst.AsSpan(0, 12), slot);
        WriteSync(burst.AsSpan(66, 24), syncPattern);
        if (!voice) WriteSlotType(burst, colorCode, dataType);
        return burst;
    }

    public static float[] ToDiscriminatorPcm(ReadOnlySpan<byte> dibits, int sampleRate = DigitalVoiceDecoder.AudioRate)
    {
        var sps = sampleRate / DigitalVoiceDecoder.SymbolRate;
        var pcm = new float[dibits.Length * sps];
        var n = 0;
        foreach (var dibit in dibits)
        {
            var level = (dibit & 3) switch { 0 => 1f, 1 => 3f, 2 => -1f, _ => -3f };
            for (var i = 0; i < sps; i++) pcm[n++] = level;
        }
        return pcm;
    }

    public static byte[] FromPattern(string pattern)
    {
        var dibits = new byte[pattern.Length];
        for (var i = 0; i < pattern.Length; i++)
            dibits[i] = pattern[i] == '3' ? (byte)3 : (byte)1;
        return dibits;
    }

    private static void WriteCach(Span<byte> cach12, int slot)
    {
        Span<byte> info = stackalloc byte[4];
        info[0] = 1;
        info[1] = (byte)(slot & 1);
        Span<byte> tact = stackalloc byte[7];
        DmrFec.Hamming74Encode(info, tact);
        Span<byte> bits = stackalloc byte[24];
        tact.CopyTo(bits);
        int[] interleave = [0, 7, 8, 9, 1, 10, 11, 12, 2, 13, 14, 15, 3, 16, 4, 17, 18, 19, 5, 20, 21, 22, 6, 23];
        for (var i = 0; i < 12; i++)
        {
            var hi = bits[interleave[i * 2]];
            var lo = bits[interleave[i * 2 + 1]];
            cach12[i] = (byte)((hi << 1) | lo);
        }
    }

    private static void WriteSlotType(byte[] burst, int colorCode, int dataType)
    {
        Span<byte> info = stackalloc byte[8];
        info[0] = (byte)((colorCode >> 3) & 1);
        info[1] = (byte)((colorCode >> 2) & 1);
        info[2] = (byte)((colorCode >> 1) & 1);
        info[3] = (byte)(colorCode & 1);
        info[4] = (byte)((dataType >> 3) & 1);
        info[5] = (byte)((dataType >> 2) & 1);
        info[6] = (byte)((dataType >> 1) & 1);
        info[7] = (byte)(dataType & 1);
        Span<byte> golay = stackalloc byte[20];
        DmrFec.Golay208Encode(info, golay);
        var n = 0;
        for (var i = 61; i <= 65; i++)
            burst[i] = (byte)((golay[n++] << 1) | golay[n++]);
        for (var i = 90; i <= 94; i++)
            burst[i] = (byte)((golay[n++] << 1) | golay[n++]);
    }

    private static void WriteSync(Span<byte> dest, string pattern)
    {
        for (var i = 0; i < dest.Length && i < pattern.Length; i++)
            dest[i] = pattern[i] == '3' ? (byte)3 : (byte)1;
    }
}
