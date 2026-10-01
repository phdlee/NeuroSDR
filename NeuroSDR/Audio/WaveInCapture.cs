using System.Runtime.InteropServices;

namespace NeuroSDR.Audio;

/// <summary>
/// winmm waveIn → float mono @ 48 kHz (device rate may differ; we resample).
/// CALLBACK_EVENT + worker thread — do not call waveIn* from CALLBACK_FUNCTION (deadlock/silence).
/// </summary>
public sealed class WaveInCapture : IDisposable
{
    private const uint CallbackEventFlag = 0x00050000;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint HeaderDone = 0x00000001;
    private const uint HeaderPrepared = 0x00000002;
    private const int HostRate = 48_000;
    private const int BufferMilliseconds = 20;
    private const int BufferCount = 8;

    private static readonly (int Rate, int Channels)[] FormatCandidates =
    [
        (48_000, 2), (48_000, 1),
        (44_100, 2), (44_100, 1),
        (32_000, 2), (32_000, 1),
        (16_000, 2), (16_000, 1)
    ];

    private readonly object _sync = new();
    private readonly AutoResetEvent _dataEvent;
    private readonly int _deviceRate;
    private readonly int _channels;
    private readonly int _bufferFrames;
    private readonly List<CaptureBuffer> _buffers;
    private readonly Thread _worker;
    private IntPtr _handle;
    private volatile bool _running;
    private volatile bool _disposed;
    private long _framesReceived;
    private int _peakBits;

