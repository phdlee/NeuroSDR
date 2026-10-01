using System.Speech.Synthesis;

namespace NeuroSDR.Audio;

/// <summary>Windows built-in SAPI voice (offline).</summary>
internal sealed class WindowsSpeechAnnouncer : IVoiceAnnouncer
{
    private readonly object _sync = new();
    private readonly Queue<string> _queue = new();
    private SpeechSynthesizer? _synth;
    private Thread? _worker;
    private volatile bool _run;
    private int _disposed;

    public bool IsAvailable { get; }
    public string EngineName => "SAPI";

    public WindowsSpeechAnnouncer()
    {
        if (!OperatingSystem.IsWindows())
        {
            IsAvailable = false;
            return;
        }
        try
        {
            // Probe that SAPI can start; actual Speak runs on a dedicated STA worker.
            using (var probe = new SpeechSynthesizer())
            {
                _ = probe.GetInstalledVoices().Count;
            }
            IsAvailable = true;
            _run = true;
            _worker = new Thread(Worker)
            {
                IsBackground = true,
                Name = "NeuroSDR-SAPI",
            };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public void SpeakFrequencyHz(long frequencyHz)
    {
        if (frequencyHz < 1_000) Speak($"{frequencyHz} hertz");
        else if (frequencyHz < 1_000_000)
            Speak($"{frequencyHz / 1_000d:0.###} kilohertz");
        else
            Speak($"{frequencyHz / 1_000_000d:0.######} megahertz");
    }

    public void SpeakMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        Speak(mode);
    }

    public void Speak(string text)
    {
        if (!IsAvailable || _disposed != 0 || string.IsNullOrWhiteSpace(text)) return;
        lock (_sync)
        {
            _queue.Clear();
            _queue.Enqueue(text.Trim());
            Monitor.Pulse(_sync);
        }
    }

    private void Worker()
    {
        try
        {
            _synth = new SpeechSynthesizer();
            _synth.SetOutputToDefaultAudioDevice();
            _synth.Rate = 1;
            _synth.Volume = 100;
        }
        catch
        {
            return;
        }

        while (_run && _disposed == 0)
        {
            string? next = null;
            lock (_sync)
            {
                while (_run && _disposed == 0 && _queue.Count == 0)
                    Monitor.Wait(_sync, 500);
                if (!_run || _disposed != 0) break;
                if (_queue.Count > 0) next = _queue.Dequeue();
            }
            if (next is null) continue;
            try { _synth?.Speak(next); }
            catch { }
        }

        try { _synth?.Dispose(); } catch { }
        _synth = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _run = false;
        lock (_sync) Monitor.PulseAll(_sync);
        try { _worker?.Join(1500); } catch { }
        _worker = null;
    }
}

/// <summary>Prefers Windows SAPI, then eSpeak NG.</summary>
internal sealed class CompositeVoiceAnnouncer : IVoiceAnnouncer
{
    private readonly IVoiceAnnouncer _inner;

    public CompositeVoiceAnnouncer()
    {
        var windows = new WindowsSpeechAnnouncer();
        if (windows.IsAvailable)
        {
            _inner = windows;
            return;
        }
        windows.Dispose();
        var espeak = new EspeakNgVoiceAnnouncer();
        _inner = espeak.IsAvailable ? espeak : new NullVoiceAnnouncer();
        if (!ReferenceEquals(_inner, espeak)) espeak.Dispose();
    }

    public bool IsAvailable => _inner.IsAvailable;
    public string EngineName => _inner.EngineName;
    public void Speak(string text) => _inner.Speak(text);
    public void SpeakFrequencyHz(long frequencyHz) => _inner.SpeakFrequencyHz(frequencyHz);
    public void SpeakMode(string mode) => _inner.SpeakMode(mode);
    public void Dispose() => _inner.Dispose();
}
