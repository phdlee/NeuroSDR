using System.Runtime.InteropServices;

namespace NeuroSDR.Audio;

public sealed class WaveOutPlayer : IDisposable
{
    private const uint CallbackFunction = 0x00030000;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WaveOutDone = 0x03BD;
    private const uint HeaderDone = 0x00000001;
    private const int BufferSamples = 960; // 20 ms at 48 kHz
    private readonly object _sync = new();
    private readonly WaveCallback _callback;
    private readonly AutoResetEvent _completionEvent = new(false);
    private readonly Dictionary<IntPtr, GCHandle> _buffers = [];
    private readonly Queue<short[]> _preRoll = [];
    private readonly short[] _staging;
    private readonly Thread _cleanupThread;
    private readonly int _pendingBufferCapacity;
    private readonly int _prerollBuffers;
    private readonly bool _restartOnStarvation;
    private readonly int _channels;
    private readonly int _frameSamples;
    private IntPtr _handle;
    private int _stagingCount;
    private volatile bool _shutdown;
    private bool _disposed;
    private bool _playbackStarted, _hadPendingBuffers;
    private long _submittedBuffers, _completedBuffers, _droppedBuffers;
    private long _starvationEvents, _lastSubmitTimestamp, _maximumSubmitGapTicks;
    private int _minimumPendingBuffers = int.MaxValue, _maximumPendingBuffers;
    private int _maximumInputPeakBits;

    public int VolumePercent { get; set; } = 45;
    public int Channels => _channels;
    public bool IsOpen => _handle != IntPtr.Zero;
    public long SubmittedBuffers => Interlocked.Read(ref _submittedBuffers);
    public long CompletedBuffers => Interlocked.Read(ref _completedBuffers);
    public long DroppedBuffers => Interlocked.Read(ref _droppedBuffers);
    public long StarvationEvents => Interlocked.Read(ref _starvationEvents);
    public double MaximumSubmitGapMilliseconds => Interlocked.Read(ref _maximumSubmitGapTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
    public int MinimumPendingBuffers => Volatile.Read(ref _minimumPendingBuffers) == int.MaxValue ? 0 : Volatile.Read(ref _minimumPendingBuffers);
    public int MaximumPendingBuffers => Volatile.Read(ref _maximumPendingBuffers);
    public int PendingBuffers { get { lock (_sync) return _buffers.Count; } }
    public float MaximumInputPeak => BitConverter.Int32BitsToSingle(Volatile.Read(ref _maximumInputPeakBits));

    public WaveOutPlayer(int sampleRate, int deviceId = -1, int maximumPendingBuffers = 32,
        int prerollBuffers = 10, bool restartOnStarvation = true, int channels = 1)
    {
        _pendingBufferCapacity = Math.Clamp(maximumPendingBuffers, 12, 128);
        _prerollBuffers = Math.Clamp(prerollBuffers, 1, 30);
        _restartOnStarvation = restartOnStarvation;
        _channels = channels is 2 ? 2 : 1;
        _frameSamples = BufferSamples * _channels;
        _callback = OnWaveMessage;
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = (ushort)_channels,
            SamplesPerSecond = (uint)sampleRate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(2 * _channels),
            AverageBytesPerSecond = (uint)(sampleRate * 2 * _channels)
        };
        var selectedDevice = deviceId < 0 ? WaveMapper : (uint)deviceId;
        var result = waveOutOpen(out _handle, selectedDevice, ref format, _callback, IntPtr.Zero, CallbackFunction);
        if (result != 0) throw new InvalidOperationException($"Could not open the Windows audio device. waveOut error {result}");

        // Staging holds one waveOut block: mono frames, or interleaved stereo frames.
        _staging = new short[_frameSamples];
        _cleanupThread = new Thread(CleanupLoop) { IsBackground = true, Name = "NeuroSDR waveOut cleanup" };
        _cleanupThread.Start();
    }

    public static IReadOnlyList<WaveOutDeviceInfo> EnumerateDevices()
    {
        var devices = new List<WaveOutDeviceInfo> { new(-1, "Windows Default Output") };
        var count = waveOutGetNumDevs();
        for (uint id = 0; id < count; id++)
        {
            if (waveOutGetDevCaps(id, out var caps, (uint)Marshal.SizeOf<WaveOutCaps>()) == 0)
                devices.Add(new WaveOutDeviceInfo((int)id, string.IsNullOrWhiteSpace(caps.Name) ? $"WaveOut {id}" : caps.Name.Trim()));
        }
        return devices;
    }

    public void Write(float[] samples) => Write(samples.AsSpan());

    public void Write(ReadOnlySpan<float> samples)
    {
        if (_disposed || _handle == IntPtr.Zero || samples.Length == 0) return;
        lock (_sync)
        {
            if (_disposed || _handle == IntPtr.Zero) return;
            var gain = VolumePercent / 100f;
            foreach (var sample in samples)
            {
                var peak = MathF.Abs(sample * gain);
                var current = MaximumInputPeak;
                while (peak > current)
                {
                    var observed = Interlocked.CompareExchange(ref _maximumInputPeakBits,
                        BitConverter.SingleToInt32Bits(peak), BitConverter.SingleToInt32Bits(current));
                    if (observed == BitConverter.SingleToInt32Bits(current)) break;
                    current = BitConverter.Int32BitsToSingle(observed);
                }
                _staging[_stagingCount++] = (short)(Math.Clamp(sample * gain, -1f, 1f) * short.MaxValue);
                if (_stagingCount != _staging.Length) continue;
                SubmitStagingLocked();
                _stagingCount = 0;
            }
        }
    }

