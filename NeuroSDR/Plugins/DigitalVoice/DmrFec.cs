namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// Hamming / Golay / BPTC from ETSI TS 102 361-1, using DSD-FME generator matrices
/// so Slot Type and CACH match the Phase-2 engine.
/// </summary>
internal static class DmrFec
{
    public static bool Hamming74Decode(Span<byte> bits7)
    {
        if (bits7.Length < 7) return false;
        var best = 8;
        var bestCode = new byte[7];
        Span<byte> encoded = stackalloc byte[7];
        for (var info = 0; info < 16; info++)
        {
            Encode(Hamming74G, 4, 7, info, encoded);
            var distance = Distance(bits7, encoded, 7);
            if (distance < best)
            {
                best = distance;
                encoded.CopyTo(bestCode);
                if (distance == 0) break;
            }
        }
        if (best > 1) return false;
        bestCode.AsSpan(0, 7).CopyTo(bits7);
        return true;
    }

    public static void Hamming74Encode(ReadOnlySpan<byte> info4, Span<byte> bits7) =>
        Encode(Hamming74G, 4, 7, Pack(info4, 4), bits7);

    public static bool Golay208Decode(Span<byte> bits20)
    {
        if (bits20.Length < 20) return false;
        var best = 21;
        var bestCode = new byte[20];
        Span<byte> encoded = stackalloc byte[20];
        for (var info = 0; info < 256; info++)
        {
            Encode(Golay208G, 8, 20, info, encoded);
            var distance = Distance(bits20, encoded, 20);
            if (distance < best)
            {
                best = distance;
                encoded.CopyTo(bestCode);
                if (distance == 0) break;
            }
        }
        if (best > 2) return false;
        bestCode.AsSpan(0, 20).CopyTo(bits20);
        return true;
    }

    public static void Golay208Encode(ReadOnlySpan<byte> info8, Span<byte> bits20) =>
        Encode(Golay208G, 8, 20, Pack(info8, 8), bits20);

    public static bool Hamming1511Decode(Span<byte> bits15, Span<byte> info11)
    {
        if (bits15.Length < 15 || info11.Length < 11) return false;
        var best = 16;
        var bestInfo = 0;
        Span<byte> encoded = stackalloc byte[15];
        for (var info = 0; info < 2048; info++)
        {
            Encode(Hamming1511G, 11, 15, info, encoded);
            var distance = Distance(bits15, encoded, 15);
            if (distance < best)
            {
                best = distance;
                bestInfo = info;
                if (distance == 0) break;
            }
        }
        if (best > 1) return false;
        Unpack(bestInfo, info11, 11);
        Encode(Hamming1511G, 11, 15, bestInfo, bits15);
        return true;
    }

    public static bool Hamming139Decode(Span<byte> bits13, Span<byte> info9)
    {
        if (bits13.Length < 13 || info9.Length < 9) return false;
        var best = 14;
        var bestInfo = 0;
        Span<byte> encoded = stackalloc byte[13];
        for (var info = 0; info < 512; info++)
        {
            Encode(Hamming139G, 9, 13, info, encoded);
            var distance = Distance(bits13, encoded, 13);
            if (distance < best)
            {
                best = distance;
                bestInfo = info;
                if (distance == 0) break;
            }
        }
        if (best > 1) return false;
        Unpack(bestInfo, info9, 9);
        Encode(Hamming139G, 9, 13, bestInfo, bits13);
        return true;
    }

    public static void BptcDeinterleave(ReadOnlySpan<byte> input196, Span<byte> output196)
    {
        for (var i = 0; i < 196; i++)
            output196[BptcDeinterleaveIndex[i]] = (byte)(input196[i] & 1);
    }

    public static bool Bptc19696Extract(ReadOnlySpan<byte> deinterleaved196, Span<byte> data96)
    {
        if (deinterleaved196.Length < 196 || data96.Length < 96) return false;
        var matrix = new byte[13, 15];
        var k = 1;
        for (var row = 0; row < 13; row++)
        for (var col = 0; col < 15; col++)
            matrix[row, col] = (byte)(deinterleaved196[k++] & 1);

        Span<byte> line = stackalloc byte[15];
        Span<byte> lineInfo = stackalloc byte[11];
        Span<byte> column = stackalloc byte[13];
        Span<byte> columnInfo = stackalloc byte[9];

        for (var pass = 0; pass < 2; pass++)
        {
            for (var row = 0; row < 9; row++)
            {
                for (var col = 0; col < 15; col++) line[col] = matrix[row, col];
                if (!Hamming1511Decode(line, lineInfo)) return pass > 0;
                for (var col = 0; col < 11; col++) matrix[row, col] = lineInfo[col];
            }
            for (var col = 0; col < 15; col++)
            {
                for (var row = 0; row < 13; row++) column[row] = matrix[row, col];
                if (!Hamming139Decode(column, columnInfo)) return pass > 0;
                for (var row = 0; row < 9; row++) matrix[row, col] = columnInfo[row];
            }
        }

        var n = 0;
        for (var i = 3; i < 11; i++) data96[n++] = matrix[0, i];
        for (var row = 1; row < 9; row++)
        for (var col = 0; col < 11; col++)
            data96[n++] = matrix[row, col];
        return n == 96;
    }

