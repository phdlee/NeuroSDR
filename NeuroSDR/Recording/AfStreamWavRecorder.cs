using System.Text;

namespace NeuroSDR.Recording;

/// <summary>Streaming mono 16-bit WAV writer (RIFF sizes fixed on Dispose).</summary>
internal sealed class AfStreamWavRecorder : IDisposable
{
    private readonly object _sync = new();
    private FileStream? _stream;
    private BinaryWriter? _writer;
    private long _dataBytes;
    private bool _disposed;

    public string Path { get; }
    public int SampleRate { get; }
    public string VfoId { get; }
    public bool IsOpen
    {
        get { lock (_sync) return _stream is not null && !_disposed; }
    }

    public AfStreamWavRecorder(string path, int sampleRate, string vfoId)
    {
        Path = path;
        SampleRate = Math.Max(8_000, sampleRate);
        VfoId = vfoId;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        // Placeholder sizes; corrected in Dispose.
        _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write(0u);
        _writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        _writer.Write(Encoding.ASCII.GetBytes("fmt "));
        _writer.Write(16u);
        _writer.Write((ushort)1); // PCM
        _writer.Write((ushort)1); // mono
        _writer.Write((uint)SampleRate);
        _writer.Write((uint)(SampleRate * 2));
        _writer.Write((ushort)2);
        _writer.Write((ushort)16);
        _writer.Write(Encoding.ASCII.GetBytes("data"));
        _writer.Write(0u);
    }

    public void WriteFloatMono(ReadOnlySpan<float> samples)
    {
        lock (_sync)
        {
            if (_writer is null || _disposed) return;
            foreach (var sample in samples)
            {
                var clipped = Math.Clamp(sample, -1f, 1f);
                _writer.Write((short)Math.Round(clipped * 32767f));
                _dataBytes += 2;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_stream is not null)
                {
                    _writer?.Flush();
                    _stream.Flush();
                    FixSizes(_stream, _dataBytes);
                }
            }
            catch { }
            finally
            {
                _writer?.Dispose();
                _writer = null;
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    private static void FixSizes(FileStream stream, long dataBytes)
    {
        var data = (uint)Math.Min(uint.MaxValue, dataBytes);
        stream.Position = 4;
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(36u + data);
        stream.Position = 40;
        writer.Write(data);
    }
}
