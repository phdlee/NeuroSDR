namespace NeuroSDR.Plugins.Caption;

/// <summary>
/// Cheap speech gate for radio AF. Sensitivity 1 = almost any audio, 10 = only clear close-mic speech.
/// AM broadcasts are continuous and relatively loud, so the noise floor must not track into the program.
/// </summary>
internal sealed class SpeechActivityGate
{
    public bool Enabled { get; set; }

    public int Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = Math.Clamp(value, 1, 10);
    }

    private int _sensitivity = 5;
    private float _noise = 0.01f;
    private float _lpSlow, _lpFast;
    private int _speechBlocks;
    private int _hangBlocks;
    private bool _inUtterance;

    public bool Observe(ReadOnlySpan<float> block, int sampleRate)
    {
        if (!Enabled) return true;
        if (block.Length < 32 || sampleRate < 4_000) return false;
        double sum = 0, band = 0;
        var crossings = 0;
        var prev = block[0];
        var dt = 1f / sampleRate;
        var aSlow = dt / (dt + 1f / (2f * MathF.PI * 300f));
        var aFast = dt / (dt + 1f / (2f * MathF.PI * 3400f));
        for (var i = 0; i < block.Length; i++)
        {
            var x = block[i];
            sum += x * x;
            if ((x >= 0 && prev < 0) || (x < 0 && prev >= 0)) crossings++;
            prev = x;
            _lpSlow += aSlow * (x - _lpSlow);
            _lpFast += aFast * (x - _lpFast);
            var speechBand = _lpFast - _lpSlow;
            band += speechBand * speechBand;
        }

        var rms = (float)Math.Sqrt(sum / block.Length);
        var zcr = crossings / (float)block.Length;
        var bandRms = (float)Math.Sqrt(band / block.Length);
        var bandRatio = rms > 1e-6f ? bandRms / rms : 0f;

        var delta = _sensitivity - 5;
        var rmsFloor = 0.010f * MathF.Pow(1.16f, delta);
        var noiseMult = 2.1f * MathF.Pow(1.10f, delta);
        var bandMin = 0.10f * MathF.Pow(1.12f, delta);
        var quietFloor = 0.006f * MathF.Pow(1.12f, delta);
        if (_sensitivity <= 2 && rms >= 0.0025f)
            return true;

        var quiet = rms < Math.Max(quietFloor, _noise * 1.35f);
        if (quiet)
            _noise += 0.04f * (rms - _noise);
        else
            _noise += 0.0008f * (Math.Min(rms, _noise * 1.4f) - _noise);
        _noise = Math.Clamp(_noise, 0.0025f, 0.025f);

        var voicedZcr = zcr is >= 0.018f and <= 0.38f;
        var loudEnough = rms >= Math.Max(rmsFloor, _noise * noiseMult);
        var voiced = loudEnough && voicedZcr && bandRatio >= bandMin;
        // AM/SAM: program never goes "quiet", so relative-to-noise tests die.
        // Treat a solid mid-band AF level as speech unless the gate is set very strict.
        var broadcast = _sensitivity <= 7 && rms >= rmsFloor * 1.15f && bandRatio >= Math.Min(0.08f, bandMin);
        var speechLike = voiced || broadcast;

        var blockMs = 1_000.0 * block.Length / sampleRate;
        var hangNeed = Math.Max(2, (int)Math.Round(450 / Math.Max(20, blockMs)));
        var startNeed = Math.Max(1, (int)Math.Round(120 / Math.Max(20, blockMs)));

        if (speechLike)
        {
            _speechBlocks++;
            _hangBlocks = hangNeed;
            if (_speechBlocks >= startNeed) _inUtterance = true;
        }
        else if (_hangBlocks > 0)
        {
            _hangBlocks--;
        }
        else
        {
            _speechBlocks = 0;
            _inUtterance = false;
        }

        return _inUtterance;
    }

    public void Reset()
    {
        _speechBlocks = 0;
        _hangBlocks = 0;
        _inUtterance = false;
        _noise = 0.01f;
        _lpSlow = 0;
        _lpFast = 0;
    }
}
