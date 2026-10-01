namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// 8 kHz DSD voice → 48 kHz ring. A wall-clock pacer pulls this for WaveOut so
/// RF-callback jitter (and TDMA gaps) are not written as choppy sample-holds.
/// </summary>
internal static class DigitalVoicePlayback
{
    public const int HostRate = 48_000;
    private const int Upsample = 6; // 8000 → 48000
    // Larger preroll absorbs decoder/TDMA burstiness closer to Portable's buffering.
    private const int StartPrerollSamples = HostRate * 160 / 1000;
    private static readonly object Sync = new();
    private static readonly float[] Ring = new float[HostRate * 3];
    private static int _read, _count;
    private static int _ownedIndex = -1;
    private static float _last48k;
    private static bool _playing;
    private static int _underrunSamples;
    private static long _pushedFrames, _pulledSamples, _underrunEvents, _overflowDrops;
    private static int _minBufferedWhilePlaying = int.MaxValue;
    private static float _voicePeak;

    public static int BufferedSamples
    {
        get { lock (Sync) return _count; }
    }

    public static int OwnedOutputIndex
    {
        get => Volatile.Read(ref _ownedIndex);
        set => Volatile.Write(ref _ownedIndex, value);
    }

    public static bool Owns(int outputIndex) =>
        outputIndex >= 0 && Volatile.Read(ref _ownedIndex) == outputIndex;

    /// <summary>True once decoded voice has prerolled (or is already playing).</summary>
    public static bool ReadyToOwnSpeaker
    {
        get
        {
            lock (Sync) return _playing || _count >= StartPrerollSamples;
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _read = 0;
            _count = 0;
            _playing = false;
            _last48k = 0;
            _underrunSamples = 0;
            _pushedFrames = _pulledSamples = _underrunEvents = _overflowDrops = 0;
            _minBufferedWhilePlaying = int.MaxValue;
            _voicePeak = 0;
        }
    }

    public static (long pushedFrames, long pulledSamples, long underruns, long overflows, int buffered, int minBuffered, float peak, bool playing)
        SnapshotMetrics()
    {
        lock (Sync)
        {
            var min = _minBufferedWhilePlaying == int.MaxValue ? 0 : _minBufferedWhilePlaying;
            return (_pushedFrames, _pulledSamples, _underrunEvents, _overflowDrops, _count, min, _voicePeak, _playing);
        }
    }

    public static void ResetMinBuffered()
    {
        lock (Sync) _minBufferedWhilePlaying = int.MaxValue;
    }

    public static void PushDecodedMono8k(ReadOnlySpan<short> mono8k)
    {
        if (mono8k.Length == 0) return;
        lock (Sync)
        {
            foreach (var sample in mono8k)
            {
                var mono = Math.Clamp(sample * (1f / 32768f), -1f, 1f);
                var mag = Math.Abs(mono);
                if (mag > _voicePeak) _voicePeak = mag;
                for (var r = 0; r < Upsample; r++)
                {
                    if (_count == Ring.Length)
                    {
                        _read = (_read + 1) % Ring.Length;
                        _count--;
                        _overflowDrops++;
                    }
                    Ring[(_read + _count) % Ring.Length] = mono;
                    _count++;
                }
                _pushedFrames++;
            }
            _underrunSamples = 0;
        }
    }

    public static void PushDecodedStereo8k(ReadOnlySpan<short> interleaved)
    {
        if (interleaved.Length < 2) return;
        lock (Sync)
        {
            for (var i = 0; i + 1 < interleaved.Length; i += 2)
            {
                var left = interleaved[i] * (1f / 32768f);
                var right = interleaved[i + 1] * (1f / 32768f);
                // Sum slots: inactive slot is ~0. Energy-based L/R switching caused
                // mid-syllable chops that looked like "30% drop" with underrun=0.
                var mono = Math.Clamp(left + right, -1f, 1f);
                var mag = Math.Abs(mono);
                if (mag > _voicePeak) _voicePeak = mag;

                // Zero-order hold ×6 — matches typical DSD digital-voice paths.
                for (var r = 0; r < Upsample; r++)
                {
                    if (_count == Ring.Length)
                    {
                        _read = (_read + 1) % Ring.Length;
                        _count--;
                        _overflowDrops++;
                    }
                    Ring[(_read + _count) % Ring.Length] = mono;
                    _count++;
                }
                _pushedFrames++;
            }
            _underrunSamples = 0;
        }
    }

    public static void Fill48k(Span<float> destination)
    {
        lock (Sync)
        {
            if (!_playing)
            {
                if (_count < StartPrerollSamples)
                {
                    destination.Fill(0f);
                    return;
                }
                _playing = true;
                _underrunSamples = 0;
            }

            if (_count < _minBufferedWhilePlaying) _minBufferedWhilePlaying = _count;

            for (var i = 0; i < destination.Length; i++)
            {
                if (_count == 0)
                {
                    // Silence — not sample-hold. Holding the last sample is what made
                    // TDMA / decode gaps sound like hard dropouts.
                    destination[i] = 0f;
                    _last48k = 0f;
                    if (_underrunSamples++ == 0) _underrunEvents++;
                    if (_underrunSamples > HostRate / 5)
                    {
                        _playing = false;
                        _underrunSamples = 0;
                    }
                    continue;
                }
                _underrunSamples = 0;
                _last48k = Ring[_read];
                destination[i] = _last48k;
                _read = (_read + 1) % Ring.Length;
                _count--;
                _pulledSamples++;
            }
        }
    }
}