    public static bool DecodeBptc19696(ReadOnlySpan<byte> interleaved196, Span<byte> data96)
    {
        Span<byte> deinterleaved = stackalloc byte[196];
        BptcDeinterleave(interleaved196, deinterleaved);
        return Bptc19696Extract(deinterleaved, data96);
    }

    public static bool Hamming16114Decode(Span<byte> bits16, Span<byte> info11)
    {
        if (bits16.Length < 16 || info11.Length < 11) return false;
        var best = 17;
        var bestInfo = 0;
        Span<byte> encoded = stackalloc byte[16];
        for (var info = 0; info < 2048; info++)
        {
            Encode(Hamming16114G, 11, 16, info, encoded);
            var distance = Distance(bits16, encoded, 16);
            if (distance < best)
            {
                best = distance;
                bestInfo = info;
                if (distance == 0) break;
            }
        }
        if (best > 1) return false;
        Unpack(bestInfo, info11, 11);
        Encode(Hamming16114G, 11, 16, bestInfo, bits16);
        return true;
    }

    /// <summary>Voice superframe EMB fragments B–E (32 bits each) → 72-bit Full LC.</summary>
    public static bool DecodeEmbeddedLc(ReadOnlySpan<byte> fragB, ReadOnlySpan<byte> fragC,
        ReadOnlySpan<byte> fragD, ReadOnlySpan<byte> fragE, Span<byte> lc72)
    {
        if (fragB.Length < 32 || fragC.Length < 32 || fragD.Length < 32 || fragE.Length < 32 ||
            lc72.Length < 72) return false;
        var matrix = new byte[8, 16];
        var burst = 0;
        var k = 0;
        for (var i = 0; i < 16; i++)
        for (var j = 0; j < 8; j++)
        {
            var frag = burst switch { 0 => fragB, 1 => fragC, 2 => fragD, _ => fragE };
            matrix[j, i] = (byte)(frag[k++] & 1);
            if (k < 32) continue;
            k = 0;
            burst++;
        }

        Span<byte> line = stackalloc byte[16];
        Span<byte> info = stackalloc byte[11];
        var errors = 0;
        for (var row = 0; row < 7; row++)
        {
            for (var col = 0; col < 16; col++) line[col] = matrix[row, col];
            if (!Hamming16114Decode(line, info)) errors++;
            else
                for (var col = 0; col < 11; col++) matrix[row, col] = info[col];
        }
        if (errors > 2) return false;

        var n = 0;
        for (var row = 0; row < 2; row++)
        for (var col = 0; col < 11; col++)
            lc72[n++] = matrix[row, col];
        for (var row = 2; row < 7; row++)
        for (var col = 0; col < 10; col++)
            lc72[n++] = matrix[row, col];
        Span<byte> crcBits = stackalloc byte[5];
        for (var row = 2; row < 7; row++) crcBits[row - 2] = matrix[row, 10];
        var extracted = 0;
        for (var i = 0; i < 5; i++) extracted = (extracted << 1) | (crcBits[i] & 1);
        return extracted == Crc5(lc72);
    }