    private WaveInCapture(IntPtr handle, AutoResetEvent dataEvent, int deviceRate, int channels,
        List<CaptureBuffer> buffers)
    {
        _handle = handle;
        _dataEvent = dataEvent;
        _deviceRate = deviceRate;
        _channels = channels;
        _bufferFrames = Math.Max(160, deviceRate * BufferMilliseconds / 1_000);
        _buffers = buffers;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "NeuroSDR WaveIn",
            Priority = ThreadPriority.AboveNormal
        };
    }

    public int DeviceSampleRate => _deviceRate;
    public int Channels => _channels;
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public float Peak => BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakBits));
    public bool IsRunning => _running && !_disposed;

    /// <summary>Mono float @ 48 kHz, −1…1.</summary>
    public event Action<float[]>? SamplesAvailable;

    public static WaveInCapture Open(int deviceIdZeroBased, out string status)
    {
        Exception? last = null;
        foreach (var (rate, channels) in FormatCandidates)
        {
            try
            {
                var capture = OpenExact(deviceIdZeroBased, rate, channels);
                status = channels == 1
                    ? $"open {rate / 1_000d:0.#} kHz mono"
                    : $"open {rate / 1_000d:0.#} kHz stereo→mono";
                if (rate != HostRate) status += $" → {HostRate / 1_000} kHz";
                return capture;
            }
            catch (Exception exception)
            {
                last = exception;
            }
        }
        throw last ?? new InvalidOperationException("Could not open any WaveIn format.");
    }

    private static WaveInCapture OpenExact(int deviceIdZeroBased, int sampleRate, int channels)
    {
        var bufferFrames = Math.Max(160, sampleRate * BufferMilliseconds / 1_000);
        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = (ushort)channels,
            SamplesPerSecond = (uint)sampleRate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(2 * channels),
            AverageBytesPerSecond = (uint)(sampleRate * 2 * channels),
            ExtraSize = 0
        };

        var dataEvent = new AutoResetEvent(false);
        var device = deviceIdZeroBased < 0 ? WaveMapper : (uint)deviceIdZeroBased;
        var result = waveInOpen(out var handle, device, ref format,
            dataEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEventFlag);
        if (result != 0)
        {
            dataEvent.Dispose();
            throw new InvalidOperationException($"waveInOpen ({result}) @ {sampleRate}×{channels}");
        }

        var buffers = new List<CaptureBuffer>(BufferCount);
        try
        {
            for (var i = 0; i < BufferCount; i++)
            {
                var pcm = new short[bufferFrames * channels];
                var pinned = GCHandle.Alloc(pcm, GCHandleType.Pinned);
                var headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                var header = new WaveHeader
                {
                    Data = pinned.AddrOfPinnedObject(),
                    BufferLength = (uint)(pcm.Length * sizeof(short)),
                    User = (IntPtr)i
                };
                Marshal.StructureToPtr(header, headerPtr, false);
                if (waveInPrepareHeader(handle, headerPtr, (uint)Marshal.SizeOf<WaveHeader>()) != 0)
                    throw new InvalidOperationException("waveInPrepareHeader failed.");
                if (waveInAddBuffer(handle, headerPtr, (uint)Marshal.SizeOf<WaveHeader>()) != 0)
                    throw new InvalidOperationException("waveInAddBuffer failed.");
                buffers.Add(new CaptureBuffer(headerPtr, pinned, pcm));
            }
        }
        catch
        {
            foreach (var buffer in buffers)
            {
                try { waveInUnprepareHeader(handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()); } catch { }
                Marshal.FreeHGlobal(buffer.Header);
                if (buffer.Pinned.IsAllocated) buffer.Pinned.Free();
            }
            try { waveInClose(handle); } catch { }
            dataEvent.Dispose();
            throw;
        }

        return new WaveInCapture(handle, dataEvent, sampleRate, channels, buffers);
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            var result = waveInStart(_handle);
            if (result != 0) throw new InvalidOperationException($"waveInStart failed ({result}).");
            _running = true;
            if (!_worker.IsAlive) _worker.Start();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            try { waveInStop(_handle); } catch { }
            try { waveInReset(_handle); } catch { }
        }
        try { _dataEvent.Set(); } catch { }
        try { if (_worker.IsAlive) _worker.Join(1_500); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var buffer in _buffers)
            {
                if (_handle != IntPtr.Zero)
                    try { waveInUnprepareHeader(_handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()); } catch { }
                Marshal.FreeHGlobal(buffer.Header);
                if (buffer.Pinned.IsAllocated) buffer.Pinned.Free();
            }
            _buffers.Clear();
            if (_handle != IntPtr.Zero)
            {
                try { waveInClose(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }
        try { _dataEvent.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            try { _dataEvent.WaitOne(40); }
            catch (ObjectDisposedException) { break; }
            if (_disposed) break;
            if (!_running) break;
            ProcessDoneBuffers();
        }
    }

    private void ProcessDoneBuffers()
    {
        foreach (var buffer in _buffers)
        {
            float[]? host = null;
            lock (_sync)
            {
                if (_disposed || !_running || _handle == IntPtr.Zero) return;
                var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                if ((header.Flags & HeaderDone) == 0) continue;

                var frames = (int)(header.BytesRecorded / (2u * (uint)_channels));
                if (frames <= 0) frames = _bufferFrames;
                var mono = ToMono(buffer.Pcm, frames, _channels);
                Interlocked.Add(ref _framesReceived, frames);
                UpdatePeak(mono);
                host = _deviceRate == HostRate ? mono : Resample(mono, _deviceRate, HostRate);

                header.Flags = HeaderPrepared;
                header.BytesRecorded = 0;
                Marshal.StructureToPtr(header, buffer.Header, false);
                _ = waveInAddBuffer(_handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>());
            }

            if (host is not { Length: > 0 }) continue;
            try { SamplesAvailable?.Invoke(host); }
            catch { /* ignore consumer faults on capture thread */ }
        }
    }

    private void UpdatePeak(float[] mono)
    {
        var peak = 0f;
        foreach (var s in mono)
        {
            var a = MathF.Abs(s);
            if (a > peak) peak = a;
        }
        while (true)
        {
            var cur = Volatile.Read(ref _peakBits);
            var curV = BitConverter.Int32BitsToSingle(cur);
            var next = Math.Max(peak, curV * 0.92f);
            if (Interlocked.CompareExchange(ref _peakBits, BitConverter.SingleToInt32Bits(next), cur) == cur)
                break;
        }
    }

    private static float[] ToMono(short[] interleaved, int frames, int channels)
    {
        var mono = new float[frames];
        if (channels <= 1)
        {
            for (var i = 0; i < frames; i++)
                mono[i] = interleaved[i] / 32768f;
            return mono;
        }
        for (var i = 0; i < frames; i++)
        {
            var l = interleaved[i * 2] / 32768f;
            var r = interleaved[i * 2 + 1] / 32768f;
            mono[i] = 0.5f * (l + r);
        }
        return mono;
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

    private sealed record CaptureBuffer(IntPtr Header, GCHandle Pinned, short[] Pcm);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveInOpen(out IntPtr waveIn, uint deviceId, ref WaveFormatEx format,
        IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr waveIn, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr waveIn, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr waveIn, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr waveIn);
    [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr waveIn);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr waveIn);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr waveIn);
}
