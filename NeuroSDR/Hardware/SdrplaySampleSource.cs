using NeuroSDR.Core;
using System.Runtime.InteropServices;
using System.Text;

namespace NeuroSDR.Hardware;

public sealed unsafe class SdrplaySampleSource : ISampleSource, IGainControlledSampleSource, IHardwareAgcSampleSource, IRfOverloadSource, ISampleRateGainCompensationSource, ISampleSourceMetrics, ISampleQueueMetrics, IConfigurableSampleRateSource
{
    private const string InstalledDll = @"C:\Program Files\SDRplay\API\x64\sdrplay_api.dll";
    private const int Success = 0;
    private const int TunerA = 1;
    private const int SingleTuner = 1;
    private const int UpdateRfFrequency = 0x00020000;
    private const int UpdateGain = 0x00008000;
    private const int UpdateAgc = 0x01000000;
    private static readonly int[] SampleRates = [2_000_000, 5_000_000, 8_000_000, 10_000_000];

    private readonly object _lifecycle = new();
    private readonly IntPtr _library;
    private readonly Api _api;
    private readonly StreamCallback _streamA;
    private readonly StreamCallback _streamB;
    private readonly EventCallback _event;
    private Device _device;
    private DeviceParams* _deviceParams;
    private RxChannelParams* _channel;
    private bool _apiOpened;
    private bool _selected;
    private long _centerFrequency = 100_000_000;
    private int _gainReduction = 38;
    private int _sampleRate = 2_000_000;
    private long _overloadDetectedEvents, _overloadCorrectedEvents;
    private bool _hardwareAgcEnabled;
    private bool _sampleRateGainCompensationEnabled;
    // Do not hide an overloaded DSP path behind a one-second RF backlog. Sixteen
    // blocks absorb about 105 ms at 10 MS/s and 262 ms at 2 MS/s while keeping
    // tuning, spectrum and audio tied to the current frequency.
    private readonly SampleBlockDispatcher _sampleDispatcher = new(32_768, 16);