    public static int Crc5(ReadOnlySpan<byte> lc72)
    {
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var value = 0;
            for (var j = 0; j < 8; j++) value = (value << 1) | (lc72[i * 8 + j] & 1);
            sum += value;
        }
        return sum % 31;
    }

    private static void Encode(byte[] generator, int k, int n, int info, Span<byte> encoded)
    {
        encoded[..n].Clear();
        for (var i = 0; i < k; i++)
        {
            if (((info >> (k - 1 - i)) & 1) == 0) continue;
            for (var j = 0; j < n; j++)
                encoded[j] ^= generator[i * n + j];
        }
    }

    private static int Pack(ReadOnlySpan<byte> bits, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++) value = (value << 1) | (bits[i] & 1);
        return value;
    }

    private static void Unpack(int value, Span<byte> bits, int count)
    {
        for (var i = 0; i < count; i++)
            bits[i] = (byte)((value >> (count - 1 - i)) & 1);
    }

    private static int Distance(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int length)
    {
        var distance = 0;
        for (var i = 0; i < length; i++)
            if ((left[i] & 1) != (right[i] & 1)) distance++;
        return distance;
    }

    // DSD-FME Hamming_7_4_m_G
    private static readonly byte[] Hamming74G =
    [
        1,0,0,0, 1,0,1,
        0,1,0,0, 1,1,1,
        0,0,1,0, 1,1,0,
        0,0,0,1, 0,1,1
    ];

    // DSD-FME Hamming_15_11_m_G
    private static readonly byte[] Hamming1511G =
    [
        1,0,0,0,0,0,0,0,0,0,0, 1,0,0,1,
        0,1,0,0,0,0,0,0,0,0,0, 1,1,0,1,
        0,0,1,0,0,0,0,0,0,0,0, 1,1,1,1,
        0,0,0,1,0,0,0,0,0,0,0, 1,1,1,0,
        0,0,0,0,1,0,0,0,0,0,0, 0,1,1,1,
        0,0,0,0,0,1,0,0,0,0,0, 1,0,1,0,
        0,0,0,0,0,0,1,0,0,0,0, 0,1,0,1,
        0,0,0,0,0,0,0,1,0,0,0, 1,0,1,1,
        0,0,0,0,0,0,0,0,1,0,0, 1,1,0,0,
        0,0,0,0,0,0,0,0,0,1,0, 0,1,1,0,
        0,0,0,0,0,0,0,0,0,0,1, 0,0,1,1
    ];

    // DSD-FME Hamming_16_11_4_m_G
    private static readonly byte[] Hamming16114G =
    [
        1,0,0,0,0,0,0,0,0,0,0, 1,0,0,1,1,
        0,1,0,0,0,0,0,0,0,0,0, 1,1,0,1,0,
        0,0,1,0,0,0,0,0,0,0,0, 1,1,1,1,1,
        0,0,0,1,0,0,0,0,0,0,0, 1,1,1,0,0,
        0,0,0,0,1,0,0,0,0,0,0, 0,1,1,1,0,
        0,0,0,0,0,1,0,0,0,0,0, 1,0,1,0,1,
        0,0,0,0,0,0,1,0,0,0,0, 0,1,0,1,1,
        0,0,0,0,0,0,0,1,0,0,0, 1,0,1,1,0,
        0,0,0,0,0,0,0,0,1,0,0, 1,1,0,0,1,
        0,0,0,0,0,0,0,0,0,1,0, 0,1,1,0,1,
        0,0,0,0,0,0,0,0,0,0,1, 0,0,1,1,1
    ];

    // DSD-FME Hamming_13_9_m_G
    private static readonly byte[] Hamming139G =
    [
        1,0,0,0,0,0,0,0,0, 1,1,1,1,
        0,1,0,0,0,0,0,0,0, 1,1,1,0,
        0,0,1,0,0,0,0,0,0, 0,1,1,1,
        0,0,0,1,0,0,0,0,0, 1,0,1,0,
        0,0,0,0,1,0,0,0,0, 0,1,0,1,
        0,0,0,0,0,1,0,0,0, 1,0,1,1,
        0,0,0,0,0,0,1,0,0, 1,1,0,0,
        0,0,0,0,0,0,0,1,0, 0,1,1,0,
        0,0,0,0,0,0,0,0,1, 0,0,1,1
    ];

    // DSD-FME Golay_20_8_m_G
    private static readonly byte[] Golay208G =
    [
        1,0,0,0,0,0,0,0, 0,0,1,1, 1,1,0,1, 1,0,1,0,
        0,1,0,0,0,0,0,0, 1,1,0,1, 1,0,0,1, 1,0,0,1,
        0,0,1,0,0,0,0,0, 0,1,1,0, 1,1,0,0, 1,1,0,1,
        0,0,0,1,0,0,0,0, 0,0,1,1, 0,1,1,0, 0,1,1,1,
        0,0,0,0,1,0,0,0, 1,1,0,1, 1,1,0,0, 0,1,1,0,
        0,0,0,0,0,1,0,0, 1,0,1,0, 1,0,0,1, 0,1,1,1,
        0,0,0,0,0,0,1,0, 1,0,0,1, 0,0,1,1, 1,1,1,0,
        0,0,0,0,0,0,0,1, 1,0,0,0, 1,1,1,0, 1,0,1,1
    ];

    // DSD-FME BPTCDeInterleavingIndex
    private static readonly int[] BptcDeinterleaveIndex =
    [
        0,13,26,39,52,65,78,91,104,117,130,143,156,169,182,195,
        12,25,38,51,64,77,90,103,116,129,142,155,168,181,194,11,
        24,37,50,63,76,89,102,115,128,141,154,167,180,193,10,23,
        36,49,62,75,88,101,114,127,140,153,166,179,192,9,22,35,
        48,61,74,87,100,113,126,139,152,165,178,191,8,21,34,47,
        60,73,86,99,112,125,138,151,164,177,190,7,20,33,46,59,
        72,85,98,111,124,137,150,163,176,189,6,19,32,45,58,71,
        84,97,110,123,136,149,162,175,188,5,18,31,44,57,70,83,
        96,109,122,135,148,161,174,187,4,17,30,43,56,69,82,95,
        108,121,134,147,160,173,186,3,16,29,42,55,68,81,94,107,
        120,133,146,159,172,185,2,15,28,41,54,67,80,93,106,119,
        132,145,158,171,184,1,14,27,40,53,66,79,92,105,118,131,
        144,157,170,183
    ];
}
