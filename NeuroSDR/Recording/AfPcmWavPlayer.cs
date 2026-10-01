using System.Diagnostics;
using System.Text;

namespace NeuroSDR.Recording;

/// <summary>
/// Real-time AF WAV reader for digital-voice discriminator PCM (NFM-style recordings).
/// Emits mono float blocks paced at wall-clock rate, resampled to 48 kHz.
/// </summary>
internal sealed class AfPcmWavPlayer : IDisposable
{
    public const int HostRate = 48_000;
    private const int BlockMilliseconds = 20;

    private readonly object _sync = new();
    private readonly string _path;
    private readonly int _fileSampleRate;
    private readonly int _channels;
    private readonly int _bitsPerSample;
    private readonly long _dataOffset;
    private readonly long _dataBytes;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private bool _disposed;

    private AfPcmWavPlayer(string path, int sampleRate, int channels, int bitsPerSample, long dataOffset, long dataBytes)
    {
        _path = path;
        _fileSampleRate = sampleRate;
        _channels = channels;
        _bitsPerSample = bitsPerSample;
        _dataOffset = dataOffset;
        _dataBytes = dataBytes;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Name { get; }
    public int FileSampleRate => _fileSampleRate;
    public bool IsRunning
    {
        get { lock (_sync) return _task is { IsCompleted: false }; }
    }

    public event Action<float[]>? SamplesAvailable;
    public event Action<string>? Completed;

    public static bool TryOpen(string path, out AfPcmWavPlayer? player, out string status)
    {
        player = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (ReadId(reader) != "RIFF" || reader.ReadUInt32() < 4 || ReadId(reader) != "WAVE")
                throw new InvalidDataException("Not a RIFF/WAVE file.");

            var sampleRate = 0;
            var channels = 0;
            var bits = 0;
            long dataOffset = 0, dataBytes = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var id = ReadId(reader);
                var length = reader.ReadUInt32();
                var next = Math.Min(stream.Length, stream.Position + length + (length & 1));
                if (id == "fmt ")
                {
                    if (length < 16) throw new InvalidDataException("Invalid fmt chunk.");
                    var format = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = checked((int)reader.ReadUInt32());
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    bits = reader.ReadUInt16();
                    if (format != 1) throw new InvalidDataException("Only PCM WAV is supported.");
                    if (channels is < 1 or > 2) throw new InvalidDataException("Only mono/stereo WAV is supported.");
                    if (bits is not (8 or 16)) throw new InvalidDataException("Only 8/16-bit PCM WAV is supported.");
                }
                else if (id == "data")
                {
                    dataOffset = stream.Position;
                    dataBytes = Math.Min(length, stream.Length - dataOffset);
                }
                stream.Position = next;
            }
            if (sampleRate < 8_000 || dataOffset == 0 || dataBytes < 2)
                throw new InvalidDataException("No usable PCM data found.");
            player = new AfPcmWavPlayer(path, sampleRate, channels, bits, dataOffset, dataBytes);
            status = $"{player.Name} · {sampleRate / 1_000d:0.#} kHz · {(channels == 1 ? "mono" : "stereo")} {bits}-bit";
            return true;
        }
        catch (Exception exception)
        {
            status = exception.Message;
            return false;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_task is { IsCompleted: false }) return;
            _cancellation = new CancellationTokenSource();
            _task = Task.Run(() => PlayLoop(_cancellation.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            cts = _cancellation;
            task = _task;
            _cancellation = null;
            _task = null;
        }
        try { cts?.Cancel(); } catch { }
        try { task?.Wait(1_500); } catch { }
        cts?.Dispose();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        GC.SuppressFinalize(this);
    }

    private void PlayLoop(CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Position = _dataOffset;
            var bytesPerFrame = _channels * (_bitsPerSample / 8);
            var fileBlockFrames = Math.Max(1, _fileSampleRate * BlockMilliseconds / 1_000);
            var fileBlockBytes = fileBlockFrames * bytesPerFrame;
            var raw = new byte[fileBlockBytes];
            var fileFloat = new float[fileBlockFrames];
            var clock = Stopwatch.StartNew();
            long emittedHostSamples = 0;
            long remaining = _dataBytes;

            while (!token.IsCancellationRequested && remaining >= bytesPerFrame)
            {
                var want = (int)Math.Min(fileBlockBytes, remaining - remaining % bytesPerFrame);
                if (want < bytesPerFrame) break;
                var read = stream.Read(raw, 0, want);
                if (read < bytesPerFrame) break;
                remaining -= read;
                var frames = read / bytesPerFrame;
                DecodeFrames(raw.AsSpan(0, read), frames, fileFloat);
                var host = Resample(fileFloat.AsSpan(0, frames), _fileSampleRate, HostRate);
                if (host.Length > 0)
                {
                    SamplesAvailable?.Invoke(host);
                    emittedHostSamples += host.Length;
                }

                var dueMs = emittedHostSamples * 1000.0 / HostRate;
                var wait = (int)Math.Round(dueMs - clock.Elapsed.TotalMilliseconds);
                if (wait > 1)
                {
                    try { Task.Delay(wait, token).Wait(token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            Completed?.Invoke(token.IsCancellationRequested ? "WAV stopped" : "WAV finished");
        }
        catch (Exception exception)
        {
            Completed?.Invoke("WAV error · " + exception.Message);
        }
    }

    private void DecodeFrames(ReadOnlySpan<byte> raw, int frames, float[] dest)
    {
        if (_bitsPerSample == 16)
        {
            if (_channels == 1)
            {
                for (var i = 0; i < frames; i++)
                    dest[i] = BitConverter.ToInt16(raw[(i * 2)..]) / 32768f;
            }
            else
            {
                for (var i = 0; i < frames; i++)
                {
                    var l = BitConverter.ToInt16(raw[(i * 4)..]) / 32768f;
                    var r = BitConverter.ToInt16(raw[(i * 4 + 2)..]) / 32768f;
                    dest[i] = 0.5f * (l + r);
                }
            }
            return;
        }

        // 8-bit unsigned PCM
        if (_channels == 1)
        {
            for (var i = 0; i < frames; i++)
                dest[i] = (raw[i] - 128) / 128f;
        }
        else
        {
            for (var i = 0; i < frames; i++)
            {
                var l = (raw[i * 2] - 128) / 128f;
                var r = (raw[i * 2 + 1] - 128) / 128f;
                dest[i] = 0.5f * (l + r);
            }
        }
    }

    private static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        if (fromRate <= 0 || toRate <= 0 || input.IsEmpty) return [];
        if (fromRate == toRate) return input.ToArray();
        var count = Math.Max(1, (int)Math.Round(input.Length * (double)toRate / fromRate));
        var output = new float[count];
        for (var i = 0; i < count; i++)
        {
            var source = i * (fromRate / (double)toRate);
            var index = (int)source;
            var frac = (float)(source - index);
            var a = input[Math.Clamp(index, 0, input.Length - 1)];
            var b = input[Math.Clamp(index + 1, 0, input.Length - 1)];
            output[i] = a + (b - a) * frac;
        }
        return output;
    }

    private static string ReadId(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
