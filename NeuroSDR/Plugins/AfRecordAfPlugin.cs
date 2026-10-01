using NeuroSDR.Recording;

namespace NeuroSDR.Plugins;

internal sealed class AfRecordAfPlugin : IAfPlugin, IAfSidebarPlugin
{
    private readonly object _sync = new();
    private readonly Panel _view = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly Button _cancel = new();
    private readonly Label _status = new();
    private readonly List<short> _pcm = [];
    private int _sampleRate = 48_000;
    private bool _recording;

    public AfRecordAfPlugin()
    {
        _view.Width = 212;
        _view.Height = 118;
        _view.BackColor = Color.FromArgb(12, 24, 34);
        var caption = new Label
        {
            Text = "AF RECORD", AutoSize = false, Width = 212, Height = 18,
            ForeColor = Color.FromArgb(84, 171, 197), Font = new Font("Segoe UI Semibold", 8f)
        };
        ConfigureButton(_start, "Start", 0, Color.FromArgb(31, 112, 153));
        ConfigureButton(_stop, "Stopped", 72, Color.FromArgb(89, 71, 142));
        ConfigureButton(_cancel, "Cancel", 144, Color.FromArgb(118, 55, 64));
        _start.Click += (_, _) => Start();
        _stop.Click += (_, _) => Stop(save: true);
        _cancel.Click += (_, _) => Stop(save: false);
        _status.Location = new Point(0, 54);
        _status.Size = new Size(212, 58);
        _status.ForeColor = Color.FromArgb(180, 198, 210);
        _status.Text = "Idle";
        _view.Controls.AddRange([caption, _start, _stop, _cancel, _status]);
        UpdateButtons();
    }

    public AfPluginInfo Info { get; } = new(
        "builtin.af.record", "AF Record", "Record demodulated AF to WAV or MP3",
        AfPluginCapabilities.AudioInput, IsBuiltIn: true);

    public event Action<AfPluginResult>? ResultAvailable;
    public Control SidebarView => _view;

    public void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("command", out var command)) return;
        if (command.Equals("start", StringComparison.OrdinalIgnoreCase)) Start();
        else if (command.Equals("stop", StringComparison.OrdinalIgnoreCase)) Stop(true);
        else if (command.Equals("cancel", StringComparison.OrdinalIgnoreCase)) Stop(false);
    }

    public AfPluginResult? Process(AfAudioBlock block)
    {
        lock (_sync)
        {
            if (!_recording) return null;
            _sampleRate = block.SampleRate;
            var input = block.Input.Span;
            for (var index = 0; index < input.Length; index++)
                _pcm.Add((short)Math.Clamp(Math.Round(input[index] * 32767), short.MinValue, short.MaxValue));
            if (_pcm.Count > _sampleRate * 60 * 30)
            {
                _recording = false;
                SetStatus("30-minute limit · Stop to save");
            }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _recording = false;
            _pcm.Clear();
        }
        _view.Dispose();
    }

    private void Start()
    {
        lock (_sync)
        {
            _pcm.Clear();
            _recording = true;
        }
        SetStatus("Recording");
        UpdateButtons();
        ResultAvailable?.Invoke(new AfPluginResult(Info.Id, "STATUS", "AF recording", DateTime.UtcNow));
    }

    private void Stop(bool save)
    {
        short[] copy;
        int rate;
        lock (_sync)
        {
            _recording = false;
            copy = _pcm.ToArray();
            rate = _sampleRate;
            _pcm.Clear();
        }
        UpdateButtons();
        if (!save)
        {
            SetStatus("Canceled");
            return;
        }
        if (copy.Length == 0)
        {
            SetStatus("No audio to save");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Save AF Recording",
            Filter = "WAV (*.wav)|*.wav|MP3 (*.mp3)|*.mp3",
            FileName = $"neurosdr-af-{DateTime.Now:yyyyMMdd-HHmmss}.wav",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(_view.FindForm()) != DialogResult.OK)
        {
            SetStatus("Save canceled");
            return;
        }

        try
        {
            if (dialog.FilterIndex == 2 || dialog.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                AfAudioFileWriter.WriteMp3(dialog.FileName, rate, copy);
            else
                AfAudioFileWriter.WriteWav(dialog.FileName, rate, copy);
            SetStatus($"Saved\r\n{Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception exception)
        {
            SetStatus(exception.GetBaseException().Message);
        }
    }

    private void ConfigureButton(Button button, string text, int x, Color color)
    {
        button.Text = text;
        button.Location = new Point(x, 22);
        button.Size = new Size(66, 26);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 104, 121);
        button.BackColor = color;
        button.ForeColor = Color.FromArgb(235, 242, 249);
        button.Font = new Font("Segoe UI Semibold", 8f);
    }

    private void UpdateButtons()
    {
        void Apply()
        {
            _start.Enabled = !_recording;
            _stop.Enabled = _recording;
            _cancel.Enabled = _recording;
        }
        if (_view.IsHandleCreated && _view.InvokeRequired) _view.BeginInvoke(Apply);
        else Apply();
    }

    private void SetStatus(string text)
    {
        void Apply() => _status.Text = text;
        if (_view.IsHandleCreated && _view.InvokeRequired) _view.BeginInvoke(Apply);
        else Apply();
    }
}
