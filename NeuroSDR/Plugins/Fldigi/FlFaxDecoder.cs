namespace NeuroSDR.Plugins.Fldigi;

/// <summary>
/// fldigi <c>wefax.cxx</c> receive path: baseband FM discriminator, APT, optional phasing, IOC-576 lines.
/// </summary>
internal sealed class FlFaxDecoder
{
    public const int NominalSampleRate = 8_000;
    public const int DefaultWidth = 1810; // 576 * π

    public event Action<string>? StatusChanged;
    public event Action<byte[]>? LineReady;
    public event Action? ChartFinished;
    public event Action? Cleared;

    private int _lpm = 120;
    private double _centerHz = 1_900;
    private double _shiftHz = 800;
    private bool _manual = true;
    private bool _phasing;
    private int _state; // 0 apt 1 phasing 2 image
    private double _phase, _prevI, _prevQ;
    private int _aptCount, _aptTrans;
    private bool _aptHigh;
    private int _imgSample, _lastCol = -1, _pixSum, _pixN;
    private readonly byte[] _line = new byte[DefaultWidth];
    private int _lines;
    private int _phaseLen, _phaseHigh;
    private bool _phaseWasHigh;
    private readonly double[] _firI = new double[65];
    private readonly double[] _firQ = new double[65];
    private int _firFill;

    // fldigi wefax medium FIR (Hamfax / ACfax)
    private static readonly double[] FaxFir =
    [
        -0.000795, -0.000779, -0.000698, -0.000517, -0.000195,
         0.000303,  0.000982,  0.0018,    0.00267,   0.00343,
         0.00389,   0.00383,   0.00306,   0.00144,  -0.00102,
        -0.0042,   -0.0078,   -0.0114,   -0.0143,   -0.0159,
        -0.0156,   -0.0127,   -0.00695,   0.00188,   0.0136,
         0.0277,    0.0434,    0.0596,    0.0752,    0.0889,
         0.0997,    0.106,     0.109,     0.106,     0.0997,
         0.0889,    0.0752,    0.0596,    0.0434,    0.0277,
         0.0136,    0.00188,  -0.00695,  -0.0127,   -0.0156,
        -0.0159,   -0.0143,   -0.0114,   -0.0078,   -0.0042,
        -0.00102,   0.00144,   0.00306,   0.00383,   0.00389,
         0.00343,   0.00267,   0.0018,    0.000982,  0.000303,
        -0.000195, -0.000517, -0.000698, -0.000779, -0.000795
    ];

    public void Configure(int lpm, double centerHz, double shiftHz, bool manual, bool phasing, bool reset)
    {
        _lpm = lpm is 60 or 120 ? lpm : 120;
        _centerHz = Math.Clamp(centerHz, 1_000, 2_800);
        _shiftHz = Math.Clamp(shiftHz, 400, 1_000);
        _manual = manual;
        _phasing = phasing;
        if (reset) Reset();
        StatusChanged?.Invoke($"fldigi WEFAX LPM={_lpm} CF={_centerHz:0} shift={_shiftHz:0} {(manual ? "manual" : "APT")}");
    }

