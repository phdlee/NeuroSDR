using System.Globalization;
using NeuroSDR.Plugins.Kiwi.Jnx;

namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// WWV / WWVH 100 Hz IRIG-H time code from USB audio.
/// Pulse widths: 0.2 s = 0, 0.5 s = 1, 0.8 s = marker. Frame is 60 seconds.
/// </summary>
internal sealed class WwvTimeDecoder
{
    private static readonly int[] MarkerSeconds = [0, 9, 19, 29, 39, 49, 59];

    public event Action<string>? StatusChanged;
    public event Action<string>? LineReceived;
    public event Action<WwvTimeDecode>? TimeDecoded;

    private readonly BiQuadraticFilter _toneFilter = new();
    private readonly BiQuadraticFilter _guardFilter = new();
    private int _sampleRate = 48_000;
    private double _toneHz = 100;
    private int _window;
    private int _n;
    private double _toneEnergy, _guardEnergy;
    private double _tonePeak = 1e-9;
    private bool _on;
    private int _onWindows;
    private int _offWindows;
    private int _onDebounce;
    private int _offDebounce;
    private readonly int[] _frame = new int[60];
    private readonly double[] _width = new double[60];
    private double _time;
    private double _tRef = double.NaN;
    private double _lastStart = double.NegativeInfinity;
    private double _lastWidth;
    private int _lastSymbol = -1;
    private int _filled;
    private bool _invert;
    private bool _filtersReady;

    public void Configure(double toneHz, bool invert)
    {
        var tone = Math.Clamp(toneHz, 50, 2_000);
        if (Math.Abs(tone - _toneHz) < 0.1 && invert == _invert && _filtersReady) return;
        _toneHz = tone;
        _invert = invert;
        SetupFilters(_sampleRate);
        Reset();
    }

    public void Reset()
    {
        _n = 0;
        _toneEnergy = _guardEnergy = 0;
        _tonePeak = 1e-9;
        _on = false;
        _onWindows = _offWindows = _onDebounce = _offDebounce = 0;
        _time = 0;
        _tRef = double.NaN;
        _lastStart = double.NegativeInfinity;
        _lastWidth = 0;
        _lastSymbol = -1;
        _filled = 0;
        Array.Fill(_frame, -1);
        Array.Clear(_width);
        _toneFilter.Reset();
        _guardFilter.Reset();
        StatusChanged?.Invoke($"hunt USB IRIG-H tone={_toneHz:0}Hz (1 s grid)");
    }

    public void Process(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (sampleRate <= 0 || samples.Length == 0) return;
        if (sampleRate != _sampleRate || !_filtersReady)
        {
            _sampleRate = sampleRate;
            SetupFilters(sampleRate);
        }

        foreach (var sample in samples)
        {
            var tone = _toneFilter.Filter(sample);
            var guard = _guardFilter.Filter(sample);
            _toneEnergy += tone * tone;
            _guardEnergy += guard * guard;
            if (++_n < _window) continue;

            var magT = Math.Sqrt(_toneEnergy / _window);
            var magG = Math.Sqrt(_guardEnergy / _window);
            _toneEnergy = _guardEnergy = 0;
            _n = 0;
            _time += _window / (double)_sampleRate;
            AcceptMagnitude(magT, magG);
        }
    }

    private void SetupFilters(int sampleRate)
    {
        _window = Math.Max(32, sampleRate / 100);
        var guardHz = Math.Clamp(Math.Max(280, _toneHz * 3.2), 200, sampleRate * 0.35);
        _toneFilter.Configure(BiQuadraticFilter.FilterType.Bandpass, _toneHz, sampleRate, 10);
        _guardFilter.Configure(BiQuadraticFilter.FilterType.Bandpass, guardHz, sampleRate, 4);
        _n = 0;
        _toneEnergy = _guardEnergy = 0;
        _filtersReady = true;
    }

