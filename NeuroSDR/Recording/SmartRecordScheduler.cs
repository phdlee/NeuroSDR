using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Settings;

namespace NeuroSDR.Recording;

/// <summary>
/// Starts/stops Smart Record audio streams and FT8 report sessions from wall-clock schedules.
/// </summary>
internal sealed class SmartRecordScheduler : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AfStreamWavRecorder> _audio = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FtxDecodeReportSession> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool Following, long HoldUntil, long Standby)> _pileup = new(StringComparer.OrdinalIgnoreCase);
    private List<SmartRecordJob> _jobs = [];
    private string _outputRoot = "";
    private bool _disposed;

    public event Action<string>? StatusChanged;
    public IReadOnlyDictionary<string, AfStreamWavRecorder> ActiveAudio => _audio;
    public int ActiveAudioCount { get { lock (_sync) return _audio.Count; } }
    public int ActiveReportCount { get { lock (_sync) return _reports.Count; } }

    public void Configure(IEnumerable<SmartRecordJob> jobs, string outputRoot)
    {
        lock (_sync)
        {
            _jobs = jobs.Select(j => j.Clone()).ToList();
            _outputRoot = string.IsNullOrWhiteSpace(outputRoot)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeuroSDR", "SmartRecord")
                : outputRoot;
            Directory.CreateDirectory(_outputRoot);
        }
    }

    public bool IsRecordingVfo(string vfoId)
    {
        lock (_sync)
            return _audio.Values.Any(r => r.VfoId.Equals(vfoId, StringComparison.OrdinalIgnoreCase));
    }

    public void Tick(
        DateTime localNow,
        Func<string, bool> ensureVfoReady,
        Action<SmartRecordJob>? applyTune)
    {
        if (_disposed) return;
        List<SmartRecordJob> jobs;
        lock (_sync) jobs = _jobs.ToList();

        foreach (var job in jobs)
        {
            var due = job.IsScheduledNow(localNow);
            if (job.Kind == SmartRecordKind.Audio)
            {
                var active = false;
                lock (_sync) active = _audio.ContainsKey(job.Id);
                if (due && !active)
                {
                    if (!ensureVfoReady(job.VfoId)) continue;
                    applyTune?.Invoke(job);
                    StartAudio(job, localNow);
                }
                else if (!due && active)
                    StopAudio(job.Id);
            }
            else if (job.Kind == SmartRecordKind.FtxReport)
            {
                var active = false;
                lock (_sync) active = _reports.ContainsKey(job.Id);
                if (due && !active)
                    StartReport(job, localNow);
                else if (!due && active)
                    StopReport(job.Id);
            }
        }

        // Stop sessions whose jobs were removed or disabled.
        lock (_sync)
        {
            foreach (var id in _audio.Keys.Where(id => jobs.All(j => j.Id != id)).ToArray())
                StopAudio(id);
            foreach (var id in _reports.Keys.Where(id => jobs.All(j => j.Id != id)).ToArray())
                StopReport(id);
        }
    }

    public void WriteAudio(string vfoId, ReadOnlySpan<float> mono)
    {
        if (mono.Length == 0) return;
        lock (_sync)
        {
            foreach (var recorder in _audio.Values)
            {
                if (!recorder.VfoId.Equals(vfoId, StringComparison.OrdinalIgnoreCase)) continue;
                recorder.WriteFloatMono(mono);
            }
        }
    }

    public void OnFtxDecode(string utc, string db, string dt, string freq, string message, string vfoId)
    {
        lock (_sync)
        {
            foreach (var session in _reports.Values)
                session.TryAppend(utc, db, dt, freq, message, vfoId);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var id in _audio.Keys.ToArray()) StopAudio(id);
            foreach (var id in _reports.Keys.ToArray()) StopReport(id);
        }
    }

    private void StartAudio(SmartRecordJob job, DateTime now)
    {
        lock (_sync)
        {
            if (_audio.ContainsKey(job.Id)) return;
            var safe = Sanitize(job.Name);
            var path = System.IO.Path.Combine(_outputRoot,
                $"{now:yyyyMMdd_HHmmss}_{safe}_{job.VfoId}.wav");
            var recorder = new AfStreamWavRecorder(path, AudioDemodulator.AudioSampleRate, job.VfoId);
            _audio[job.Id] = recorder;
            if (job.PileupFollowEnabled)
                _pileup[job.Id] = (false, 0, job.TuneFrequencyHz > 0 ? job.TuneFrequencyHz : 0);
            StatusChanged?.Invoke($"Smart Record · audio START · {job.Name} → {System.IO.Path.GetFileName(path)}");
        }
    }

    private void StopAudio(string jobId)
    {
        lock (_sync)
        {
            if (!_audio.Remove(jobId, out var recorder)) return;
            _pileup.Remove(jobId);
            var path = recorder.Path;
            recorder.Dispose();
            StatusChanged?.Invoke($"Smart Record · audio STOP · {System.IO.Path.GetFileName(path)}");
        }
    }

    private void StartReport(SmartRecordJob job, DateTime now)
    {
        lock (_sync)
        {
            if (_reports.ContainsKey(job.Id)) return;
            var safe = Sanitize(job.Name);
            var path = System.IO.Path.Combine(_outputRoot,
                $"{now:yyyyMMdd_HHmmss}_{safe}_ftx-report.html");
            _reports[job.Id] = new FtxDecodeReportSession(path, job);
            StatusChanged?.Invoke($"Smart Record · FT8 report START · {job.Name}");
        }
    }

    private void StopReport(string jobId)
    {
        lock (_sync)
        {
            if (!_reports.Remove(jobId, out var session)) return;
            var path = session.Path;
            session.Dispose();
            StatusChanged?.Invoke($"Smart Record · FT8 report STOP · {System.IO.Path.GetFileName(path)}");
        }
    }

    private void ProcessPileup(List<SmartRecordJob> jobs, Action<string, long> applyFrequency)
    {
        _ = jobs;
        _ = applyFrequency;
    }

    public bool TryGetPileupJob(string vfoId, out SmartRecordJob? job, out long standby)
    {
        lock (_sync)
        {
            job = _jobs.FirstOrDefault(j =>
                j.Kind == SmartRecordKind.Audio &&
                j.PileupFollowEnabled &&
                j.VfoId.Equals(vfoId, StringComparison.OrdinalIgnoreCase) &&
                _audio.ContainsKey(j.Id));
            standby = 0;
            if (job is null) return false;
            if (_pileup.TryGetValue(job.Id, out var state)) standby = state.Standby;
            else standby = job.TuneFrequencyHz;
            return true;
        }
    }

    public void NotePileupState(string jobId, bool following, long holdUntil, long standby)
    {
        lock (_sync) _pileup[jobId] = (following, holdUntil, standby);
    }

    public (bool Following, long HoldUntil, long Standby) GetPileupState(string jobId)
    {
        lock (_sync)
            return _pileup.TryGetValue(jobId, out var state) ? state : (false, 0, 0);
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var s = new string(chars).Trim('_');
        return string.IsNullOrEmpty(s) ? "job" : s[..Math.Min(40, s.Length)];
    }
}
