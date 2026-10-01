using System.Diagnostics;
using System.Text;

namespace NeuroSDR.Audio;

/// <summary>
/// Offline, OS-independent voice announcements via eSpeak NG (or classic eSpeak).
/// Works on Windows and Linux when <c>espeak-ng</c>/<c>espeak</c> is on PATH
/// or shipped under the app's <c>native/</c> folder — no internet required.
/// </summary>
internal interface IVoiceAnnouncer : IDisposable
{
    bool IsAvailable { get; }
    string EngineName { get; }
    void Speak(string text);
    void SpeakFrequencyHz(long frequencyHz);
    void SpeakMode(string mode);
}

internal sealed class NullVoiceAnnouncer : IVoiceAnnouncer
{
    public bool IsAvailable => false;
    public string EngineName => "none";
    public void Speak(string text) { }
    public void SpeakFrequencyHz(long frequencyHz) { }
    public void SpeakMode(string mode) { }
    public void Dispose() { }
}

internal sealed class EspeakNgVoiceAnnouncer : IVoiceAnnouncer
{
    private readonly object _sync = new();
    private readonly string? _executable;
    private Process? _current;
    private int _disposed;

    public bool IsAvailable => !string.IsNullOrEmpty(_executable);
    public string EngineName => Path.GetFileNameWithoutExtension(_executable ?? "none");

    public EspeakNgVoiceAnnouncer()
    {
        _executable = ResolveExecutable();
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
        Speak(ExpandMode(mode));
    }

    public void Speak(string text)
    {
        if (_disposed != 0 || string.IsNullOrWhiteSpace(text) || _executable is null) return;
        text = Sanitize(text);
        if (text.Length == 0) return;

        lock (_sync)
        {
            try
            {
                try { _current?.Kill(entireProcessTree: true); } catch { }
                try { _current?.Dispose(); } catch { }
                _current = null;

                var start = new ProcessStartInfo
                {
                    FileName = _executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // -v en : English voice; -a 120 amplitude; -s 160 words/min
                start.ArgumentList.Add("-v");
                start.ArgumentList.Add("en");
                start.ArgumentList.Add("-a");
                start.ArgumentList.Add("120");
                start.ArgumentList.Add("-s");
                start.ArgumentList.Add("160");
                start.ArgumentList.Add("--");
                start.ArgumentList.Add(text);

                var process = Process.Start(start);
                if (process is null) return;
                _current = process;
                _ = process.WaitForExitAsync().ContinueWith(static (task, state) =>
                {
                    if (state is not Process p) return;
                    try { p.Dispose(); } catch { }
                }, process);
            }
            catch
            {
                // Missing binary / sandbox — stay silent.
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_sync)
        {
            try { _current?.Kill(entireProcessTree: true); } catch { }
            try { _current?.Dispose(); } catch { }
            _current = null;
        }
    }

    private static string ExpandMode(string mode) => mode.ToUpperInvariant() switch
    {
        "NFM" => "N F M",
        "WFM" => "W F M",
        "USB" => "U S B",
        "LSB" => "L S B",
        "CW" => "C W",
        "AM" => "A M",
        "SAM" => "S A M",
        "DMR" => "D M R",
        "DSTAR" => "D star",
        "C4FM" => "C 4 F M",
        "RAW" => "raw",
        _ => string.Join(' ', mode.ToUpperInvariant().ToCharArray())
    };

    private static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or ',' or '-' or '+' or ' ' or '/')
                sb.Append(ch);
            else if (ch == '\n' || ch == '\r' || ch == '\t')
                sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static string? ResolveExecutable()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "espeak-ng.exe", "espeak.exe" }
            : new[] { "espeak-ng", "espeak" };

        foreach (var name in names)
        {
            var local = Path.Combine(AppContext.BaseDirectory, "native", name);
            if (File.Exists(local)) return local;
            local = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(local)) return local;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         @"C:\Program Files\eSpeak NG",
                         @"C:\Program Files (x86)\eSpeak NG"
                     })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                foreach (var name in names)
                {
                    var candidate = Path.Combine(root, name);
                    if (File.Exists(candidate)) return candidate;
                    candidate = Path.Combine(root, "eSpeak NG", name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        else
        {
            foreach (var candidate in new[] { "/usr/bin/espeak-ng", "/usr/bin/espeak", "/usr/local/bin/espeak-ng" })
                if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
