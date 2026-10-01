using System.Text;

namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>D-STAR radio header: scramble + 24-way deinterleave + 1/2 Viterbi (G4KLX / DSD-FME).</summary>
internal static class DstarHeaderDecoder
{
    public static bool TryDecode(ReadOnlySpan<int> bits660, out DstarHeader header)
    {
        header = default;
        if (bits660.Length < 660) return false;
        Span<int> scrambled = stackalloc int[660];
        Span<int> interleaved = stackalloc int[660];
        Span<int> decoded = stackalloc int[330];
        for (var i = 0; i < 660; i++)
            scrambled[i] = (bits660[i] & 1) ^ Scrambler[i];
        Deinterleave(scrambled, interleaved);
        Viterbi(interleaved, decoded);

        var octets = new byte[41];
        var octet = 0;
        var bit = 0;
        for (var i = 0; i < 328; i++)
        {
            if (decoded[i] != 0) octets[octet] |= (byte)(1 << bit);
            bit++;
            if (bit < 8) continue;
            octet++;
            bit = 0;
        }

        header = new DstarHeader(
            Call(octets.AsSpan(19, 8)),
            Call(octets.AsSpan(27, 12)),
            Call(octets.AsSpan(11, 8)),
            Call(octets.AsSpan(3, 8)));
        return header.Destination.Length > 0 || header.Source.Length > 0;
    }

    private static string Call(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            builder.Append(ch is >= ' ' and <= '~' ? ch : ' ');
        return builder.ToString().Trim();
    }

    private static void Deinterleave(ReadOnlySpan<int> input, Span<int> output)
    {
        var k = 0;
        for (var i = 0; i < 660; i++)
        {
            output[k] = input[i];
            k += 24;
            if (k >= 672) k -= 671;
            else if (k >= 660) k -= 647;
        }
    }

    private static void Viterbi(ReadOnlySpan<int> input, Span<int> output)
    {
        var mem0 = new int[330];
        var mem1 = new int[330];
        var mem2 = new int[330];
        var mem3 = new int[330];
        var metric = new int[4];
        var n = 0;
        for (var i = 0; i < 660; i += 2, n++)
        {
            var d0 = input[i + 1] != 0 ? 1 : 0;
            var d1 = input[i] != 0 ? 1 : 0;
            var m = new int[8];
            m[0] = (d1 ^ 0) + (d0 ^ 0);
            m[1] = (d1 ^ 1) + (d0 ^ 1);
            m[2] = (d1 ^ 1) + (d0 ^ 0);
            m[3] = (d1 ^ 0) + (d0 ^ 1);
            m[4] = (d1 ^ 1) + (d0 ^ 1);
            m[5] = (d1 ^ 0) + (d0 ^ 0);
            m[6] = (d1 ^ 0) + (d0 ^ 1);
            m[7] = (d1 ^ 1) + (d0 ^ 0);
            var temp = new int[4];
            Choose(m[0] + metric[0], m[4] + metric[2], out mem0[n], out temp[0]);
            Choose(m[1] + metric[0], m[5] + metric[2], out mem1[n], out temp[1]);
            Choose(m[2] + metric[1], m[6] + metric[3], out mem2[n], out temp[2]);
            Choose(m[3] + metric[1], m[7] + metric[3], out mem3[n], out temp[3]);
            Array.Copy(temp, metric, 4);
        }

        var state = 0;
        for (var loop = 329; loop >= 0; loop--)
        {
            switch (state)
            {
                case 0:
                    state = mem0[loop] != 0 ? 2 : 0;
                    output[loop] = 0;
                    break;
                case 1:
                    state = mem1[loop] != 0 ? 2 : 0;
                    output[loop] = 1;
                    break;
                case 2:
                    state = mem2[loop] != 0 ? 3 : 1;
                    output[loop] = 0;
                    break;
                default:
                    state = mem3[loop] != 0 ? 3 : 1;
                    output[loop] = 1;
                    break;
            }
        }
    }

