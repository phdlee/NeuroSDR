using System.Globalization;

namespace NeuroSDR.Plugins.DigitalVoice;

internal sealed record DigitalVoiceQso(
    DateTime Utc,
    string Protocol,
    string Sync,
    int? Slot,
    int? ColorCode,
    int? SourceId,
    int? TargetId,
    string CallType,
    string Callsign,
    string Name,
    string City,
    string Country,
    float RssiDb,
    float BerPercent);

internal sealed class DigitalVoiceDecoder
{
    public const int SymbolRate = 4_800;
    public const int BurstDibits = 144;
    public const int AudioRate = 48_000;

    private const int SyncLength = 24;
    private const int SyncOffset = 66;

    private readonly byte[] _dibits = new byte[8_192];
    private int _dibitCount;
    private readonly int[] _dstarBits = new int[660];
    private int _dstarNeed;
    private double _symbolPhase;
    private double _centerAccum, _earlyAccum, _lateAccum;
    private int _centerCount, _earlyCount, _lateCount;
    private float _peak = 1e-3f;
    private int _lastBurstAt = -1;
    private int _pendingSyncAt = -1;
    private string _pendingSyncName = "";
    private int _lockStart = -1;
    private int _lockMiss;
    private int _autoXor;
    private int _voiceCounter;
    private readonly byte[][] _embFrags = [new byte[32], new byte[32], new byte[32], new byte[32], new byte[32], new byte[32]];
    private int? _latchedSource, _latchedTarget, _latchedColor;
    private string _latchedCallType = "Voice";

    public bool Invert { get; set; }
    public DsdFmeDibitQueue DibitQueue { get; } = new();
    public RadioIdDirectory RadioIds { get; } = new();

    public event Action<DigitalVoiceQso>? QsoDecoded;
    public event Action<DsdFmeBurst>? BurstReady;
    public event Action<string>? StatusChanged;

    public void Reset()
    {
        _dibitCount = 0;
        _dstarNeed = 0;
        _symbolPhase = 0;
        _centerAccum = _earlyAccum = _lateAccum = 0;
        _centerCount = _earlyCount = _lateCount = 0;
        _peak = 1e-3f;
        _lastBurstAt = -1;
        _pendingSyncAt = -1;
        _lockStart = -1;
        _lockMiss = 0;
        _autoXor = 0;
        _voiceCounter = 0;
        _latchedSource = _latchedTarget = _latchedColor = null;
        DibitQueue.Clear();
    }

    public void ProcessAudio(ReadOnlySpan<float> samples, int sampleRate, DateTime utc, float rssiDb)
    {
        if (samples.IsEmpty || sampleRate < 8_000) return;
        var samplesPerSymbol = sampleRate / (double)SymbolRate;
        foreach (var sample in samples)
        {
            _symbolPhase += 1;
            var pos = _symbolPhase;
            if (pos < samplesPerSymbol * 0.5) { _earlyAccum += sample; _earlyCount++; }
            else { _lateAccum += sample; _lateCount++; }
            if (pos >= samplesPerSymbol * 0.3 && pos <= samplesPerSymbol * 0.7)
            {
                _centerAccum += sample;
                _centerCount++;
            }
            if (pos < samplesPerSymbol) continue;

            var mean = _centerCount > 0
                ? (float)(_centerAccum / _centerCount)
                : (float)((_earlyAccum + _lateAccum) / Math.Max(1, _earlyCount + _lateCount));
            var early = Math.Abs(_earlyAccum) / Math.Max(1, _earlyCount);
            var late = Math.Abs(_lateAccum) / Math.Max(1, _lateCount);
            _symbolPhase -= samplesPerSymbol;
            if (late > early * 1.05) _symbolPhase -= 0.25;
            else if (early > late * 1.05) _symbolPhase += 0.25;
            if (_symbolPhase < -2) _symbolPhase = -2;
            if (_symbolPhase > 2) _symbolPhase = 2;
            _centerAccum = _earlyAccum = _lateAccum = 0;
            _centerCount = _earlyCount = _lateCount = 0;

            var mag = Math.Abs(mean);
            if (mag > _peak) _peak = mag;
            else _peak = 0.997f * _peak + 0.003f * mag;
            PushDibit(Slice4Fsk(mean, Math.Max(_peak * 0.5f, 1e-5f)), utc, rssiDb);
        }
    }