    public string Name { get; private set; } = "SDRplay";
    public int SampleRate => Volatile.Read(ref _sampleRate);
    public IReadOnlyList<int> SupportedSampleRates => SampleRates;
    public int ConfiguredSampleRate
    {
        get => SampleRate;
        set
        {
            if (IsRunning) throw new InvalidOperationException("The SDRplay sample rate cannot be changed while receiving.");
            if (!SampleRates.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _sampleDispatcher.ConfigureBlockSize(value >= 10_000_000 ? 131_072 :
                value >= 8_000_000 ? 65_536 : 32_768);
            Volatile.Write(ref _sampleRate, value);
        }
    }
    public bool IsRunning { get; private set; }
    public long TotalSamples => _sampleDispatcher.TotalSamples;
    public long DeliveredSamples => _sampleDispatcher.DeliveredSamples;
    public long DroppedSamples => _sampleDispatcher.DroppedSamples;
    public long LastDeliveryAgeMilliseconds => _sampleDispatcher.LastDeliveryAgeMilliseconds;
    public int QueuedBlocks => _sampleDispatcher.QueuedBlocks;
    public int MaximumQueuedBlocks => _sampleDispatcher.MaximumQueuedBlocks;
    public long RfOverloadEvents => Interlocked.Read(ref _overloadDetectedEvents);
    internal long OverloadDetectedEvents => RfOverloadEvents;
    internal long OverloadCorrectedEvents => Interlocked.Read(ref _overloadCorrectedEvents);
    internal float CurrentGainDb => _channel is null ? float.NaN : _channel->Tuner.Gain.Current;
    internal int CurrentGainReductionDb => _channel is null ? _gainReduction : _channel->Tuner.Gain.GainReductionDb;
    internal byte CurrentLnaState => _channel is null ? (byte)0 : _channel->Tuner.Gain.LnaState;
    internal int CurrentTunerBandwidth => _channel is null ? TunerBandwidthFor(SampleRate) : _channel->Tuner.Bandwidth;
    public string LastStatus { get; private set; } = "Ready";
    public event Action<Complex32[]>? SamplesAvailable;

    public bool HardwareAgcEnabled
    {
        get => _hardwareAgcEnabled;
        set
        {
            if (_hardwareAgcEnabled == value) return;
            _hardwareAgcEnabled = value;
            lock (_lifecycle)
            {
                if (!IsRunning || _channel is null) return;
                ConfigureAgc(_channel, value);
                Check(_api.Update(_device.Handle, TunerA, UpdateAgc, 0), "SDRplay hardware AGC");
            }
        }
    }

    public bool SampleRateGainCompensationEnabled
    {
        get => _sampleRateGainCompensationEnabled;
        set => _sampleRateGainCompensationEnabled = value;
    }

    public long CenterFrequency
    {
        get => Interlocked.Read(ref _centerFrequency);
        set
        {
            value = Math.Clamp(value, 1_000, 2_000_000_000);
            Interlocked.Exchange(ref _centerFrequency, value);
            lock (_lifecycle)
            {
                if (!IsRunning || _channel is null) return;
                _channel->Tuner.RfFrequency.RfHz = value;
                Check(_api.Update(_device.Handle, TunerA, UpdateRfFrequency, 0), "Change frequency");
            }
        }
    }

    public int GainPercent
    {
        get => (int)Math.Round((59 - _gainReduction) * 100d / 39);
        set
        {
            _gainReduction = 59 - Math.Clamp(value, 0, 100) * 39 / 100;
            lock (_lifecycle)
            {
                if (!IsRunning || _channel is null) return;
                _channel->Tuner.Gain.MinimumGainReduction = 20;
                _channel->Tuner.Gain.GainReductionDb = EffectiveGainReduction();
                Check(_api.Update(_device.Handle, TunerA, UpdateGain, 0), "Change gain");
            }
        }
    }

    private SdrplaySampleSource(IntPtr library, Api api, Device device)
    {
        _library = library;
        _api = api;
        _device = device;
        _apiOpened = true;
        _streamA = OnStream;
        _streamB = OnStream;
        _event = OnEvent;
        _sampleDispatcher.SamplesAvailable += samples => SamplesAvailable?.Invoke(samples);
        Name = $"{HardwareName(device.HardwareVersion)} ({ReadSerial(device)})";
    }

    public static bool TryCreate(out SdrplaySampleSource? source, out string status)
    {
        source = null;
        status = "The SDRplay API could not be found.";
        if (!NativeLibrary.TryLoad(InstalledDll, out var library)) return false;

        Api? api = null;
        try
        {
            api = new Api(library);
            var result = api.Open();
            if (result != Success)
            {
                status = $"Failed to open the SDRplay API: {api.Error(result)}";
                NativeLibrary.Free(library);
                return false;
            }

            var devices = stackalloc Device[16];
            uint count = 0;
            result = api.GetDevices(devices, &count, 16);
            if (result != Success || count == 0)
            {
                status = result == Success ? "No SDRplay device is connected." : $"Device search failed: {api.Error(result)}";
                _ = api.Close();
                NativeLibrary.Free(library);
                return false;
            }

            source = new SdrplaySampleSource(library, api, devices[0]);
            status = $"{source.Name} found";
            return true;
        }
        catch (Exception exception)
        {
            if (api is not null) _ = api.Close();
            NativeLibrary.Free(library);
            status = $"SDRplay initialization failed: {exception.Message}";
            return false;
        }
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (IsRunning) return;
            try
            {
                _sampleDispatcher.Start();
                Interlocked.Exchange(ref _overloadDetectedEvents, 0);
                Interlocked.Exchange(ref _overloadCorrectedEvents, 0);
                _device.Tuner = TunerA;
                _device.RspDuoMode = SingleTuner;
                Check(_api.SelectDevice(ref _device), "Select device");
                _selected = true;
                _ = _api.UnlockDeviceApi();

                DeviceParams* parameters = null;
                Check(_api.GetDeviceParams(_device.Handle, &parameters), "Read device parameters");
                if (parameters is null || parameters->Device is null || parameters->RxChannelA is null)
                    throw new InvalidOperationException("SDRplay returned a null parameter pointer.");
                _deviceParams = parameters;
                _channel = parameters->RxChannelA;

                // SDRplay reads the initial receiver configuration during Init.
                // Populate it first so a restart does not race a series of
                // post-Init updates against the newly started stream.
                parameters->Device->SampleRate.FrequencyHz = SampleRate;
                _channel->Tuner.Bandwidth = TunerBandwidthFor(SampleRate);
                _channel->Tuner.IfType = 0;
                _channel->Tuner.LoMode = 1;
                _channel->Tuner.RfFrequency.RfHz = CenterFrequency;
                _channel->Tuner.Gain.MinimumGainReduction = 20;
                _channel->Tuner.Gain.GainReductionDb = EffectiveGainReduction();
                _channel->Tuner.Gain.LnaState = 0;
                _channel->Control.DcOffset.DcEnable = 1;
                _channel->Control.DcOffset.IqEnable = 1;
                _channel->Control.Decimation.Enable = 0;
                ConfigureAgc(_channel, _hardwareAgcEnabled);

                var callbacks = new CallbackFunctions
                {
                    StreamA = Marshal.GetFunctionPointerForDelegate(_streamA),
                    StreamB = Marshal.GetFunctionPointerForDelegate(_streamB),
                    Event = Marshal.GetFunctionPointerForDelegate(_event)
                };
                Check(_api.Init(_device.Handle, ref callbacks, IntPtr.Zero), "Start stream");

                // After init, replace API defaults with the Zero-IF receive setup.
                parameters->Device->SampleRate.FrequencyHz = SampleRate;
                _channel->Tuner.Bandwidth = TunerBandwidthFor(SampleRate);
                _channel->Tuner.IfType = 0;
                _channel->Tuner.LoMode = 1;
                _channel->Tuner.RfFrequency.RfHz = CenterFrequency;
                _channel->Tuner.Gain.MinimumGainReduction = 20;
                _channel->Tuner.Gain.GainReductionDb = EffectiveGainReduction();
                _channel->Tuner.Gain.LnaState = 0;
                _channel->Control.DcOffset.DcEnable = 1;
                _channel->Control.DcOffset.IqEnable = 1;
                _channel->Control.Decimation.Enable = 0;
                ConfigureAgc(_channel, _hardwareAgcEnabled);

                if (_hardwareAgcEnabled)
                    Check(_api.Update(_device.Handle, TunerA, UpdateAgc, 0), "Configure AGC");
                Check(_api.Update(_device.Handle, TunerA, 0x00000001, 0), "Set sample rate");
                Check(_api.Update(_device.Handle, TunerA, 0x00040000, 0), "Set bandwidth");
                Check(_api.Update(_device.Handle, TunerA, 0x00080000, 0), "Configure IF");
                Check(_api.Update(_device.Handle, TunerA, 0x00200000, 0), "Configure LO");
                Check(_api.Update(_device.Handle, TunerA, 0x00400000, 0), "Configure DC/IQ correction");
                Check(_api.Update(_device.Handle, TunerA, UpdateRfFrequency, 0), "Set frequency");
                Check(_api.Update(_device.Handle, TunerA, UpdateGain, 0), "Set gain");
                IsRunning = true;
                LastStatus = "Receiving";
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

    private void StopCore()
    {
        IsRunning = false;
        if (_selected)
        {
            if (_device.Handle != IntPtr.Zero) _ = _api.Uninit(_device.Handle);
            _ = _api.ReleaseDevice(ref _device);
        }
        _selected = false;
        _deviceParams = null;
        _channel = null;
        _sampleDispatcher.Stop();
        LastStatus = "Stopped";
    }

    private void OnStream(short* xi, short* xq, StreamCallbackParams* parameters, uint count, uint reset, IntPtr context)
    {
        if (!IsRunning || reset != 0 || xi is null || xq is null || count == 0) return;
        try
        {
            _sampleDispatcher.WriteInt16(xi, xq, count, DigitalGainLinear());
        }
        catch (Exception exception)
        {
            LastStatus = $"Callback error: {exception.Message}";
        }
    }

    private void OnEvent(int eventId, int tuner, IntPtr parameters, IntPtr context)
    {
        if (eventId == 1)
        {
            var overloadState = parameters == IntPtr.Zero ? -1 : Marshal.ReadInt32(parameters);
            if (overloadState == 0) Interlocked.Increment(ref _overloadDetectedEvents);
            else if (overloadState == 1) Interlocked.Increment(ref _overloadCorrectedEvents);
            // Re-arm SDRplay overload reporting. This acknowledgement does not
            // alter the receiver gain.
            _ = _api.Update(_device.Handle, tuner, 0x04000000, 0);
            return;
        }
        if (eventId == 2) LastStatus = "The device was disconnected.";
    }

    private void Check(int result, string operation)
    {
        if (result != Success) throw new InvalidOperationException($"{operation} failed: {_api.Error(result)} ({result})");
    }

    private static void ConfigureAgc(RxChannelParams* channel, bool enabled)
    {
        channel->Control.Agc.Enable = enabled ? 4 : 0;
        channel->Control.Agc.SetPointDbfs = enabled ? -30 : -60;
        channel->Control.Agc.AttackMs = enabled ? (ushort)500 : (ushort)0;
        channel->Control.Agc.DecayMs = enabled ? (ushort)500 : (ushort)0;
        channel->Control.Agc.DecayDelayMs = enabled ? (ushort)200 : (ushort)0;
        channel->Control.Agc.DecayThresholdDb = enabled ? (ushort)5 : (ushort)0;
    }

    private int RequestedGainCompensationDb() => _sampleRateGainCompensationEnabled
        ? GainCompensationForVerification(SampleRate)
        : 0;

    private int HardwareGainCompensationDb() => Math.Min(RequestedGainCompensationDb(),
        Math.Max(0, _gainReduction - 20));

    private int EffectiveGainReduction() => Math.Clamp(_gainReduction - HardwareGainCompensationDb(), 20, 59);

    private float DigitalGainLinear()
    {
        var remainingDb = RequestedGainCompensationDb() - HardwareGainCompensationDb();
        return remainingDb <= 0 ? 1f : MathF.Pow(10f, remainingDb / 20f);
    }

    internal static int GainCompensationForVerification(int sampleRate) => sampleRate switch
    {
        <= 2_000_000 => 0,
        <= 5_000_000 => 8,
        _ => 12
    };

    private static int TunerBandwidthFor(int sampleRate) => sampleRate switch
    {
        <= 2_000_000 => 1536,
        <= 5_000_000 => 5000,
        _ => 8000
    };

    public void Dispose()
    {
        lock (_lifecycle)
        {
            StopCore();
            if (_apiOpened) _ = _api.Close();
            _apiOpened = false;
            NativeLibrary.Free(_library);
        }
        GC.SuppressFinalize(this);
    }

    private static string HardwareName(byte version) => version switch
    {
        1 => "RSP1", 2 => "RSP2", 3 => "RSPduo", 4 => "RSPdx", 255 => "RSP1A", _ => $"RSP hw={version}"
    };

    private static string ReadSerial(Device device)
    {
        var bytes = new ReadOnlySpan<byte>(device.Serial, 64);
        var length = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(bytes[..(length < 0 ? bytes.Length : length)]);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StreamCallback(short* xi, short* xq, StreamCallbackParams* parameters, uint count, uint reset, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EventCallback(int eventId, int tuner, IntPtr parameters, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArg();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDevices(Device* devices, uint* count, uint maximum);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SelectDevice(ref Device device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDeviceParams(IntPtr device, DeviceParams** parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init(IntPtr device, ref CallbackFunctions callbacks, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceCall(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Update(IntPtr device, int tuner, int reason, int extension);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorString(int error);

    private sealed class Api
    {
        public readonly NoArg Open;
        public readonly NoArg Close;
        public readonly NoArg UnlockDeviceApi;
        public readonly GetDevices GetDevices;
        public readonly SelectDevice SelectDevice;
        public readonly SelectDevice ReleaseDevice;
        public readonly GetDeviceParams GetDeviceParams;
        public readonly Init Init;
        public readonly DeviceCall Uninit;
        public readonly Update Update;
        private readonly ErrorString _errorString;

        public Api(IntPtr library)
        {
            Open = Get<NoArg>(library, "sdrplay_api_Open");
            Close = Get<NoArg>(library, "sdrplay_api_Close");
            UnlockDeviceApi = Get<NoArg>(library, "sdrplay_api_UnlockDeviceApi");
            GetDevices = Get<GetDevices>(library, "sdrplay_api_GetDevices");
            SelectDevice = Get<SelectDevice>(library, "sdrplay_api_SelectDevice");
            ReleaseDevice = Get<SelectDevice>(library, "sdrplay_api_ReleaseDevice");
            GetDeviceParams = Get<GetDeviceParams>(library, "sdrplay_api_GetDeviceParams");
            Init = Get<Init>(library, "sdrplay_api_Init");
            Uninit = Get<DeviceCall>(library, "sdrplay_api_Uninit");
            Update = Get<Update>(library, "sdrplay_api_Update");
            _errorString = Get<ErrorString>(library, "sdrplay_api_GetErrorString");
        }

        public string Error(int error) => Marshal.PtrToStringAnsi(_errorString(error)) ?? "Unknown error";
        private static T Get<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }

    [StructLayout(LayoutKind.Sequential)] internal unsafe struct Device
    {
        public fixed byte Serial[64];
        public byte HardwareVersion;
        private fixed byte _padding0[3];
        public int Tuner;
        public int RspDuoMode;
        public byte Valid;
        private fixed byte _padding1[3];
        public double RspDuoSampleFrequency;
        public IntPtr Handle;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DeviceParams { public DeviceParameters* Device; public RxChannelParams* RxChannelA; public RxChannelParams* RxChannelB; }
    [StructLayout(LayoutKind.Sequential)] internal struct CallbackFunctions { public IntPtr StreamA; public IntPtr StreamB; public IntPtr Event; }
    [StructLayout(LayoutKind.Sequential)] internal struct StreamCallbackParams { public uint FirstSampleNumber; public int GainChanged; public int RfChanged; public int SampleRateChanged; public uint SampleCount; }

    [StructLayout(LayoutKind.Sequential)] internal struct DeviceParameters
    {
        public double Ppm;
        public SampleRateParameters SampleRate;
        public uint SyncSampleNumber, SyncPeriod;
        public byte ResetGain, ResetRf, ResetSampleRate, Padding0;
        public int TransferMode;
        public uint SamplesPerPacket;
        public byte Rsp1aRfNotch, Rsp1aDabNotch, Rsp2ExternalReference, Padding1;
        public int RspDuoExternalReference;
        public byte RspDxHdr, RspDxBiasT;
        public ushort Padding2;
        public int RspDxAntenna;
        public byte RspDxRfNotch, RspDxDabNotch;
        public ushort Padding3;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct SampleRateParameters { public double FrequencyHz; public byte SyncUpdate, Recalibrate; private fixed byte _padding[6]; }
    [StructLayout(LayoutKind.Sequential)] internal struct RxChannelParams
    {
        public TunerParameters Tuner;
        public ControlParameters Control;
        public byte Rsp1aBiasT;
        private fixed byte _padding0[3];
        public Rsp2TunerParameters Rsp2;
        public RspDuoTunerParameters RspDuo;
        public int RspDxHdrBandwidth;
        private fixed byte _padding1[4];
    }

    [StructLayout(LayoutKind.Sequential)] internal struct TunerParameters
    {
        public int Bandwidth, IfType, LoMode;
        public GainParameters Gain;
        public RfFrequencyParameters RfFrequency;
        public byte DcCalibration, DcSpeedUp;
        public ushort Padding;
        public int DcTrackTime, DcRefreshTime;
        private fixed byte _tailPadding[4];
    }

    [StructLayout(LayoutKind.Sequential)] internal struct GainParameters
    {
        public int GainReductionDb;
        public byte LnaState, SyncUpdate;
        public ushort Padding;
        public int MinimumGainReduction;
        public float Current, Maximum, Minimum;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct RfFrequencyParameters { public double RfHz; public byte SyncUpdate; private fixed byte _padding[7]; }
    [StructLayout(LayoutKind.Sequential)] internal struct ControlParameters
    {
        public DcOffsetParameters DcOffset;
        public DecimationParameters Decimation;
        private fixed byte _padding[3];
        public AgcParameters Agc;
        public int AdsbMode;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct DcOffsetParameters { public byte DcEnable, IqEnable; }
    [StructLayout(LayoutKind.Sequential)] internal struct DecimationParameters { public byte Enable, Factor, WideBand; }
    [StructLayout(LayoutKind.Sequential)] internal struct AgcParameters { public int Enable, SetPointDbfs; public ushort AttackMs, DecayMs, DecayDelayMs, DecayThresholdDb; public int SyncUpdate; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rsp2TunerParameters { public byte BiasT; private fixed byte _padding0[3]; public int AmPort, Antenna; public byte RfNotch; private fixed byte _padding1[3]; }
    [StructLayout(LayoutKind.Sequential)] internal struct RspDuoTunerParameters { public byte BiasT; private fixed byte _padding0[3]; public int AmPort; public byte AmNotch, RfNotch, DabNotch; private byte _padding1; }
}
