// Port of libmodes / dump1090 Mode S detect+decode (Salvatore Sanfilippo, Thomas Watson).
// BSD 2-Clause. Timing assumes 2 MS/s magnitude samples (1 sample = 0.5 µs).

namespace NeuroSDR.Plugins.Dump1090;

internal sealed class ModeSMessage
{
    public byte[] Msg { get; } = new byte[ModeSDecoder.LongMsgBytes];
    public int MsgBits;
    public int MsgType;
    public bool CrcOk;
    public uint Crc;
    public int ErrorBit = -1;
    public int Aa1, Aa2, Aa3;
    public bool PhaseCorrected;
    public int Ca;
    public int MeType;
    public int MeSub;
    public bool HeadingIsValid;
    public int Heading;
    public int AircraftType;
    public int FFlag;
    public int TFlag;
    public int RawLatitude;
    public int RawLongitude;
    public string Flight = "";
    public int EwDir, EwVelocity, NsDir, NsVelocity;
    public int VertRateSource, VertRateSign, VertRate, Velocity;
    public int Fs, Dr, Um, Identity;
    public int Altitude, Unit;

    public uint Address => (uint)((Aa1 << 16) | (Aa2 << 8) | Aa3);
    public string Icao => Address.ToString("X6");
}

/// <summary>dump1090/libmodes Mode S preamble detector and DF decoder.</summary>
internal sealed class ModeSDecoder
{
    internal const int PreambleUs = 8;
    internal const int LongMsgBits = 112;
    internal const int ShortMsgBits = 56;
    internal const int LongMsgBytes = LongMsgBits / 8;
    internal const int FullLen = PreambleUs + LongMsgBits;
    internal const int IcaoCacheLen = 1024;
    internal const int IcaoCacheTtlSeconds = 60;
    internal const int UnitFeet = 0;
    internal const int UnitMeters = 1;
    internal const int TargetSampleRate = 2_000_000;