    public void ProcessDibits(ReadOnlySpan<byte> dibits, DateTime utc, float rssiDb = -80)
    {
        foreach (var dibit in dibits) PushDibit((byte)(dibit & 3), utc, rssiDb);
    }

    private void PushDibit(byte raw, DateTime utc, float rssiDb)
    {
        var dibit = (byte)((Invert ? raw ^ 2 : raw) ^ _autoXor);
        CompactDibits();
        _dibits[_dibitCount++] = dibit;
        DibitQueue.PushByte(dibit);

        if (_dstarNeed > 0)
        {
            _dstarBits[660 - _dstarNeed] = dibit & 1;
            _dstarNeed--;
            if (_dstarNeed == 0) EmitDstarHeader(utc, rssiDb);
            return;
        }

        if (_lockStart >= 0)
        {
            DrainLockedBursts(utc, rssiDb);
            return;
        }

        if (_pendingSyncAt >= 0)
        {
            var burstStart = _pendingSyncAt - SyncOffset;
            if (burstStart >= 0 && _dibitCount >= burstStart + BurstDibits)
            {
                _lastBurstAt = burstStart;
                _lockStart = burstStart + BurstDibits;
                var pendingName = _pendingSyncName;
                _pendingSyncAt = -1;
                ParseDmrBurst(_dibits.AsSpan(burstStart, BurstDibits), pendingName, utc, rssiDb);
            }
            return;
        }

        if (_dibitCount < SyncLength) return;
        var start = _dibitCount - SyncLength;
        if (_lastBurstAt >= 0 && start - _lastBurstAt < BurstDibits - 8) return;
        if (!TryMatchSync(start, out var name, out var protocol, out var isHeader)) return;

        if (protocol == "D-STAR")
        {
            _lastBurstAt = start;
            if (isHeader) _dstarNeed = 660;
            EmitQso(new DigitalVoiceQso(utc, "D-STAR", name, null, null, null, null,
                isHeader ? "Header" : "Voice", "", "", "", "", rssiDb, 0), utc, protocol, name, start);
            return;
        }

        if (start < SyncOffset) return;
        _pendingSyncAt = start;
        _pendingSyncName = name;
        var readyStart = start - SyncOffset;
        if (_dibitCount >= readyStart + BurstDibits)
        {
            _lastBurstAt = readyStart;
            _lockStart = readyStart + BurstDibits;
            _pendingSyncAt = -1;
            ParseDmrBurst(_dibits.AsSpan(readyStart, BurstDibits), name, utc, rssiDb);
        }
    }

    private void DrainLockedBursts(DateTime utc, float rssiDb)
    {
        while (_lockStart >= 0 && _dibitCount >= _lockStart + BurstDibits)
        {
            var burst = _dibits.AsSpan(_lockStart, BurstDibits);
            var center = _lockStart + SyncOffset;
            var name = "VOICE EMB";
            if (TryMatchSync(center, out var matched, out var protocol, out _) && protocol == "DMR")
                name = matched;
            ParseDmrBurst(burst, name, utc, rssiDb);
            _lastBurstAt = _lockStart;
            _lockStart += BurstDibits;
            if (name.Contains("DATA", StringComparison.Ordinal) ||
                (name.Contains("VOICE", StringComparison.Ordinal) && !name.Contains("EMB", StringComparison.Ordinal)) ||
                TryParseCach(burst[..12], out _, out _))
                _lockMiss = 0;
            else if (++_lockMiss > 12)
            {
                _lockStart = -1;
                _voiceCounter = 0;
                _lastBurstAt = -1;
                break;
            }
        }
    }

