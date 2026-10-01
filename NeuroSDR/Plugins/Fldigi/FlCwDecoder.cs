using System.Globalization;
using System.Text;

namespace NeuroSDR.Plugins.Fldigi;

/// <summary>
/// fldigi <c>view_cw.cxx</c>: 100 Hz viewer channels from 400 Hz, fft-style mix + envelope Morse.
/// </summary>
internal sealed class FlCwDecoder
{
    public const int NominalSampleRate = 8_000;
    public const int ChannelCount = 30;
    public const int ChannelSpacing = 100;
    public const int FirstHz = 200;
    private const int DecRatio = 16;
    private const int FilterLen = 64;
    private const int ViewerTimeoutSec = 15;
    private const int Kwpm = (12 * NominalSampleRate / 10) / DecRatio;
    private const double BaseCwSquelch = 3.0;
    private double _sqlDb;
    private double _cwSquelch = BaseCwSquelch;

    private static readonly Dictionary<string, string> Morse = new()
    {
        [".-"] = "A", ["-..."] = "B", ["-.-."] = "C", ["-.."] = "D", ["."] = "E",
        ["..-."] = "F", ["--."] = "G", ["...."] = "H", [".."] = "I", [".---"] = "J",
        ["-.-"] = "K", [".-.."] = "L", ["--"] = "M", ["-."] = "N", ["---"] = "O",
        [".--."] = "P", ["--.-"] = "Q", [".-."] = "R", ["..."] = "S", ["-"] = "T",
        ["..-"] = "U", ["...-"] = "V", [".--"] = "W", ["-..-"] = "X", ["-.--"] = "Y",
        ["--.."] = "Z", ["-----"] = "0", [".----"] = "1", ["..---"] = "2", ["...--"] = "3",
        ["....-"] = "4", ["....."] = "5", ["-...."] = "6", ["--..."] = "7", ["---.."] = "8",
        ["----."] = "9", [".-.-.-"] = ".", ["--..--"] = ",", ["..--.."] = "?",
        ["-.-.--"] = "!", ["-....-"] = "-", [".-.-."] = "+", ["-...-"] = "=",
        ["-..-."] = "/", ["-.--."] = "(", ["-.--.-"] = ")", [".-..."] = "&"
    };

    public event Action<int, int, string>? ChannelText;
    public event Action<int>? ChannelCleared;
    public event Action<string>? StatusChanged;

    private readonly Channel[] _ch = new Channel[ChannelCount];
    private readonly double[] _fir;
    private int _wpm = 18;
    private bool _prepared;
    private int _statusSamples;

    public FlCwDecoder()
    {
        _fir = DesignLowpass(FilterLen, ChannelSpacing * 0.6 / NominalSampleRate);
        for (var i = 0; i < ChannelCount; i++)
            _ch[i] = new Channel(i, FirstHz + ChannelSpacing * i, Kwpm / 18);
    }

    public void Configure(int wpm, double sqlDb = 0)
    {
        var next = Math.Clamp(wpm <= 0 ? 18 : wpm, 5, 60);
        _sqlDb = Math.Clamp(sqlDb, 0, 24);
        _cwSquelch = BaseCwSquelch + _sqlDb;
        if (_prepared && next == _wpm) return;
        _wpm = next;
        var two = 2 * Kwpm / _wpm;
        foreach (var ch in _ch) ch.TwoDots = two;
        _prepared = true;
        StatusChanged?.Invoke($"fldigi view-CW {_wpm} WPM · {ChannelCount} ch @ {FirstHz}–{FirstHz + ChannelSpacing * (ChannelCount - 1)} Hz");
    }

    public void Reset()
    {
        foreach (var ch in _ch)
        {
            ch.Reset();
            ChannelCleared?.Invoke(ch.Index);
        }
        StatusChanged?.Invoke("view-CW reset");
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        if (!_prepared) Configure(_wpm);
        foreach (var sample in samples)
        {
            for (var i = 0; i < ChannelCount; i++)
                Step(_ch[i], sample);
        }

        var nf = Percentile(_ch.Select(item => item.SigAvg).ToArray(), 0.25);
        if (nf < 1e-6) nf = 1e-6;
        var bestMetric = -1.0;
        var bestHz = FirstHz;
        for (var n = 0; n < ChannelCount; n++)
        {
            var ch = _ch[n];
            if (ch.Metric > bestMetric)
            {
                bestMetric = ch.Metric;
                bestHz = (int)ch.Freq;
            }
            if (ch.Timeout <= 0) continue;
            ch.Timeout -= samples.Length;
            if (ch.Timeout > 0) continue;
            ch.Timeout = 0;
            ch.Reset();
            ChannelCleared?.Invoke(n);
        }
        for (var n = 0; n < ChannelCount; n++) _ch[n].NoiseFloor = nf;

        _statusSamples += samples.Length;
        if (_statusSamples >= NominalSampleRate / 2)
        {
            _statusSamples = 0;
            var live = 0;
            for (var n = 0; n < ChannelCount; n++)
                if (_ch[n].Occupied) live++;
            StatusChanged?.Invoke(bestMetric > _cwSquelch
                ? $"view-CW {_wpm} WPM · {live} live · peak {bestMetric:F0} dB @ {bestHz} Hz"
                : $"view-CW {_wpm} WPM · no tone yet · USB pitch {FirstHz}–{FirstHz + ChannelSpacing * (ChannelCount - 1)} Hz");
        }
    }

