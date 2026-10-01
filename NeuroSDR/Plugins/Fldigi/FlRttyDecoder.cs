using System.Globalization;
using System.Text;

namespace NeuroSDR.Plugins.Fldigi;

/// <summary>
/// fldigi <c>view_rtty.cxx</c>: scan the AF passband in 100 Hz slots, Kahn mark/space demod,
/// raised-cosine FIR (same 1.4×baud scaling as <c>fftfilt::rtty_filter</c>), Baudot UART.
/// </summary>
internal sealed class FlRttyDecoder
{
    public const int NominalSampleRate = 8_000;
    public const int ChannelCount = 30;
    private const int FftSize = 2_048;
    private const int FilterLen = 64;
    /// <summary>fldigi default VIEWER_rttysquelch −6 dB as linear power ratio.</summary>
    private const double SquelchRatio = 0.251189; // 10^(-6/10)

    private static readonly char[] Letters =
    [
        '\0','E','\n','A',' ','S','I','U','\r','D','R','J','N','F','C','K',
        'T','Z','L','W','H','Y','P','Q','O','B','G',' ','M','X','V',' '
    ];
    private static readonly char[] Figures =
    [
        '\0','3','\n','-',' ','\a','8','7','\r','$','4','\'',',','!',':','(',
        '5','"',')','2','#','6','0','1','9','?','&',' ','.','/',';',' '
    ];

    public event Action<int, int, char>? ChannelCharacter; // ch, freqHz, char
    public event Action<int>? ChannelCleared;
    public event Action<string>? StatusChanged;

    private const int ViewerTimeoutSec = 15;
    private const int WfBlock = 512;
    private const int SigSearch = 5;
    private const int Idle = 0, Search = 1, Receiving = 2, Wait = 3;

    private double _baud = 45.45;
    private double _shiftHz = 170;
    private bool _inverse;
    private bool _uos = true;
    private double _sqlDb;
    private double _threshold = 0.251189;
    private int _symbollen = 176;
    private double[] _fir = [];
    private readonly Channel[] _ch = new Channel[ChannelCount];
    private readonly float[] _fftBuf = new float[FftSize];
    private double[] _specAvg = [];
    private int _fftFill;
    private bool _prepared;

    public FlRttyDecoder()
    {
        for (var i = 0; i < ChannelCount; i++)
            _ch[i] = new Channel();
    }

