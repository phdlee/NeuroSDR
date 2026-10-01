using System.Globalization;
using System.Text;
using NeuroSDR.Core;

namespace NeuroSDR.Plugins.Dump1090;

/// <summary>
/// dump1090/libmodes ADS-B (1090 MHz Mode S ES) IQ plug-in.
/// Requires ~2 MS/s timing; faster captures are decimated to 2 MS/s.
/// 978 MHz UAT is a different protocol and is not decoded.
/// </summary>
internal sealed class AdsbIqPlugin : IIqPlugin
{
    public const string PluginId = "builtin.iq.adsb";
    public const long ModeSFrequencyHz = 1_090_000_000;

    private readonly object _sync = new();
    private readonly ModeSDecoder _decoder = new();
    private readonly AdsbAircraftTracker _tracker = new();
    private readonly List<ushort> _magOverlap = [];
    private readonly List<ushort> _resampleCarry = [];
    private bool _enabled;
    private int _configuredRate = -1;
    private float _peak = 0.2f;
    private int _decoded;
    private string _lastStatus = "";
    private long _nextStatusTick;
    private double _resamplePhase;

    public IqPluginInfo Info { get; } = new(
        PluginId,
        "ADS-B 1090ES",
        "dump1090/libmodes Mode S ES decoder (1090 MHz, ~2 MS/s IQ)",
        IqPluginCapabilities.IqInput | IqPluginCapabilities.Display | IqPluginCapabilities.HostResults);

