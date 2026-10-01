using NeuroSDR.Core;
using System.Text;
using System.Threading.Channels;

namespace NeuroSDR.Recording;

internal sealed class IqWaveRecorder : IDisposable
{
    private const long MaximumDataBytes = 0xFFFF_F000L;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly Channel<Complex32[]> _queue;
    private readonly Task _writerTask;
    private readonly long _riffSizeOffset;
    private readonly long _dataSizeOffset;
    private long _dataBytes, _writtenSamples, _droppedSamples;
    private bool _stopping;

    public IqWaveRecorder(string path, int sampleRate, long centerFrequency, long tunedFrequency)
    {
        Path = path;
        SampleRate = sampleRate;
        CenterFrequency = centerFrequency;
        TunedFrequency = tunedFrequency;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        (_riffSizeOffset, _dataSizeOffset) = WriteHeader(_writer, sampleRate, centerFrequency, tunedFrequency);
        _queue = Channel.CreateBounded<Complex32[]>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _writerTask = Task.Run(WriteLoop);
    }

    public string Path { get; }
    public int SampleRate { get; }
    public long CenterFrequency { get; }
    public long TunedFrequency { get; }
    public long WrittenSamples => Interlocked.Read(ref _writtenSamples);
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);
    public string? Error { get; private set; }
    public bool IsRecording => !_stopping && Error is null;

    public void Write(Complex32[] samples)
    {
        if (_stopping || samples.Length == 0) return;
        if (!_queue.Writer.TryWrite(samples)) Interlocked.Add(ref _droppedSamples, samples.Length);
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var samples in _queue.Reader.ReadAllAsync())
            {
                if (_dataBytes + samples.Length * 4L > MaximumDataBytes)
                {
                    Error = "The IQ WAV file has reached the 4 GB limit.";
                    Interlocked.Add(ref _droppedSamples, samples.Length);
                    continue;
                }
                foreach (var sample in samples)
                {
                    _writer.Write(ToPcm16(sample.I));
                    _writer.Write(ToPcm16(sample.Q));
                }
                _dataBytes += samples.Length * 4L;
                Interlocked.Add(ref _writtenSamples, samples.Length);
            }
        }
        catch (Exception exception) { Error = exception.Message; }
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _queue.Writer.TryComplete();
        try { _writerTask.Wait(5_000); } catch (AggregateException exception) { Error ??= exception.GetBaseException().Message; }
        FinalizeHeader();
    }

    private void FinalizeHeader()
    {
        if (!_stream.CanWrite) return;
        _writer.Flush();
        _stream.Position = _dataSizeOffset;
        _writer.Write((uint)_dataBytes);
        _stream.Position = _riffSizeOffset;
        _writer.Write((uint)(_stream.Length - 8));
        _writer.Flush();
    }

    private static (long RiffSizeOffset, long DataSizeOffset) WriteHeader(BinaryWriter writer, int sampleRate, long centerFrequency, long tunedFrequency)
    {
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        var riffOffset = writer.BaseStream.Position;
        writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * 4));
        writer.Write((ushort)4);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("enrf"));
        writer.Write(16u);
        writer.Write(centerFrequency);
        writer.Write(tunedFrequency);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        var dataOffset = writer.BaseStream.Position;
        writer.Write(0u);
        return (riffOffset, dataOffset);
    }

    private static short ToPcm16(float value) => (short)Math.Clamp(Math.Round(value * 32767), short.MinValue, short.MaxValue);

    public void Dispose()
    {
        Stop();
        _writer.Dispose();
        _stream.Dispose();
    }
}