    private void CompactDibits()
    {
        if (_dibitCount < _dibits.Length) return;
        var drop = _lockStart >= 64 ? _lockStart - 32 : _dibits.Length / 2;
        if (_pendingSyncAt >= 0) drop = Math.Min(drop, Math.Max(0, _pendingSyncAt - SyncOffset - 8));
        drop = Math.Clamp(drop, 1, Math.Max(1, _dibitCount - BurstDibits));
        Array.Copy(_dibits, drop, _dibits, 0, _dibitCount - drop);
        _dibitCount -= drop;
        if (_lastBurstAt >= 0) _lastBurstAt -= drop;
        if (_pendingSyncAt >= 0) _pendingSyncAt -= drop;
        if (_lockStart >= 0) _lockStart -= drop;
    }

    private void ParseDmrBurst(ReadOnlySpan<byte> burst, string syncName, DateTime utc, float rssiDb)
    {
        var namedVoice = syncName.Contains("VOICE", StringComparison.OrdinalIgnoreCase) &&
                         !syncName.Contains("EMB", StringComparison.OrdinalIgnoreCase);
        var namedData = syncName.Contains("DATA", StringComparison.OrdinalIgnoreCase);
        int? slot = null;
        if (TryParseCach(burst[..12], out var cachSlot, out _))
            slot = cachSlot + 1;

        int? color = null;
        var callType = namedVoice || syncName.Contains("EMB", StringComparison.Ordinal) ? "Voice" : "Data";
        int? source = null;
        int? target = null;
        var ber = 0f;
        var gotLc = false;

        if (TryParseSlotTypePolarity(burst, out var cc, out var dataType, out var slotErrors))
        {
            color = cc;
            ber = slotErrors / 20f * 100f;
            callType = DataTypeName(dataType);
            if (dataType is 1 or 2 or 3 && TryParseLinkControl(burst, out var lcSource, out var lcTarget, out var group, out var emergency))
            {
                source = lcSource;
                target = lcTarget;
                callType = emergency ? "Emergency" : group ? "Group" : "Private";
                if (dataType == 3) callType = "CSBK";
                gotLc = true;
                Latch(source, target, color, callType);
            }
        }

        if (namedVoice) _voiceCounter = 1;
        else if (namedData) _voiceCounter = 0;
        else if (_voiceCounter is >= 2 and <= 5)
        {
            CopyEmbFrag(burst, _voiceCounter - 1);
            if (_voiceCounter == 5 && TryEmbeddedLc(out var embSource, out var embTarget, out var groupCall))
            {
                source = embSource;
                target = embTarget;
                callType = groupCall ? "Group" : "Private";
                gotLc = true;
                Latch(source, target, color ?? _latchedColor, callType);
            }
        }
        if (namedVoice || (!namedData && _voiceCounter >= 1))
            _voiceCounter++;
        if (_voiceCounter > 8) _voiceCounter = 0;

        if (source is null && target is null)
        {
            source = _latchedSource;
            target = _latchedTarget;
            color ??= _latchedColor;
            if (callType is "Voice" or "Data" && _latchedCallType.Length > 0 && source is not null)
                callType = _latchedCallType;
        }

        if (syncName.Contains("EMB", StringComparison.Ordinal) && !gotLc) return;

        var user = source is int id ? RadioIds.Lookup(id) : null;
        EmitQso(new DigitalVoiceQso(utc, "DMR", syncName, slot, color, source, target, callType,
            user?.Callsign ?? "", user?.Name ?? "", user?.City ?? "", user?.Country ?? "", rssiDb, ber),
            utc, "DMR", syncName, 0, burst);
    }

    private void Latch(int? source, int? target, int? color, string callType)
    {
        if (source is > 0) _latchedSource = source;
        if (target is >= 0) _latchedTarget = target;
        if (color is >= 0) _latchedColor = color;
        if (!string.IsNullOrEmpty(callType)) _latchedCallType = callType;
    }

    private void CopyEmbFrag(ReadOnlySpan<byte> burst, int index)
    {
        if ((uint)index >= 6) return;
        var bits = _embFrags[index];
        var n = 0;
        for (var i = 70; i <= 85 && n < 32; i++)
        {
            bits[n++] = (byte)((burst[i] >> 1) & 1);
            bits[n++] = (byte)(burst[i] & 1);
        }
    }

