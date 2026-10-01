using System.Globalization;
using System.Text;
using NeuroSDR.Core;

namespace NeuroSDR.Plugins.Lte;

/// <summary>
/// LTE / PS-LTE IQ plugin.
/// Modes:
///   managed — C# PSS/SSS only (default deploy; no ens_lte.dll required)
///   hybrid  — managed SYNC + optional ens_lte MIB→SIB1 when DLL present
///   native  — ens_lte.dll only (experimental)
/// </summary>
internal sealed class LteIqPlugin : IIqPlugin
{
    public const string PluginId = "builtin.iq.lte";
    public const int MinimumSampleRate = 1_920_000;

    private const int MaxRows = 24;
    private const int StatusHoldMs = 2_000;
    private const int UiHoldMs = 750;

    private readonly object _sync = new();
    private readonly LteCellSearcher _managed = new();
    private IntPtr _native;
    private bool _nativeOk;
    private bool _nativeProbed;
    private string _nativeDetail = "";
    private bool _enabled;
    /// <summary>hybrid | native | managed</summary>
    private string _mode = "managed";
    private string _lastStatus = "";
    private long _nextStatusTick;
    private long _nextUiTick;
    private int _lastHintPci = -1;
    private float _lastHintPsr = -1;
    private int _lastHintHits;
    private int _lastRate;
    private long _lastCenterHz;
    private string _stickyManaged = "PSS idle";

    public IqPluginInfo Info { get; } = new(
        PluginId,
        "LTE / PS-LTE",
        "LTE SYNC (managed PSS/SSS). Optional ens_lte.dll for MIB→SIB1.",
        IqPluginCapabilities.IqInput | IqPluginCapabilities.Display | IqPluginCapabilities.HostResults);

