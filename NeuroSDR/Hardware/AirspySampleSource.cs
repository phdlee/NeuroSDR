using NeuroSDR.Core;
using System.Runtime.InteropServices;

namespace NeuroSDR.Hardware;

/// <summary>Airspy R2 / Mini via libairspy (FLOAT32 interleaved IQ).</summary>
public sealed unsafe class AirspySampleSource : ISampleSource, IGainControlledSampleSource, IHardwareAgcSampleSource,
    IRfAmplifierSampleSource, ISampleSourceMetrics, ISampleQueueMetrics, IConfigurableSampleRateSource
{
    private static readonly int[] DefaultSampleRates = [2_500_000, 10_000_000];
    private readonly object _lifecycle = new();
    private readonly IntPtr _library;
    private readonly Api _api;
    private readonly SampleBlockDispatcher _samples = new(32_768, 64);
    private readonly SampleCallback _callback;
    private readonly IReadOnlyList<int> _supportedSampleRates;
    private IntPtr _device;
    private long _centerFrequency = 100_000_000;
    private int _gainPercent = 60;
    private bool _hardwareAgc;
    private bool _rfAmplifierEnabled;
    private int _sampleRate = 2_500_000;
    private bool _initialized = true, _disposed;

    public string Name => "Airspy";
    public int SampleRate => Volatile.Read(ref _sampleRate);
    public IReadOnlyList<int> SupportedSampleRates => _supportedSampleRates;
    public int ConfiguredSampleRate
    {
        get => SampleRate;
        set
        {
            if (IsRunning) throw new InvalidOperationException("The Airspy sample rate cannot be changed while receiving.");
            if (!_supportedSampleRates.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _samples.ConfigureBlockSize(value >= 6_000_000 ? 65_536 : 32_768);
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
            value = Math.Clamp(value, 24_000_000, 1_800_000_000);
            Interlocked.Exchange(ref _centerFrequency, value);
            lock (_lifecycle)
                if (_device != IntPtr.Zero) Check(_api.SetFreq(_device, (uint)value), "Set Airspy frequency");
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

    public bool HardwareAgcEnabled
    {
        get => _hardwareAgc;
        set
        {
            _hardwareAgc = value;
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
                    Check(_api.SetRfBias(_device, value ? (byte)1 : (byte)0), "Airspy bias-T / RF amp");
        }
    }

    private AirspySampleSource(IntPtr library, Api api, IReadOnlyList<int> rates)
    {
        _library = library;
        _api = api;
        _supportedSampleRates = rates.Count > 0 ? rates : DefaultSampleRates;
        if (_supportedSampleRates.Contains(2_500_000)) _sampleRate = 2_500_000;
        else _sampleRate = _supportedSampleRates[0];
        _callback = OnSamples;
        _samples.SamplesAvailable += block => SamplesAvailable?.Invoke(block);
    }

    public static bool TryCreate(out AirspySampleSource? source, out string status)
    {
        source = null;
        if (!NativeLibraryLocator.TryLoad(["airspy.dll", "libairspy.dll"], out var library, out var location, out var failure))
        {
            status = $"Airspy library not found ({failure})";
            return false;
        }
        try
        {
            var api = new Api(library);
            Check(api.Init(), "Initialize Airspy API");
            var open = api.Open(out var device);
            if (open != 0)
            {
                _ = api.Exit();
                NativeLibrary.Free(library);
                status = "No Airspy device connected";
                return false;
            }

            var rates = ReadSampleRates(api, device);
            _ = api.Close(device);
            source = new AirspySampleSource(library, api, rates);
            status = $"Airspy found · {location}";
            return true;
        }
        catch (Exception exception)
        {
            NativeLibrary.Free(library);
            status = $"Airspy initialization failed: {exception.Message}";
            return false;
        }
    }

    internal static string ProbeNativeLibrary()
    {
        if (!NativeLibraryLocator.TryLoad(["airspy.dll", "libairspy.dll"], out var library, out var location, out var failure))
            return $"FAIL · {(Environment.Is64BitProcess ? "x64" : "x86")} · {failure}";
        try
        {
            _ = new Api(library);
            return $"OK · {(Environment.Is64BitProcess ? "x64" : "x86")} · {Path.GetFullPath(location)}";
        }
        catch (Exception exception) { return $"FAIL · {exception.GetBaseException().Message}"; }
        finally { NativeLibrary.Free(library); }
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) return;
            Check(_api.Open(out _device), "Open Airspy");
            try
            {
                Check(_api.SetSampleType(_device, SampleTypeFloat32Iq), "Set Airspy sample type");
                Check(_api.SetSampleRate(_device, (uint)SampleRate), "Set Airspy sample rate");
                Check(_api.SetFreq(_device, (uint)CenterFrequency), "Set Airspy frequency");
                Check(_api.SetRfBias(_device, RfAmplifierEnabled ? (byte)1 : (byte)0), "Airspy bias-T");
                ApplyGain();
                _samples.Start();
                IsRunning = true;
                Check(_api.StartRx(_device, _callback, IntPtr.Zero), "Start Airspy reception");
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
        if (!IsRunning || transfer is null || transfer->Samples == IntPtr.Zero || transfer->SampleCount <= 0)
            return IsRunning ? 0 : -1;
        if (transfer->SampleType == SampleTypeFloat32Iq)
            _samples.WriteFloatInterleaved((float*)transfer->Samples, transfer->SampleCount);
        else if (transfer->SampleType == SampleTypeInt16Iq)
            _samples.WriteInt16Interleaved((short*)transfer->Samples, transfer->SampleCount);
        return 0;
    }

    private void ApplyGain()
    {
        if (_hardwareAgc)
        {
            Check(_api.SetLnaAgc(_device, 1), "Airspy LNA AGC");
            Check(_api.SetMixerAgc(_device, 1), "Airspy mixer AGC");
            return;
        }
        Check(_api.SetLnaAgc(_device, 0), "Airspy LNA AGC off");
        Check(_api.SetMixerAgc(_device, 0), "Airspy mixer AGC off");
        // Sensitivity preset 0..21 maps well to a single UI gain slider.
        var sensitivity = (byte)Math.Clamp((int)Math.Round(_gainPercent * 21d / 100d), 0, 21);
        Check(_api.SetSensitivityGain(_device, sensitivity), "Airspy sensitivity gain");
    }

    private void StopCore()
    {
        IsRunning = false;
        if (_device != IntPtr.Zero) _ = _api.StopRx(_device);
        _samples.Stop();
        if (_device != IntPtr.Zero) _ = _api.Close(_device);
        _device = IntPtr.Zero;
    }

    private static IReadOnlyList<int> ReadSampleRates(Api api, IntPtr device)
    {
        uint count = 0;
        if (api.GetSampleRates(device, &count, 0) != 0 || count == 0) return DefaultSampleRates;
        var buffer = stackalloc uint[(int)Math.Min(count, 32)];
        if (api.GetSampleRates(device, buffer, Math.Min(count, 32)) != 0) return DefaultSampleRates;
        var list = new List<int>((int)count);
        for (var i = 0; i < (int)Math.Min(count, 32); i++)
            if (buffer[i] is >= 1_000_000 and <= 20_000_000)
                list.Add((int)buffer[i]);
        list.Sort();
        return list.Count > 0 ? list : DefaultSampleRates;
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

    private const int SampleTypeFloat32Iq = 0;
    private const int SampleTypeInt16Iq = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Transfer
    {
        public IntPtr Device;
        public IntPtr Ctx;
        public IntPtr Samples;
        public int SampleCount;
        public ulong DroppedSamples;
        public int SampleType;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArg();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceCall(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetUInt(IntPtr device, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetByte(IntPtr device, byte value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate int GetSampleRates(IntPtr device, uint* buffer, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSampleType(IntPtr device, int sampleType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SampleCallback(Transfer* transfer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartRx(IntPtr device, SampleCallback callback, IntPtr context);

    private sealed class Api
    {
        public readonly NoArg Init, Exit;
        public readonly Open Open;
        public readonly DeviceCall Close, StopRx;
        public readonly SetUInt SetSampleRate, SetFreq;
        public readonly GetSampleRates GetSampleRates;
        public readonly SetSampleType SetSampleType;
        public readonly SetByte SetLnaAgc, SetMixerAgc, SetSensitivityGain, SetRfBias;
        public readonly StartRx StartRx;

        public Api(IntPtr library)
        {
            Init = Get<NoArg>(library, "airspy_init");
            Exit = Get<NoArg>(library, "airspy_exit");
            Open = Get<Open>(library, "airspy_open");
            Close = Get<DeviceCall>(library, "airspy_close");
            SetSampleRate = Get<SetUInt>(library, "airspy_set_samplerate");
            GetSampleRates = Get<GetSampleRates>(library, "airspy_get_samplerates");
            SetSampleType = Get<SetSampleType>(library, "airspy_set_sample_type");
            SetFreq = Get<SetUInt>(library, "airspy_set_freq");
            SetLnaAgc = Get<SetByte>(library, "airspy_set_lna_agc");
            SetMixerAgc = Get<SetByte>(library, "airspy_set_mixer_agc");
            SetSensitivityGain = Get<SetByte>(library, "airspy_set_sensitivity_gain");
            SetRfBias = Get<SetByte>(library, "airspy_set_rf_bias");
            StartRx = Get<StartRx>(library, "airspy_start_rx");
            StopRx = Get<DeviceCall>(library, "airspy_stop_rx");
        }

        private static T Get<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }
}
