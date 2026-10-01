using System.Globalization;

namespace NeuroSDR.Dsp;

/// <summary>
/// EIA/NATO CTCSS detector. Band-limits AF (~40–280 Hz), then runs a Goertzel bank.
/// Uses a ≥600 ms window and peak-vs-median scoring so open-squelch FM noise does not lock.
/// </summary>
internal sealed class CtcssToneDecoder
{
    public static readonly float[] StandardTonesHz =
    [
        67.0f, 69.3f, 71.9f, 74.4f, 77.0f, 79.7f, 82.5f, 85.4f, 88.5f, 91.5f,
        94.8f, 97.4f, 100.0f, 103.5f, 107.2f, 110.9f, 114.8f, 118.8f, 123.0f, 127.3f,
        131.8f, 136.5f, 141.3f, 146.2f, 151.4f, 156.7f, 159.8f, 162.2f, 165.5f, 167.9f,
        171.3f, 173.8f, 177.3f, 179.9f, 183.5f, 186.2f, 189.9f, 192.8f, 196.6f, 199.5f,
        203.5f, 206.5f, 210.7f, 218.1f, 225.7f, 229.1f, 233.6f, 241.8f, 250.3f, 254.1f
    ];

    private readonly object _sync = new();
    private readonly double[] _coeff;
    private readonly double[] _q1;
    private readonly double[] _q2;
    private readonly double[] _mag;
    private readonly float[] _staging;
    private float _hpX, _hpY, _lpY;
    private float _hpA, _lpA;
    private int _stagingCount;
    private int _sampleRate = AudioDemodulator.AudioSampleRate;
    private int _blockSamples;
    private int _filterConfiguredRate = -1;
    private int _stableHits;
    private float _candidateHz;
    private float _detectedHz;
    private float _bestMagnitude;
    private float _bestSnr;
    private float _bestPeakRatio;
    private float _bandRms;
    private float _debugBestHz;

    public CtcssToneDecoder()
    {
        var n = StandardTonesHz.Length;
        _coeff = new double[n];
        _q1 = new double[n];
        _q2 = new double[n];
        _mag = new double[n];
        _staging = new float[AudioDemodulator.AudioSampleRate * 3];
        // 600 ms — main lobe narrow enough to separate 67.0 from 69.3.
        _blockSamples = AudioDemodulator.AudioSampleRate * 3 / 5;
        RebuildCoefficients();
    }

    public float DetectedToneHz { get { lock (_sync) return _detectedHz; } }
    public float DebugBandRms { get { lock (_sync) return _bandRms; } }
    public float DebugBestSnr { get { lock (_sync) return _bestSnr; } }
    public float DebugBestHz { get { lock (_sync) return _debugBestHz; } }
    public float DebugPeakRatio { get { lock (_sync) return _bestPeakRatio; } }

    public string DetectedLabel
    {
        get
        {
            var hz = DetectedToneHz;
            return hz > 0 ? hz.ToString("0.0", CultureInfo.InvariantCulture) : "";
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            Array.Clear(_q1);
            Array.Clear(_q2);
            Array.Clear(_mag);
            _stagingCount = 0;
            _stableHits = 0;
            _candidateHz = 0;
            _detectedHz = 0;
            _bestMagnitude = 0;
            _bestSnr = 0;
            _bestPeakRatio = 0;
            _bandRms = 0;
            _debugBestHz = 0;
            _hpX = _hpY = _lpY = 0;
            _filterConfiguredRate = -1;
        }
    }

    public void Process(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (mono.Length == 0 || sampleRate < 8_000) return;
        lock (_sync)
        {
            if (sampleRate != _sampleRate)
            {
                _sampleRate = sampleRate;
                _blockSamples = Math.Clamp(sampleRate * 3 / 5, sampleRate / 2, sampleRate);
                RebuildCoefficients();
                Array.Clear(_q1);
                Array.Clear(_q2);
                _stagingCount = 0;
                _filterConfiguredRate = -1;
            }
            EnsureFilters();

            foreach (var raw in mono)
            {
                var hp = _hpA * (_hpY + raw - _hpX);
                _hpX = raw;
                _hpY = hp;
                _lpY += _lpA * (hp - _lpY);
                var filtered = _lpY;
                if (_stagingCount >= _staging.Length)
                {
                    Array.Copy(_staging, _blockSamples / 2, _staging, 0, _staging.Length - _blockSamples / 2);
                    _stagingCount = _staging.Length - _blockSamples / 2;
                }
                _staging[_stagingCount++] = filtered;

                while (_stagingCount >= _blockSamples)
                {
                    AnalyzeBlock(_staging.AsSpan(0, _blockSamples));
                    var keep = _blockSamples / 2;
                    var remain = _stagingCount - keep;
                    Array.Copy(_staging, keep, _staging, 0, remain);
                    _stagingCount = remain;
                }
            }
        }
    }