    public void Configure(double baud, double shiftHz, bool inverse, bool uos, double sqlDb = 0)
    {
        var nextBaud = Math.Clamp(baud, 40, 300);
        var nextShift = Math.Clamp(shiftHz, 20, 900);
        var nextSql = Math.Clamp(sqlDb, 0, 24);
        var changed = !_prepared || Math.Abs(nextBaud - _baud) > 0.01 || Math.Abs(nextShift - _shiftHz) > 0.5;
        _baud = nextBaud;
        _shiftHz = nextShift;
        _inverse = inverse;
        _uos = uos;
        _sqlDb = nextSql;
        _threshold = SquelchRatio * Math.Pow(10, _sqlDb / 10.0);
        if (!changed) return;
        _symbollen = Math.Clamp((int)(NominalSampleRate / _baud + 0.5), 16, 400);
        _fir = DesignRaisedCosine(FilterLen, 1.4 * _baud / NominalSampleRate);
        foreach (var ch in _ch) ch.Reset(_symbollen);
        _prepared = true;
        StatusChanged?.Invoke($"fldigi view-RTTY {_baud.ToString("0.##", CultureInfo.InvariantCulture)} Bd shift={_shiftHz:0} SQL={_sqlDb:0} dB");
    }

    public void Reset()
    {
        foreach (var ch in _ch)
        {
            var index = ch.Index;
            ch.Reset(_symbollen);
            ChannelCleared?.Invoke(index);
        }
        _fftFill = 0;
        StatusChanged?.Invoke("scan reset");
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        if (!_prepared) Configure(_baud, _shiftHz, _inverse, _uos);
        foreach (var sample in samples)
        {
            if (_fftFill < FftSize) _fftBuf[_fftFill] = sample;
            _fftFill++;
            if (_fftFill >= FftSize)
            {
                FindSignals();
                Array.Copy(_fftBuf, FftSize / 2, _fftBuf, 0, FftSize / 2);
                _fftFill = FftSize / 2;
            }

            for (var i = 0; i < ChannelCount; i++)
            {
                var ch = _ch[i];
                if (ch.State == Idle) continue;
                StepChannel(ch, sample);
            }
        }
    }

    private void StepChannel(Channel ch, float sample)
    {
        // fldigi: z = cmplx(s,s); mix with exp(-j 2π f t)
        var zRe = sample;
        var zIm = sample;
        Mix(ref ch.MarkPhase, ch.Frequency + _shiftHz / 2, zRe, zIm, out var mI, out var mQ);
        Mix(ref ch.SpacePhase, ch.Frequency - _shiftHz / 2, zRe, zIm, out var sI, out var sQ);
        Fir(ch.MarkFir, mI, mQ, out var fmI, out var fmQ);
        Fir(ch.SpaceFir, sI, sQ, out var fsI, out var fsQ);
        var markN = fmI * fmI + fmQ * fmQ;
        var spaceN = fsI * fsI + fsQ * fsQ;
        var bit = markN >= spaceN;
        if (_inverse) bit = !bit;
        Rx(ch, bit);
    }

    private static void Mix(ref double phase, double freq, double re, double im, out double oI, out double oQ)
    {
        var c = Math.Cos(phase);
        var s = Math.Sin(phase);
        oI = c * re - s * im;
        oQ = s * re + c * im;
        phase -= 2 * Math.PI * freq / NominalSampleRate;
        if (phase < -2 * Math.PI) phase += 2 * Math.PI;
        if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
    }

    private void Fir(double[] hist, double i, double q, out double oI, out double oQ)
    {
        Array.Copy(hist, 2, hist, 0, hist.Length - 2);
        hist[^2] = i;
        hist[^1] = q;
        oI = oQ = 0;
        for (var n = 0; n < _fir.Length; n++)
        {
            oI += _fir[n] * hist[n * 2];
            oQ += _fir[n] * hist[n * 2 + 1];
        }
    }

    private void Rx(Channel ch, bool bit)
    {
        Array.Copy(ch.BitBuf, 1, ch.BitBuf, 0, _symbollen - 1);
        ch.BitBuf[_symbollen - 1] = bit;
        switch (ch.RxState)
        {
            case 0:
                if (IsMarkSpace(ch, out var correction))
                {
                    ch.RxState = 1;
                    ch.Counter = Math.Max(1, correction);
                }
                break;
            case 1:
                if (--ch.Counter == 0)
                {
                    if (!IsMark(ch))
                    {
                        ch.RxState = 2;
                        ch.Counter = _symbollen;
                        ch.BitCntr = 0;
                        ch.RxData = 0;
                    }
                    else ch.RxState = 0;
                }
                break;
            case 2:
                if (--ch.Counter == 0)
                {
                    if (IsMark(ch)) ch.RxData |= 1 << ch.BitCntr;
                    ch.BitCntr++;
                    ch.Counter = _symbollen;
                }
                if (ch.BitCntr == 5) ch.RxState = 3;
                break;
            case 3:
                if (--ch.Counter == 0)
                {
                    if (IsMark(ch))
                    {
                        var c = Baudot(ch, (byte)(ch.RxData & 0x1F));
                        if (c != 0 && ch.State is Receiving or Wait && ch.Metric > _threshold)
                            ChannelCharacter?.Invoke(ch.Index, (int)Math.Round(ch.Frequency), c);
                    }
                    ch.RxState = 0;
                }
                break;
        }
    }

    private bool IsMarkSpace(Channel ch, out int correction)
    {
        correction = 0;
        if (!ch.BitBuf[0] || ch.BitBuf[_symbollen - 1]) return false;
        for (var i = 0; i < _symbollen; i++) if (ch.BitBuf[i]) correction++;
        return Math.Abs(_symbollen / 2 - correction) < 6;
    }

    private bool IsMark(Channel ch) => ch.BitBuf[_symbollen / 2];

    private char Baudot(Channel ch, byte data) => data switch
    {
        0x1F => SetLetters(ch),
        0x1B => SetFigures(ch),
        0x04 => Space(ch),
        _ => ch.Letters ? Letters[data & 31] : Figures[data & 31]
    };

    private static char SetLetters(Channel ch) { ch.Letters = true; return '\0'; }
    private static char SetFigures(Channel ch) { ch.Letters = false; return '\0'; }
    private char Space(Channel ch)
    {
        if (_uos) ch.Letters = true;
        return ' ';
    }

    private void FindSignals()
    {
        var spec = PowerSpectrum(_fftBuf);
        if (_specAvg.Length != spec.Length) _specAvg = new double[spec.Length];
        for (var i = 0; i < spec.Length; i++)
            _specAvg[i] = _specAvg[i] < 1e-20 ? spec[i] : _specAvg[i] * 0.85 + spec[i] * 0.15;
        var noiseFloor = Percentile(_specAvg, 0.20);
        var binHz = (double)NominalSampleRate / FftSize;
        var half = _shiftHz / 2;
        var deltaBins = Math.Max(1, (int)(_baud / 8 / binHz));

        for (var i = 0; i < ChannelCount; i++)
        {
            if (_ch[i].State != Idle)
            {
                UpdateMetric(_ch[i], _specAvg, binHz, noiseFloor);
                continue;
            }

            var cf0 = Math.Max((int)_shiftHz, 100 * i);
            for (var chf = cf0; chf < cf0 + 100 - _baud / 4; chf += 5)
            {
                var lo = BandPower(_specAvg, binHz, chf - half, deltaBins);
                var hi = BandPower(_specAvg, binHz, chf + half, deltaBins);
                var np = BandPower(_specAvg, binHz, chf, deltaBins) * 3000 / _baud + noiseFloor * 3000 / _baud + 1e-10;
                if (lo / np <= _threshold || hi / np <= _threshold) continue;
                if (i > 0 && IsBusy(_ch[i - 1])) break;
                if (i > 1 && IsBusy(_ch[i - 2])) break;
                if (i + 1 < ChannelCount && IsBusy(_ch[i + 1])) break;
                _ch[i].Activate(i, chf, _symbollen);
                break;
            }
        }

        for (var i = 1; i < ChannelCount; i++)
        {
            if (_ch[i].State == Idle || _ch[i - 1].State == Idle) continue;
            if (Math.Abs(_ch[i].Frequency - _ch[i - 1].Frequency) < _baud / 2)
                ClearChannel(_ch[i]);
        }

        var locked = 0;
        var sb = new StringBuilder();
        for (var i = 0; i < ChannelCount; i++)
        {
            if (_ch[i].State is Idle or Wait) continue;
            locked++;
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append($"{(int)_ch[i].Frequency}");
        }
        if (locked == 0) StatusChanged?.Invoke($"scanning AF · {_baud.ToString("0.##", CultureInfo.InvariantCulture)} Bd shift={_shiftHz:0}");
        else StatusChanged?.Invoke($"{locked} RTTY @ {sb} Hz");
    }

    private void UpdateMetric(Channel ch, double[] spec, double binHz, double noiseFloor)
    {
        var delta = _baud / 2;
        var width = Math.Max(1, (int)(delta / binHz));
        var np = BandPower(spec, binHz, ch.Frequency, width) * 3000 / delta + noiseFloor * 3000 / delta + 1e-10;
        var sp = BandPower(spec, binHz, ch.Frequency - _shiftHz / 2, width)
               + BandPower(spec, binHz, ch.Frequency + _shiftHz / 2, width);
        ch.SigPwr = Decay(ch.SigPwr, sp, sp > ch.SigPwr ? 2 : 16);
        ch.NoisePwr = Decay(ch.NoisePwr, np, 16);
        ch.Metric = Math.Clamp(ch.SigPwr / ch.NoisePwr, 0, 100);

        if (ch.SigSearch > 0)
        {
            ch.SigSearch--;
            if (ch.SigSearch == 0) ch.State = Receiving;
        }

        if (ch.Metric >= _threshold)
        {
            ch.LowHops = 0;
            if (ch.State == Wait)
            {
                ch.State = Receiving;
                ch.Timeout = 0;
            }
            else if (ch.State == Receiving)
                ch.Timeout = 0;
            return;
        }

        if (ch.State == Receiving)
        {
            if (++ch.LowHops < 4) return;
            ch.Timeout = ViewerTimeoutSec * NominalSampleRate / (FftSize / 2);
            ch.State = Wait;
        }
        if (ch.State == Wait && ch.Timeout > 0 && --ch.Timeout == 0)
            ClearChannel(ch);
    }

    private void ClearChannel(Channel ch)
    {
        var index = ch.Index;
        ch.Reset(_symbollen);
        ChannelCleared?.Invoke(index);
    }

    private static bool IsBusy(Channel ch) => ch.State is Search or Receiving;

    private static double Decay(double avg, double value, double weight) =>
        avg + (value - avg) / Math.Max(1, weight);

    private static double Percentile(double[] spec, double p)
    {
        var copy = spec.Where(v => v > 0).ToArray();
        if (copy.Length == 0) return 1e-10;
        Array.Sort(copy);
        return copy[Math.Clamp((int)(p * copy.Length), 0, copy.Length - 1)];
    }

    private static double BandPower(double[] spec, double binHz, double freq, int widthBins)
    {
        var bin = (int)Math.Round(freq / binHz);
        var sum = 0.0;
        var n = 0;
        for (var i = bin - widthBins; i <= bin + widthBins; i++)
        {
            if (i < 0 || i >= spec.Length) continue;
            sum += spec[i];
            n++;
        }
        return n == 0 ? 1e-12 : sum / n;
    }

    private static double[] PowerSpectrum(float[] x)
    {
        var n = x.Length;
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++)
        {
            var w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            re[i] = x[i] * w;
        }
        Fft(re, im, false);
        var spec = new double[n / 2];
        for (var i = 0; i < spec.Length; i++)
            spec[i] = re[i] * re[i] + im[i] * im[i];
        return spec;
    }

    private static void Fft(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wlenRe = Math.Cos(ang);
            var wlenIm = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double wRe = 1, wIm = 0;
                for (var j = 0; j < len / 2; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = re[i + j + len / 2] * wRe - im[i + j + len / 2] * wIm;
                    var vIm = re[i + j + len / 2] * wIm + im[i + j + len / 2] * wRe;
                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + len / 2] = uRe - vRe;
                    im[i + j + len / 2] = uIm - vIm;
                    var nRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nRe;
                }
            }
        }
    }

    private static double[] DesignRaisedCosine(int length, double fNorm)
    {
        var h = new double[length];
        var m = (length - 1) / 2.0;
        var sum = 0.0;
        for (var i = 0; i < length; i++)
        {
            var x = i - m;
            var sinc = Math.Abs(x) < 1e-9 ? 2 * fNorm : Math.Sin(2 * Math.PI * fNorm * x) / (Math.PI * x);
            var w = 0.5 + 0.5 * Math.Cos(Math.PI * x / (m + 1e-9));
            h[i] = sinc * w;
            sum += h[i];
        }
        if (Math.Abs(sum) > 1e-12)
            for (var i = 0; i < length; i++) h[i] /= sum;
        return h;
    }

    private sealed class Channel
    {
        public int Index;
        public int State;
        public double Frequency;
        public double MarkPhase, SpacePhase;
        public double[] MarkFir = new double[FilterLen * 2];
        public double[] SpaceFir = new double[FilterLen * 2];
        public bool[] BitBuf = new bool[512];
        public int RxState, Counter, BitCntr, RxData, SigSearch, Timeout, LowHops;
        public double SigPwr, NoisePwr = 1e-10, Metric;
        public bool Letters = true;

        public void Reset(int symbolLen)
        {
            State = Idle;
            Frequency = 0;
            MarkPhase = SpacePhase = 0;
            Array.Clear(MarkFir);
            Array.Clear(SpaceFir);
            Array.Clear(BitBuf);
            RxState = Counter = BitCntr = RxData = SigSearch = Timeout = LowHops = 0;
            SigPwr = Metric = 0;
            NoisePwr = 1e-10;
            Letters = true;
            _ = symbolLen;
        }

        public void Activate(int index, double freq, int symbolLen)
        {
            Reset(symbolLen);
            Index = index;
            State = Search;
            this.SigSearch = 5;
            Frequency = freq;
        }

        public void ResetUart()
        {
            RxState = Counter = BitCntr = RxData = 0;
            Array.Clear(BitBuf);
        }
    }
}
