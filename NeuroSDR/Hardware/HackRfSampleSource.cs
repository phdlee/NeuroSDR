using NeuroSDR.Core;
using System.Runtime.InteropServices;

namespace NeuroSDR.Hardware;

public sealed unsafe class HackRfSampleSource : ISampleSource, IGainControlledSampleSource, IRfAmplifierSampleSource, ISampleSourceMetrics, ISampleQueueMetrics, IConfigurableSampleRateSource
{
    private static readonly int[] ModernSampleRates = [2_000_000, 5_000_000, 8_000_000, 10_000_000, 15_000_000, 20_000_000];
    private static readonly int[] LegacySampleRates = [2_000_000, 5_000_000, 8_000_000, 10_000_000, 15_000_000, 20_000_000];
    private readonly object _lifecycle = new();
    private readonly IntPtr _library;
    private readonly Api _api;
    // High-rate HackRF firmware delivers USB transfers in large bursts. At
    // 20 MS/s a 32-block queue is only ~105 ms and can overflow despite DSP
    // being faster than the long-term input rate. This firmware has produced
    // bursts above 128 blocks, so 256 blocks absorb up to ~840 ms on demand.
    private readonly SampleBlockDispatcher _samples = new(32_768, 256);
    private readonly RxCallback _callback;
    private readonly int _sampleRateCommandMultiplier;
    private readonly IReadOnlyList<int> _supportedSampleRates;
    private IntPtr _device;
    private long _centerFrequency = 100_000_000;
    private int _gainPercent = 60;
    private bool _rfAmplifierEnabled;
    private int _sampleRate = 2_000_000;
    private bool _initialized = true, _disposed;

