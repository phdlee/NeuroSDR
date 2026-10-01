namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// Phase-2 hand-off for DSD-FME. DSD's slicer wants ±1/±3 symbols (or dibits 0..3),
/// not discriminator PCM. Keep bursts in that native shape so voice can attach later
/// without relicensing AMBE into this process.
/// </summary>
internal static class DsdFmeSymbolBridge
{
    public const int DmrBurstDibits = 144;
    public const int DsdSymbolFileRate = 4_800;

    /// <summary>DSD <c>digitize()</c> constellation: 0→+1, 1→+3, 2→−1, 3→−3.</summary>
    public static sbyte DibitToDsdSymbol(int dibit) => (dibit & 3) switch
    {
        0 => 1,
        1 => 3,
        2 => -1,
        _ => -3
    };

    public static byte DsdSymbolToDibit(sbyte symbol)
    {
        if (symbol >= 2) return 1;
        if (symbol >= 0) return 0;
        if (symbol >= -2) return 2;
        return 3;
    }

    public static sbyte[] ToDsdSymbolFile(ReadOnlySpan<byte> dibits)
    {
        var symbols = new sbyte[dibits.Length];
        for (var i = 0; i < dibits.Length; i++) symbols[i] = DibitToDsdSymbol(dibits[i]);
        return symbols;
    }

    public static byte[] PackDibits(ReadOnlySpan<byte> dibits)
    {
        var packed = new byte[(dibits.Length + 3) / 4];
        for (var i = 0; i < dibits.Length; i++)
            packed[i / 4] |= (byte)((dibits[i] & 3) << (2 * (i % 4)));
        return packed;
    }
}

/// <summary>Bounded dibit ring that Phase 2 can drain into <c>dsdfme_push_dibits</c>.</summary>
internal sealed class DsdFmeDibitQueue
{
    private readonly byte[] _buffer;
    private int _head, _count;

    public DsdFmeDibitQueue(int capacity = 4_800 * 8)
    {
        _buffer = new byte[Math.Max(4_800, capacity)];
    }

    public int Count => _count;
    public int Capacity => _buffer.Length;

    public int Push(ReadOnlySpan<byte> dibits)
    {
        var accepted = 0;
        foreach (var dibit in dibits)
        {
            PushByte(dibit);
            accepted++;
        }
        return accepted;
    }

    public void PushByte(byte dibit)
    {
        if (_count == _buffer.Length)
        {
            _head = (_head + 1) % _buffer.Length;
            _count--;
        }
        _buffer[(_head + _count) % _buffer.Length] = (byte)(dibit & 3);
        _count++;
    }

    public int Pull(Span<byte> destination)
    {
        var take = Math.Min(destination.Length, _count);
        for (var i = 0; i < take; i++)
        {
            destination[i] = _buffer[_head];
            _head = (_head + 1) % _buffer.Length;
        }
        _count -= take;
        return take;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}

internal readonly record struct DsdFmeBurst(
    DateTime Utc,
    string Protocol,
    string Kind,
    byte[] Dibits,
    sbyte[] DsdSymbols);