    public event Action<IqPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            var command = Text(options, "command", "");
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _decoder.Reset();
                _tracker.Clear();
                _magOverlap.Clear();
                _resampleCarry.Clear();
                _decoded = 0;
                _resamplePhase = 0;
                EmitFleet();
                EmitStatus("cleared");
            }
        }
    }

    public IqPluginResult? Process(IqSampleBlock block)
    {
        List<ModeSMessage>? messages = null;
        string? status = null;
        lock (_sync)
        {
            if (!_enabled || block.Samples.Length == 0) return null;
            if (_configuredRate != block.SampleRate)
            {
                _configuredRate = block.SampleRate;
                _magOverlap.Clear();
                _resampleCarry.Clear();
                _resamplePhase = 0;
            }

            if (block.SampleRate < 1_500_000)
            {
                status = $"Need ≥ 2 MS/s (dump1090 timing). Current {FormatRate(block.SampleRate)}.";
            }
            else
            {
                var span = block.Samples.Span;
                var blockPeak = ModeSDecoder.PeakAbs(span);
                _peak = _peak * 0.95f + blockPeak * 0.05f;
                if (blockPeak > _peak) _peak = blockPeak;
                var mag = new ushort[span.Length];
                var magLen = ModeSDecoder.MagnitudeFromIq(span, mag, Math.Max(_peak, 0.05f));
                var twoMs = ResampleToTwoMs(mag.AsSpan(0, magLen), block.SampleRate);
                if (twoMs.Length > 0)
                {
                    var combined = new ushort[_magOverlap.Count + twoMs.Length];
                    _magOverlap.CopyTo(combined);
                    twoMs.CopyTo(combined.AsSpan(_magOverlap.Count));
                    messages = [];
                    _decoder.Detect(combined, combined.Length, mm =>
                    {
                        if (!mm.CrcOk) return;
                        _decoded++;
                        _tracker.Apply(mm, block.TimestampUtc);
                        messages.Add(mm);
                    });
                    var keep = Math.Min(ModeSDecoder.FullLen * 2, combined.Length);
                    _magOverlap.Clear();
                    _magOverlap.AddRange(combined.AsSpan(combined.Length - keep).ToArray());
                }

                var halfSpan = block.SampleRate / 2.0;
                var inBand = Math.Abs(block.CenterFrequencyHz - ModeSFrequencyHz) <= halfSpan + 50_000;
                var aircraft = _tracker.Snapshot().Count;
                status = inBand
                    ? $"1090 ES @ {FormatRate(block.SampleRate)} → 2.00 MS/s · {aircraft} ac · {_decoded} msg"
                    : $"Tune RF near 1090.000 MHz (Mode S ES). Center {block.CenterFrequencyHz / 1_000_000.0:F3} MHz · {aircraft} ac";
            }
        }

        if (messages is { Count: > 0 }) EmitFleet();
        var now = Environment.TickCount64;
        if (status is not null && (status != _lastStatus || now >= _nextStatusTick))
        {
            _lastStatus = status;
            _nextStatusTick = now + 400;
            EmitStatus(status);
        }
        return null;
    }

    public void Dispose()
    {
        lock (_sync) _enabled = false;
    }

    internal AdsbAircraft[] PeekAircraft()
    {
        lock (_sync) return _tracker.Snapshot().ToArray();
    }

    private ushort[] ResampleToTwoMs(ReadOnlySpan<ushort> mag, int sampleRate)
    {
        if (sampleRate == ModeSDecoder.TargetSampleRate) return mag.ToArray();

        var carryCount = _resampleCarry.Count;
        var total = carryCount + mag.Length;
        var combined = new ushort[total];
        _resampleCarry.CopyTo(combined);
        mag.CopyTo(combined.AsSpan(carryCount));

        if (sampleRate > ModeSDecoder.TargetSampleRate && sampleRate % ModeSDecoder.TargetSampleRate == 0)
        {
            var factor = sampleRate / ModeSDecoder.TargetSampleRate;
            var outLen = total / factor;
            var output = new ushort[outLen];
            var src = 0;
            for (var o = 0; o < outLen; o++)
            {
                var sum = 0;
                for (var k = 0; k < factor; k++) sum += combined[src++];
                output[o] = (ushort)(sum / factor);
            }
            _resampleCarry.Clear();
            if (src < total) _resampleCarry.AddRange(combined.AsSpan(src).ToArray());
            return output;
        }

        var produced = new List<ushort>(Math.Max(8, total));
        var step = (double)sampleRate / ModeSDecoder.TargetSampleRate;
        var pos = _resamplePhase;
        while (pos + 1 < total)
        {
            var i0 = (int)pos;
            var frac = pos - i0;
            var a = combined[i0];
            var b = combined[Math.Min(i0 + 1, total - 1)];
            produced.Add((ushort)(a + (b - a) * frac));
            pos += step;
        }
        var keepFrom = Math.Max(0, (int)pos);
        _resampleCarry.Clear();
        if (keepFrom < total) _resampleCarry.AddRange(combined.AsSpan(keepFrom).ToArray());
        _resamplePhase = pos - keepFrom;
        return produced.ToArray();
    }

    private void EmitFleet()
    {
        var rows = new StringBuilder();
        foreach (var ac in _tracker.Snapshot())
        {
            var age = Math.Max(0, (int)(DateTime.UtcNow - ac.LastUtc).TotalSeconds);
            rows.Append(ac.Icao).Append('\t')
                .Append(ac.Flight).Append('\t')
                .Append(ac.AltitudeFt?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\t')
                .Append(ac.SpeedKt?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\t')
                .Append(ac.Heading?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\t')
                .Append(ac.Squawk is { } sq ? sq.ToString("0000", CultureInfo.InvariantCulture) : "").Append('\t')
                .Append(ac.Latitude is { } lat ? lat.ToString("F4", CultureInfo.InvariantCulture) : "").Append('\t')
                .Append(ac.Longitude is { } lon ? lon.ToString("F4", CultureInfo.InvariantCulture) : "").Append('\t')
                .Append(age.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(ac.Messages.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        ResultAvailable?.Invoke(new IqPluginResult(Info.Id, "FLEET",
            $"{_tracker.Snapshot().Count} aircraft", DateTime.UtcNow,
            Fields: new Dictionary<string, string> { ["rows"] = rows.ToString(), ["decoded"] = _decoded.ToString(CultureInfo.InvariantCulture) }));
    }

    private void EmitStatus(string text) =>
        ResultAvailable?.Invoke(new IqPluginResult(Info.Id, "STATUS", text, DateTime.UtcNow));

    private static string FormatRate(int sampleRate) =>
        sampleRate >= 1_000_000
            ? $"{sampleRate / 1_000_000.0:F2} MS/s"
            : $"{sampleRate / 1_000.0:F1} kS/s";

    private static bool Bool(IReadOnlyDictionary<string, string> o, string k, bool f) =>
        o.TryGetValue(k, out var t) && bool.TryParse(t, out var v) ? v : f;

    private static string Text(IReadOnlyDictionary<string, string> o, string k, string f) =>
        o.TryGetValue(k, out var t) ? t : f;
}
