using NeuroSDR.Core;
using System.Runtime.InteropServices;
using System.Text;

namespace NeuroSDR.Hardware;

public sealed unsafe class RtlSdrSampleSource : ISampleSource, IGainControlledSampleSource, ISampleSourceMetrics, ISampleQueueMetrics
{
    private readonly object _lifecycle = new();
    private readonly IntPtr _library;
    private readonly Api _api;
    private readonly SampleBlockDispatcher _samples = new();
    private readonly ReadAsyncCallback _callback;
    private IntPtr _device;
    private Thread? _readerThread;
    private long _centerFrequency = 100_000_000;
    private int _gainPercent = 60;
    private bool _disposed;

    public string Name { get; }
    public int SampleRate => 2_048_000;
    public bool IsRunning { get; private set; }
    public long TotalSamples => _samples.TotalSamples;
    public long DeliveredSamples => _samples.DeliveredSamples;
    public long DroppedSamples => _samples.DroppedSamples;
    public long LastDeliveryAgeMilliseconds => _samples.LastDeliveryAgeMilliseconds;
    public int QueuedBlocks => _samples.QueuedBlocks;
    public int MaximumQueuedBlocks => _samples.MaximumQueuedBlocks;
    public event Action<Complex32[]>? SamplesAvailable;

    public long CenterFrequency
    {
        get => Interlocked.Read(ref _centerFrequency);
        set
        {
            value = Math.Clamp(value, 24_000_000, 1_766_000_000);
            Interlocked.Exchange(ref _centerFrequency, value);
            lock (_lifecycle)
                if (_device != IntPtr.Zero)
                    Check(_api.SetCenterFrequency(_device, (uint)value), "Set RTL-SDR frequency");
        }
    }

    public int GainPercent
    {
        get => _gainPercent;
        set
        {
            _gainPercent = Math.Clamp(value, 0, 100);
            lock (_lifecycle)
                if (_device != IntPtr.Zero) ApplyGain();
        }
    }

    private RtlSdrSampleSource(IntPtr library, Api api, string name)
    {
        _library = library;
        _api = api;
        Name = name;
        _callback = OnSamples;
        _samples.SamplesAvailable += block => SamplesAvailable?.Invoke(block);
    }

    public static bool TryCreate(out RtlSdrSampleSource? source, out string status)
    {
        source = null;
        if (!NativeLibraryLocator.TryLoad(["rtlsdr.dll", "librtlsdr.dll"], out var library, out var location, out var failure))
        {
            status = $"Failed to load the RTL-SDR {ProcessArchitecture} library: {failure}";
            return false;
        }
        try
        {
            var api = new Api(library);
            var count = api.GetDeviceCount();
            if (count == 0)
            {
                NativeLibrary.Free(library);
                status = "No RTL-SDR device connected";
                return false;
            }
            source = new RtlSdrSampleSource(library, api, BuildDeviceName(api, 0));
            status = $"{source.Name} found · {location}";
            return true;
        }
        catch (Exception exception)
        {
            NativeLibrary.Free(library);
            status = $"RTL-SDR initialization failed: {exception.Message}";
            return false;
        }
    }

    internal static string ProbeNativeLibrary()
    {
        if (!NativeLibraryLocator.TryLoad(["rtlsdr.dll", "librtlsdr.dll"], out var library, out var location, out var failure))
            return $"FAIL · {ProcessArchitecture} · {failure}";
        try
        {
            _ = new Api(library);
            return $"OK · {ProcessArchitecture} · {Path.GetFullPath(location)}";
        }
        catch (Exception exception) { return $"FAIL · {ProcessArchitecture} · {exception.GetBaseException().Message}"; }
        finally { NativeLibrary.Free(library); }
    }

