using System.Text;
using NeuroSDR.Controls;
using NeuroSDR.Settings;

namespace NeuroSDR.Recording;

/// <summary>Builds an HTML-ish report of FT8/FT4 decodes that matched Smart Record filters.</summary>
internal sealed class FtxDecodeReportSession : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private readonly SmartRecordJob _job;
    private readonly HashSet<string> _callsigns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _worked = new(StringComparer.OrdinalIgnoreCase);
    private int _rows;
    private bool _disposed;

    public string Path { get; }
    public string JobId { get; }

    public FtxDecodeReportSession(string path, SmartRecordJob job)
    {
        Path = path;
        JobId = job.Id;
        _job = job.Clone();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), Encoding.UTF8);
        WriteHeader();
    }

    public bool TryAppend(string utc, string db, string dt, string freq, string message, string vfoId)
    {
        if (!Matches(message)) return false;
        var call = AudioSpectrumWaterfallControl.ExtractFtxCallsign(message);
        var called = AudioSpectrumWaterfallControl.ExtractCalledCallsign(message);
        var isCq = AudioSpectrumWaterfallControl.IsCqMessage(message);
        lock (_sync)
        {
            if (_disposed) return false;
            if (call.Length > 0) _callsigns.Add(call);
            if (called.Length > 0 && call.Length > 0) _worked.Add($"{call} → {called}");
            _rows++;
            var kind = isCq ? "CQ" : (called.Length > 0 ? "QSO" : "MSG");
            _writer.WriteLine(
                $"<tr><td>{Escape(utc)}</td><td>{Escape(vfoId)}</td><td>{Escape(db)}</td><td>{Escape(dt)}</td>" +
                $"<td>{Escape(freq)}</td><td>{kind}</td><td>{Escape(call)}</td><td>{Escape(called)}</td>" +
                $"<td class=\"msg\">{Escape(message)}</td></tr>");
            if (_rows % 8 == 0) _writer.Flush();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                WriteFooter();
                _writer.Flush();
            }
            catch { }
            finally { _writer.Dispose(); }
        }
    }

    private bool Matches(string message)
    {
        if (_job.CqOnly && !AudioSpectrumWaterfallControl.IsCqMessage(message)) return false;
        if (!string.IsNullOrWhiteSpace(_job.MessageContains) &&
            message.IndexOf(_job.MessageContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        var call = AudioSpectrumWaterfallControl.ExtractFtxCallsign(message);
        if (!string.IsNullOrWhiteSpace(_job.CallsignContains) &&
            call.IndexOf(_job.CallsignContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0 &&
            message.IndexOf(_job.CallsignContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        if (!string.IsNullOrWhiteSpace(_job.Prefix))
        {
            var prefix = _job.Prefix.Trim();
            if (!call.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !message.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        // No filters → log everything while the report window is open.
        return true;
    }

    private void WriteHeader()
    {
        _writer.WriteLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
        _writer.WriteLine("<title>NeuroSDR FT8 Report</title>");
        _writer.WriteLine("<style>");
        _writer.WriteLine("body{font-family:Segoe UI,Arial,sans-serif;background:#0b1620;color:#dce8f0;margin:24px}");
        _writer.WriteLine("h1{color:#ffc145;font-size:22px} h2{color:#9fd0e8;font-size:15px;margin-top:28px}");
        _writer.WriteLine("table{border-collapse:collapse;width:100%;font-size:13px}");
        _writer.WriteLine("th,td{border:1px solid #2a4558;padding:6px 8px;text-align:left}");
        _writer.WriteLine("th{background:#163040;color:#ffc145} tr:nth-child(even){background:#102030}");
        _writer.WriteLine(".msg{font-family:Consolas,monospace} .meta{color:#9bb4c4;font-size:13px}");
        _writer.WriteLine("</style></head><body>");
        _writer.WriteLine($"<h1>FT8 / FT4 Decode Report</h1>");
        _writer.WriteLine($"<p class=\"meta\">Job: {Escape(_job.Name)} · Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} · VFO filter: {Escape(_job.VfoId)}</p>");
        _writer.WriteLine("<p class=\"meta\">Filters: " +
                          $"callsign contains “{Escape(_job.CallsignContains)}”, prefix “{Escape(_job.Prefix)}”, " +
                          $"message “{Escape(_job.MessageContains)}”, CQ only={_job.CqOnly}</p>");
        _writer.WriteLine("<h2>Decoded traffic</h2>");
        _writer.WriteLine("<table><thead><tr>" +
                          "<th>UTC</th><th>VFO</th><th>dB</th><th>DT</th><th>AF Hz</th><th>Kind</th>" +
                          "<th>Call</th><th>To</th><th>Message</th></tr></thead><tbody>");
    }

    private void WriteFooter()
    {
        _writer.WriteLine("</tbody></table>");
        _writer.WriteLine($"<h2>Summary</h2>");
        _writer.WriteLine($"<p>Rows: {_rows} · Unique callsigns: {_callsigns.Count} · Directed pairs: {_worked.Count}</p>");
        _writer.WriteLine("<h2>Callsigns heard</h2><ul>");
        foreach (var call in _callsigns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            _writer.WriteLine($"<li>{Escape(call)}</li>");
        _writer.WriteLine("</ul><h2>Directed activity</h2><ul>");
        foreach (var pair in _worked.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            _writer.WriteLine($"<li>{Escape(pair)}</li>");
        _writer.WriteLine($"</ul><p class=\"meta\">Closed {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p></body></html>");
    }

    private static string Escape(string? text) =>
        System.Net.WebUtility.HtmlEncode(text ?? "");
}
