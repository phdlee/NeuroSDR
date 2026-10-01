namespace NeuroSDR.Plugins.Kiwi;

/// <summary>
/// HF weather-fax decoder ported from KiwiSDR extensions/FAX (OpenCPN/yahfax/hamfax lineage).
/// Operates on mono PCM16 at a fixed nominal sample rate (12 kHz).
/// </summary>
internal sealed class KiwiFaxDecoder : IDisposable
{
    public enum FirBandwidth { Narrow, Middle, Wide }
    private enum HeaderKind { Image, Start, Stop }

    public const int NominalSampleRate = 12_000;
    public const int DefaultImageWidth = 1_024;
    public const int DefaultCarrierHz = 1_900;
    public const int DefaultDeviationHz = 400;

    private readonly object _sync = new();
    private readonly FirFilter[] _fir = [new(FirBandwidth.Middle), new(FirBandwidth.Middle)];
    private short[] _lineSamples = [];
    private byte[] _demod = [];
    private byte[] _image = [];
    private byte[] _outLine = [];
    private int[] _phasingPos = [];
    private int _samplesPerLine;
    private int _sampIndex;
    private double _fi;
    private double _sampleRateRatio = 1;
    private double _samplesPerSec = NominalSampleRate;
    private double _iPrev, _qPrev;
    private double _lineIncrFrac, _lineIncrAcc, _lineBlend;
    private int _imageWidth = DefaultImageWidth;
    private int _height = 256;
    private int _imgPos;
    private int _lpm = 120;
    private double _carrier = DefaultCarrierHz;
    private double _deviation = DefaultDeviationHz;
    private bool _includeHeaders = true;
    private bool _usePhasing = true;
    private bool _autoStop;
    private bool _autoStopped;
    private bool _skipHeaderDetection;
    private bool _endDecoding = true;
    private HeaderKind _lastType = HeaderKind.Image;
    private int _typeCount;
    private int _phasingLines = 40;
    private int _phasingLinesLeft;
    private int _phasingSkipData;
    private int _skip;
    private bool _havePhasing;
    private bool _configured;

    public int ImageWidth => _imageWidth;
    public int ImageLine { get; private set; }
    public int BufferHeight => Math.Max(0, ImageLine);
    public int LinesPerMinute => _lpm;
    public bool AutoStopped => _autoStopped;
    public string Status { get; private set; } = "Idle";

    public event Action<string>? StatusChanged;
    public event Action<byte[]>? LineReady; // grayscale width bytes
    public event Action? ChartFinished;
    public event Action? Cleared;

    public void Configure(int lpm, int imageWidth, int carrierHz, int deviationHz, FirBandwidth bandwidth,
        bool includeHeaders, bool usePhasing, bool autoStop, bool reset)
    {
        lock (_sync)
        {
            _lpm = lpm is 60 or 120 ? lpm : 120;
            _carrier = carrierHz;
            _deviation = Math.Max(50, deviationHz);
            _includeHeaders = includeHeaders;
            _usePhasing = usePhasing;
            _autoStop = autoStop;
            _skipHeaderDetection = !(usePhasing || autoStop);
            _fir[0] = new FirFilter(bandwidth);
            _fir[1] = new FirFilter(bandwidth);
            if (reset || !_configured || _imageWidth != imageWidth)
            {
                _imageWidth = Math.Clamp(imageWidth, 320, 2_400);
                SetupBuffers();
                InitializeImage();
            }
            _lineIncrFrac = _imageWidth / (Math.PI * 576.0);
            _lineBlend = _lineIncrFrac;
            _endDecoding = false;
            _configured = true;
            SetStatus($"KiwiFAX {_lpm} LPM · {_imageWidth}px · car {_carrier:0}±{_deviation:0}");
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            InitializeImage();
            _sampIndex = 0;
            _fi = 0;
            _skip = 0;
            _autoStopped = false;
            Cleared?.Invoke();
            SetStatus("Restarted");
        }
    }

    public void ProcessPcm16(ReadOnlySpan<short> samples, double sampleRateHz)
    {
        lock (_sync)
        {
            if (_endDecoding || !_configured || samples.IsEmpty) return;
            _samplesPerSec = sampleRateHz > 0 ? sampleRateHz : NominalSampleRate;
            _sampleRateRatio = _samplesPerSec / NominalSampleRate;

            var offset = 0;
            var remaining = samples.Length;
            if (_skip > 0)
            {
                var skip = Math.Min(remaining, _skip);
                offset += skip;
                remaining -= skip;
                _skip -= skip;
            }

            var i = 0;
            while (i < remaining)
            {
                while (i < remaining && _sampIndex < _samplesPerLine)
                {
                    _lineSamples[_sampIndex++] = samples[offset + i];
                    _fi += _sampleRateRatio;
                    i = (int)_fi;
                }
                if (_sampIndex == _samplesPerLine)
                {
                    DecodeFaxLine();
                    _sampIndex = 0;
                }
            }
            _fi -= remaining;
            if (_fi < 0) _fi = 0;
        }
    }

