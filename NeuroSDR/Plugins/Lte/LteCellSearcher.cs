using System.Numerics;
using NeuroSDR.Core;

namespace NeuroSDR.Plugins.Lte;

internal sealed class LteDetection
{
    public int Pci { get; init; }
    public int NId1 { get; init; }
    public int NId2 { get; init; }
    public bool ExtendedCp { get; set; }
    public float PssPsr { get; set; }
    public float PssPowerDb { get; set; }
    public float CfoHz { get; set; }
    public int Subframe { get; set; }
    public int Hits { get; set; }
    public long LastSeenTick { get; set; }
    /// <summary>0=PSS only, 1=PCI (PSS+SSS), 2=MIB, 3=SIB1/PLMN.</summary>
    public int BroadcastStage { get; set; } = 1;
    public int NofPrb { get; set; } = 6;
    public int NofPorts { get; set; }
    public string Tac { get; set; } = "";
    public string CellIdentity { get; set; } = "";
    public string Plmn { get; set; } = "";
}

/// <summary>
/// Streaming LTE cell search at 1.92 MS/s (6-PRB PSS/SSS), based on srsRAN cell_search / synch_file flow.
/// </summary>
internal sealed class LteCellSearcher
{
    private readonly LtePssDetector _pss = new();
    private readonly LteSssDetector _sss = new();
    private readonly Complex[] _ring = new Complex[LtePssDetector.FrameSamples * 2];
    private readonly Complex[] _frame = new Complex[LtePssDetector.FrameSamples];
    private readonly Dictionary<int, LteDetection> _cells = new();
    private int _ringWrite;
    private int _ringCount;
    private double _resamplePhase;
    private int _configuredRate = -1;
    private long _nextScanTick;
    private int _scans;

    public IReadOnlyDictionary<int, LteDetection> Cells => _cells;
    public int Scans => _scans;
    public string LastDetail { get; private set; } = "";

    public void Reset()
    {
        _ringWrite = 0;
        _ringCount = 0;
        _resamplePhase = 0;
        _configuredRate = -1;
        _cells.Clear();
        _scans = 0;
        LastDetail = "";
    }

    public void Process(ReadOnlySpan<Complex32> iq, int sampleRate, long nowTick)
    {
        if (sampleRate < LteIqPlugin.MinimumSampleRate) return;
        if (_configuredRate != sampleRate)
        {
            _configuredRate = sampleRate;
            _ringWrite = 0;
            _ringCount = 0;
            _resamplePhase = 0;
        }

        PushResampled(iq, sampleRate);

        // ~8 Hz scan budget
        if (nowTick < _nextScanTick) return;
        if (_ringCount < LtePssDetector.FrameSamples) return;
        _nextScanTick = nowTick + 120;

        CopyFrame();
        _scans++;
        ScanFrame(nowTick);
        Expire(nowTick);
    }

    private void PushResampled(ReadOnlySpan<Complex32> iq, int sampleRate)
    {
        if (sampleRate == LtePssDetector.SearchRate)
        {
            for (var i = 0; i < iq.Length; i++)
                Push(new Complex(iq[i].I, iq[i].Q));
            return;
        }

        var step = (double)LtePssDetector.SearchRate / sampleRate;
        for (var i = 0; i < iq.Length; i++)
        {
            _resamplePhase += step;
            while (_resamplePhase >= 1.0)
            {
                _resamplePhase -= 1.0;
                // Linear blend with next sample when available
                Complex sample;
                if (i + 1 < iq.Length)
                {
                    var a = iq[i];
                    var b = iq[i + 1];
                    var t = _resamplePhase; // residual after consume — use 0 for nearest
                    t = 0;
                    sample = new Complex(a.I * (1 - t) + b.I * t, a.Q * (1 - t) + b.Q * t);
                }
                else
                {
                    sample = new Complex(iq[i].I, iq[i].Q);
                }
                Push(sample);
            }
        }
    }

    private void Push(Complex sample)
    {
        _ring[_ringWrite] = sample;
        _ringWrite = (_ringWrite + 1) % _ring.Length;
        if (_ringCount < _ring.Length) _ringCount++;
    }

