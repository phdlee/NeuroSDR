using NeuroSDR.Core;
using System.Net.Sockets;

namespace NeuroSDR.Hardware;

/// <summary>
/// Network rtl_tcp client (Osmocom / rtl_tcp protocol). Always listed so users can
/// point at a remote RTL-SDR without local USB.
/// </summary>
public sealed unsafe class RtlTcpSampleSource : ISampleSource, IGainControlledSampleSource,
    ISampleSourceMetrics, ISampleQueueMetrics
{
    private readonly object _lifecycle = new();
    private readonly SampleBlockDispatcher _samples = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _reader;
    private long _centerFrequency = 100_000_000;
    private int _gainPercent = 60;
    private string _endpoint = "127.0.0.1:1234";
    private bool _disposed;
    private volatile bool _running;

    public string Name => $"rtl_tcp ({_endpoint})";
    public int SampleRate => 2_048_000;
    public bool IsRunning => _running;
    public long TotalSamples => _samples.TotalSamples;
    public long DeliveredSamples => _samples.DeliveredSamples;
    public long DroppedSamples => _samples.DroppedSamples;
    public long LastDeliveryAgeMilliseconds => _samples.LastDeliveryAgeMilliseconds;
    public int QueuedBlocks => _samples.QueuedBlocks;
    public int MaximumQueuedBlocks => _samples.MaximumQueuedBlocks;
    public event Action<Complex32[]>? SamplesAvailable;

    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (IsRunning) throw new InvalidOperationException("Cannot change rtl_tcp host while receiving.");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Endpoint required.", nameof(value));
            _endpoint = value.Trim();
        }
    }

    public long CenterFrequency
    {
        get => Interlocked.Read(ref _centerFrequency);
        set
        {
            value = Math.Clamp(value, 1_000_000, 1_800_000_000);
            Interlocked.Exchange(ref _centerFrequency, value);
            lock (_lifecycle)
                if (_stream is not null) SendCommand(0x01, (uint)value);
        }
    }

    public int GainPercent
    {
        get => _gainPercent;
        set
        {
            _gainPercent = Math.Clamp(value, 0, 100);
            lock (_lifecycle)
                if (_stream is not null) ApplyGain();
        }
    }

    public RtlTcpSampleSource()
    {
        _samples.SamplesAvailable += block => SamplesAvailable?.Invoke(block);
    }

    public static bool TryCreate(out RtlTcpSampleSource? source, out string status)
    {
        source = new RtlTcpSampleSource();
        status = "rtl_tcp ready · default 127.0.0.1:1234 (set Endpoint before Start)";
        return true;
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            var (host, port) = ParseEndpoint(_endpoint);
            _client = new TcpClient();
            try
            {
                _client.NoDelay = true;
                _client.ReceiveBufferSize = 1 << 20;
                _client.Connect(host, port);
                _stream = _client.GetStream();
                SendCommand(0x02, (uint)SampleRate);
                SendCommand(0x01, (uint)CenterFrequency);
                SendCommand(0x03, 1); // manual gain
                ApplyGain();
                _samples.Start();
                _running = true;
                _reader = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "rtl_tcp reader",
                    Priority = ThreadPriority.Highest
                };
                _reader.Start();
            }
            catch
            {
                StopCore();
                throw;
            }
        }
    }

    public void Stop()
    {
        _running = false;
        try { _client?.Close(); } catch { }
        Thread? reader;
        lock (_lifecycle) reader = _reader;
        if (reader is not null && reader != Thread.CurrentThread) reader.Join(2_000);
        lock (_lifecycle) StopCore();
    }

    private void ReadLoop()
    {
        var buffer = new byte[262_144];
        var skippedHeader = false;
        try
        {
            while (_running && _stream is not null)
            {
                var read = _stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                var offset = 0;
                if (!skippedHeader && read >= 4 &&
                    buffer[0] == (byte)'R' && buffer[1] == (byte)'T' && buffer[2] == (byte)'L' && buffer[3] == (byte)'0')
                {
                    offset = Math.Min(12, read);
                    skippedHeader = true;
                    if (offset >= read) continue;
                }
                else skippedHeader = true;

                var iqBytes = read - offset;
                if (iqBytes < 2) continue;
                // Keep even length for interleaved IQ.
                iqBytes &= ~1;
                fixed (byte* p = &buffer[offset])
                    _samples.WriteUnsignedInterleaved(p, (uint)iqBytes);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        _running = false;
    }

    private void ApplyGain()
    {
        // rtl_tcp gain is 0.1 dB units; map 0..100% → 0..496 (≈0..49.6 dB).
        var tenths = (uint)Math.Clamp(_gainPercent * 496 / 100, 0, 496);
        SendCommand(0x04, tenths);
    }

    private void SendCommand(byte command, uint parameter)
    {
        if (_stream is null) return;
        Span<byte> packet = stackalloc byte[5];
        packet[0] = command;
        packet[1] = (byte)(parameter >> 24);
        packet[2] = (byte)(parameter >> 16);
        packet[3] = (byte)(parameter >> 8);
        packet[4] = (byte)parameter;
        _stream.Write(packet);
        _stream.Flush();
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var parts = endpoint.Split(':', 2, StringSplitOptions.TrimEntries);
        var host = parts[0].Length == 0 ? "127.0.0.1" : parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 1234;
        return (host, Math.Clamp(port, 1, 65535));
    }

    private void StopCore()
    {
        _running = false;
        _samples.Stop();
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        _reader = null;
    }

    public void Dispose()
    {
        Stop();
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
