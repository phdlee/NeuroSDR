using NeuroSDR.Core;
using System.Runtime.InteropServices;

namespace NeuroSDR.Hardware;

/// <summary>
/// Optional SoapySDR facade. When PothosSDR / SoapySDR is installed with modules
/// (Lime, Pluto, BladeRF, Airspy, UHD, …), those devices appear in SOURCE.
/// Built-in RTL/HackRF/SDRplay/Airspy adapters remain preferred when present.
/// </summary>
public sealed unsafe class SoapySdrSampleSource : ISampleSource, IGainControlledSampleSource,
    IHardwareAgcSampleSource, ISampleSourceMetrics, ISampleQueueMetrics, IConfigurableSampleRateSource
{
    private const int SoapySdrRx = 1;
    private readonly object _lifecycle = new();
    private readonly IntPtr _library;
    private readonly Api _api;
    private readonly string _args;
    private readonly string _label;
    private readonly SampleBlockDispatcher _samples = new(32_768, 64);
    private readonly List<int> _supportedSampleRates = [];
    private IntPtr _device;
    private IntPtr _stream;
    private Thread? _reader;
    private long _centerFrequency = 100_000_000;
    private int _gainPercent = 60;
    private bool _hardwareAgc;
    private int _sampleRate = 2_000_000;
    private double _gainMin, _gainMax = 40;
    private bool _disposed;
    private volatile bool _running;

    public string Name => _label;
    public int SampleRate => Volatile.Read(ref _sampleRate);
    public IReadOnlyList<int> SupportedSampleRates => _supportedSampleRates.Count > 0 ? _supportedSampleRates : [2_000_000, 2_500_000, 5_000_000, 10_000_000];
    public int ConfiguredSampleRate
    {
        get => SampleRate;
        set
        {
            if (IsRunning) throw new InvalidOperationException("SoapySDR sample rate cannot change while receiving.");
            if (!SupportedSampleRates.Contains(value) && SupportedSampleRates.Count > 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _samples.ConfigureBlockSize(value >= 8_000_000 ? 65_536 : 32_768);
            Volatile.Write(ref _sampleRate, value);
        }
    }
    public bool IsRunning => _running;
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
            value = Math.Clamp(value, 0, 6_000_000_000);
            Interlocked.Exchange(ref _centerFrequency, value);
            lock (_lifecycle)
                if (_device != IntPtr.Zero)
                    _ = _api.SetFrequency(_device, SoapySdrRx, 0, value, IntPtr.Zero);
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
                if (_device != IntPtr.Zero)
                {
                    _ = _api.SetGainMode(_device, SoapySdrRx, 0, value);
                    if (!value) ApplyGain();
                }
        }
    }

    private SoapySdrSampleSource(IntPtr library, Api api, string args, string label)
    {
        _library = library;
        _api = api;
        _args = args;
        _label = string.IsNullOrWhiteSpace(label) ? $"SoapySDR ({args})" : label;
        _samples.SamplesAvailable += block => SamplesAvailable?.Invoke(block);
        ProbeCapabilities();
    }

    private static bool TryLoadLibrary(out IntPtr library, out Api api, out string location, out string status)
    {
        library = IntPtr.Zero;
        api = null!;
        location = string.Empty;
        if (!TryLoadSoapy(out library, out location, out var failure))
        {
            status = failure;
            return false;
        }
        try
        {
            api = new Api(library);
            try { api.LoadModules?.Invoke(); } catch { /* optional */ }
            status = location;
            return true;
        }
        catch (Exception exception)
        {
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
            status = exception.Message;
            return false;
        }
    }

    public static IEnumerable<SampleSourceDiscoveryResult> DiscoverAll()
    {
        if (!TryLoadLibrary(out var library, out var api, out var location, out var status))
        {
            return
            [
                new SampleSourceDiscoveryResult(null,
                    $"SoapySDR: not available ({status}). Install PothosSDR for Lime/Pluto/BladeRF/USRP/…")
            ];
        }

        var results = new List<SampleSourceDiscoveryResult>();
        try
        {
            EnumerateInto(api, location, results);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
        return results;
    }

    private static void EnumerateInto(Api api, string location, List<SampleSourceDiscoveryResult> results)
    {
        nuint length = 0;
        var list = api.Enumerate(null, &length);
        try
        {
            if (list == null || length == 0)
            {
                results.Add(new SampleSourceDiscoveryResult(null, $"SoapySDR loaded ({location}) · no devices from modules"));
                return;
            }

            for (nuint i = 0; i < length; i++)
            {
                var kwargs = list + i;
                var driver = GetKwarg(api, kwargs, "driver") ?? "soapy";
                var label = GetKwarg(api, kwargs, "label") ?? driver;
                var args = api.KwargsToString(kwargs);
                var argsText = args != IntPtr.Zero ? Marshal.PtrToStringAnsi(args) ?? $"driver={driver}" : $"driver={driver}";
                if (args != IntPtr.Zero) api.Free(args);

                if (IsNativeCoveredDriver(driver))
                    continue;

                SoapySdrSampleSource? source = null;
                string deviceStatus;
                try
                {
                    if (!TryLoadLibrary(out var lib2, out var api2, out _, out var loadFail))
                        deviceStatus = $"SoapySDR {label}: {loadFail}";
                    else
                    {
                        source = new SoapySdrSampleSource(lib2, api2, argsText, $"Soapy · {label}");
                        deviceStatus = $"SoapySDR · {label} · {argsText}";
                    }
                }
                catch (Exception exception)
                {
                    deviceStatus = $"SoapySDR {label}: {exception.Message}";
                }
                results.Add(new SampleSourceDiscoveryResult(source, deviceStatus));
            }

            if (results.Count == 0)
                results.Add(new SampleSourceDiscoveryResult(null,
                    $"SoapySDR loaded ({location}) · only native-covered drivers present"));
        }
        finally
        {
            if (list != null) api.KwargsListClear(list, length);
        }
    }

    internal static string ProbeNativeLibrary()
    {
        if (!TryLoadLibrary(out var library, out var api, out var location, out var status))
            return $"FAIL · {status}";
        try
        {
            nuint length = 0;
            var list = api.Enumerate(null, &length);
            if (list != null) api.KwargsListClear(list, length);
            return $"OK · {(Environment.Is64BitProcess ? "x64" : "x86")} · {location} · devices={length}";
        }
        catch (Exception exception) { return $"FAIL · {exception.Message}"; }
        finally { NativeLibrary.Free(library); }
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            var argsPtr = Marshal.StringToHGlobalAnsi(_args);
            try
            {
                _device = _api.MakeStrArgs(argsPtr);
            }
            finally { Marshal.FreeHGlobal(argsPtr); }
            if (_device == IntPtr.Zero)
                throw new InvalidOperationException($"SoapySDR open failed for '{_args}'.");

            try
            {
                _ = _api.SetSampleRate(_device, SoapySdrRx, 0, SampleRate);
                _ = _api.SetFrequency(_device, SoapySdrRx, 0, CenterFrequency, IntPtr.Zero);
                _ = _api.SetGainMode(_device, SoapySdrRx, 0, _hardwareAgc);
                if (!_hardwareAgc) ApplyGain();

                var format = Marshal.StringToHGlobalAnsi("CF32");
                try
                {
                    nuint channel = 0;
                    _stream = _api.SetupStream(_device, SoapySdrRx, format, &channel, 1, IntPtr.Zero);
                }
                finally { Marshal.FreeHGlobal(format); }
                if (_stream == IntPtr.Zero)
                    throw new InvalidOperationException("SoapySDR setupStream(CF32) failed.");

                if (_api.ActivateStream(_device, _stream, 0, 0, 0) != 0)
                    throw new InvalidOperationException("SoapySDR activateStream failed.");

                _samples.Start();
                _running = true;
                _reader = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "SoapySDR reader",
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
        Thread? reader;
        lock (_lifecycle) reader = _reader;
        if (reader is not null && reader != Thread.CurrentThread) reader.Join(2_000);
        lock (_lifecycle) StopCore();
    }

    private void ReadLoop()
    {
        const int chunk = 16_384;
        var buffer = new float[chunk * 2];
        fixed (float* p = buffer)
        {
            var buffs = stackalloc IntPtr[1];
            while (_running && _device != IntPtr.Zero && _stream != IntPtr.Zero)
            {
                buffs[0] = (IntPtr)p;
                long timeNs = 0;
                int flags = 0;
                var got = _api.ReadStream(_device, _stream, buffs, (nuint)chunk, &flags, &timeNs, 200_000);
                if (got > 0) _samples.WriteFloatInterleaved(p, got);
                else if (got == -1 /* SOAPY_SDR_TIMEOUT */) continue;
                else if (got < 0) break;
            }
        }
        _running = false;
    }

    private void ApplyGain()
    {
        var span = Math.Max(0.1, _gainMax - _gainMin);
        var gain = _gainMin + span * (_gainPercent / 100.0);
        _ = _api.SetGain(_device, SoapySdrRx, 0, gain);
    }

    private void ProbeCapabilities()
    {
        var argsPtr = Marshal.StringToHGlobalAnsi(_args);
        IntPtr device = IntPtr.Zero;
        try
        {
            device = _api.MakeStrArgs(argsPtr);
            if (device == IntPtr.Zero) return;

            nuint length = 0;
            var rates = _api.ListSampleRates(device, SoapySdrRx, 0, &length);
            if (rates != null && length > 0)
            {
                for (nuint i = 0; i < length && i < 64; i++)
                {
                    var hz = (int)Math.Round(rates[i]);
                    if (hz is >= 48_000 and <= 61_440_000 && !_supportedSampleRates.Contains(hz))
                        _supportedSampleRates.Add(hz);
                }
                _supportedSampleRates.Sort();
                if (_supportedSampleRates.Count > 0)
                    _sampleRate = PickDefaultRate(_supportedSampleRates);
                _api.Free((IntPtr)rates);
            }

            var range = _api.GetGainRange(device, SoapySdrRx, 0);
            _gainMin = range.Minimum;
            _gainMax = range.Maximum <= range.Minimum ? range.Minimum + 40 : range.Maximum;
        }
        catch { /* best-effort */ }
        finally
        {
            if (device != IntPtr.Zero) _ = _api.Unmake(device);
            Marshal.FreeHGlobal(argsPtr);
        }
    }

    private static int PickDefaultRate(IReadOnlyList<int> rates)
    {
        foreach (var prefer in new[] { 2_000_000, 2_048_000, 2_500_000, 1_000_000, 768_000 })
            if (rates.Contains(prefer)) return prefer;
        return rates[Math.Min(rates.Count - 1, rates.Count / 2)];
    }

    private void StopCore()
    {
        _running = false;
        if (_device != IntPtr.Zero && _stream != IntPtr.Zero)
        {
            _ = _api.DeactivateStream(_device, _stream, 0, 0);
            _ = _api.CloseStream(_device, _stream);
            _stream = IntPtr.Zero;
        }
        _samples.Stop();
        if (_device != IntPtr.Zero)
        {
            _ = _api.Unmake(_device);
            _device = IntPtr.Zero;
        }
        _reader = null;
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

    private static bool IsNativeCoveredDriver(string driver)
    {
        var d = driver.Trim().ToLowerInvariant();
        return d is "rtlsdr" or "rtl" or "hackrf" or "sdrplay" or "sdrplay3" or "airspy";
        // airspyhf left to Soapy when native DLL missing (x64).
    }

    private static string? GetKwarg(Api api, SoapyKwargs* kwargs, string key)
    {
        var keyPtr = Marshal.StringToHGlobalAnsi(key);
        try
        {
            var value = api.KwargsGet(kwargs, keyPtr);
            return value == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(value);
        }
        finally { Marshal.FreeHGlobal(keyPtr); }
    }

    private static bool TryLoadSoapy(out IntPtr library, out string location, out string failure)
    {
        var names = new[] { "SoapySDR.dll", "libSoapySDR.dll" };
        var extras = new List<string>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { pf, pf86, @"C:\Program Files", @"C:\Program Files (x86)" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            extras.Add(Path.Combine(root, "PothosSDR", "bin", "SoapySDR.dll"));
            extras.Add(Path.Combine(root, "SoapySDR", "bin", "SoapySDR.dll"));
            extras.Add(Path.Combine(root, "radioconda", "Library", "bin", "SoapySDR.dll"));
        }
        var soapRoot = Environment.GetEnvironmentVariable("SOAPY_SDR_ROOT");
        if (!string.IsNullOrWhiteSpace(soapRoot))
            extras.Add(Path.Combine(soapRoot, "bin", "SoapySDR.dll"));

        foreach (var path in extras.Where(File.Exists))
        {
            if (NativeLibraryLocator.TryLoad(names, out library, out location, out failure, path))
                return true;
        }
        return NativeLibraryLocator.TryLoad(names, out library, out location, out failure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SoapyKwargs
    {
        public nuint Size;
        public IntPtr Keys;
        public IntPtr Vals;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SoapyRange
    {
        public double Minimum;
        public double Maximum;
        public double Step;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LoadModules();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate SoapyKwargs* Enumerate(SoapyKwargs* args, nuint* length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate void KwargsListClear(SoapyKwargs* args, nuint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate IntPtr KwargsToString(SoapyKwargs* args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate IntPtr KwargsGet(SoapyKwargs* args, IntPtr key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr ptr);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr MakeStrArgs(IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Unmake(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSampleRate(IntPtr device, int direction, nuint channel, double rate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetFrequency(IntPtr device, int direction, nuint channel, double frequency, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetGain(IntPtr device, int direction, nuint channel, double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetGainMode(IntPtr device, int direction, nuint channel, [MarshalAs(UnmanagedType.I1)] bool automatic);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate SoapyRange GetGainRange(IntPtr device, int direction, nuint channel);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate double* ListSampleRates(IntPtr device, int direction, nuint channel, nuint* length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate IntPtr SetupStream(IntPtr device, int direction, IntPtr format, nuint* channels, nuint channelCount, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseStream(IntPtr device, IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ActivateStream(IntPtr device, IntPtr stream, int flags, long timeNs, nuint numElems);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeactivateStream(IntPtr device, IntPtr stream, int flags, long timeNs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private unsafe delegate int ReadStream(IntPtr device, IntPtr stream, IntPtr* buffs, nuint numElems, int* flags, long* timeNs, long timeoutUs);

    private sealed class Api
    {
        public readonly LoadModules? LoadModules;
        public readonly Enumerate Enumerate;
        public readonly KwargsListClear KwargsListClear;
        public readonly KwargsToString KwargsToString;
        public readonly KwargsGet KwargsGet;
        public readonly Free Free;
        public readonly MakeStrArgs MakeStrArgs;
        public readonly Unmake Unmake;
        public readonly SetSampleRate SetSampleRate;
        public readonly SetFrequency SetFrequency;
        public readonly SetGain SetGain;
        public readonly SetGainMode SetGainMode;
        public readonly GetGainRange GetGainRange;
        public readonly ListSampleRates ListSampleRates;
        public readonly SetupStream SetupStream;
        public readonly CloseStream CloseStream;
        public readonly ActivateStream ActivateStream;
        public readonly DeactivateStream DeactivateStream;
        public readonly ReadStream ReadStream;

        public Api(IntPtr library)
        {
            LoadModules = TryGet<LoadModules>(library, "SoapySDR_loadModules");
            Enumerate = Get<Enumerate>(library, "SoapySDRDevice_enumerate");
            KwargsListClear = Get<KwargsListClear>(library, "SoapySDRKwargsList_clear");
            KwargsToString = Get<KwargsToString>(library, "SoapySDRKwargs_toString");
            KwargsGet = Get<KwargsGet>(library, "SoapySDRKwargs_get");
            Free = Get<Free>(library, "SoapySDR_free");
            MakeStrArgs = Get<MakeStrArgs>(library, "SoapySDRDevice_makeStrArgs");
            Unmake = Get<Unmake>(library, "SoapySDRDevice_unmake");
            SetSampleRate = Get<SetSampleRate>(library, "SoapySDRDevice_setSampleRate");
            SetFrequency = Get<SetFrequency>(library, "SoapySDRDevice_setFrequency");
            SetGain = Get<SetGain>(library, "SoapySDRDevice_setGain");
            SetGainMode = Get<SetGainMode>(library, "SoapySDRDevice_setGainMode");
            GetGainRange = Get<GetGainRange>(library, "SoapySDRDevice_getGainRange");
            ListSampleRates = Get<ListSampleRates>(library, "SoapySDRDevice_listSampleRates");
            SetupStream = Get<SetupStream>(library, "SoapySDRDevice_setupStream");
            CloseStream = Get<CloseStream>(library, "SoapySDRDevice_closeStream");
            ActivateStream = Get<ActivateStream>(library, "SoapySDRDevice_activateStream");
            DeactivateStream = Get<DeactivateStream>(library, "SoapySDRDevice_deactivateStream");
            ReadStream = Get<ReadStream>(library, "SoapySDRDevice_readStream");
        }

        private static T Get<T>(IntPtr library, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        private static T? TryGet<T>(IntPtr library, string name) where T : Delegate
        {
            if (!NativeLibrary.TryGetExport(library, name, out var address) || address == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }
}