    private bool TryEmbeddedLc(out int source, out int target, out bool group)
    {
        source = 0;
        target = 0;
        group = true;
        Span<byte> lc = stackalloc byte[72];
        if (!DmrFec.DecodeEmbeddedLc(_embFrags[1], _embFrags[2], _embFrags[3], _embFrags[4], lc))
            return false;
        var flco = BitsToInt(lc, 2, 6);
        group = flco == 0;
        target = BitsToInt(lc, 24, 24);
        source = BitsToInt(lc, 48, 24);
        return source != 0 || target != 0;
    }

    private bool TryParseSlotTypePolarity(ReadOnlySpan<byte> burst, out int colorCode, out int dataType, out int errors)
    {
        if (TryParseSlotType(burst, out colorCode, out dataType, out errors)) return true;
        Span<byte> flipped = stackalloc byte[BurstDibits];
        for (var i = 0; i < BurstDibits; i++) flipped[i] = (byte)(burst[i] ^ 2);
        if (!TryParseSlotType(flipped, out colorCode, out dataType, out errors)) return false;
        _autoXor ^= 2;
        return true;
    }

    private void EmitDstarHeader(DateTime utc, float rssiDb)
    {
        if (!DstarHeaderDecoder.TryDecode(_dstarBits, out var header)) return;
        EmitQso(new DigitalVoiceQso(utc, "D-STAR", "DSTAR_HD", null, null, null, null, "Header",
            header.Source, header.Repeater1, "", header.Destination, rssiDb, 0),
            utc, "D-STAR", "DSTAR_HD", 0);
    }