    private static readonly uint[] ChecksumTable =
    [
        0x3935ea, 0x1c9af5, 0xf1b77e, 0x78dbbf, 0xc397db, 0x9e31e9, 0xb0e2f0, 0x587178,
        0x2c38bc, 0x161c5e, 0x0b0e2f, 0xfa7d13, 0x82c48d, 0xbe9842, 0x5f4c21, 0xd05c14,
        0x682e0a, 0x341705, 0xe5f186, 0x72f8c3, 0xc68665, 0x9cb936, 0x4e5c9b, 0xd8d449,
        0x939020, 0x49c810, 0x24e408, 0x127204, 0x093902, 0x049c81, 0xfdb444, 0x7eda22,
        0x3f6d11, 0xe04c8c, 0x702646, 0x381323, 0xe3f395, 0x8e03ce, 0x4701e7, 0xdc7af7,
        0x91c77f, 0xb719bb, 0xa476d9, 0xadc168, 0x56e0b4, 0x2b705a, 0x15b82d, 0xf52612,
        0x7a9309, 0xc2b380, 0x6159c0, 0x30ace0, 0x185670, 0x0c2b38, 0x06159c, 0x030ace,
        0x018567, 0xff38b7, 0x80665f, 0xbfc92b, 0xa01e91, 0xaff54c, 0x57faa6, 0x2bfd53,
        0xea04ad, 0x8af852, 0x457c29, 0xdd4410, 0x6ea208, 0x375104, 0x1ba882, 0x0dd441,
        0xf91024, 0x7c8812, 0x3e4409, 0xe0d800, 0x706c00, 0x383600, 0x1c1b00, 0x0e0d80,
        0x0706c0, 0x038360, 0x01c1b0, 0x00e0d8, 0x00706c, 0x003836, 0x001c1b, 0xfff409,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

    private static readonly char[] AisCharset =
        "?ABCDEFGHIJKLMNOPQRSTUVWXYZ?Edit FavoriteFavoritesFavoritesFavorites?0123456789FavoritesOK".ToCharArray();

    private static readonly ushort[] MagLut = BuildMagLut();

    private readonly uint[] _icaoCache = new uint[IcaoCacheLen * 2];
    public bool FixErrors { get; set; } = true;
    public bool Aggressive { get; set; }
    public bool CheckCrc { get; set; } = true;

    public void Reset() => Array.Clear(_icaoCache);

    public static int MessageLengthBits(int type) =>
        type is 16 or 17 or 19 or 20 or 21 ? LongMsgBits : ShortMsgBits;

    public static byte[] IdentificationFrame(uint icao, string flight)
    {
        var msg = new byte[LongMsgBytes];
        msg[0] = (byte)((17 << 3) | 5);
        msg[1] = (byte)((icao >> 16) & 0xff);
        msg[2] = (byte)((icao >> 8) & 0xff);
        msg[3] = (byte)(icao & 0xff);
        msg[4] = (byte)(4 << 3);
        EncodeAisFlight(msg, flight);
        StampCrc(msg, LongMsgBits);
        return msg;
    }

    private static void EncodeAisFlight(byte[] msg, string flight)
    {
        var padded = (flight ?? "").ToUpperInvariant().PadRight(8)[..8];
        var values = new int[8];
        for (var i = 0; i < 8; i++)
        {
            var ch = padded[i];
            var index = Array.IndexOf(AisCharset, ch);
            values[i] = index < 0 ? Array.IndexOf(AisCharset, ' ') : index;
        }
        msg[5] = (byte)((values[0] << 2) | (values[1] >> 4));
        msg[6] = (byte)((values[1] << 4) | (values[2] >> 2));
        msg[7] = (byte)((values[2] << 6) | values[3]);
        msg[8] = (byte)((values[4] << 2) | (values[5] >> 4));
        msg[9] = (byte)((values[5] << 4) | (values[6] >> 2));
        msg[10] = (byte)((values[6] << 6) | values[7]);
    }

    public static uint Checksum(ReadOnlySpan<byte> msg, int bits)
    {
        uint crc = 0;
        var offset = bits == 112 ? 0 : 112 - 56;
        for (var j = 0; j < bits; j++)
        {
            var bitmask = 1 << (7 - j % 8);
            if ((msg[j / 8] & bitmask) != 0) crc ^= ChecksumTable[j + offset];
        }
        return crc;
    }

    public static void StampCrc(Span<byte> msg, int bits)
    {
        var crc = Checksum(msg, bits);
        var last = bits / 8 - 1;
        msg[last - 2] = (byte)((crc >> 16) & 0xff);
        msg[last - 1] = (byte)((crc >> 8) & 0xff);
        msg[last] = (byte)(crc & 0xff);
    }

    public ModeSMessage Decode(ReadOnlySpan<byte> source)
    {
        var mm = new ModeSMessage();
        source[..Math.Min(source.Length, LongMsgBytes)].CopyTo(mm.Msg);
        Decode(mm, mm.Msg);
        return mm;
    }

    public void Detect(ushort[] mag, int magLen, Action<ModeSMessage> callback)
    {
        var bits = new byte[LongMsgBits];
        var msg = new byte[LongMsgBits / 2];
        var aux = new ushort[LongMsgBits * 2];
        var useCorrection = false;
        var limit = magLen - FullLen * 2;
        if (limit <= 0) return;

        for (var j = 0; j < limit; j++)
        {
            var goodMessage = false;
            if (!useCorrection)
            {
                if (!(mag[j] > mag[j + 1] &&
                      mag[j + 1] < mag[j + 2] &&
                      mag[j + 2] > mag[j + 3] &&
                      mag[j + 3] < mag[j] &&
                      mag[j + 4] < mag[j] &&
                      mag[j + 5] < mag[j] &&
                      mag[j + 6] < mag[j] &&
                      mag[j + 7] > mag[j + 8] &&
                      mag[j + 8] < mag[j + 9] &&
                      mag[j + 9] > mag[j + 6]))
                    continue;

                var high = (mag[j] + mag[j + 2] + mag[j + 7] + mag[j + 9]) / 6;
                if (mag[j + 4] >= high || mag[j + 5] >= high) continue;
                if (mag[j + 11] >= high || mag[j + 12] >= high || mag[j + 13] >= high || mag[j + 14] >= high)
                    continue;
            }

            if (useCorrection)
            {
                mag.AsSpan(j + PreambleUs * 2, aux.Length).CopyTo(aux);
                if (j > 0 && DetectOutOfPhase(mag, j) != 0)
                    ApplyPhaseCorrection(mag, j);
            }

            var errors = 0;
            for (var i = 0; i < LongMsgBits * 2; i += 2)
            {
                var low = mag[j + i + PreambleUs * 2];
                var high = mag[j + i + PreambleUs * 2 + 1];
                var delta = low > high ? low - high : high - low;
                if (i > 0 && delta < 256) bits[i / 2] = bits[i / 2 - 1];
                else if (low == high)
                {
                    bits[i / 2] = 2;
                    if (i < ShortMsgBits * 2) errors++;
                }
                else bits[i / 2] = (byte)(low > high ? 1 : 0);
            }

            if (useCorrection)
                aux.AsSpan().CopyTo(mag.AsSpan(j + PreambleUs * 2, aux.Length));

            for (var i = 0; i < LongMsgBits; i += 8)
            {
                msg[i / 8] = (byte)(
                    bits[i] << 7 | bits[i + 1] << 6 | bits[i + 2] << 5 | bits[i + 3] << 4 |
                    bits[i + 4] << 3 | bits[i + 5] << 2 | bits[i + 6] << 1 | bits[i + 7]);
            }

            var msgType = msg[0] >> 3;
            var msgLen = MessageLengthBits(msgType) / 8;
            var quality = 0;
            for (var i = 0; i < msgLen * 8 * 2; i += 2)
                quality += Math.Abs(mag[j + i + PreambleUs * 2] - mag[j + i + PreambleUs * 2 + 1]);
            quality /= msgLen * 4;
            if (quality < 10 * 255)
            {
                useCorrection = false;
                continue;
            }

            if (errors == 0 || (Aggressive && errors < 3))
            {
                var mm = new ModeSMessage();
                Decode(mm, msg);
                if (mm.CrcOk)
                {
                    j += (PreambleUs + msgLen * 8) * 2;
                    goodMessage = true;
                    if (useCorrection) mm.PhaseCorrected = true;
                }
                if (!CheckCrc || mm.CrcOk) callback(mm);
            }

            if (!goodMessage && !useCorrection)
            {
                j--;
                useCorrection = true;
            }
            else useCorrection = false;
        }
    }

    public static void ComputeMagnitude(ReadOnlySpan<byte> iq, Span<ushort> mag)
    {
        var n = Math.Min(iq.Length / 2, mag.Length);
        for (var j = 0; j < n; j++)
        {
            var i = iq[j * 2] - 127;
            var q = iq[j * 2 + 1] - 127;
            if (i < 0) i = -i;
            if (q < 0) q = -q;
            mag[j] = MagLut[i * 129 + q];
        }
    }

    public static int MagnitudeFromIq(ReadOnlySpan<NeuroSDR.Core.Complex32> samples, Span<ushort> mag, float peak)
    {
        var n = Math.Min(samples.Length, mag.Length);
        var scale = peak > 1e-8f ? 127f / peak : 127f;
        for (var j = 0; j < n; j++)
        {
            var i = (int)Math.Clamp(MathF.Round(samples[j].I * scale), -127, 127);
            var q = (int)Math.Clamp(MathF.Round(samples[j].Q * scale), -127, 127);
            if (i < 0) i = -i;
            if (q < 0) q = -q;
            mag[j] = MagLut[i * 129 + q];
        }
        return n;
    }

    public static float PeakAbs(ReadOnlySpan<NeuroSDR.Core.Complex32> samples)
    {
        var peak = 1e-6f;
        for (var i = 0; i < samples.Length; i++)
        {
            var a = MathF.Abs(samples[i].I);
            var b = MathF.Abs(samples[i].Q);
            if (a > peak) peak = a;
            if (b > peak) peak = b;
        }
        return peak;
    }

    /// <summary>Build 2 MS/s I/Q for a packed Mode S frame (unit tests / verification).</summary>
    public static NeuroSDR.Core.Complex32[] SynthesizeIq(ReadOnlySpan<byte> packed, float amplitude = 0.8f)
    {
        var bits = MessageLengthBits(packed[0] >> 3);
        var dataSamples = bits * 2;
        var magLen = PreambleUs * 2 + dataSamples + FullLen * 2 + 8;
        var iq = new NeuroSDR.Core.Complex32[magLen];
        Span<float> mag = stackalloc float[magLen];
        mag[0] = mag[2] = mag[7] = mag[9] = amplitude;
        for (var i = 0; i < bits; i++)
        {
            var one = (packed[i / 8] & (1 << (7 - i % 8))) != 0;
            mag[PreambleUs * 2 + i * 2] = one ? amplitude : 0.05f;
            mag[PreambleUs * 2 + i * 2 + 1] = one ? 0.05f : amplitude;
        }
        for (var i = 0; i < magLen; i++)
            iq[i] = new NeuroSDR.Core.Complex32(mag[i], 0);
        return iq;
    }

    private void Decode(ModeSMessage mm, byte[] msg)
    {
        msg.AsSpan(0, LongMsgBytes).CopyTo(mm.Msg);
        mm.MsgType = msg[0] >> 3;
        mm.MsgBits = MessageLengthBits(mm.MsgType);
        var last = mm.MsgBits / 8;
        mm.Crc = ((uint)msg[last - 3] << 16) | ((uint)msg[last - 2] << 8) | msg[last - 1];
        var crc2 = Checksum(msg, mm.MsgBits);
        mm.ErrorBit = -1;
        mm.CrcOk = mm.Crc == crc2;

        if (!mm.CrcOk && FixErrors && mm.MsgType is 11 or 17)
        {
            mm.ErrorBit = FixSingleBitErrors(msg, mm.MsgBits);
            if (mm.ErrorBit != -1)
            {
                mm.Crc = Checksum(msg, mm.MsgBits);
                mm.CrcOk = true;
            }
            else if (Aggressive && mm.MsgType == 17)
            {
                mm.ErrorBit = FixTwoBitsErrors(msg, mm.MsgBits);
                if (mm.ErrorBit != -1)
                {
                    mm.Crc = Checksum(msg, mm.MsgBits);
                    mm.CrcOk = true;
                }
            }
        }

        mm.Ca = msg[0] & 7;
        mm.Aa1 = msg[1];
        mm.Aa2 = msg[2];
        mm.Aa3 = msg[3];
        mm.MeType = msg[4] >> 3;
        mm.MeSub = msg[4] & 7;
        mm.Fs = msg[0] & 7;
        mm.Dr = msg[1] >> 3 & 31;
        mm.Um = ((msg[1] & 7) << 3) | (msg[2] >> 5);

        var a = ((msg[3] & 0x80) >> 5) | ((msg[2] & 0x02) >> 0) | ((msg[2] & 0x08) >> 3);
        var b = ((msg[3] & 0x02) << 1) | ((msg[3] & 0x08) >> 2) | ((msg[3] & 0x20) >> 5);
        var c = ((msg[2] & 0x01) << 2) | ((msg[2] & 0x04) >> 1) | ((msg[2] & 0x10) >> 4);
        var d = ((msg[3] & 0x01) << 2) | ((msg[3] & 0x04) >> 1) | ((msg[3] & 0x10) >> 4);
        mm.Identity = a * 1000 + b * 100 + c * 10 + d;

        if (mm.MsgType is not 11 and not 17)
            mm.CrcOk = BruteForceAp(msg, mm);
        else if (mm.CrcOk && mm.ErrorBit == -1)
            AddIcao(mm.Address);

        if (mm.MsgType is 0 or 4 or 16 or 20)
            mm.Altitude = DecodeAc13(msg, out mm.Unit);

        if (mm.MsgType == 17)
        {
            if (mm.MeType is >= 1 and <= 4)
            {
                mm.AircraftType = mm.MeType - 1;
                Span<char> flight = stackalloc char[8];
                flight[0] = AisCharset[msg[5] >> 2];
                flight[1] = AisCharset[((msg[5] & 3) << 4) | (msg[6] >> 4)];
                flight[2] = AisCharset[((msg[6] & 15) << 2) | (msg[7] >> 6)];
                flight[3] = AisCharset[msg[7] & 63];
                flight[4] = AisCharset[msg[8] >> 2];
                flight[5] = AisCharset[((msg[8] & 3) << 4) | (msg[9] >> 4)];
                flight[6] = AisCharset[((msg[9] & 15) << 2) | (msg[10] >> 6)];
                flight[7] = AisCharset[msg[10] & 63];
                mm.Flight = new string(flight).Trim();
            }
            else if (mm.MeType is >= 9 and <= 18)
            {
                mm.FFlag = msg[6] & (1 << 2);
                mm.TFlag = msg[6] & (1 << 3);
                mm.Altitude = DecodeAc12(msg, out mm.Unit);
                mm.RawLatitude = ((msg[6] & 3) << 15) | (msg[7] << 7) | (msg[8] >> 1);
                mm.RawLongitude = ((msg[8] & 1) << 16) | (msg[9] << 8) | msg[10];
            }
            else if (mm.MeType == 19 && mm.MeSub is >= 1 and <= 4)
            {
                if (mm.MeSub is 1 or 2)
                {
                    mm.EwDir = (msg[5] & 4) >> 2;
                    mm.EwVelocity = ((msg[5] & 3) << 8) | msg[6];
                    mm.NsDir = (msg[7] & 0x80) >> 7;
                    mm.NsVelocity = ((msg[7] & 0x7f) << 3) | ((msg[8] & 0xe0) >> 5);
                    mm.VertRateSource = (msg[8] & 0x10) >> 4;
                    mm.VertRateSign = (msg[8] & 0x8) >> 3;
                    mm.VertRate = ((msg[8] & 7) << 6) | ((msg[9] & 0xfc) >> 2);
                    mm.Velocity = (int)Math.Sqrt(mm.NsVelocity * (double)mm.NsVelocity +
                                                mm.EwVelocity * (double)mm.EwVelocity);
                    if (mm.Velocity != 0)
                    {
                        var ewv = mm.EwDir != 0 ? -mm.EwVelocity : mm.EwVelocity;
                        var nsv = mm.NsDir != 0 ? -mm.NsVelocity : mm.NsVelocity;
                        var heading = Math.Atan2(ewv, nsv) * 360 / (Math.PI * 2);
                        if (heading < 0) heading += 360;
                        mm.Heading = (int)heading;
                    }
                }
                else
                {
                    mm.HeadingIsValid = (msg[5] & (1 << 2)) != 0;
                    mm.Heading = (int)((360.0 / 128) * (((msg[5] & 3) << 5) | (msg[6] >> 3)));
                }
            }
        }
    }

    private static int FixSingleBitErrors(byte[] msg, int bits)
    {
        var aux = new byte[LongMsgBits / 8];
        for (var j = 0; j < bits; j++)
        {
            msg.AsSpan(0, bits / 8).CopyTo(aux);
            aux[j / 8] ^= (byte)(1 << (7 - j % 8));
            var crc1 = ((uint)aux[bits / 8 - 3] << 16) | ((uint)aux[bits / 8 - 2] << 8) | aux[bits / 8 - 1];
            if (crc1 == Checksum(aux, bits))
            {
                aux.AsSpan(0, bits / 8).CopyTo(msg);
                return j;
            }
        }
        return -1;
    }

    private static int FixTwoBitsErrors(byte[] msg, int bits)
    {
        var aux = new byte[LongMsgBits / 8];
        for (var j = 0; j < bits; j++)
        {
            var mask1 = (byte)(1 << (7 - j % 8));
            for (var i = j + 1; i < bits; i++)
            {
                msg.AsSpan(0, bits / 8).CopyTo(aux);
                aux[j / 8] ^= mask1;
                aux[i / 8] ^= (byte)(1 << (7 - i % 8));
                var crc1 = ((uint)aux[bits / 8 - 3] << 16) | ((uint)aux[bits / 8 - 2] << 8) | aux[bits / 8 - 1];
                if (crc1 == Checksum(aux, bits))
                {
                    aux.AsSpan(0, bits / 8).CopyTo(msg);
                    return j | (i << 8);
                }
            }
        }
        return -1;
    }

    private static uint IcaoHash(uint a)
    {
        a = ((a >> 16) ^ a) * 0x45d9f3b;
        a = ((a >> 16) ^ a) * 0x45d9f3b;
        a = (a >> 16) ^ a;
        return a & (IcaoCacheLen - 1);
    }

    private void AddIcao(uint addr)
    {
        var h = IcaoHash(addr);
        _icaoCache[h * 2] = addr;
        _icaoCache[h * 2 + 1] = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private bool SeenIcao(uint addr)
    {
        var h = IcaoHash(addr);
        var a = _icaoCache[h * 2];
        var t = (int)_icaoCache[h * 2 + 1];
        return a != 0 && a == addr && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t <= IcaoCacheTtlSeconds;
    }

    private bool BruteForceAp(byte[] msg, ModeSMessage mm)
    {
        if (mm.MsgType is not (0 or 4 or 5 or 16 or 20 or 21 or 24)) return false;
        var aux = new byte[LongMsgBytes];
        var bytes = mm.MsgBits / 8;
        msg.AsSpan(0, bytes).CopyTo(aux);
        var crc = Checksum(aux, mm.MsgBits);
        var last = bytes - 1;
        aux[last] ^= (byte)(crc & 0xff);
        aux[last - 1] ^= (byte)((crc >> 8) & 0xff);
        aux[last - 2] ^= (byte)((crc >> 16) & 0xff);
        var addr = (uint)(aux[last] | (aux[last - 1] << 8) | (aux[last - 2] << 16));
        if (!SeenIcao(addr)) return false;
        mm.Aa1 = aux[last - 2];
        mm.Aa2 = aux[last - 1];
        mm.Aa3 = aux[last];
        return true;
    }

    private static int DecodeAc13(byte[] msg, out int unit)
    {
        var mBit = (msg[3] & (1 << 6)) != 0;
        var qBit = (msg[3] & (1 << 4)) != 0;
        unit = mBit ? UnitMeters : UnitFeet;
        if (!mBit && qBit)
        {
            var n = ((msg[2] & 31) << 6) | ((msg[3] & 0x80) >> 2) | ((msg[3] & 0x20) >> 1) | (msg[3] & 15);
            return n * 25 - 1000;
        }
        return 0;
    }

    private static int DecodeAc12(byte[] msg, out int unit)
    {
        if ((msg[5] & 1) != 0)
        {
            unit = UnitFeet;
            var n = ((msg[5] >> 1) << 4) | ((msg[6] & 0xF0) >> 4);
            return n * 25 - 1000;
        }
        unit = UnitFeet;
        return 0;
    }

    private static int DetectOutOfPhase(ushort[] mag, int j)
    {
        if (mag[j + 3] > mag[j + 2] / 3) return 1;
        if (mag[j + 10] > mag[j + 9] / 3) return 1;
        if (mag[j + 6] > mag[j + 7] / 3) return -1;
        if (j > 0 && mag[j - 1] > mag[j + 1] / 3) return -1;
        return 0;
    }

    private static void ApplyPhaseCorrection(ushort[] mag, int j)
    {
        var start = j + 16;
        for (var i = 0; i < (LongMsgBits - 1) * 2; i += 2)
        {
            if (mag[start + i] > mag[start + i + 1])
                mag[start + i + 2] = (ushort)(mag[start + i + 2] * 5 / 4);
            else
                mag[start + i + 2] = (ushort)(mag[start + i + 2] * 4 / 5);
        }
    }

    private static ushort[] BuildMagLut()
    {
        var lut = new ushort[129 * 129];
        for (var i = 0; i <= 128; i++)
        {
            for (var q = 0; q <= 128; q++)
                lut[i * 129 + q] = (ushort)Math.Round(Math.Sqrt(i * i + q * q) * 360);
        }
        return lut;
    }
}
