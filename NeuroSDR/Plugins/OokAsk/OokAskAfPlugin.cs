using System.Globalization;
using System.Text;
using NeuroSDR.Controls;
using NeuroSDR.Plugins;

namespace NeuroSDR.Plugins.OokAsk;

/// <summary>
/// AF OOK/ASK remote-burst analyzer for short keyfob-style pulses (~50–150 ms).
/// Always listens; RF noise gate only filters completed bursts (never arms after the signal).
/// </summary>
internal sealed class OokAskAfPlugin : IAfPlugin, IAfVisualPlugin, IAfRfSpectrumAware
{
    public const string PluginId = "builtin.af.ookask";

    private readonly object _sync = new();
    private readonly OokAskDecoder _decoder = new();
    private bool _enabled;
    private OokAskPluginView? _view;

    public AfPluginInfo Info { get; } = new(
        PluginId,
        "OOK / ASK Remote",
        "Decode amplitude-keyed remote bursts from AF (OOK/ASK pulse display).",
        AfPluginCapabilities.AudioInput | AfPluginCapabilities.Display | AfPluginCapabilities.HostResults,
        IsBuiltIn: true);

    public event Action<AfPluginResult>? ResultAvailable;

    public Control CreateView(IAfPluginUiHost ui)
    {
        _view = new OokAskPluginView();
        _view.OptionsChanged += () =>
        {
            var opts = _view.BuildOptions();
            opts["enabled"] = ui.IsRunning ? "true" : "false";
            ui.SaveOptions(opts);
            ui.Configure(opts);
        };
        var loaded = ui.LoadOptions();
        _view.LoadOptions(loaded);
        Configure(loaded);
        return _view;
    }

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = !options.TryGetValue("enabled", out var e) ||
                       !bool.TryParse(e, out var en) || en;
            _decoder.Configure(options);
        }
    }

    public void OnRfSpectrum(float[] spectrumDb, long centerHz, int sampleRateHz, long tunedHz, int channelBandwidthHz)
    {
        lock (_sync)
            _decoder.UpdateRfSpectrum(spectrumDb, centerHz, sampleRateHz, tunedHz, channelBandwidthHz);
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        if (!_enabled || block.Input.Length == 0) return null;
        OokAskDecode? hit;
        string status;
        lock (_sync)
        {
            hit = _decoder.Process(block.Input.Span, block.SampleRate, block.TimestampUtc);
            status = _decoder.TakeStatusUpdate();
        }

        if (!string.IsNullOrEmpty(status))
            ResultAvailable?.Invoke(new AfPluginResult(PluginId, "STATUS", status, block.TimestampUtc));

        if (hit is null) return null;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bits"] = hit.Bits,
            ["hex"] = hit.Hex,
            ["pulses"] = hit.PulseSummary,
            ["pulseSamples"] = hit.PulseSamplesCsv,
            ["sampleRate"] = hit.SampleRate.ToString(CultureInfo.InvariantCulture),
            ["baud"] = hit.BaudEstimate.ToString("0.#", CultureInfo.InvariantCulture),
            ["durationMs"] = hit.DurationMs.ToString("0.0", CultureInfo.InvariantCulture),
            ["signalDb"] = hit.SignalDb.ToString("0.0", CultureInfo.InvariantCulture),
            ["noiseDb"] = hit.NoiseDb.ToString("0.0", CultureInfo.InvariantCulture),
            ["unitSamples"] = hit.UnitSamples.ToString(CultureInfo.InvariantCulture)
        };
        var text = string.IsNullOrEmpty(hit.Hex)
            ? hit.Bits
            : $"{hit.Hex} · {hit.Bits.Length}b · {hit.DurationMs:0}ms";
        var result = new AfPluginResult(PluginId, "OOK_BURST", text, hit.Utc, Fields: fields);
        ResultAvailable?.Invoke(result);
        return result;
    }

    public void Dispose() { }
}

internal sealed class OokAskDecode
{
    public DateTime Utc { get; init; }
    public string Bits { get; init; } = "";
    public string Hex { get; init; } = "";
    public string PulseSummary { get; init; } = "";
    public string PulseSamplesCsv { get; init; } = "";
    public int SampleRate { get; init; }
    public int UnitSamples { get; init; }
    public double BaudEstimate { get; init; }
    public double DurationMs { get; init; }
    public float SignalDb { get; init; }
    public float NoiseDb { get; init; }
}