    public string Name => "HackRF One";
    public int SampleRate => Volatile.Read(ref _sampleRate);
    public IReadOnlyList<int> SupportedSampleRates => _supportedSampleRates;
    public int ConfiguredSampleRate
    {
        get => SampleRate;
        set
        {
            if (IsRunning) throw new InvalidOperationException("The HackRF sample rate cannot be changed while receiving.");
            if (!_supportedSampleRates.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _samples.ConfigureBlockSize(value >= 8_000_000 ? 65_536 : 32_768);
            Volatile.Write(ref _sampleRate, value);
        }
    }
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
            value = Math.Clamp(value, 1_000_000, 6_000_000_000);
            Interlocked.Exchange(ref _centerFrequency, value);
            lock (_lifecycle)
                if (_device != IntPtr.Zero) Check(_api.SetFrequency(_device, (ulong)value), "Set HackRF frequency");
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

    public bool RfAmplifierEnabled
    {
        get => _rfAmplifierEnabled;
        set
        {
            _rfAmplifierEnabled = value;
            lock (_lifecycle)
                if (_device != IntPtr.Zero)
                    Check(_api.SetAmpEnable(_device, value ? (byte)1 : (byte)0), "HackRF RF amplifier setting");
        }
    }

    private HackRfSampleSource(IntPtr library, Api api, ushort usbApiVersion)
    {
        _library = library;
        _api = api;
        _callback = OnSamples;
        // This connected API 1.03-era firmware delivers exactly half of the
        // commanded quadrature rate on both old and current Windows hosts.
        // Command twice the requested rate so the public/DSP rate stays true.
        _sampleRateCommandMultiplier = usbApiVersion <= 0x0103 ? 2 : 1;
        _supportedSampleRates = _sampleRateCommandMultiplier == 2 ? LegacySampleRates : ModernSampleRates;
        _samples.SamplesAvailable += block => SamplesAvailable?.Invoke(block);
    }

    public static bool TryCreate(out HackRfSampleSource? source, out string status)
    {
        source = null;
        if (!NativeLibraryLocator.TryLoad(["hackrf.dll", "libhackrf.dll"], out var library, out var location))
        {
            status = "HackRF library not found";
            return false;
        }
        try
        {
            var api = new Api(library);
            Check(api.Init(), "Initialize HackRF API");
            var result = api.Open(out var device);
            if (result != 0)
            {
                _ = api.Exit();
                NativeLibrary.Free(library);
                status = "No HackRF device connected";
                return false;
            }
            ushort usbApiVersion = 0;
            _ = api.ReadUsbApiVersion(device, out usbApiVersion);
            _ = api.Close(device);
            source = new HackRfSampleSource(library, api, usbApiVersion);
            var compatibility = usbApiVersion <= 0x0103 ? " · legacy rate compensation" : string.Empty;
            status = $"HackRF found · {location} · USB API {usbApiVersion >> 8}.{usbApiVersion & 0xff:00}{compatibility}";
            return true;
        }
        catch (Exception exception)
        {
            NativeLibrary.Free(library);
            status = $"HackRF initialization failed: {exception.Message}";
            return false;
        }
    }

    public static bool TryResetConnectedDevice(out string status)
    {
        status = string.Empty;
        if (!NativeLibraryLocator.TryLoad(["hackrf.dll", "libhackrf.dll"], out var library, out _))
        {
            status = "HackRF library not found";
            return false;
        }
        Api? api = null;
        IntPtr device = IntPtr.Zero;
        try
        {
            api = new Api(library);
            Check(api.Init(), "HackRF API initialization");
            Check(api.Open(out device), "HackRF open");
            Check(api.Reset(device), "HackRF reset");
            status = "HackRF reset requested";
            return true;
        }
        catch (Exception exception)
        {
            status = exception.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (device != IntPtr.Zero && api is not null) _ = api.Close(device);
            if (api is not null) _ = api.Exit();
            NativeLibrary.Free(library);
        }
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;
            Check(_api.Open(out _device), "Open HackRF");
            try
            {
                Check(_api.SetSampleRate(_device, DeviceSampleRateCommand()), "Set HackRF sample rate");
                Check(_api.SetBasebandBandwidth(_device, BasebandBandwidthFor(SampleRate)), "Set HackRF baseband filter");
                Check(_api.SetFrequency(_device, (ulong)CenterFrequency), "Set HackRF frequency");
                Check(_api.SetAmpEnable(_device, RfAmplifierEnabled ? (byte)1 : (byte)0), "HackRF RF amplifier setting");
                ApplyGain();
                _samples.Start();
                IsRunning = true;
                Check(_api.StartRx(_device, _callback, IntPtr.Zero), "Start HackRF reception");
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
        lock (_lifecycle) StopCore();
    }

    private int OnSamples(Transfer* transfer)
    {
        if (!IsRunning || transfer is null || transfer->Buffer == IntPtr.Zero) return IsRunning ? 0 : -1;
        _samples.WriteSignedInterleaved((sbyte*)transfer->Buffer, transfer->ValidLength);
        return 0;
    }

    private void ApplyGain()
    {
        var lna = (uint)(Math.Round((_gainPercent * 40d / 100) / 8) * 8);
        var vga = (uint)(Math.Round((_gainPercent * 62d / 100) / 2) * 2);
        Check(_api.SetLnaGain(_device, Math.Min(lna, 40)), "Set HackRF LNA gain");
        Check(_api.SetVgaGain(_device, Math.Min(vga, 62)), "Set HackRF VGA gain");
    }

    private static uint BasebandBandwidthFor(int sampleRate) => sampleRate switch
    {
        <= 2_000_000 => 1_750_000,
        <= 5_000_000 => 5_000_000,
        <= 8_000_000 => 8_000_000,
        <= 10_000_000 => 10_000_000,
        <= 15_000_000 => 15_000_000,
        _ => 20_000_000
    };

    private double DeviceSampleRateCommand()
    {
        if (_sampleRateCommandMultiplier == 1) return SampleRate;
        // This API 1.03 firmware has a non-linear divider above 20 MHz.
        // The 15 MS/s point is calibrated separately; other verified points
        // retain the exact 2x command.
        return SampleRate == 15_000_000 ? 24_770_000d : SampleRate * 2d;
    }

    private void StopCore()
    {
        IsRunning = false;
        if (_device != IntPtr.Zero) _ = _api.StopRx(_device);
        _samples.Stop();
        if (_device != IntPtr.Zero) _ = _api.Close(_device);
        _device = IntPtr.Zero;
    }

    private static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"{operation} failed ({result})");
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            StopCore();
            if (_disposed) return;
            _disposed = true;
            if (_initialized) _ = _api.Exit();
            _initialized = false;
            NativeLibrary.Free(_library);
        }
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Transfer
    {
        public IntPtr Device;
        public IntPtr Buffer;
        public int BufferLength;
        public int ValidLength;
        public IntPtr RxContext;
        public IntPtr TxContext;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArg();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceCall(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDouble(IntPtr device, double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadUsbApiVersion(IntPtr device, out ushort version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetUInt(IntPtr device, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetByte(IntPtr device, byte value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetULong(IntPtr device, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RxCallback(Transfer* transfer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartRx(IntPtr device, RxCallback callback, IntPtr context);

    private sealed class Api
    {
        public readonly NoArg Init, Exit;
        public readonly Open Open;
        public readonly DeviceCall Close, StopRx;
        public readonly DeviceCall Reset;
        public readonly SetDouble SetSampleRate;
        public readonly ReadUsbApiVersion ReadUsbApiVersion;
        public readonly SetUInt SetBasebandBandwidth, SetLnaGain, SetVgaGain;
        public readonly SetByte SetAmpEnable;
        public readonly SetULong SetFrequency;
        public readonly StartRx StartRx;

        public Api(IntPtr library)
        {
            Init = Get<NoArg>(library, "hackrf_init");
            Exit = Get<NoArg>(library, "hackrf_exit");
            Open = Get<Open>(library, "hackrf_open");
            Close = Get<DeviceCall>(library, "hackrf_close");
            Reset = Get<DeviceCall>(library, "hackrf_reset");
            SetSampleRate = Get<SetDouble>(library, "hackrf_set_sample_rate");
            ReadUsbApiVersion = Get<ReadUsbApiVersion>(library, "hackrf_usb_api_version_read");
            SetBasebandBandwidth = Get<SetUInt>(library, "hackrf_set_baseband_filter_bandwidth");
            SetFrequency = Get<SetULong>(library, "hackrf_set_freq");
            SetLnaGain = Get<SetUInt>(library, "hackrf_set_lna_gain");
            SetVgaGain = Get<SetUInt>(library, "hackrf_set_vga_gain");
            SetAmpEnable = Get<SetByte>(library, "hackrf_set_amp_enable");
            StartRx = Get<StartRx>(library, "hackrf_start_rx");
            StopRx = Get<DeviceCall>(library, "hackrf_stop_rx");
        }
        private static T Get<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }
}