    private void CopyFrame()
    {
        var start = (_ringWrite - LtePssDetector.FrameSamples + _ring.Length) % _ring.Length;
        for (var i = 0; i < LtePssDetector.FrameSamples; i++)
            _frame[i] = _ring[(start + i) % _ring.Length];
    }

    private void ScanFrame(long nowTick)
    {
        if (!_pss.Find(_frame, out var nId2, out var peakPos, out var psr, out var peakAbs))
        {
            LastDetail = $"PSS none (scan #{_scans})";
            return;
        }

        var cfo = _pss.EstimateCfoHz(_frame, peakPos, nId2);
        var powerDb = 10f * MathF.Log10(Math.Max(peakAbs, 1e-20f));

        var decoded = false;
        var bestScore = 0f;
        var bestNId1 = -1;
        var bestSf = 0;
        var bestExt = false;

        foreach (var ext in new[] { false, true })
        {
            // peakPos = PSS start (matched-filter lag). srsRAN uses PSS end = start+FftSize.
            var sssStart = peakPos + LtePssDetector.FftSize - LteSssDetector.SssOffsetFromPssPeak(ext);
            if (sssStart < 0 || sssStart + LteSssDetector.FftSize > _frame.Length) continue;
            var window = _frame.AsSpan(sssStart, LteSssDetector.FftSize);
            if (!_sss.TryDecode(window, nId2, out var nId1, out var sf, out var score)) continue;
            if (score <= bestScore) continue;
            bestScore = score;
            bestNId1 = nId1;
            bestSf = sf;
            bestExt = ext;
            decoded = true;
        }

        if (!decoded)
        {
            LastDetail = $"PSS N_id_2={nId2} PSR={psr:0.00} · SSS fail @ {peakPos}";
            // Still record N_id_2-only hint as provisional PCI candidates are incomplete
            return;
        }

        var pci = bestNId1 * 3 + nId2;
        if (!_cells.TryGetValue(pci, out var cell))
        {
            cell = new LteDetection
            {
                Pci = pci,
                NId1 = bestNId1,
                NId2 = nId2,
                ExtendedCp = bestExt,
                PssPsr = psr,
                PssPowerDb = powerDb,
                CfoHz = cfo,
                Subframe = bestSf,
                Hits = 0
            };
            _cells[pci] = cell;
        }

        cell.Hits++;
        cell.LastSeenTick = nowTick;
        cell.PssPsr = psr;
        cell.PssPowerDb = powerDb;
        cell.CfoHz = cfo;
        cell.ExtendedCp = bestExt;
        cell.Subframe = bestSf;
        LastDetail = $"PCI {pci} (N_id_1={bestNId1} N_id_2={nId2}) PSR={psr:0.00} CFO={cfo:0.#} Hz";
    }

    private void Expire(long nowTick)
    {
        const long ttl = 15_000;
        var stale = _cells.Where(kv => nowTick - kv.Value.LastSeenTick > ttl).Select(kv => kv.Key).ToList();
        foreach (var key in stale) _cells.Remove(key);
    }

    /// <summary>Self-test helper: synthesize one 5 ms frame with PSS+SSS for a known PCI.</summary>
    public static Complex[] SynthesizeFrame(int pci, bool extendedCp = false)
    {
        var frame = new Complex[LtePssDetector.FrameSamples];
        var nId2 = pci % 3;
        var txPss = LtePssDetector.BuildTimeDomain(nId2);

        var sssFreq = LteSssDetector.GenerateSssFreq(pci, subframe5: false);
        var sssCentered = new Complex[LteSssDetector.FftSize];
        var start = (LteSssDetector.FftSize - 62) / 2;
        for (var i = 0; i < 62; i++) sssCentered[start + i] = sssFreq[i];
        var txSss = new Complex[LteSssDetector.FftSize];
        LteDft.InverseFromCentered(sssCentered, txSss);

        // Place PSS at pssStart. srsRAN peak_pos ≈ PSS end → SSS at end - 2*(sz+cp).
        var pssStart = 3_500;
        var sssStart = pssStart + LtePssDetector.FftSize - LteSssDetector.SssOffsetFromPssPeak(extendedCp);

        for (var i = 0; i < txPss.Length; i++)
            frame[pssStart + i] += txPss[i];
        for (var i = 0; i < txSss.Length; i++)
            if (sssStart + i >= 0) frame[sssStart + i] += txSss[i];
        return frame;
    }
}