internal sealed class OokAskDecoder
{
    // ~80 ms AF pre-roll so a short chirp is already buffered when it crosses threshold.
    private const int PrerollMs = 80;
    private float[] _preroll = new float[48_000 * PrerollMs / 1000];
    private int _prerollWrite;
    private int _prerollCount;

    private float _noise = 0.01f;
    private bool _high;
    private int _run;
    private readonly List<int> _widths = new(256);
    private int _rate = 48_000;
    /// <summary>0 = most sensitive, 1 = least. Maps to +1…+24 dB above AF noise floor.</summary>
    private float _threshold01 = 0.40f;
    private long _idleSamples;
    private bool _inBurst;
    private bool _useRfNoiseGate;
    private float _noiseMarginDb = 6f;
    private float _noiseSpanHz = 100_000f;
    private float _rfNoiseDb = -120f;
    private float _rfSignalDb = -140f;
    private float _burstMaxRfDb = -140f;
    private long _statusTick;
    private int _livePulses;
    private float _lastThrOn;
    private float _lastDbAbove;

    public string StatusLine { get; private set; } = "";
    private string _pendingStatus = "";

    public string TakeStatusUpdate()
    {
        var s = _pendingStatus;
        _pendingStatus = "";
        return s;
    }

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("threshold", out var t) &&
            float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var thr))
            _threshold01 = Math.Clamp(thr, 0f, 1f);

        if (options.TryGetValue("rfNoiseGate", out var g) && bool.TryParse(g, out var gate))
            _useRfNoiseGate = gate;

        if (options.TryGetValue("noiseMarginDb", out var m) &&
            float.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out var margin))
            _noiseMarginDb = Math.Clamp(margin, 2f, 40f);

        if (options.TryGetValue("noiseSpanKHz", out var s) &&
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var spanKhz))
            _noiseSpanHz = Math.Clamp(spanKhz, 25f, 500f) * 1_000f;
    }

    public void UpdateRfSpectrum(float[] spectrumDb, long centerHz, int sampleRateHz, long tunedHz, int channelBandwidthHz)
    {
        if (spectrumDb.Length < 16 || sampleRateHz <= 0) return;

        var halfBw = Math.Max(channelBandwidthHz, 2_000) / 2L;
        var captureLeft = centerHz - sampleRateHz / 2d;
        var binHz = sampleRateHz / (double)spectrumDb.Length;

        float PeakIn(long lowHz, long highHz)
        {
            var first = Math.Clamp((int)Math.Floor((lowHz - captureLeft) / binHz), 0, spectrumDb.Length - 1);
            var last = Math.Clamp((int)Math.Ceiling((highHz - captureLeft) / binHz), first, spectrumDb.Length - 1);
            var peak = -140f;
            for (var i = first; i <= last; i++)
                peak = Math.Max(peak, spectrumDb[i]);
            return peak;
        }

        float AverageOutsideChannel(long spanHz)
        {
            var excludeLow = tunedHz - halfBw;
            var excludeHigh = tunedHz + halfBw;
            var leftLow = tunedHz - spanHz;
            var rightHigh = tunedHz + spanHz;
            var first = Math.Clamp((int)Math.Floor((leftLow - captureLeft) / binHz), 0, spectrumDb.Length - 1);
            var last = Math.Clamp((int)Math.Ceiling((rightHigh - captureLeft) / binHz), first, spectrumDb.Length - 1);
            var exFirst = Math.Clamp((int)Math.Floor((excludeLow - captureLeft) / binHz), 0, spectrumDb.Length - 1);
            var exLast = Math.Clamp((int)Math.Ceiling((excludeHigh - captureLeft) / binHz), exFirst, spectrumDb.Length - 1);

            double sum = 0;
            var count = 0;
            for (var i = first; i <= last; i++)
            {
                if (i >= exFirst && i <= exLast) continue;
                sum += spectrumDb[i];
                count++;
            }
            return count > 0 ? (float)(sum / count) : -120f;
        }

        _rfSignalDb = PeakIn(tunedHz - halfBw, tunedHz + halfBw);
        _rfNoiseDb = AverageOutsideChannel((long)_noiseSpanHz);
        if (_inBurst)
            _burstMaxRfDb = Math.Max(_burstMaxRfDb, _rfSignalDb);

        var now = Environment.TickCount64;
        if (now - _statusTick >= 350)
        {
            _statusTick = now;
            var gateNote = _useRfNoiseGate
                ? $"RF filter · peak {_burstMaxRfDb:0.0}/{_rfSignalDb:0.0} · noise {_rfNoiseDb:0.0} · Δ{_noiseMarginDb:0}"
                : "AF always-on";
            StatusLine =
                $"thr {(int)Math.Round(_threshold01 * 100)}% (+{_lastDbAbove:0.0} dB) · pulses {_livePulses} · {gateNote}";
            _pendingStatus = StatusLine;
        }
    }

    /// <summary>
    /// Threshold is ONLY vs AF noise floor (dB above noise). Peak is never used —
    /// peak-chasing caused the all-or-nothing ping-pong.
    /// 0% → +1 dB, 100% → +24 dB above noise, with 2 dB hysteresis.
    /// </summary>
    private void ComputeThreshold(out float thrOn, out float thrOff, out float dbAbove)
    {
        var noiseFloor = Math.Max(1e-6f, _noise);
        dbAbove = 1f + _threshold01 * 23f;
        thrOn = noiseFloor * MathF.Pow(10f, dbAbove / 20f);
        thrOff = thrOn * MathF.Pow(10f, -2f / 20f);
        _lastThrOn = thrOn;
        _lastDbAbove = dbAbove;
    }

    public OokAskDecode? Process(ReadOnlySpan<float> pcm, int sampleRate, DateTime utc)
    {
        if (sampleRate > 0 && sampleRate != _rate)
        {
            _rate = sampleRate;
            EnsurePrerollSize();
        }

        OokAskDecode? finished = null;
        ComputeThreshold(out var thrOn, out var thrOff, out _);

        for (var i = 0; i < pcm.Length; i++)
        {
            var a = MathF.Abs(pcm[i]);
            PushPreroll(a);

            // Update noise only from quiet samples (below off-threshold), never from peaks.
            if (!_inBurst && a < thrOff)
                _noise += (a - _noise) * 0.0035f;
            else if (!_inBurst && a < thrOn)
                _noise += (a - _noise) * 0.0004f;

            // Recompute occasionally so slider moves apply mid-block; noise drifts slowly.
            if ((i & 63) == 0)
                ComputeThreshold(out thrOn, out thrOff, out _);

            // Hysteresis: enter high at thrOn, leave at thrOff.
            var on = _high ? a >= thrOff : a >= thrOn;

            if (!_inBurst && on)
            {
                BeginBurstFromPreroll(thrOn, thrOff);
                _inBurst = true;
                _burstMaxRfDb = _rfSignalDb;
                _livePulses = _widths.Count;
            }

            if (on == _high)
            {
                _run++;
                _idleSamples = on ? 0 : _idleSamples + 1;
            }
            else
            {
                if (_run > 1)
                {
                    _widths.Add(_high ? _run : -_run);
                    _livePulses = _widths.Count;
                }
                _high = on;
                _run = 1;
                _idleSamples = 0;
            }

            var endIdle = Math.Max(8, _rate * 12 / 1000);
            if (_inBurst && !_high && _idleSamples > endIdle && _widths.Count >= 3)
            {
                finished = FinalizeBurst(utc);
                ResetBurstState();
                ComputeThreshold(out thrOn, out thrOff, out _);
            }

            if (_widths.Count > 500)
            {
                finished ??= FinalizeBurst(utc);
                ResetBurstState();
            }
        }

        return finished;
    }

    private void EnsurePrerollSize()
    {
        var need = Math.Max(256, _rate * PrerollMs / 1000);
        if (_preroll.Length == need) return;
        _preroll = new float[need];
        _prerollWrite = 0;
        _prerollCount = 0;
    }

    private void PushPreroll(float a)
    {
        if (_preroll.Length == 0) EnsurePrerollSize();
        _preroll[_prerollWrite] = a;
        _prerollWrite = (_prerollWrite + 1) % _preroll.Length;
        if (_prerollCount < _preroll.Length) _prerollCount++;
    }

    private void BeginBurstFromPreroll(float thrOn, float thrOff)
    {
        _widths.Clear();
        _high = false;
        _run = 0;
        _idleSamples = 0;
        if (_prerollCount < 8) return;

        var n = _prerollCount;
        var start = (_prerollWrite - n + _preroll.Length) % _preroll.Length;
        var high = false;
        var run = 0;
        for (var i = 0; i < n; i++)
        {
            var a = _preroll[(start + i) % _preroll.Length];
            var on = high ? a >= thrOff : a >= thrOn;
            if (on == high) run++;
            else
            {
                if (run > 1) _widths.Add(high ? run : -run);
                high = on;
                run = 1;
            }
        }
        _high = high;
        _run = Math.Max(1, run);
    }

    private void ResetBurstState()
    {
        _widths.Clear();
        _high = false;
        _run = 0;
        _idleSamples = 0;
        _inBurst = false;
        _livePulses = 0;
        _burstMaxRfDb = -140f;
    }

    private OokAskDecode? FinalizeBurst(DateTime utc)
    {
        if (_run > 1)
            _widths.Add(_high ? _run : -_run);

        if (_widths.Count < 3) return null;
        var total = _widths.Sum(Math.Abs);
        // ~2 ms minimum at 48 kHz — keep short chirps.
        if (total < Math.Max(48, _rate / 500)) return null;

        // RF gate validates the burst that was already decoded (peak during burst).
        var sig = Math.Max(_burstMaxRfDb, _rfSignalDb);
        if (_useRfNoiseGate && sig < _rfNoiseDb + _noiseMarginDb)
            return null;

        var abs = _widths.Select(Math.Abs).OrderBy(v => v).ToArray();
        var unit = Math.Max(1, abs[abs.Length / 2]);
        var bits = new StringBuilder();
        foreach (var w in _widths)
        {
            var q = Math.Clamp((int)Math.Round(Math.Abs(w) / (double)unit), 1, 8);
            bits.Append(w > 0 ? '1' : '0', q);
        }
        var bitStr = bits.ToString();
        if (bitStr.Length < 4) return null;

        var ones = bitStr.Count(static c => c == '1');
        if (ones < 2) return null;

        var hex = ToHex(bitStr);
        if (hex.Length >= 2 && hex.All(static c => c == '0') && ones < 3) return null;

        var marks = _widths.Where(w => w > 0).Select(Math.Abs).DefaultIfEmpty(unit).Average();
        var baud = _rate / Math.Max(1.0, marks);
        var pulse = string.Join(" ", _widths.Take(48).Select(w =>
            $"{(w > 0 ? "H" : "L")}{Math.Abs(w) * 1000.0 / _rate:0.#}"));
        var pulseCsv = string.Join(",", _widths);

        return new OokAskDecode
        {
            Utc = utc,
            Bits = bitStr,
            Hex = hex,
            PulseSummary = pulse,
            PulseSamplesCsv = pulseCsv,
            SampleRate = _rate,
            UnitSamples = unit,
            BaudEstimate = baud,
            DurationMs = total * 1000.0 / _rate,
            SignalDb = sig,
            NoiseDb = _rfNoiseDb
        };
    }

    private static string ToHex(string bits)
    {
        var sb = new StringBuilder();
        var n = bits.Length - bits.Length % 8;
        for (var i = 0; i + 8 <= n && i < 64; i += 8)
        {
            var v = 0;
            for (var b = 0; b < 8; b++)
                v = (v << 1) | (bits[i + b] == '1' ? 1 : 0);
            sb.Append(v.ToString("X2"));
        }
        // Odd leftover nibble for very short packets.
        if (n == 0 && bits.Length > 0)
        {
            var v = 0;
            for (var b = 0; b < bits.Length && b < 8; b++)
                v = (v << 1) | (bits[b] == '1' ? 1 : 0);
            sb.Append(v.ToString("X2"));
        }
        return sb.ToString();
    }
}
