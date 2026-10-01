namespace NeuroSDR.Plugins.Lte;

/// <summary>
/// ASN.1 UPER helpers for LTE RRC PLMN / SIB1 cell-access fields (3GPP TS 36.331).
/// Bit packing matches srsRAN <c>asn1::rrc::plmn_id_s</c> (see srsenb/test/upper/plmn_test.cc).
/// </summary>
internal sealed class LteBitReader
{
    private readonly byte[] _data;
    private int _bit;

    public LteBitReader(byte[] data) => _data = data;
    public int BitPosition => _bit;
    public int BitsRemaining => _data.Length * 8 - _bit;

    public bool ReadBit()
    {
        if (_bit >= _data.Length * 8) throw new InvalidOperationException("ASN.1 overrun");
        var v = (_data[_bit / 8] >> (7 - (_bit % 8))) & 1;
        _bit++;
        return v != 0;
    }

    public uint ReadBits(int n)
    {
        uint v = 0;
        for (var i = 0; i < n; i++)
            v = (v << 1) | (ReadBit() ? 1u : 0u);
        return v;
    }

    /// <summary>Constrained whole-number 0..9 → 4 bits (srsRAN integer_packer).</summary>
    public int ReadDigit() => (int)ReadBits(4);
}

/// <summary>One PLMN-Identity (MCC + 2/3-digit MNC).</summary>
internal readonly record struct LtePlmnId(string Mcc, string Mnc)
{
    public string Text => string.IsNullOrEmpty(Mnc) ? Mcc : $"{Mcc}-{Mnc}";
    public override string ToString() => Text;
}

/// <summary>cellAccessRelatedInfo subset used for UI (PLMN list + TAC + CellIdentity).</summary>
internal sealed class LteSib1AccessInfo
{
    public List<LtePlmnId> Plmns { get; } = [];
    public string Tac { get; init; } = "";
    public string CellIdentity { get; init; } = "";
}

internal static class LtePlmnAsn1
{
    /// <summary>
    /// Unpack <c>PLMN-Identity</c> (not the outer PLMN-IdentityInfo).
    /// Test vector from srsRAN plmn_test: {0x89,0x19,0x14} → MCC 123 / MNC 45.
    /// </summary>
    public static LtePlmnId UnpackPlmnIdentity(LteBitReader br)
    {
        var mccPresent = br.ReadBit();
        var mcc = "000";
        if (mccPresent)
        {
            var d0 = br.ReadDigit();
            var d1 = br.ReadDigit();
            var d2 = br.ReadDigit();
            mcc = $"{d0}{d1}{d2}";
        }

        // SEQUENCE (SIZE (2..3)) OF INTEGER (0..9)
        var mncLen = 2 + (int)br.ReadBits(1); // length determinant for 2..3
        var mncChars = new char[mncLen];
        for (var i = 0; i < mncLen; i++)
            mncChars[i] = (char)('0' + br.ReadDigit());
        return new LtePlmnId(mcc, new string(mncChars));
    }

    public static LtePlmnId UnpackPlmnIdentity(byte[] data) =>
        UnpackPlmnIdentity(new LteBitReader(data));

    /// <summary>
    /// Best-effort scan of a BCCH-DL-SCH transport block for the first PLMN-Identity
    /// (MCC present + 2/3-digit MNC). Full SIB1 UPER needs the complete RRC codec;
    /// once PDSCH delivers the TB, prefer a full unpack. This finder validates PLMN
    /// bit layout against live captures and golden vectors.
    /// </summary>
    public static bool TryFindPlmnInTb(ReadOnlySpan<byte> tb, out LtePlmnId plmn)
    {
        plmn = default;
        if (tb.Length < 3) return false;
        // Brute force bit offsets — PLMN-Identity is small and highly structured.
        for (var bitOff = 0; bitOff < Math.Min(tb.Length * 8 - 24, 512); bitOff++)
        {
            try
            {
                var copy = tb.ToArray();
                var br = new LteBitReader(copy);
                for (var i = 0; i < bitOff; i++) br.ReadBit();
                if (!br.ReadBit()) continue; // require mcc_present
                var d0 = br.ReadDigit();
                var d1 = br.ReadDigit();
                var d2 = br.ReadDigit();
                if (d0 > 9 || d1 > 9 || d2 > 9) continue;
                // Korea public land MCC is 450; also accept common test 001/002/123
                if (d0 == 0 && d1 == 0 && d2 == 0) continue;
                var mncLenBit = br.ReadBit();
                var mncLen = mncLenBit ? 3 : 2;
                var mnc = new char[mncLen];
                var ok = true;
                for (var i = 0; i < mncLen; i++)
                {
                    var d = br.ReadDigit();
                    if (d > 9) { ok = false; break; }
                    mnc[i] = (char)('0' + d);
                }
                if (!ok) continue;
                plmn = new LtePlmnId($"{d0}{d1}{d2}", new string(mnc));
                // Prefer plausible public MCC (2xx/3xx/4xx/5xx/6xx)
                if (d0 is >= 2 and <= 7) return true;
            }
            catch
            {
                // try next offset
            }
        }
        return false;
    }
}