    /// <summary>PARIS-standard Morse audio for tests (A1A keyed tone).</summary>
    internal static float[] SynthesizeMorse(string text, int sampleRate, int pitchHz, int wpm, float amplitude = 0.45f)
    {
        var unit = Math.Max(1, (int)Math.Round(sampleRate * 1.2 / Math.Clamp(wpm, 5, 60)));
        var map = new Dictionary<char, string>
        {
            ['A'] = ".-", ['B'] = "-...", ['C'] = "-.-.", ['D'] = "-..", ['E'] = ".",
            ['F'] = "..-.", ['G'] = "--.", ['H'] = "....", ['I'] = "..", ['J'] = ".---",
            ['K'] = "-.-", ['L'] = ".-..", ['M'] = "--", ['N'] = "-.", ['O'] = "---",
            ['P'] = ".--.", ['Q'] = "--.-", ['R'] = ".-.", ['S'] = "...", ['T'] = "-",
            ['U'] = "..-", ['V'] = "...-", ['W'] = ".--", ['X'] = "-..-", ['Y'] = "-.--",
            ['Z'] = "--..", ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--",
            ['4'] = "....-", ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..",
            ['9'] = "----.", [' '] = ""
        };
        var samples = new List<float>(sampleRate * 8);
        var phase = 0.0;
        var step = 2 * Math.PI * pitchHz / sampleRate;
        void Tone(int units)
        {
            var n = units * unit;
            for (var i = 0; i < n; i++)
            {
                samples.Add(amplitude * (float)Math.Sin(phase));
                phase += step;
            }
        }
        void Silence(int units)
        {
            var n = units * unit;
            for (var i = 0; i < n; i++) samples.Add(0);
        }
        Silence(8);
        var words = text.ToUpperInvariant();
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i] == ' ')
            {
                Silence(7);
                continue;
            }
            if (!map.TryGetValue(words[i], out var code) || code.Length == 0) continue;
            for (var e = 0; e < code.Length; e++)
            {
                Tone(code[e] == '-' ? 3 : 1);
                if (e + 1 < code.Length) Silence(1);
            }
            if (i + 1 < words.Length && words[i + 1] != ' ') Silence(3);
        }
        Silence(24);
        return samples.ToArray();
    }

    private void Step(Channel ch, float sample)
    {
        var zI = sample * Math.Cos(ch.Phase);
        var zQ = sample * Math.Sin(ch.Phase);
        ch.Phase += ch.PhaseInc;
        if (ch.Phase > 2 * Math.PI) ch.Phase -= 2 * Math.PI;
        Array.Copy(ch.Hist, 2, ch.Hist, 0, ch.Hist.Length - 2);
        ch.Hist[^2] = zI;
        ch.Hist[^1] = zQ;
        double oI = 0, oQ = 0;
        for (var n = 0; n < _fir.Length; n++)
        {
            oI += _fir[n] * ch.Hist[n * 2];
            oQ += _fir[n] * ch.Hist[n * 2 + 1];
        }
        if (++ch.Dec < DecRatio) return;
        ch.Dec = 0;
        ch.Smpl++;
        var mag = Math.Sqrt(oI * oI + oQ * oQ);
        ch.Bit = ch.Bit + (mag - ch.Bit) / 10;
        Detect(ch);
    }

    private void Detect(Channel ch)
    {
        var value = ch.Bit;
        ch.SigAvg = Decay(ch.SigAvg, value, 1000);
        if (ch.Agc < 1e-12)
        {
            if (value <= 1e-6) return;
            ch.Agc = value;
        }
        else
            ch.Agc = Decay(ch.Agc, value, value > ch.Agc ? 100 : 1000);
        value /= ch.Agc;
        var normSig = ch.SigAvg / ch.Agc;
        var metric = Math.Clamp(20 * Math.Log10(Math.Max(1e-12, ch.SigAvg / ch.NoiseFloor)), 0, 40);
        ch.Metric = metric;
        var occupied = metric > _cwSquelch && ch.SigAvg > ch.NoiseFloor * 1.4;
        ch.Occupied = occupied;
        var upper = normSig + 0.1;
        var lower = normSig - 0.1;
        if (occupied)
        {
            if (value >= upper && ch.CwState != 1)
                Key(ch, 1);
            if (value < lower && ch.CwState == 1)
                Key(ch, 0);
        }
        if ((occupied || ch.CwState != 0) && Query(ch) is { } text)
        {
            ChannelText?.Invoke(ch.Index, (int)ch.Freq, text);
            ch.Timeout = ViewerTimeoutSec * NominalSampleRate;
        }
    }

    private static void Key(Channel ch, int down)
    {
        if (down == 1)
        {
            if (ch.CwState == 1) return;
            if (ch.CwState == 0) { ch.Smpl = 0; ch.Rep.Clear(); }
            ch.Start = ch.Smpl;
            ch.CwState = 1;
            return;
        }
        if (ch.CwState != 1) return;
        var dur = Math.Max(0, ch.Smpl - ch.Start);
        ch.End = ch.Smpl;
        if (dur < ch.TwoDots / 10) { ch.CwState = 0; return; }
        if (ch.LastEl > 0) UpdateTracking(ch, ch.LastEl, dur);
        ch.LastEl = dur;
        ch.Rep.Append(dur <= ch.TwoDots ? '.' : '-');
        if (ch.Rep.Length > 7) { ch.CwState = 0; ch.Rep.Clear(); return; }
        ch.CwState = 2;
    }

    private string? Query(Channel ch)
    {
        if (ch.CwState == 1) return null;
        var silence = Math.Max(0, ch.Smpl - ch.End);
        if (silence < ch.TwoDots) return null;
        if (silence < 2 * ch.TwoDots)
        {
            var code = Morse.GetValueOrDefault(ch.Rep.ToString(), "");
            ch.CwState = 0;
            ch.Rep.Clear();
            ch.SpaceSent = false;
            return string.IsNullOrEmpty(code) ? null : code;
        }
        if (!ch.SpaceSent)
        {
            ch.SpaceSent = true;
            return " ";
        }
        return null;
    }

    private static void UpdateTracking(Channel ch, int a, int b)
    {
        var minDot = (Kwpm / 60) / 2;
        var maxDash = 6 * Kwpm / 10;
        if (a > b && a > 4 * b) return;
        if (b > a && b > 4 * a) return;
        if (a < minDot || b < minDot) return;
        if (a > maxDash || b > maxDash) return;
        ch.TwoDots = (int)Decay(ch.TwoDots, (a + b) / 2.0, 16);
    }

    private static double Decay(double avg, double value, double weight) =>
        avg + (value - avg) / Math.Max(1, weight);

    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 1e-6;
        Array.Sort(values);
        var index = Math.Clamp((int)Math.Round((values.Length - 1) * p), 0, values.Length - 1);
        return values[index];
    }

    private static double[] DesignLowpass(int length, double fc)
    {
        var h = new double[length];
        var m = (length - 1) / 2.0;
        var sum = 0.0;
        for (var i = 0; i < length; i++)
        {
            var x = i - m;
            var sinc = Math.Abs(x) < 1e-9 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
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
        public double Freq, Phase, PhaseInc;
        public double[] Hist = new double[FilterLen * 2];
        public double Bit, SigAvg = 0.5, Agc, NoiseFloor = 1e-3, Metric;
        public int Dec, Smpl, Start, End, LastEl, TwoDots, Timeout, CwState;
        public bool SpaceSent = true, Occupied;
        public StringBuilder Rep = new();

        public Channel(int index, double freq, int twoDots)
        {
            Index = index;
            Freq = freq;
            PhaseInc = 2 * Math.PI * freq / NominalSampleRate;
            TwoDots = twoDots;
        }

        public void Reset()
        {
            SpaceSent = true;
            LastEl = CwState = Timeout = Dec = Smpl = Start = End = 0;
            Rep.Clear();
            Bit = 0;
            SigAvg = 0.5;
            Agc = 0;
            Metric = 0;
            Occupied = false;
        }
    }

    internal string DebugSnapshot()
    {
        var best = _ch.OrderByDescending(item => item.Metric).First();
        return $"wpm={_wpm} peak={best.Metric:F1}dB @{best.Freq:F0}Hz state={best.CwState} sig={best.SigAvg:E2} agc={best.Agc:E2} nf={best.NoiseFloor:E2} bit={best.Bit:E2}";
    }
}