    private void DecodeFaxLine()
    {
        DemodulateData();
        var type = _skipHeaderDetection
            ? HeaderKind.Image
            : DetectLineType(_demod, Math.Min(_samplesPerLine, 3_000));

        if (type == _lastType && type != HeaderKind.Image) _typeCount++;
        else
        {
            _typeCount--;
            if (_typeCount < 0) _typeCount = 0;
        }
        _lastType = type;

        if (type != HeaderKind.Image)
        {
            const int leeway = 4;
            var prepare = (int)(5.0 * _lpm / 60.0 - leeway);
            if (_typeCount == prepare)
            {
                if (type == HeaderKind.Start)
                {
                    if (!_includeHeaders)
                    {
                        ImageLine = 0;
                        _imgPos = 0;
                        _lineIncrAcc = 0;
                    }
                    _phasingLinesLeft = _phasingLines;
                    _phasingSkipData = 0;
                    _havePhasing = false;
                    if (_autoStopped)
                    {
                        _autoStopped = false;
                        SetStatus("Start tone · receiving");
                    }
                    else SetStatus("Start tone");
                }
                else if (_autoStop)
                {
                    _autoStopped = true;
                    SetStatus("Stop tone · autostop");
                    ChartFinished?.Invoke();
                }
            }
        }

        const int phasingSkipLines = 2;
        if (_usePhasing && _phasingLinesLeft > 0 && _phasingLinesLeft <= _phasingLines - phasingSkipLines)
            _phasingPos[_phasingLinesLeft - 1] = FaxPhasingLinePosition(_demod, _samplesPerLine);

        if (_usePhasing && type == HeaderKind.Image && _phasingLinesLeft >= -phasingSkipLines)
        {
            if (--_phasingLinesLeft == 0)
            {
                var values = _phasingPos.Take(_phasingLines - phasingSkipLines).ToArray();
                _phasingSkipData = Median(values);
                var sorted = values.OrderBy(v => v).ToArray();
                var ten = sorted[Math.Clamp(sorted.Length * 10 / 100, 0, sorted.Length - 1)];
                var ninety = sorted[Math.Clamp(sorted.Length * 90 / 100, 0, sorted.Length - 1)];
                if (ninety - ten > _samplesPerLine / 6) _phasingSkipData = 0;
            }
        }

        if (_includeHeaders || !_usePhasing || (type == HeaderKind.Image && _phasingLinesLeft < -phasingSkipLines))
        {
            if (ImageLine >= _height)
            {
                _height *= 2;
                Array.Resize(ref _image, _imageWidth * _height);
            }

            if (!_autoStopped)
                DecodeImageLine(_demod, _imgPos);

            if (_phasingSkipData != 0 && _usePhasing && !_havePhasing)
            {
                _skip = _phasingSkipData % _samplesPerLine;
                _havePhasing = true;
                SetStatus($"Phased · skip {_skip}");
            }

            _imgPos += _imageWidth;
            ImageLine++;
        }
    }

    private void DemodulateData()
    {
        double f = 0;
        var phInc = _carrier / _samplesPerSec;
        var scale = -1.3 * (NominalSampleRate / _deviation / 8.0);
        for (var i = 0; i < _samplesPerLine; i++)
        {
            var samp = _lineSamples[i] / 32768.0;
            var angle = 2 * Math.PI * f;
            var iCur = _fir[0].Process(samp * Math.Cos(angle));
            var qCur = _fir[1].Process(samp * Math.Sin(angle));
            f += phInc;
            if (f > 1.0) f -= 1.0;
            var mag = Math.Sqrt(qCur * qCur + iCur * iCur);
            if (mag < 1e-12) mag = 1e-12;
            iCur /= mag;
            qCur /= mag;
            var x = (iCur * (qCur - _qPrev) - qCur * (iCur - _iPrev)) * scale;
            x = x / 2.0 + 0.5;
            var pixel = (int)Math.Round(x * 255.0);
            _demod[i] = (byte)Math.Clamp(pixel, 0, 255);
            _iPrev = iCur;
            _qPrev = qCur;
        }
    }

