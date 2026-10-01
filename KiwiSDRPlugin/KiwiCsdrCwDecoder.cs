using System.Text;

namespace KiwiSDRPlugin;

/// <summary>
/// C# port of KiwiSDR vendored <c>rx/csdr/cw.cpp</c> (Marat Fayzullin / Csdr::CwDecoder&lt;float&gt;).
/// Consumes magnitude samples and emits decoded characters via <see cref="CharactersAvailable"/>.
/// </summary>
public sealed class KiwiCsdrCwDecoder
{
    private static readonly string CwTable =
        "__TEMNAIOGKDWRUS" + // 00000000
        "__QZYCXBJP_L_FVH" +
        "09_8_<_7_(___/-6" + // <AR>
        "1______&2___3_45" +
        "_______:____,___" + // 01000000
        "__)_!;________-_" +
        "_'___@____._____" +
        "___?______{_____" + // <SK>
        "________________" + // 10000000
        "________________" +
        "________________" +
        "________________" +
        "________________" + // 11000000
        "________________" +
        "________________" +
        "______$_________";

    private readonly Queue<byte> _output = new();
    private readonly int _sampleRate;
    private readonly int _quStep;
    private readonly double _attack;
    private readonly double _decay;
    private readonly int _nbTime;
    private readonly bool _showCw;

    private double _magL = 0.5, _magH = 0.5;
    private bool _realState0, _filtState0;
    private ulong _curSeconds;
    private uint _curSamples;
    private ulong _lastStartT, _startTimeH, _startTimeL;
    private double _avgDitT = 50, _avgDahT = 100, _avgBrkT = 50;
    private uint _code = 1;
    private int _wpm;

    public KiwiCsdrCwDecoder(int sampleRate, bool showCw = false)
    {
        _sampleRate = Math.Clamp(sampleRate, 1_000, 96_000);
        const int quTimeMs = 5;
        _nbTime = 20;
        _showCw = showCw;
        _quStep = Math.Max(1, quTimeMs * _sampleRate / 1_000);
        _attack = quTimeMs / 50.0;
        _decay = quTimeMs / 5_000.0;
    }

    public int Wpm => _wpm;
    public int QuantumSamples => _quStep;

    public void Reset()
    {
        _realState0 = _filtState0 = false;
        _magL = _magH = 0.5;
        _lastStartT = _startTimeH = _startTimeL = 0;
        _avgDitT = 50;
        _avgDahT = 100;
        _avgBrkT = 50;
        _code = 1;
        _wpm = 0;
        _curSeconds = 0;
        _curSamples = 0;
        _output.Clear();
    }

    /// <summary>Feed power/magnitude samples. Returns true if at least one character is pending.</summary>
    public bool Process(ReadOnlySpan<float> samples)
    {
        var offset = 0;
        while (samples.Length - offset >= _quStep)
        {
            ProcessQuantum(samples.Slice(offset, _quStep));
            offset += _quStep;
        }
        return _output.Count > 0;
    }

    public int Available => _output.Count;

    public int Read(Span<byte> destination)
    {
        var n = Math.Min(destination.Length, _output.Count);
        for (var i = 0; i < n; i++) destination[i] = _output.Dequeue();
        return n;
    }

    public string DrainText()
    {
        if (_output.Count == 0) return string.Empty;
        var sb = new StringBuilder(_output.Count);
        while (_output.Count > 0) sb.Append((char)_output.Dequeue());
        return sb.ToString();
    }

    private void ProcessQuantum(ReadOnlySpan<float> data)
    {
        var range = _magH - _magL;
        double magnitude = 0;
        for (var i = 0; i < data.Length; i++) magnitude += Math.Abs(data[i]);
        magnitude /= data.Length;

        var realState = magnitude > _magL + range * 0.7 ? true
            : magnitude < _magL + range * 0.5 ? false
            : _realState0;

        _magL += magnitude < _magL ? (magnitude - _magL) * _attack : range * _decay;
        _magH += magnitude > _magH ? (magnitude - _magH) * _attack : -range * _decay;

        ProcessInternal(realState);

        _curSamples += (uint)_quStep;
        if (_curSamples >= (uint)_sampleRate)
        {
            var secs = _curSamples / (uint)_sampleRate;
            _curSeconds += secs;
            _curSamples -= secs * (uint)_sampleRate;
        }
    }

    private void ProcessInternal(bool newState)
    {
        var millis = Msecs();
        if (newState != _realState0) _lastStartT = millis;
        var filtState = (millis - _lastStartT) > (ulong)_nbTime ? newState : _filtState0;

        if (filtState != _filtState0)
        {
            if (filtState)
            {
                _startTimeH = millis;
                var duration = (double)(millis - _startTimeL);
                if (_code > 1 && duration >= 2.5 * _avgBrkT)
                {
                    Emit(Cw2Char(_code));
                    if (duration >= 5.0 * _avgBrkT) Emit((byte)' ');
                    _code = 1;
                }
                if (duration > 20.0 && duration < 1.5 * _avgDitT && duration > 0.6 * _avgDitT)
                    _avgBrkT += (duration - _avgBrkT) / 4.0;
            }
            else
            {
                _startTimeL = millis;
                var duration = (double)(millis - _startTimeH);
                var midT = (_avgDitT + _avgDahT) / 2.0;
                if (duration <= midT && duration > 0.5 * _avgDitT)
                {
                    _code = (_code << 1) | 1;
                    if (_showCw) Emit((byte)'.');
                }
                else if (duration > midT && duration < 3.0 * _avgDahT)
                {
                    _code = (_code << 1) | 0;
                    _wpm = (_wpm + (int)(3600.0 / duration)) / 2;
                    if (_showCw) Emit((byte)'-');
                }
                if (duration > 20.0 && duration < 0.4 * _avgDahT)
                    _avgDitT += (duration - _avgDitT) / 4.0;
                if (duration < 500.0 && duration > 2.5 * _avgDitT)
                    _avgDahT += (duration - _avgDahT) / 4.0;
            }
        }

        if (_code > 1 && !filtState && (millis - _startTimeL) > 5.0 * _avgBrkT)
        {
            Emit(Cw2Char(_code));
            Emit((byte)' ');
            _code = 1;
        }

        _realState0 = newState;
        _filtState0 = filtState;
    }

    private ulong Msecs() => 1000UL * _curSeconds + 1000UL * _curSamples / (uint)_sampleRate;

    private void Emit(byte ch)
    {
        if (_output.Count > 4_000) _output.Dequeue();
        _output.Enqueue(ch);
    }

    private static byte Cw2Char(uint data) =>
        data < 256 ? (byte)CwTable[(int)data] : (byte)'_';
}
