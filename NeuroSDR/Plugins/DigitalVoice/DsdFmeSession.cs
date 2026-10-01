namespace NeuroSDR.Plugins.DigitalVoice;

/// <summary>
/// Host session for dsdfme.dll. Only one native instance exists.
/// </summary>
internal sealed class DsdFmeSession : IDisposable
{
    private readonly object _sync = new();
    private readonly byte[] _dibitPush = new byte[4_800];
    private readonly short[] _pcmPush = new short[4_800];
    private readonly short[] _pull = new short[1_600];
    private IntPtr _handle;
    private bool _failed;
    private bool _pcmMode;
    private bool _inverted;
    private int _protocol; // 0 auto, 1 DMR, 2 DSTAR, 3 YSF

    public string Status { get; private set; } = "";
    public bool PcmMode => _pcmMode;

    public bool IsRunning
    {
        get { lock (_sync) return _handle != IntPtr.Zero; }
    }

    public bool EnsureStarted(bool pcmMode = false, bool invertedDmr = false, int protocol = 0)
    {
        lock (_sync)
        {
            if (_handle != IntPtr.Zero)
            {
                if (_pcmMode == pcmMode && _inverted == invertedDmr && _protocol == protocol) return true;
                DisposeLocked();
            }
            if (_failed && _pcmMode == pcmMode && _inverted == invertedDmr && _protocol == protocol) return false;
            _failed = false;
            if (!DsdFmeNative.LibraryAvailable())
            {
                _failed = true;
                Status = "dsdfme.dll not found (rebuild DSD-FME library)";
                return false;
            }
            try
            {
                var config = new DsdFmeNative.Config
                {
                    InputSampleRate = 48_000,
                    DmrOnly = protocol == 1 ? 1 : 0,
                    InvertedDmr = invertedDmr ? 1 : 0,
                    Verbose = 0,
                    DibitInput = pcmMode ? 0 : 1,
                    Protocol = protocol
                };
                _handle = DsdFmeNative.Create(in config);
                if (_handle == IntPtr.Zero)
                {
                    _failed = true;
                    Status = "dsdfme_create failed (another DSD-FME session may be active)";
                    return false;
                }
                _pcmMode = pcmMode;
                _inverted = invertedDmr;
                _protocol = protocol;
                var tag = protocol switch { 1 => "DMR", 2 => "D-STAR", 3 => "YSF", _ => "multi" };
                Status = pcmMode
                    ? $"DSD-FME PCM in (48 kHz, {tag}) / 8 kHz voice out"
                    : "DSD-FME dibit in / 8 kHz PCM out";
                return true;
            }
            catch (DllNotFoundException)
            {
                _failed = true;
                Status = "dsdfme.dll not found";
                return false;
            }
            catch (Exception exception)
            {
                _failed = true;
                Status = exception.GetBaseException().Message;
                return false;
            }
        }
    }

    public void PushDibits(ReadOnlySpan<byte> dibits)
    {
        if (dibits.IsEmpty) return;
        lock (_sync)
        {
            if (_handle == IntPtr.Zero || _pcmMode) return;
            var offset = 0;
            while (offset < dibits.Length)
            {
                var n = Math.Min(_dibitPush.Length, dibits.Length - offset);
                dibits.Slice(offset, n).CopyTo(_dibitPush);
                DsdFmeNative.PushDibits(_handle, _dibitPush, n);
                offset += n;
            }
        }
    }

    public void PushPcm16(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return;
        lock (_sync)
        {
            if (_handle == IntPtr.Zero || !_pcmMode) return;
            var offset = 0;
            while (offset < samples.Length)
            {
                var n = Math.Min(_pcmPush.Length, samples.Length - offset);
                samples.Slice(offset, n).CopyTo(_pcmPush);
                DsdFmeNative.PushPcm16(_handle, _pcmPush, n);
                offset += n;
            }
        }
    }

    public void PumpDecodedAudio()
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return;
            int got;
            while ((got = DsdFmeNative.PullPcm16(_handle, _pull, _pull.Length)) > 0)
                DigitalVoicePlayback.PushDecodedStereo8k(_pull.AsSpan(0, got));
        }
    }

    public DsdFmeNative.Status? TryGetStatus()
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return null;
            if (DsdFmeNative.GetStatus(_handle, out var status) != 0) return null;
            return status;
        }
    }

    public DsdFmeNative.Identity? TryGetIdentity()
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return null;
            if (DsdFmeNative.GetIdentity(_handle, out var identity) != 0) return null;
            return identity;
        }
    }

    public IReadOnlyList<DsdFmeNative.TextMessage> PullTextMessages(int max = 8)
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return [];
            var list = new List<DsdFmeNative.TextMessage>(max);
            while (list.Count < max && DsdFmeNative.PullText(_handle, out var message) == 1)
            {
                if (!string.IsNullOrWhiteSpace(message.Text))
                    list.Add(message);
            }
            return list;
        }
    }

    public void Dispose()
    {
        lock (_sync) DisposeLocked();
        DigitalVoicePlayback.Clear();
    }

    private void DisposeLocked()
    {
        if (_handle == IntPtr.Zero) return;
        try { DsdFmeNative.Destroy(_handle); } catch { }
        _handle = IntPtr.Zero;
        _failed = false;
    }
}