    private static void Choose(int upper, int lower, out int path, out int metric)
    {
        if (upper < lower)
        {
            path = 0;
            metric = upper;
        }
        else
        {
            path = 1;
            metric = lower;
        }
    }

    private static readonly int[] Scrambler =
    [
        0,0,0,0,1,1,1,0,1,1,1,1,0,0,1,0,1,1,0,0,1,0,0,1,0,0,0,0,0,0,1,0,
        0,0,1,0,0,1,1,0,0,0,1,0,1,1,1,0,1,0,1,1,0,1,1,0,0,0,0,0,1,1,0,0,
        1,1,0,1,0,1,0,0,1,1,1,0,0,1,1,1,1,0,1,1,0,1,0,0,0,0,1,0,1,0,1,0,
        1,1,1,1,1,0,1,0,0,1,0,1,0,0,0,1,1,0,1,1,1,0,0,0,1,1,1,1,1,1,1,0,
        0,0,0,1,1,1,0,1,1,1,1,0,0,1,0,1,1,0,0,1,0,0,1,0,0,0,0,0,0,1,0,0,
        0,1,0,0,1,1,0,0,0,1,0,1,1,1,0,1,0,1,1,0,1,1,0,0,0,0,0,1,1,0,0,1,
        1,0,1,0,1,0,0,1,1,1,0,0,1,1,1,1,0,1,1,0,1,0,0,0,0,1,0,1,0,1,0,1,
        1,1,1,1,0,1,0,0,1,0,1,0,0,0,1,1,0,1,1,1,0,0,0,1,1,1,1,1,1,1,0,0,
        0,0,1,1,1,0,1,1,1,1,0,0,1,0,1,1,0,0,1,0,0,1,0,0,0,0,0,0,1,0,0,0,
        1,0,0,1,1,0,0,0,1,0,1,1,1,0,1,0,1,1,0,1,1,0,0,0,0,0,1,1,0,0,1,1,
        0,1,0,1,0,0,1,1,1,0,0,1,1,1,1,0,1,1,0,1,0,0,0,0,1,0,1,0,1,0,1,1,
        1,1,1,0,1,0,0,1,0,1,0,0,0,1,1,0,1,1,1,0,0,0,1,1,1,1,1,1,1,0,0,0,
        0,1,1,1,0,1,1,1,1,0,0,1,0,1,1,0,0,1,0,0,1,0,0,0,0,0,0,1,0,0,0,1,
        0,0,1,1,0,0,0,1,0,1,1,1,0,1,0,1,1,0,1,1,0,0,0,0,0,1,1,0,0,1,1,0,
        1,0,1,0,0,1,1,1,0,0,1,1,1,1,0,1,1,0,1,0,0,0,0,1,0,1,0,1,0,1,1,1,
        1,1,0,1,0,0,1,0,1,0,0,0,1,1,0,1,1,1,0,0,0,1,1,1,1,1,1,1,0,0,0,0,
        1,1,1,0,1,1,1,1,0,0,1,0,1,1,0,0,1,0,0,1,0,0,0,0,0,0,1,0,0,0,1,0,
        0,1,1,0,0,0,1,0,1,1,1,0,1,0,1,1,0,1,1,0,0,0,0,0,1,1,0,0,1,1,0,1,
        0,1,0,0,1,1,1,0,0,1,1,1,1,0,1,1,0,1,0,0,0,0,1,0,1,0,1,0,1,1,1,1,
        1,0,1,0,0,1,0,1,0,0,0,1,1,0,1,1,1,0,0,0,1,1,1,1,1,1,1,0,0,0,0,1,
        1,1,0,1,1,1,1,0,0,1,0,1,1,0,0,1,0,0,1,0
    ];
}

internal readonly record struct DstarHeader(string Destination, string Source, string Repeater1, string Repeater2);