    private static string ProcessArchitecture => Environment.Is64BitProcess ? "x64" : "x86";

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;
            Check(_api.Open(out _device, 0), "Open RTL-SDR");
            try
            {
                Check(_api.SetSampleRate(_device, (uint)SampleRate), "Set RTL-SDR sample rate");
                Check(_api.SetCenterFrequency(_device, (uint)CenterFrequency), "Set RTL-SDR frequency");
                _ = _api.SetAgcMode(_device, 0);
                ApplyGain();
                Check(_api.ResetBuffer(_device), "Reset RTL-SDR buffer");
                _samples.Start();
                IsRunning = true;
                _readerThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "RTL-SDR async reader",
                    Priority = ThreadPriority.Highest
                };
                _readerThread.Start();
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
        Thread? reader;
        lock (_lifecycle)
        {
            IsRunning = false;
            if (_device != IntPtr.Zero) _ = _api.CancelAsync(_device);
            reader = _readerThread;
        }
        if (reader is not null && reader != Thread.CurrentThread) reader.Join(2_000);
        lock (_lifecycle) StopCore();
    }

    private void ReadLoop()
    {
        // buf_num=0 uses librtlsdr default (15). 262144 bytes ≈ 131072 complex samples.
        _ = _api.ReadAsync(_device, _callback, IntPtr.Zero, 0, 262_144);
        IsRunning = false;
    }

    private void OnSamples(byte* buffer, uint length, IntPtr context)
    {
        if (IsRunning) _samples.WriteUnsignedInterleaved(buffer, length);
    }

    private void ApplyGain()
    {
        Check(_api.SetTunerGainMode(_device, 1), "Set RTL-SDR manual gain");
        var count = _api.GetTunerGains(_device, null);
        if (count <= 0) return;
        var gains = stackalloc int[count];
        count = _api.GetTunerGains(_device, gains);
        if (count <= 0) return;
        var index = (count - 1) * _gainPercent / 100;
        Check(_api.SetTunerGain(_device, gains[index]), "Set RTL-SDR gain");
    }

    private void StopCore()
    {
        IsRunning = false;
        _readerThread = null;
        _samples.Stop();
        if (_device != IntPtr.Zero) _ = _api.Close(_device);
        _device = IntPtr.Zero;
    }

    private static string BuildDeviceName(Api api, uint index)
    {
        var fallback = Marshal.PtrToStringAnsi(api.GetDeviceName(index)) ?? "RTL-SDR";
        var manufact = new StringBuilder(256);
        var product = new StringBuilder(256);
        var serial = new StringBuilder(256);
        if (api.GetDeviceUsbStrings(index, manufact, product, serial) != 0)
            return $"RTL-SDR ({fallback})";

        var label = string.Join(' ', new[] { manufact.ToString(), product.ToString() }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        if (string.IsNullOrWhiteSpace(label)) label = fallback;
        var sn = serial.ToString();
        return string.IsNullOrWhiteSpace(sn) ? $"RTL-SDR ({label})" : $"RTL-SDR ({label} · {sn})";
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"{operation} failed ({result})");
    }

    public void Dispose()
    {
        Stop();
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            NativeLibrary.Free(_library);
        }
        GC.SuppressFinalize(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetDeviceCount();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetDeviceName(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceUsbStrings(uint index, StringBuilder manufact, StringBuilder product, StringBuilder serial);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(out IntPtr device, uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceCall(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetUInt(IntPtr device, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetInt(IntPtr device, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetTunerGains(IntPtr device, int* gains);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReadAsyncCallback(byte* buffer, uint length, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadAsync(IntPtr device, ReadAsyncCallback callback, IntPtr context, uint bufferCount, uint bufferLength);

    private sealed class Api
    {
        public readonly GetDeviceCount GetDeviceCount;
        public readonly GetDeviceName GetDeviceName;
        public readonly GetDeviceUsbStrings GetDeviceUsbStrings;
        public readonly Open Open;
        public readonly DeviceCall Close, ResetBuffer, CancelAsync;
        public readonly SetUInt SetCenterFrequency, SetSampleRate;
        public readonly SetInt SetTunerGainMode, SetTunerGain, SetAgcMode;
        public readonly GetTunerGains GetTunerGains;
        public readonly ReadAsync ReadAsync;

        public Api(IntPtr library)
        {
            GetDeviceCount = Get<GetDeviceCount>(library, "rtlsdr_get_device_count");
            GetDeviceName = Get<GetDeviceName>(library, "rtlsdr_get_device_name");
            GetDeviceUsbStrings = Get<GetDeviceUsbStrings>(library, "rtlsdr_get_device_usb_strings");
            Open = Get<Open>(library, "rtlsdr_open");
            Close = Get<DeviceCall>(library, "rtlsdr_close");
            SetCenterFrequency = Get<SetUInt>(library, "rtlsdr_set_center_freq");
            SetSampleRate = Get<SetUInt>(library, "rtlsdr_set_sample_rate");
            SetTunerGainMode = Get<SetInt>(library, "rtlsdr_set_tuner_gain_mode");
            GetTunerGains = Get<GetTunerGains>(library, "rtlsdr_get_tuner_gains");
            SetTunerGain = Get<SetInt>(library, "rtlsdr_set_tuner_gain");
            SetAgcMode = Get<SetInt>(library, "rtlsdr_set_agc_mode");
            ResetBuffer = Get<DeviceCall>(library, "rtlsdr_reset_buffer");
            ReadAsync = Get<ReadAsync>(library, "rtlsdr_read_async");
            CancelAsync = Get<DeviceCall>(library, "rtlsdr_cancel_async");
        }
        private static T Get<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }
}