    private void SubmitStagingLocked()
    {
        // RF devices can deliver several hundred milliseconds in a burst at
        // their widest capture rates. Keep enough queued audio to absorb that
        // jitter; steady streams still remain near the normal small pending count.
        if (_buffers.Count + _preRoll.Count >= _pendingBufferCapacity)
        {
            Interlocked.Increment(ref _droppedBuffers);
            return;
        }

        var pcm = (short[])_staging.Clone();
        if (!_playbackStarted)
        {
            _preRoll.Enqueue(pcm);
            if (_preRoll.Count < _prerollBuffers) return;
            _playbackStarted = true;
            while (_preRoll.TryDequeue(out var buffered)) SubmitPcmLocked(buffered);
            return;
        }
        SubmitPcmLocked(pcm);
    }

    private void SubmitPcmLocked(short[] pcm)
    {
        var pinned = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        var headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        var header = new WaveHeader { Data = pinned.AddrOfPinnedObject(), BufferLength = (uint)(pcm.Length * sizeof(short)) };
        Marshal.StructureToPtr(header, headerPointer, false);
        if (waveOutPrepareHeader(_handle, headerPointer, (uint)Marshal.SizeOf<WaveHeader>()) != 0)
        {
            Marshal.FreeHGlobal(headerPointer);
            pinned.Free();
            Interlocked.Increment(ref _droppedBuffers);
            return;
        }

        _buffers[headerPointer] = pinned; // Register before waveOutWrite so a fast completion callback cannot race.
        if (waveOutWrite(_handle, headerPointer, (uint)Marshal.SizeOf<WaveHeader>()) == 0)
        {
            var submitted = Interlocked.Increment(ref _submittedBuffers);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var previous = Interlocked.Exchange(ref _lastSubmitTimestamp, now);
            if (previous != 0) UpdateMaximum(ref _maximumSubmitGapTicks, now - previous);
            if (submitted >= 3) UpdatePendingRange(_buffers.Count);
            _hadPendingBuffers = true;
            return;
        }

        _buffers.Remove(headerPointer);
        _ = waveOutUnprepareHeader(_handle, headerPointer, (uint)Marshal.SizeOf<WaveHeader>());
        Marshal.FreeHGlobal(headerPointer);
        pinned.Free();
        Interlocked.Increment(ref _droppedBuffers);
    }

    // Only the SetEvent-style signal that Microsoft documents. Do not call other waveOut functions here.
    private void OnWaveMessage(IntPtr waveOut, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2)
    {
        if (message == WaveOutDone) _completionEvent.Set();
    }

    private void CleanupLoop()
    {
        try
        {
            while (!_shutdown)
            {
                try { _completionEvent.WaitOne(100); }
                catch (ObjectDisposedException) { break; }
                CleanupCompletedBuffers(false);
            }
            CleanupCompletedBuffers(true);
        }
        catch (ObjectDisposedException) { }
    }

    private void CleanupCompletedBuffers(bool force)
    {
        lock (_sync)
        {
            foreach (var headerPointer in _buffers.Keys.ToArray())
            {
                var header = Marshal.PtrToStructure<WaveHeader>(headerPointer);
                if (!force && (header.Flags & HeaderDone) == 0) continue;
                var result = waveOutUnprepareHeader(_handle, headerPointer, (uint)Marshal.SizeOf<WaveHeader>());
                if (result != 0) continue;
                var pinned = _buffers[headerPointer];
                _buffers.Remove(headerPointer);
                Marshal.FreeHGlobal(headerPointer);
                if (pinned.IsAllocated) pinned.Free();
                Interlocked.Increment(ref _completedBuffers);
            }
            if (!force && !_shutdown && _buffers.Count == 0 && _hadPendingBuffers)
            {
                Interlocked.Increment(ref _starvationEvents);
                _hadPendingBuffers = false;
                if (_restartOnStarvation) _playbackStarted = false;
            }
            if (Interlocked.Read(ref _submittedBuffers) >= 3) UpdatePendingRange(_buffers.Count);
        }
    }

    private void UpdatePendingRange(int pending)
    {
        var minimum = Volatile.Read(ref _minimumPendingBuffers);
        while (pending < minimum)
        {
            var observed = Interlocked.CompareExchange(ref _minimumPendingBuffers, pending, minimum);
            if (observed == minimum) break;
            minimum = observed;
        }
        var maximum = Volatile.Read(ref _maximumPendingBuffers);
        while (pending > maximum)
        {
            var observed = Interlocked.CompareExchange(ref _maximumPendingBuffers, pending, maximum);
            if (observed == maximum) break;
            maximum = observed;
        }
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown = true;
            if (_handle != IntPtr.Zero) _ = waveOutReset(_handle);
        }
        try { _completionEvent.Set(); } catch (ObjectDisposedException) { }
        _cleanupThread.Join(2_000);
        lock (_sync)
        {
            CleanupCompletedBuffers(true);
            _preRoll.Clear();
            if (_handle != IntPtr.Zero)
            {
                _ = waveOutClose(_handle);
                _handle = IntPtr.Zero;
            }
        }
        try { _completionEvent.Dispose(); } catch (ObjectDisposedException) { }
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort ManufacturerId, ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
        public uint Support;
    }

    private delegate void WaveCallback(IntPtr waveOut, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2);
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr waveOut, uint deviceId, ref WaveFormat format, WaveCallback callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr waveOut, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr waveOut, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr waveOut, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr waveOut);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr waveOut);
    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")]
    private static extern uint waveOutGetDevCaps(uint deviceId, out WaveOutCaps caps, uint size);
}

public sealed record WaveOutDeviceInfo(int Id, string Name)
{
    public override string ToString() => Name;
}