    private void AcceptMagnitude(double magT, double magG)
    {
        _tonePeak = Math.Max(magT, _tonePeak * 0.997);
        var rawOn = magT > magG * 2.2 && magT > _tonePeak * 0.28 && magT > 1e-5;
        if (_invert) rawOn = !rawOn;

        if (rawOn)
        {
            _onDebounce++;
            _offDebounce = 0;
        }
        else
        {
            _offDebounce++;
            _onDebounce = 0;
        }

        if (!_on)
        {
            if (_onDebounce >= 3)
            {
                _on = true;
                _onWindows = _onDebounce;
                _offWindows = 0;
            }
            return;
        }

        if (_offDebounce > 0 && _offDebounce < 8)
            return;

        if (_offDebounce >= 8)
        {
            _on = false;
            ClassifyPulse(_onWindows * 0.01);
            _onWindows = 0;
            _offWindows = _offDebounce;
            return;
        }

        _onWindows++;
        _offWindows = 0;
    }

    private void ClassifyPulse(double seconds)
    {
        var symbol = seconds switch
        {
            >= 0.62 and <= 1.10 => 2,
            >= 0.38 and <= 0.61 => 1,
            >= 0.14 and <= 0.37 => 0,
            _ => -1
        };
        if (symbol < 0)
        {
            StatusChanged?.Invoke($"ignore {seconds:0.00}s");
            return;
        }

        var dt = _window / (double)_sampleRate;
        var tStart = _time - _offDebounce * dt - seconds;
        if (tStart - _lastStart < 0.55)
        {
            if (seconds <= _lastWidth) return;
            // Same second: replace the weaker fragment.
        }

        _lastStart = tStart;
        _lastWidth = seconds;
        _lastSymbol = symbol;

        if (double.IsNaN(_tRef))
        {
            if (symbol != 2 || seconds < 0.70) return;
            _tRef = tStart;
        }

        var slot = (int)Math.Round((tStart - _tRef));
        if (slot < 0) return;
        while (slot >= 60)
        {
            TryDecodeRing();
            _tRef += 60;
            slot = (int)Math.Round((tStart - _tRef));
            _filled = 0;
            Array.Fill(_frame, -1);
            Array.Clear(_width);
        }

        if (_frame[slot] < 0) _filled++;
        else if (seconds < _width[slot]) return;
        _frame[slot] = symbol;
        _width[slot] = seconds;

        var glyph = symbol == 2 ? "M" : symbol.ToString(CultureInfo.InvariantCulture);
        var markers = CountMarkers();
        LineReceived?.Invoke($"s={slot:00} {glyph}  {seconds:0.00}s  filled={_filled}/60  M={markers}/7");
        StatusChanged?.Invoke($"grid s={slot:00} {glyph}  filled={_filled}/60  M={markers}/7");
        TryDecodeRing();
    }

    private int CountMarkers()
    {
        var n = 0;
        foreach (var m in MarkerSeconds)
            if (_frame[m] == 2) n++;
        return n;
    }

    private void TryDecodeRing()
    {
        if (_filled < 40) return;
        var bestRot = -1;
        var bestScore = 0;
        for (var rot = 0; rot < 60; rot++)
        {
            var score = MarkerScore(rot);
            if (score > bestScore)
            {
                bestScore = score;
                bestRot = rot;
            }
        }
        if (bestRot < 0 || bestScore < 4)
        {
            StatusChanged?.Invoke($"no TIME yet  filled={_filled}/60  best M={bestScore}/7 (need 4+)");
            return;
        }
        if (TryDecodeBits(Rotate(bestRot))) return;
        StatusChanged?.Invoke($"markers {bestScore}/7 but BCD invalid  filled={_filled}/60");
    }

    private int MarkerScore(int rot)
    {
        var hits = 0;
        foreach (var marker in MarkerSeconds)
            if (_frame[(rot + marker) % 60] == 2) hits++;
        var extra = 0;
        for (var i = 0; i < 60; i++)
        {
            if (_frame[(rot + i) % 60] == 2 && Array.IndexOf(MarkerSeconds, i) < 0)
                extra++;
        }
        return extra > 4 ? 0 : hits;
    }

    private int[] Rotate(int rot)
    {
        var bits = new int[60];
        for (var i = 0; i < 60; i++) bits[i] = _frame[(rot + i) % 60];
        return bits;
    }

