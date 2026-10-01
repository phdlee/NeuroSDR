using NeuroSDR.Core;
using System.Runtime.InteropServices;

namespace NeuroSDR.Hardware;

/// <summary>Airspy HF+ / Discovery via libairspyhf (float complex IQ).</summary>
public sealed unsafe class AirspyHfSampleSource : ISampleSource, IGainControlledSampleSource, IHardwareAgcSampleSource,
    ISampleSourceMetrics, ISampleQueueMetrics, IConfigurableSampleRateSource
{
    private static readonly int[] DefaultSampleRates = [768_000, 256_000, 192_000];
    private readonly object _lifecycle = new();
    private readonly IntPtr _library;
    private readonly Api _api;
    private readonly SampleBlockDispatcher _samples = new(16_384, 32);
    private readonly SampleCallback _callback;
    private readonly IReadOnlyList<int> _supportedSampleRates;
    private IntPtr _device;
    private long _centerFrequency = 7_200_000;
    private int _gainPercent = 60;
    private bool _hardwareAgc = true;
    private int _sampleRate = 768_000;
    private bool _disposed;

    public string Name => "Airspy HF+";
    public int SampleRate => Volatile.Read(ref _sampleRate);
    public IReadOnlyList<int> SupportedSampleRates => _supportedSampleRates;
    public int ConfiguredSampleRate
    {
        get => SampleRate;
        set
        {
            if (IsRunning) throw new InvalidOperationException("The Airspy HF+ sample rate cannot be changed while receiving.");
            if (!_supportedSampleRates.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
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
            value = Math.Clamp(value, 0, 31_000_000);
            Interlocked.Exchange(ref _centerFrequency, value);
            lock (_lifecycle)
                if (_device != IntPtr.Zero) Check(_api.SetFreq(_device, (uint)value), "Set Airspy HF+ frequency");
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

    private AirspyHfSampleSource(IntPtr library, Api api, IReadOnlyList<int> rates)
    {
        _library = library;
        _api = api;
        _supportedSampleRates = rates.Count > 0 ? rates : DefaultSampleRates;
        _sampleRate = _supportedSampleRates[0];
        _callback = OnSamples;
        _samples.SamplesAvailable += block => SamplesAvailable?.Invoke(block);
    }

    public static bool TryCreate(out AirspyHfSampleSource? source, out string status)
    {
        source = null;
        if (!NativeLibraryLocator.TryLoad(["airspyhf.dll", "libairspyhf.dll"], out var library, out var location, out var failure))
        {
            status = $"Airspy HF+ library not found ({failure})";
            return false;
        }
        try
        {
            var api = new Api(library);
            var open = api.Open(out var device);
            if (open != 0)
            {
                NativeLibrary.Free(library);
                status = "No Airspy HF+ device connected";
                return false;
            }

            var rates = ReadSampleRates(api, device);
            _ = api.Close(device);
            source = new AirspyHfSampleSource(library, api, rates);
            status = $"Airspy HF+ found · {location}";
            return true;
        }
        catch (Exception exception)
        {
            NativeLibrary.Free(library);
            status = $"Airspy HF+ initialization failed: {exception.Message}";
            return false;
        }
    }

    internal static string ProbeNativeLibrary()
    {
        if (!NativeLibraryLocator.TryLoad(["airspyhf.dll", "libairspyhf.dll"], out var library, out var location, out var failure))
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
            Check(_api.Open(out _device), "Open Airspy HF+");
            try
            {
                Check(_api.SetSampleRate(_device, (uint)SampleRate), "Set Airspy HF+ sample rate");
                Check(_api.SetFreq(_device, (uint)CenterFrequency), "Set Airspy HF+ frequency");
                ApplyGain();
                _samples.Start();
                IsRunning = true;
                Check(_api.StartRx(_device, _callback, IntPtr.Zero), "Start Airspy HF+ reception");
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
        // libairspyhf delivers interleaved float complex (re, im).
        _samples.WriteFloatInterleaved((float*)transfer->Samples, transfer->SampleCount);
        return 0;
    }

    private void ApplyGain()
    {
        if (_api.SetHfAgc is not null)
            Check(_api.SetHfAgc(_device, _hardwareAgc ? (byte)1 : (byte)0), "Airspy HF+ AGC");
        if (_hardwareAgc) return;
        // Attenuation 0..8 (higher = more attenuation). Invert UI gain.
        if (_api.SetHfAtt is not null)
        {
            var att = (byte)Math.Clamp(8 - (int)Math.Round(_gainPercent * 8d / 100d), 0, 8);
            Check(_api.SetHfAtt(_device, att), "Airspy HF+ attenuation");
        }
        if (_api.SetHfLna is not null)
            Check(_api.SetHfLna(_device, _gainPercent >= 70 ? (byte)1 : (byte)0), "Airspy HF+ LNA");
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
            if (buffer[i] is >= 48_000 and <= 10_000_000)
                list.Add((int)buffer[i]);
        list.Sort();
        list.Reverse(); // prefer highest native rate first
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
            NativeLibrary.Free(_library);
        }
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Transfer
    {
        public IntPtr Device;
        public IntPtr Ctx;
        public IntPtr Samples;
        public int SampleCount;
        public ulong DroppedSamples;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Open(out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceCall(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetUInt(IntPtr device, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetByte(IntPtr device, byte value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate int GetSampleRates(IntPtr device, uint* buffer, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SampleCallback(Transfer* transfer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartRx(IntPtr device, SampleCallback callback, IntPtr context);

    private sealed class Api
    {
        public readonly Open Open;
        public readonly DeviceCall Close, StopRx;
        public readonly SetUInt SetSampleRate, SetFreq;
        public readonly GetSampleRates GetSampleRates;
        public readonly StartRx StartRx;
        public readonly SetByte? SetHfAgc, SetHfAtt, SetHfLna;

        public Api(IntPtr library)
        {
            Open = Get<Open>(library, "airspyhf_open");
            Close = Get<DeviceCall>(library, "airspyhf_close");
            StartRx = Get<StartRx>(library, "airspyhf_start");
            StopRx = Get<DeviceCall>(library, "airspyhf_stop");
            SetSampleRate = Get<SetUInt>(library, "airspyhf_set_samplerate");
            GetSampleRates = Get<GetSampleRates>(library, "airspyhf_get_samplerates");
            SetFreq = Get<SetUInt>(library, "airspyhf_set_freq");
            SetHfAgc = TryGet<SetByte>(library, "airspyhf_set_hf_agc");
            SetHfAtt = TryGet<SetByte>(library, "airspyhf_set_hf_att");
            SetHfLna = TryGet<SetByte>(library, "airspyhf_set_hf_lna");
        }

        private static T Get<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        private static T? TryGet<T>(IntPtr library, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, name, out var address) || address == IntPtr.Zero)
                return null;
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }
}
