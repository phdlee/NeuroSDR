using System.Threading.Channels;
using System.Collections.Concurrent;

namespace NeuroSDR.Core;

internal sealed unsafe class SampleBlockDispatcher
{
    private int _blockSize;
    private readonly int _queueCapacity;
    private readonly object _writeSync = new();
    private readonly ConcurrentBag<Complex32[]> _bufferPool = new();
    private Complex32[] _writeBuffer;
    private int _writeCount;
    private Channel<Complex32[]>? _queue;
    private Task? _dispatchTask;
    private int _queuedBlocks;
    private long _totalSamples, _deliveredSamples, _droppedSamples, _lastDeliveryTick;
    private int _maximumQueuedBlocks;

    public SampleBlockDispatcher(int blockSize = 16_384, int queueCapacity = 8)
    {
        _blockSize = blockSize;
        _queueCapacity = Math.Max(2, queueCapacity);
        _writeBuffer = new Complex32[blockSize];
    }

    public event Action<Complex32[]>? SamplesAvailable;
    public long TotalSamples => Interlocked.Read(ref _totalSamples);
    public long DeliveredSamples => Interlocked.Read(ref _deliveredSamples);
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);
    public long LastDeliveryAgeMilliseconds => Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastDeliveryTick));
    public int QueuedBlocks => Volatile.Read(ref _queuedBlocks);
    public int MaximumQueuedBlocks => Volatile.Read(ref _maximumQueuedBlocks);

    public void ConfigureBlockSize(int blockSize)
    {
        if (blockSize < 1_024) throw new ArgumentOutOfRangeException(nameof(blockSize));
        lock (_writeSync)
        {
            if (_queue is not null) throw new InvalidOperationException("Cannot change the RF block size while streaming.");
            if (_blockSize == blockSize) return;
            _blockSize = blockSize;
            _writeBuffer = new Complex32[blockSize];
            _writeCount = 0;
            while (_bufferPool.TryTake(out _)) { }
        }
    }

    public void Start()
    {
        Stop();
        lock (_writeSync)
        {
            Recycle(_writeBuffer);
            _writeBuffer = RentBuffer();
            _writeCount = 0;
        }
        Interlocked.Exchange(ref _totalSamples, 0);
        Interlocked.Exchange(ref _deliveredSamples, 0);
        Interlocked.Exchange(ref _droppedSamples, 0);
        Interlocked.Exchange(ref _queuedBlocks, 0);
        Interlocked.Exchange(ref _maximumQueuedBlocks, 0);
        Interlocked.Exchange(ref _lastDeliveryTick, Environment.TickCount64);
        var queue = Channel.CreateBounded<Complex32[]>(new BoundedChannelOptions(_queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _queue = queue;
        _dispatchTask = Task.Run(() => Dispatch(queue.Reader));
    }

    public void Stop()
    {
        Channel<Complex32[]>? queue;
        lock (_writeSync)
        {
            if (_writeCount > 0)
            {
                var partial = new Complex32[_writeCount];
                Array.Copy(_writeBuffer, partial, _writeCount);
                Queue(partial);
                _writeCount = 0;
            }
            queue = _queue;
            _queue = null;
        }
        queue?.Writer.TryComplete();
        try { _dispatchTask?.Wait(8_000); } catch (AggregateException) { }
        _dispatchTask = null;
    }

    public void WriteInt16(short* i, short* q, uint count, float gain = 1f)
    {
        if (i is null || q is null || count == 0) return;
        Interlocked.Add(ref _totalSamples, count);
        lock (_writeSync) WriteInt16Core(i, q, checked((int)count), gain);
    }

    public void WriteUnsignedInterleaved(byte* data, uint byteCount)
    {
        if (data is null || byteCount < 2) return;
        var count = byteCount / 2;
        Interlocked.Add(ref _totalSamples, count);
        lock (_writeSync) WriteUnsignedCore(data, checked((int)count));
    }

    public void WriteSignedInterleaved(sbyte* data, int byteCount)
    {
        if (data is null || byteCount < 2) return;
        var count = byteCount / 2;
        Interlocked.Add(ref _totalSamples, count);
        lock (_writeSync) WriteSignedCore(data, count);
    }

    /// <summary>Interleaved int16 I/Q (Airspy INT16_IQ, Soapy CS16).</summary>
    public void WriteInt16Interleaved(short* data, int sampleCount, float gain = 1f)
    {
        if (data is null || sampleCount <= 0) return;
        Interlocked.Add(ref _totalSamples, sampleCount);
        lock (_writeSync) WriteInt16InterleavedCore(data, sampleCount, gain);
    }

    /// <summary>Interleaved float32 I/Q already scaled near ±1 (Airspy FLOAT32_IQ, Soapy CF32).</summary>
    public void WriteFloatInterleaved(float* data, int sampleCount, float gain = 1f)
    {
        if (data is null || sampleCount <= 0) return;
        Interlocked.Add(ref _totalSamples, sampleCount);
        lock (_writeSync) WriteFloatInterleavedCore(data, sampleCount, gain);
    }

    private void WriteInt16Core(short* i, short* q, int count, float gain)
    {
        var scale = gain / 32768f;
        while (count > 0)
        {
            var take = Math.Min(count, _blockSize - _writeCount);
            fixed (Complex32* destination = &_writeBuffer[_writeCount])
                for (var n = 0; n < take; n++) destination[n] = new Complex32(i[n] * scale, q[n] * scale);
            i += take;
            q += take;
            count -= take;
            CompleteWrite(take);
        }
    }

    private void WriteUnsignedCore(byte* data, int count)
    {
        const float scale = 1f / 128f;
        while (count > 0)
        {
            var take = Math.Min(count, _blockSize - _writeCount);
            fixed (Complex32* destination = &_writeBuffer[_writeCount])
                for (var n = 0; n < take; n++)
                    destination[n] = new Complex32((data[n * 2] - 127.5f) * scale, (data[n * 2 + 1] - 127.5f) * scale);
            data += take * 2;
            count -= take;
            CompleteWrite(take);
        }
    }

    private void WriteSignedCore(sbyte* data, int count)
    {
        const float scale = 1f / 128f;
        while (count > 0)
        {
            var take = Math.Min(count, _blockSize - _writeCount);
            fixed (Complex32* destination = &_writeBuffer[_writeCount])
                for (var n = 0; n < take; n++)
                    destination[n] = new Complex32(data[n * 2] * scale, data[n * 2 + 1] * scale);
            data += take * 2;
            count -= take;
            CompleteWrite(take);
        }
    }

    private void WriteInt16InterleavedCore(short* data, int count, float gain)
    {
        var scale = gain / 32768f;
        while (count > 0)
        {
            var take = Math.Min(count, _blockSize - _writeCount);
            fixed (Complex32* destination = &_writeBuffer[_writeCount])
                for (var n = 0; n < take; n++)
                    destination[n] = new Complex32(data[n * 2] * scale, data[n * 2 + 1] * scale);
            data += take * 2;
            count -= take;
            CompleteWrite(take);
        }
    }

    private void WriteFloatInterleavedCore(float* data, int count, float gain)
    {
        while (count > 0)
        {
            var take = Math.Min(count, _blockSize - _writeCount);
            fixed (Complex32* destination = &_writeBuffer[_writeCount])
                for (var n = 0; n < take; n++)
                    destination[n] = new Complex32(data[n * 2] * gain, data[n * 2 + 1] * gain);
            data += take * 2;
            count -= take;
            CompleteWrite(take);
        }
    }

    private void CompleteWrite(int count)
    {
        _writeCount += count;
        if (_writeCount != _blockSize) return;
        Queue(_writeBuffer);
        _writeBuffer = RentBuffer();
        _writeCount = 0;
    }

    private void Queue(Complex32[] samples)
    {
        var queue = _queue;
        var queued = Interlocked.Increment(ref _queuedBlocks);
        UpdateMaximum(ref _maximumQueuedBlocks, queued);
        if (queue is not null && queued <= _queueCapacity && queue.Writer.TryWrite(samples)) return;
        Interlocked.Decrement(ref _queuedBlocks);
        Interlocked.Add(ref _droppedSamples, samples.Length);
        Recycle(samples);
    }

    private void Dispatch(ChannelReader<Complex32[]> reader)
    {
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            while (reader.TryRead(out var samples))
            {
                Interlocked.Decrement(ref _queuedBlocks);
                Interlocked.Add(ref _deliveredSamples, samples.Length);
                Interlocked.Exchange(ref _lastDeliveryTick, Environment.TickCount64);
                try { SamplesAvailable?.Invoke(samples); } catch { }
                finally { Recycle(samples); }
            }
    }

    private Complex32[] RentBuffer() => _bufferPool.TryTake(out var buffer) ? buffer : new Complex32[_blockSize];

    private void Recycle(Complex32[] buffer)
    {
        if (buffer.Length == _blockSize) _bufferPool.Add(buffer);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