    private bool TryDecodeBits(int[] bits)
    {
        var minute = Bcd(bits, 1, [1, 2, 4, 8, 0, 10, 20, 40]);
        var hour = Bcd(bits, 10, [1, 2, 4, 8, 0, 10, 20]);
        var day = Bcd(bits, 20, [1, 2, 4, 8, 0, 10, 20, 40, 80]) +
                  Bcd(bits, 30, [100, 200]);
        var year = 2000 + Bcd(bits, 45, [1, 2, 4, 8]) + Bcd(bits, 50, [10, 20, 40, 80]);
        var leapYear = bits[55] == 1;
        var leapSecond = bits[56] == 1;
        var dst1 = bits[57] == 1;
        var dst2 = bits[58] == 1;

        if (minute is < 0 or > 59 || hour is < 0 or > 23 || day is < 1 or > 366 || year is < 2000 or > 2099)
            return false;

        DateTime utc;
        try
        {
            utc = new DateTime(year, 1, 1, hour, minute, 0, DateTimeKind.Utc).AddDays(day - 1);
        }
        catch
        {
            return false;
        }

        var text = utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        var extra = leapYear ? " LY" : "";
        if (leapSecond) extra += " LSW";
        if (dst1 || dst2) extra += dst1 && dst2 ? " DST" : " DST-trans";
        LineReceived?.Invoke($"TIME {text}{extra}  (doy {day})");
        StatusChanged?.Invoke($"TIME {text}{extra}");
        TimeDecoded?.Invoke(new WwvTimeDecode(utc, day, leapYear, leapSecond, dst1, dst2, text + extra));
        return true;
    }

    private static int Bcd(int[] bits, int offset, int[] weights)
    {
        var value = 0;
        for (var i = 0; i < weights.Length; i++)
        {
            var bit = bits[offset + i];
            if (bit < 0 || bit == 2) return -1;
            if (bit == 1) value += weights[i];
        }
        return value;
    }

    internal static float[] SynthesizeFrame(int sampleRate, double toneHz, DateTime utc)
    {
        var symbols = EncodeFrame(utc);
        var total = sampleRate * 60;
        var audio = new float[total];
        for (var second = 0; second < 60; second++)
        {
            var width = symbols[second] switch { 2 => 0.80, 1 => 0.50, _ => 0.20 };
            var nOn = (int)(width * sampleRate);
            var offset = second * sampleRate;
            for (var i = 0; i < nOn; i++)
                audio[offset + i] = 0.5f * (float)Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
        }
        return audio;
    }

    private static int[] EncodeFrame(DateTime utc)
    {
        utc = utc.ToUniversalTime();
        var bits = new int[60];
        foreach (var m in MarkerSeconds) bits[m] = 2;
        WriteBcd(bits, 1, utc.Minute, [1, 2, 4, 8, 0, 10, 20, 40]);
        WriteBcd(bits, 10, utc.Hour, [1, 2, 4, 8, 0, 10, 20]);
        var doy = utc.DayOfYear;
        WriteBcd(bits, 20, doy % 100, [1, 2, 4, 8, 0, 10, 20, 40, 80]);
        WriteBcd(bits, 30, doy / 100 * 100, [100, 200]);
        var yy = utc.Year % 100;
        WriteBcd(bits, 45, yy % 10, [1, 2, 4, 8]);
        WriteBcd(bits, 50, yy / 10 * 10, [10, 20, 40, 80]);
        bits[55] = DateTime.IsLeapYear(utc.Year) ? 1 : 0;
        return bits;
    }

    private static void WriteBcd(int[] bits, int offset, int value, int[] weights)
    {
        for (var i = weights.Length - 1; i >= 0; i--)
        {
            if (weights[i] == 0) continue;
            if (value >= weights[i])
            {
                bits[offset + i] = 1;
                value -= weights[i];
            }
        }
    }
}

internal readonly record struct WwvTimeDecode(
    DateTime UtcMinute,
    int DayOfYear,
    bool LeapYear,
    bool LeapSecondWarning,
    bool Dst1,
    bool Dst2,
    string Text);