    private void DecodeImageLine(byte[] buffer, int imageOffset)
    {
        var spl = _samplesPerLine;
        for (var i = 0; i < _imageWidth; i++)
        {
            var first = spl * i / _imageWidth;
            var last = spl * (i + 1) / _imageWidth - 1;
            var sum = 0;
            var count = 0;
            for (var sample = first; sample <= last; sample++)
            {
                sum += buffer[sample];
                count++;
            }
            var pixel = count > 0 ? sum / count : 0;
            _image[imageOffset + i] = (byte)pixel;
            _outLine[i] = (byte)pixel;
        }

        var emit = false;
        if (_lineIncrAcc >= 1.0)
        {
            _lineIncrAcc -= 1.0;
            if (ImageLine != 0 && _lineIncrAcc != 0)
            {
                var nextBlend = _lineIncrAcc / _lineBlend;
                var prevBlend = 1.0 - nextBlend;
                var prevOffset = imageOffset - _imageWidth;
                if (prevOffset >= 0)
                {
                    for (var i = 0; i < _imageWidth; i++)
                    {
                        var pixel = (int)Math.Round(_outLine[i] * nextBlend + _image[prevOffset + i] * prevBlend);
                        _outLine[i] = (byte)Math.Min(255, pixel);
                    }
                }
                _lineBlend = _lineIncrFrac;
            }
            emit = true;
        }
        else _lineBlend += _lineIncrFrac;
        _lineIncrAcc += _lineIncrFrac;

        if (emit)
        {
            var copy = new byte[_imageWidth];
            Buffer.BlockCopy(_outLine, 0, copy, 0, _imageWidth);
            LineReady?.Invoke(copy);
            if (ImageLine > 0 && ImageLine % 40 == 0)
                SetStatus($"Line {ImageLine}");
        }
    }

    private HeaderKind DetectLineType(byte[] buffer, int bufferLen)
    {
        const double threshold = 5;
        var start = Fourier(buffer, bufferLen, 300) / bufferLen;
        var stop = Fourier(buffer, bufferLen, 450) / bufferLen;
        if (start > threshold) return HeaderKind.Start;
        if (stop > threshold) return HeaderKind.Stop;
        return HeaderKind.Image;
    }

    private double Fourier(byte[] buffer, int bufferLen, int freq)
    {
        var k = -2 * Math.PI * freq * 60.0 / _lpm / _samplesPerLine;
        double re = 0, im = 0;
        for (var n = 0; n < bufferLen; n++)
        {
            re += buffer[n] * Math.Cos(k * n);
            im += buffer[n] * Math.Sin(k * n);
        }
        return Math.Sqrt(re * re + im * im);
    }

    private static int FaxPhasingLinePosition(byte[] image, int samplesPerLine)
    {
        var n = (int)(samplesPerLine * .07);
        var mintotal = -1;
        var min = 0;
        const int pixelResolution = 4;
        var incr = Math.Max(1, samplesPerLine / 1_024 * pixelResolution);
        for (var i = 0; i < samplesPerLine; i += incr)
        {
            var total = 0;
            for (var j = 0; j < n; j += pixelResolution)
                total += (n / 2 - Math.Abs(j - n / 2)) * (255 - image[(i + j) % samplesPerLine]);
            if (total < mintotal || mintotal < 0)
            {
                mintotal = total;
                min = i;
            }
        }
        return samplesPerLine == 0 ? 0 : (min + n / 2) % samplesPerLine;
    }

    private static int Median(int[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private void SetupBuffers()
    {
        _samplesPerLine = Math.Max(64, (int)(NominalSampleRate * 60.0 / _lpm));
        _lineSamples = new short[_samplesPerLine];
        _demod = new byte[_samplesPerLine];
        _outLine = new byte[_imageWidth];
        _phasingPos = new int[_phasingLines];
        _sampIndex = 0;
        _fi = 0;
        _phasingLinesLeft = 0;
        _phasingSkipData = 0;
        _havePhasing = false;
        _sampleRateRatio = 1;
    }

    private void InitializeImage()
    {
        _height = 256;
        _imgPos = 0;
        ImageLine = 0;
        _lineIncrAcc = 0;
        _image = new byte[_imageWidth * _height];
        _lastType = HeaderKind.Image;
        _typeCount = 0;
        _autoStopped = false;
        _iPrev = _qPrev = 0;
    }

    private void SetStatus(string text)
    {
        Status = text;
        StatusChanged?.Invoke(text);
    }

    public void Dispose() => _endDecoding = true;

    private sealed class FirFilter
    {
        private static readonly double[][] Coefficients =
        [
            [-7, -18, -15, 11, 56, 116, 177, 223, 240, 223, 177, 116, 56, 11, -15, -18, -7],
            [0, -18, -38, -39, 0, 83, 191, 284, 320, 284, 191, 83, 0, -39, -38, -18, 0],
            [6, 20, 7, -42, -74, -12, 159, 353, 440, 353, 159, -12, -74, -42, 7, 20, 6]
        ];

        private readonly double[] _coeff;
        private readonly double[] _buffer = new double[17];
        private int _current;

        public FirFilter(FirBandwidth bandwidth) =>
            _coeff = Coefficients[(int)bandwidth];

        public double Process(double sample)
        {
            _buffer[_current] = sample;
            double sum = 0;
            var index = _current;
            for (var tap = 0; tap < 17; tap++)
            {
                sum += _buffer[index] * _coeff[tap];
                index++;
                if (index == 17) index = 0;
            }
            _current--;
            if (_current < 0) _current = 16;
            return sum;
        }
    }
}