    public void Reset()
    {
        _state = _manual ? 2 : 0;
        _phase = _prevI = _prevQ = 0;
        _aptCount = _aptTrans = 0;
        _aptHigh = false;
        _imgSample = 0;
        _lastCol = -1;
        _pixSum = _pixN = 0;
        _lines = 0;
        _phaseLen = _phaseHigh = 0;
        _phaseWasHigh = false;
        Array.Clear(_line);
        Array.Clear(_firI);
        Array.Clear(_firQ);
        _firFill = 0;
        Cleared?.Invoke();
        StatusChanged?.Invoke(_manual ? "image (manual)" : "APT start");
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        const double clip = 0.001;
        var ratio = (NominalSampleRate / _shiftHz) / (2 * Math.PI);
        foreach (var sample in samples)
        {
            var mixI = sample * Math.Cos(_phase);
            var mixQ = sample * Math.Sin(_phase);
            _phase += 2 * Math.PI * _centerHz / NominalSampleRate;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            Array.Copy(_firI, 1, _firI, 0, _firI.Length - 1);
            Array.Copy(_firQ, 1, _firQ, 0, _firQ.Length - 1);
            _firI[^1] = mixI;
            _firQ[^1] = mixQ;
            if (_firFill < FaxFir.Length) { _firFill++; continue; }
            double i = 0, q = 0;
            for (var n = 0; n < FaxFir.Length; n++)
            {
                i += FaxFir[n] * _firI[n];
                q += FaxFir[n] * _firQ[n];
            }
            var mag = Math.Sqrt(i * i + q * q);
            var prevMag = Math.Sqrt(_prevI * _prevI + _prevQ * _prevQ);
            int pix;
            if (mag <= clip && prevMag <= clip)
                pix = 255;
            else
            {
                var di = i * _prevI + q * _prevQ;
                var dq = q * _prevI - i * _prevQ;
                var arg = Math.Atan2(dq, di);
                pix = (int)Math.Round(255 * (0.5 - ratio * arg));
                pix = Math.Clamp(pix, 0, 255);
            }
            _prevI = i;
            _prevQ = q;
            Step(pix);
        }
    }

    private void Step(int x)
    {
        if (_state == 0)
        {
            if (x > 215 && !_aptHigh) { _aptHigh = true; _aptTrans++; }
            else if (x < 40 && _aptHigh) _aptHigh = false;
            _aptCount++;
            if (_aptCount >= NominalSampleRate / 2)
            {
                var freq = _aptTrans * 2;
                _aptCount = _aptTrans = 0;
                StatusChanged?.Invoke($"APT ~{freq} Hz");
                if (freq is >= 250 and <= 360)
                {
                    _state = _phasing ? 1 : 2;
                    _imgSample = 0;
                    StatusChanged?.Invoke(_state == 1 ? "phasing" : "image");
                }
                else if (freq is >= 400 and <= 500)
                {
                    StatusChanged?.Invoke("APT stop");
                    if (_lines > 20) ChartFinished?.Invoke();
                    Reset();
                }
            }
            return;
        }

        if (_state == 1)
        {
            var high = x > 180;
            _phaseLen++;
            if (high) _phaseHigh++;
            if (high != _phaseWasHigh && _phaseWasHigh && _phaseLen > 100)
            {
                var frac = _phaseHigh / (double)_phaseLen;
                if (frac is > 0.02 and < 0.12)
                {
                    _state = 2;
                    _imgSample = 0;
                    _lastCol = -1;
                    StatusChanged?.Invoke("image (phasing lock)");
                }
                _phaseLen = _phaseHigh = 0;
            }
            _phaseWasHigh = high;
            if (_imgSample > NominalSampleRate * 8)
            {
                _state = 2;
                _imgSample = 0;
                StatusChanged?.Invoke("image (phasing timeout)");
            }
            _imgSample++;
            return;
        }

        var samplesPerLine = NominalSampleRate * 60.0 / _lpm;
        var row = _imgSample / samplesPerLine;
        var col = (int)(DefaultWidth * (row - Math.Floor(row)));
        if (col == _lastCol)
        {
            _pixSum += x;
            _pixN++;
        }
        else
        {
            if (_lastCol >= 0 && _lastCol < DefaultWidth && _pixN > 0)
                _line[_lastCol] = (byte)Math.Clamp(_pixSum / _pixN, 0, 255);
            if (col < _lastCol)
            {
                LineReady?.Invoke((byte[])_line.Clone());
                _lines++;
                Array.Clear(_line);
                StatusChanged?.Invoke($"image line {_lines}");
                if (_lines >= 1_200)
                {
                    ChartFinished?.Invoke();
                    Reset();
                    return;
                }
            }
            _lastCol = col;
            _pixSum = x;
            _pixN = 1;
        }
        _imgSample++;
    }
}
