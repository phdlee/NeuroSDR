using NeuroSDR.Audio;

namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// Wall-clock 20 ms WaveOut pump for decoded digital voice. Keeps speaker pacing
/// independent of RF callback burst size (Portable DSD-FME does the same via OSS/PA).
/// Playback level uses the normal MAIN OUT WaveOut volume fader.
/// </summary>
internal static class DigitalVoicePacer
{
    private const int BlockSamples = 960; // 20 ms @ 48 kHz
    private static readonly object Sync = new();
    private static readonly float[] Block = new float[BlockSamples];
    private static Thread? _thread;
    private static volatile bool _run;
    private static Func<WaveOutPlayer?>? _resolveOutput;
    private static Action<float[]>? _onBlock;
    private static long _blocks, _silentBlocks;

    public static long BlocksWritten => Interlocked.Read(ref _blocks);
    public static long SilentBlocks => Interlocked.Read(ref _silentBlocks);

    public static void Configure(Func<WaveOutPlayer?> resolveOutput, Action<float[]>? onBlock = null)
    {
        lock (Sync)
        {
            _resolveOutput = resolveOutput;
            _onBlock = onBlock;
        }
    }

    public static void Start()
    {
        lock (Sync)
        {
            if (_thread is { IsAlive: true }) return;
            _run = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "NeuroSDR digital-voice pacer",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }
    }

    public static void Stop()
    {
        Thread? thread;
        lock (Sync)
        {
            _run = false;
            thread = _thread;
            _thread = null;
        }
        thread?.Join(500);
        Interlocked.Exchange(ref _blocks, 0);
        Interlocked.Exchange(ref _silentBlocks, 0);
    }

    private static void Loop()
    {
        var next = Environment.TickCount64;
        while (_run)
        {
            next += 20;
            try
            {
                if (DigitalVoicePlayback.OwnedOutputIndex >= 0)
                {
                    DigitalVoicePlayback.Fill48k(Block);
                    var peak = 0f;
                    for (var i = 0; i < Block.Length; i++)
                    {
                        var mag = Math.Abs(Block[i]);
                        if (mag > peak) peak = mag;
                    }
                    if (peak < 1e-5f) Interlocked.Increment(ref _silentBlocks);
                    else Interlocked.Increment(ref _blocks);

                    Func<WaveOutPlayer?>? resolve;
                    Action<float[]>? onBlock;
                    lock (Sync)
                    {
                        resolve = _resolveOutput;
                        onBlock = _onBlock;
                    }
                    resolve?.Invoke()?.Write(Block);
                    if (onBlock is not null)
                    {
                        var copy = new float[Block.Length];
                        Block.CopyTo(copy, 0);
                        onBlock(copy);
                    }
                }
            }
            catch
            {
                // Keep pacing even if a transient WaveOut failure occurs.
            }

            var sleep = next - Environment.TickCount64;
            if (sleep > 0) Thread.Sleep((int)Math.Min(sleep, 20));
            else if (sleep < -100) next = Environment.TickCount64;
        }
    }
}