    public event Action<IqPluginResult>? ResultAvailable;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        lock (_sync)
        {
            _enabled = Bool(options, "enabled", _enabled);
            var mode = Text(options, "mode", "");
            if (!string.IsNullOrEmpty(mode))
            {
                _mode = mode.Equals("native", StringComparison.OrdinalIgnoreCase) ? "native"
                    : mode.Equals("hybrid", StringComparison.OrdinalIgnoreCase) ? "hybrid"
                    : "managed";
                if (_mode != "managed" && _native == IntPtr.Zero)
                    _nativeProbed = false;
            }
            EnsureNative();
            var command = Text(options, "command", "");
            if (command.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                _managed.Reset();
                if (_native != IntPtr.Zero) NeuroLteNative.ens_lte_reset(_native);
                _lastHintPci = -1;
                _lastHintPsr = -1;
                _lastHintHits = 0;
                _stickyManaged = "cleared";
                EmitCells();
                MaybeStatus($"cleared · {_mode}", Environment.TickCount64, force: true);
            }
            else if (!string.IsNullOrEmpty(mode))
            {
                _managed.Reset();
                if (_native != IntPtr.Zero) NeuroLteNative.ens_lte_reset(_native);
                _lastHintPci = -1;
                MaybeStatus($"mode={_mode}", Environment.TickCount64, force: true);
                EmitCells();
            }
        }
    }

    private bool UseManaged => _mode is "hybrid" or "managed";
    private bool UseNative => _nativeOk && (_mode is "hybrid" or "native");

    private void EnsureNative()
    {
        // Managed-only deploy: never load ens_lte.dll.
        if (_mode == "managed") return;
        if (_native != IntPtr.Zero || _nativeProbed) return;
        _nativeProbed = true;
        HostNativeDllResolver.EnsureRegistered();
        if (!NeuroLteNative.TryProbe(out _nativeDetail))
        {
            _nativeOk = false;
            return;
        }
        _native = NeuroLteNative.ens_lte_create();
        _nativeOk = _native != IntPtr.Zero;
        if (!_nativeOk) _nativeDetail = "ens_lte_create failed";
    }

    public IqPluginResult? Process(IqSampleBlock block)
    {
        lock (_sync)
        {
            if (!_enabled || block.Samples.Length == 0) return null;
            EnsureNative();
            _lastRate = block.SampleRate;
            _lastCenterHz = block.CenterFrequencyHz;
            var now = Environment.TickCount64;

            if (block.SampleRate < MinimumSampleRate)
            {
                MaybeStatus(
                    $"Need ≥ {MinimumSampleRate / 1_000_000d:0.###} MS/s. Current {FormatRate(block.SampleRate)}.",
                    now);
                return null;
            }

            if (UseManaged)
            {
                _managed.Process(block.Samples.Span, block.SampleRate, now);
                UpdateStickyManaged();
            }
            else
                _stickyManaged = "managed off";

            string nativeSt = "";
            if (UseNative && _native != IntPtr.Zero)
            {
                // Hybrid: feed managed PCI hint. Native-only: rely on DLL cellsearch.
                if (_mode == "hybrid")
                {
                    var best = _managed.Cells.Values
                        .Where(c => c.PssPsr >= 1.85f)
                        .OrderByDescending(c => c.Hits)
                        .ThenByDescending(c => c.PssPsr)
                        .FirstOrDefault();
                    if (best is not null)
                    {
                        var better = best.Pci != _lastHintPci || best.PssPsr >= _lastHintPsr + 0.25f ||
                                     best.Hits >= _lastHintHits + 2;
                        if (better)
                        {
                            _lastHintPci = best.Pci;
                            _lastHintPsr = best.PssPsr;
                            _lastHintHits = best.Hits;
                            NeuroLteNative.ens_lte_hint_cell(_native, best.Pci, best.ExtendedCp ? 1 : 0, best.CfoHz);
                        }
                    }
                }

                var iq = NeuroLteNative.ToInterleaved(block.Samples.Span);
                NeuroLteNative.ens_lte_process_iq(_native, iq, block.Samples.Length, block.SampleRate,
                    block.CenterFrequencyHz);
                nativeSt = NeuroLteNative.StatusOf(_native);
            }

            MaybeStatus(BuildStatus(nativeSt), now);
            if (now >= _nextUiTick)
            {
                _nextUiTick = now + UiHoldMs;
                EmitCells();
            }
        }
        return null;
    }

    private void UpdateStickyManaged()
    {
        var best = _managed.Cells.Values
            .OrderByDescending(c => c.Hits)
            .ThenByDescending(c => c.PssPsr)
            .FirstOrDefault();
        if (best is not null)
        {
            _stickyManaged =
                $"PCI {best.Pci} hits={best.Hits} PSR={best.PssPsr:0.00} CFO={best.CfoHz:0.#} Hz";
            return;
        }
        if (!string.IsNullOrWhiteSpace(_managed.LastDetail))
            _stickyManaged = _managed.LastDetail;
    }

    private string BuildStatus(string nativeSt)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(_mode).Append("] ");
        if (UseManaged)
            sb.Append(_stickyManaged);
        else
            sb.Append("no managed");
        if (_nativeOk && UseNative)
        {
            sb.Append(" · ens_lte ");
            sb.Append(string.IsNullOrWhiteSpace(nativeSt) ? _nativeDetail : nativeSt);
        }
        else if (_mode != "managed" && !string.IsNullOrWhiteSpace(_nativeDetail))
        {
            sb.Append(" · no ens_lte (").Append(_nativeDetail).Append(')');
        }
        sb.Append(" · ").Append(FormatRate(_lastRate));
        sb.Append(" · ").Append((_lastCenterHz / 1_000_000d).ToString("0.000", CultureInfo.InvariantCulture)).Append(" MHz");
        if (UseManaged)
            sb.Append(" · cells ").Append(_managed.Cells.Count);
        return sb.ToString();
    }

    private void MaybeStatus(string status, long now, bool force = false)
    {
        if (!force)
        {
            if (now < _nextStatusTick) return;
            if (status == _lastStatus) return;
        }
        _lastStatus = status;
        _nextStatusTick = now + StatusHoldMs;
        EmitStatus(status);
    }

    private void EmitStatus(string text) =>
        ResultAvailable?.Invoke(new IqPluginResult(PluginId, "STATUS", text, DateTime.UtcNow));

    private void EmitCells()
    {
        var sb = new StringBuilder();
        var freqMhz = _lastCenterHz / 1_000_000d;
        var earfcn = LteEarfcn.FromFrequencyMhz(freqMhz);
        var band = earfcn >= 0 ? LteEarfcn.BandOf(earfcn) : null;

        var nativeByPci = new Dictionary<int, NeuroLteNative.Cell>();
        if (UseNative && _native != IntPtr.Zero)
        {
            var n = NeuroLteNative.ens_lte_cell_count(_native);
            for (var i = 0; i < n; i++)
            {
                if (NeuroLteNative.ens_lte_get_cell(_native, i, out var c) != 0) continue;
                if (c.Earfcn < 0) c.Earfcn = earfcn;
                if (c.FreqMhz <= 0) c.FreqMhz = freqMhz;
                nativeByPci[c.Pci] = c;
            }
        }

        var rows = 0;

        // Native-only UI: show DLL SYNC+ rows (PCI/CP/PSS/CFO — same class as pre-DLL managed).
        if (_mode == "native")
        {
            foreach (var kv in nativeByPci
                         .OrderByDescending(k => k.Value.Stage)
                         .ThenByDescending(k => k.Value.Hits)
                         .ThenBy(k => k.Key))
            {
                var c = kv.Value;
                var e = c.Earfcn >= 0 ? c.Earfcn : earfcn;
                var stage = c.Stage >= 3 ? "SIB1" : c.Stage == 2 ? "MIB" : "SYNC";
                AppendRow(sb, e, c.FreqMhz > 0 ? c.FreqMhz : freqMhz, c.Pci,
                    c.NofPrb > 0 ? c.NofPrb : 6, c.NofPorts, c.ExtendedCp != 0,
                    c.PssPowerDb, c.CfoHz, c.Tac, c.CellId, NeuroLteNative.FormatPlmn(c));
                var note = $"{stage}(ens_lte) hits={c.Hits}";
                if (band is not null) note += $" B{band}";
                if (c.PssPsr > 0) note += $" PSR={c.PssPsr:0.00}";
                if (c.Stage == 2)
                    note += $" · need ≥{Sib1RateMs(c.NofPrb):0.##} MS/s for MCC/MNC";
                sb.Append(note).Append('\n');
                rows++;
                if (rows >= MaxRows) break;
            }
            ResultAvailable?.Invoke(new IqPluginResult(PluginId, "CELLS", "", DateTime.UtcNow)
            {
                Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["rows"] = sb.ToString() }
            });
            return;
        }

        if (UseManaged)
        {
            foreach (var cell in _managed.Cells.Values
                         .OrderByDescending(c => c.Hits)
                         .ThenByDescending(c => c.PssPsr)
                         .ThenBy(c => c.Pci)
                         .Take(MaxRows))
            {
                var hasNative = nativeByPci.TryGetValue(cell.Pci, out var nat);
                var useNative = hasNative && (nat.Stage >= 2 || nat.PlmnCount > 0);
                var e = useNative && nat.Earfcn >= 0 ? nat.Earfcn : earfcn;
                var f = useNative && nat.FreqMhz > 0 ? nat.FreqMhz : freqMhz;
                var prb = useNative && nat.NofPrb > 0 ? nat.NofPrb : cell.NofPrb;
                var ports = useNative && nat.NofPorts > 0 ? nat.NofPorts : cell.NofPorts;
                var cp = useNative ? nat.ExtendedCp != 0 : cell.ExtendedCp;
                var pss = useNative && nat.PssPowerDb != 0 ? nat.PssPowerDb : cell.PssPowerDb;
                var cfo = useNative && nat.CfoHz != 0 ? nat.CfoHz : cell.CfoHz;
                var tac = useNative && !string.IsNullOrEmpty(nat.Tac) ? nat.Tac : cell.Tac;
                var cid = useNative && !string.IsNullOrEmpty(nat.CellId) ? nat.CellId : cell.CellIdentity;
                var plmn = useNative ? NeuroLteNative.FormatPlmn(nat) : cell.Plmn;
                if (string.IsNullOrEmpty(plmn)) plmn = cell.Plmn;

                var stage = useNative
                    ? nat.Stage >= 3 ? "SIB1" : nat.Stage == 2 ? "MIB" : "SYNC"
                    : "SYNC";

                AppendRow(sb, e, f, cell.Pci, prb, ports, cp, pss, cfo, tac, cid, plmn);
                var note = $"{stage} hits={cell.Hits}";
                if (band is not null) note += $" B{band}";
                if (useNative && nat.Stage == 2)
                    note += $" · need ≥{Sib1RateMs(nat.NofPrb):0.##} MS/s for MCC/MNC";
                else if (useNative && nat.Stage >= 3) note += " · SIB1";
                else if (_nativeOk && _mode == "hybrid" && cell.Pci == _lastHintPci) note += " · ens_lte track";
                sb.Append(note).Append('\n');
                rows++;
            }
        }

        if (_mode == "hybrid")
        {
            foreach (var kv in nativeByPci.OrderByDescending(k => k.Value.Stage).ThenByDescending(k => k.Value.Hits))
            {
                if (_managed.Cells.ContainsKey(kv.Key)) continue;
                var c = kv.Value;
                var e = c.Earfcn >= 0 ? c.Earfcn : earfcn;
                AppendRow(sb, e, c.FreqMhz > 0 ? c.FreqMhz : freqMhz, c.Pci,
                    c.NofPrb > 0 ? c.NofPrb : 6, c.NofPorts, c.ExtendedCp != 0, c.PssPowerDb, c.CfoHz,
                    c.Tac, c.CellId, NeuroLteNative.FormatPlmn(c));
            var stage = c.Stage >= 3 ? "SIB1" : c.Stage == 2 ? "MIB" : "SYNC";
            var note = $"{stage}(ens_lte) hits={c.Hits}";
            if (band is not null) note += $" B{band}";
            if (c.Stage == 2)
                note += $" · need ≥{Sib1RateMs(c.NofPrb):0.##} MS/s for MCC/MNC";
            sb.Append(note).Append('\n');
                rows++;
                if (rows >= MaxRows) break;
            }
        }

        ResultAvailable?.Invoke(new IqPluginResult(PluginId, "CELLS", "", DateTime.UtcNow)
        {
            Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["rows"] = sb.ToString() }
        });
    }

    private static double Sib1RateMs(int nofPrb) =>
        nofPrb switch
        {
            <= 6 => 1.92,
            <= 15 => 3.84,
            <= 25 => 7.68,
            <= 50 => 15.36,
            <= 75 => 23.04,
            _ => 30.72
        };

    private static void AppendRow(StringBuilder sb, int e, double f, int pci, int prb, int ports, bool cp,
        float pss, float cfo, string? tac, string? cid, string? plmn)
    {
        sb.Append(e >= 0 ? e.ToString(CultureInfo.InvariantCulture) : "").Append('\t');
        sb.Append(f.ToString("0.000", CultureInfo.InvariantCulture)).Append('\t');
        sb.Append(pci.ToString(CultureInfo.InvariantCulture)).Append('\t');
        sb.Append(prb.ToString(CultureInfo.InvariantCulture)).Append('\t');
        sb.Append(ports > 0 ? ports.ToString(CultureInfo.InvariantCulture) : "?").Append('\t');
        sb.Append(cp ? "E" : "N").Append('\t');
        sb.Append(pss.ToString("0.0", CultureInfo.InvariantCulture)).Append('\t');
        sb.Append(cfo.ToString("0.0", CultureInfo.InvariantCulture)).Append('\t');
        sb.Append(tac ?? "").Append('\t');
        sb.Append(cid ?? "").Append('\t');
        sb.Append(plmn ?? "").Append('\t');
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_native != IntPtr.Zero)
            {
                NeuroLteNative.ens_lte_destroy(_native);
                _native = IntPtr.Zero;
            }
        }
    }

    private static bool Bool(IReadOnlyDictionary<string, string> options, string key, bool fallback) =>
        options.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static string Text(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;

    private static string FormatRate(int sampleRate) =>
        sampleRate >= 1_000_000 ? $"{sampleRate / 1_000_000d:0.###} MS/s" : $"{sampleRate / 1_000d:0.#} ksps";
}
