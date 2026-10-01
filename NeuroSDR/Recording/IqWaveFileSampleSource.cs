using NeuroSDR.Core;
using System.Diagnostics;
using System.Text;

namespace NeuroSDR.Recording;

public sealed class IqWaveFileSampleSource : ISampleSource, ISampleSourceMetrics, IFixedCenterFrequencySampleSource
{
    private readonly object _lifecycle = new();
    private readonly string _path;
    private readonly long _dataOffset, _dataLength;
    private CancellationTokenSource? _cancellation;
    private Task? _readerTask;
    private long _totalSamples, _deliveredSamples, _lastDeliveryTick;
    private bool _disposed;

    private IqWaveFileSampleSource(string path, int sampleRate, long centerFrequency, long dataOffset, long dataLength)
    {
        _path = path;
        SampleRate = sampleRate;
        CenterFrequency = centerFrequency;
        _dataOffset = dataOffset;
        _dataLength = dataLength;
        Name = $"IQ WAV ({System.IO.Path.GetFileName(path)})";
    }

    public string Name { get; }
    public long CenterFrequency { get; set; }
    public int SampleRate { get; }
    public bool IsRunning { get; private set; }
    public long TotalSamples => Interlocked.Read(ref _totalSamples);
    public long DeliveredSamples => Interlocked.Read(ref _deliveredSamples);
    public long DroppedSamples => 0;
    public long LastDeliveryAgeMilliseconds => Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastDeliveryTick));
    public event Action<Complex32[]>? SamplesAvailable;

    public static bool TryOpen(string path, out IqWaveFileSampleSource? source, out string status)
    {
        source = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (ReadId(reader) != "RIFF" || reader.ReadUInt32() < 4 || ReadId(reader) != "WAVE")
                throw new InvalidDataException("The file is not a RIFF/WAVE file.");

            int sampleRate = 0;
            long centerFrequency = 0, dataOffset = 0, dataLength = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var id = ReadId(reader);
                var length = reader.ReadUInt32();
                var next = Math.Min(stream.Length, stream.Position + length + (length & 1));
                if (id == "fmt ")
                {
                    if (length < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 2)
                        throw new InvalidDataException("Only 16-bit stereo PCM IQ WAV files are supported.");
                    sampleRate = checked((int)reader.ReadUInt32());
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    if (reader.ReadUInt16() != 16) throw new InvalidDataException("Only 16-bit IQ WAV files are supported.");
                }
                else if (id == "enrf" && length >= 8) centerFrequency = reader.ReadInt64();
                else if (id == "data")
                {
                    dataOffset = stream.Position;
                    dataLength = Math.Min(length, stream.Length - dataOffset);
                }
                stream.Position = next;
            }
            if (sampleRate <= 0 || dataOffset == 0 || dataLength < 4)
                throw new InvalidDataException("No valid IQ WAV data was found.");
            source = new IqWaveFileSampleSource(path, sampleRate, centerFrequency, dataOffset, dataLength);
            status = $"{source.Name} · {sampleRate / 1_000_000d:0.###} MS/s";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Failed to open IQ WAV: {exception.Message}";
            return false;
        }
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;
            Interlocked.Exchange(ref _totalSamples, 0);
            Interlocked.Exchange(ref _deliveredSamples, 0);
            Interlocked.Exchange(ref _lastDeliveryTick, Environment.TickCount64);
            _cancellation = new CancellationTokenSource();
            IsRunning = true;
            _readerTask = Task.Run(() => ReadLoop(_cancellation.Token));
        }
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream);
            stream.Position = _dataOffset;
            var remaining = _dataLength;
            var stopwatch = Stopwatch.StartNew();
            long samplesRead = 0;
            while (!cancellationToken.IsCancellationRequested && remaining >= 4)
            {
                var count = (int)Math.Min(16_384, remaining / 4);
                var samples = new Complex32[count];
                for (var n = 0; n < count; n++) samples[n] = new Complex32(reader.ReadInt16() / 32768f, reader.ReadInt16() / 32768f);
                remaining -= count * 4L;
                samplesRead += count;
                Interlocked.Add(ref _totalSamples, count);
                Interlocked.Add(ref _deliveredSamples, count);
                Interlocked.Exchange(ref _lastDeliveryTick, Environment.TickCount64);
                try { SamplesAvailable?.Invoke(samples); } catch { }

                var targetMilliseconds = samplesRead * 1000L / SampleRate;
                while (!cancellationToken.IsCancellationRequested && stopwatch.ElapsedMilliseconds < targetMilliseconds)
                    Thread.Sleep((int)Math.Min(10, targetMilliseconds - stopwatch.ElapsedMilliseconds));
            }
        }
        finally { IsRunning = false; }
    }

    public void Stop()
    {
        Task? reader;
        lock (_lifecycle)
        {
            IsRunning = false;
            _cancellation?.Cancel();
            reader = _readerTask;
        }
        if (reader is not null && Task.CurrentId != reader.Id) try { reader.Wait(2_000); } catch (AggregateException) { }
        lock (_lifecycle)
        {
            _readerTask = null;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string ReadId(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