    private void EmitQso(DigitalVoiceQso qso, DateTime utc, string protocol, string kind, int _, ReadOnlySpan<byte> burst = default)
    {
        QsoDecoded?.Invoke(qso);
        StatusChanged?.Invoke($"{qso.Protocol} {qso.Sync} slot={qso.Slot?.ToString(CultureInfo.InvariantCulture) ?? "-"} cc={qso.ColorCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        if (burst.Length == BurstDibits)
        {
            var copy = burst.ToArray();
            BurstReady?.Invoke(new DsdFmeBurst(utc, protocol, kind, copy, DsdFmeSymbolBridge.ToDsdSymbolFile(copy)));
        }
    }

    private static bool TryParseCach(ReadOnlySpan<byte> cach12, out int slot, out bool activity)
    {
        slot = 0;
        activity = false;
        Span<byte> bits = stackalloc byte[24];
        for (var i = 0; i < 12; i++)
        {
            var dibit = cach12[i];
            bits[CachInterleave[i * 2]] = (byte)((dibit >> 1) & 1);
            bits[CachInterleave[i * 2 + 1]] = (byte)(dibit & 1);
        }
        Span<byte> tact = stackalloc byte[7];
        bits[..7].CopyTo(tact);
        if (!DmrFec.Hamming74Decode(tact)) return false;
        activity = tact[0] != 0;
        slot = tact[1];
        return true;
    }

    private static bool TryParseSlotType(ReadOnlySpan<byte> burst, out int colorCode, out int dataType, out int errors)
    {
        colorCode = 0;
        dataType = 0;
        errors = 0;
        Span<byte> slotType = stackalloc byte[20];
        FillSlotType(burst, slotType);
        var before = slotType.ToArray();
        if (!DmrFec.Golay208Decode(slotType)) return false;
        for (var i = 0; i < 20; i++)
            if (before[i] != slotType[i]) errors++;
        colorCode = (slotType[0] << 3) | (slotType[1] << 2) | (slotType[2] << 1) | slotType[3];
        dataType = (slotType[4] << 3) | (slotType[5] << 2) | (slotType[6] << 1) | slotType[7];
        return true;
    }

    private static void FillSlotType(ReadOnlySpan<byte> burst, Span<byte> slotType)
    {
        var n = 0;
        for (var i = 61; i <= 65; i++)
        {
            slotType[n++] = (byte)((burst[i] >> 1) & 1);
            slotType[n++] = (byte)(burst[i] & 1);
        }
        for (var i = 90; i <= 94; i++)
        {
            slotType[n++] = (byte)((burst[i] >> 1) & 1);
            slotType[n++] = (byte)(burst[i] & 1);
        }
    }

    private static bool TryParseLinkControl(ReadOnlySpan<byte> burst, out int source, out int target, out bool group, out bool emergency)
    {
        source = 0;
        target = 0;
        group = true;
        emergency = false;
        Span<byte> info = stackalloc byte[196];
        UnpackPayload(burst, info);
        Span<byte> lc = stackalloc byte[96];
        if (!DmrFec.DecodeBptc19696(info, lc)) return false;
        var flco = BitsToInt(lc, 2, 6);
        group = flco == 0;
        emergency = lc[16] != 0;
        target = BitsToInt(lc, 24, 24);
        source = BitsToInt(lc, 48, 24);
        return source != 0 || target != 0;
    }

    private static void UnpackPayload(ReadOnlySpan<byte> burst, Span<byte> info196)
    {
        var n = 0;
        for (var i = 12; i < 61; i++)
        {
            info196[n++] = (byte)((burst[i] >> 1) & 1);
            info196[n++] = (byte)(burst[i] & 1);
        }
        for (var i = 95; i < 144; i++)
        {
            info196[n++] = (byte)((burst[i] >> 1) & 1);
            info196[n++] = (byte)(burst[i] & 1);
        }
    }

    private static int BitsToInt(ReadOnlySpan<byte> bits, int start, int length)
    {
        var value = 0;
        for (var i = 0; i < length; i++) value = (value << 1) | (bits[start + i] & 1);
        return value;
    }

    private static string DataTypeName(int type) => type switch
    {
        0 => "PI",
        1 => "Voice LC",
        2 => "Term LC",
        3 => "CSBK",
        4 => "MBC Header",
        6 => "Data Header",
        9 => "Idle",
        _ => $"Data {type}"
    };

    private static byte Slice4Fsk(float sample, float mid)
    {
        if (sample >= mid) return 1;
        if (sample >= 0f) return 0;
        if (sample >= -mid) return 2;
        return 3;
    }

    private bool TryMatchSync(int start, out string name, out string protocol, out bool dstarHeader)
    {
        name = "";
        protocol = "";
        dstarHeader = false;
        if (start < 0 || start + SyncLength > _dibitCount) return false;
        var best = 4;
        foreach (var (key, proto, header, label) in Syncs)
        {
            var distance = 0;
            for (var i = 0; i < SyncLength; i++)
            {
                var isThree = (_dibits[start + i] & 2) != 0;
                if (isThree != (key[i] == '3')) distance++;
                if (distance > best) break;
            }
            if (distance > best) continue;
            best = distance;
            name = label;
            protocol = proto;
            dstarHeader = header;
            if (distance == 0) return true;
        }
        return best <= 2;
    }

    private static readonly (string Pattern, string Protocol, bool Header, string Label)[] Syncs =
    [
        ("131111333113313313113313", "DMR", false, "BS VOICE"),
        ("313333111331131131331131", "DMR", false, "BS DATA"),
        ("133313311131311113313331", "DMR", false, "MS VOICE"),
        ("311131133313133331131113", "DMR", false, "MS DATA"),
        ("113111131333131311133333", "DMR", false, "DM TS1 VOICE"),
        ("331333313111313133311111", "DMR", false, "DM TS1 DATA"),
        ("133133333111331111311133", "DMR", false, "DM TS2 VOICE"),
        ("311311111333113333133311", "DMR", false, "DM TS2 DATA"),
        ("313131313133131113313111", "D-STAR", false, "DSTAR"),
        ("131313131311313331131333", "D-STAR", false, "DSTAR INV"),
        ("131313131333133113131111", "D-STAR", true, "DSTAR_HD"),
        ("313131313111311331313333", "D-STAR", true, "DSTAR_HD INV")
    ];

    private static readonly int[] CachInterleave =
    [
        0, 7, 8, 9, 1, 10, 11, 12, 2, 13, 14, 15, 3, 16, 4, 17, 18, 19, 5, 20, 21, 22, 6, 23
    ];
}