    private void EnsureFilters()
    {
        if (_filterConfiguredRate == _sampleRate) return;
        _filterConfiguredRate = _sampleRate;
        _hpA = MathF.Exp(-2 * MathF.PI * 35f / _sampleRate);
        _lpA = 1f - MathF.Exp(-2 * MathF.PI * 280f / _sampleRate);
        _hpX = _hpY = _lpY = 0;
    }

    private void RebuildCoefficients()
    {
        for (var i = 0; i < StandardTonesHz.Length; i++)
        {
            var k = 2.0 * Math.PI * StandardTonesHz[i] / _sampleRate;
            _coeff[i] = 2.0 * Math.Cos(k);
        }
    }

    private void AnalyzeBlock(ReadOnlySpan<float> block)
    {
        double energy = 0;
        for (var n = 0; n < block.Length; n++) energy += block[n] * block[n];
        var rms = Math.Sqrt(energy / Math.Max(1, block.Length));
        _bandRms = (float)rms;
        if (rms < 5e-6)
        {
            _debugBestHz = 0;
            RegisterCandidate(0, 0, 0, 0);
            return;
        }

        var gain = (float)Math.Clamp(0.25 / rms, 1f, 120f);

        Array.Clear(_q1);
        Array.Clear(_q2);
        var nSamples = block.Length;
        for (var n = 0; n < nSamples; n++)
        {
            var window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / Math.Max(1, nSamples - 1));
            var sample = block[n] * gain * (float)window;
            for (var i = 0; i < _coeff.Length; i++)
            {
                var q0 = _coeff[i] * _q1[i] - _q2[i] + sample;
                _q2[i] = _q1[i];
                _q1[i] = q0;
            }
        }

        var best = -1;
        var bestMag = 0.0;
        var second = 0.0;
        for (var i = 0; i < _coeff.Length; i++)
        {
            var mag = _q1[i] * _q1[i] + _q2[i] * _q2[i] - _coeff[i] * _q1[i] * _q2[i];
            if (mag < 0) mag = 0;
            _mag[i] = mag;
            if (mag > bestMag)
            {
                second = bestMag;
                bestMag = mag;
                best = i;
            }
            else if (mag > second) second = mag;
        }

        _debugBestHz = best >= 0 ? StandardTonesHz[best] : 0;

        // Median of tone powers — open FM noise is flat; a real PL is a spike.
        var sorted = (double[])_mag.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        var peakRatio = median > 1e-12 ? bestMag / median : 100;

        var neighbor = 0.0;
        if (best > 0) neighbor = Math.Max(neighbor, _mag[best - 1]);
        if (best >= 0 && best + 1 < _mag.Length) neighbor = Math.Max(neighbor, _mag[best + 1]);

        var snr = second > 1e-12 ? bestMag / second : 100;
        var neighborSnr = neighbor > 1e-12 ? bestMag / neighbor : 100;
        _bestMagnitude = (float)bestMag;
        _bestSnr = (float)snr;
        _bestPeakRatio = (float)peakRatio;

        // Noise/DCS: peakRatio can spike on digital lines — demand strong tonal lock.
        if (best < 0 || snr < 2.8 || neighborSnr < 1.7 || peakRatio < 6.0)
        {
            RegisterCandidate(0, (float)bestMag, (float)snr, (float)peakRatio);
            return;
        }

        RegisterCandidate(StandardTonesHz[best], (float)bestMag, (float)snr, (float)peakRatio);
    }

    private void RegisterCandidate(float hz, float mag, float snr, float peakRatio)
    {
        _bestMagnitude = mag;
        _bestSnr = snr;
        _bestPeakRatio = peakRatio;

        if (_detectedHz > 0 && hz == 0)
        {
            _stableHits++;
            if (_stableHits < 4) return;
            _detectedHz = 0;
            _candidateHz = 0;
            _stableHits = 0;
            return;
        }

        if (Math.Abs(hz - _candidateHz) < 0.2f || (hz == 0 && _candidateHz == 0))
            _stableHits++;
        else
        {
            _candidateHz = hz;
            _stableHits = 1;
        }

        // 3× overlapping 600 ms ≈ 1.2 s before lock — rejects DCS spectral wander.
        if (hz <= 0 || _stableHits < 3) return;
        if (Math.Abs(_detectedHz - _candidateHz) < 0.2f) return;
        _detectedHz = _candidateHz;
    }
}
