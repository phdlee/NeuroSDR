using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

/// <summary>Pick channels from another SCENE and copy them into the current one.</summary>
internal sealed class SceneChannelCopyForm : Form
{
    private readonly ComboBox _sceneBox = new();
    private readonly CheckedListBox _channels = new();
    private readonly Label _hint = new();
    private readonly IReadOnlyList<RxScene> _scenes;
    private readonly string _excludeSceneId;

    public IReadOnlyList<SceneFrequencyChannel> SelectedChannels { get; private set; } = [];

    public SceneChannelCopyForm(IReadOnlyList<RxScene> scenes, string excludeSceneId)
    {
        _scenes = scenes;
        _excludeSceneId = excludeSceneId ?? "";
        Text = "Copy channels from SCENE";
        ClientSize = new Size(360, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 27, 37);
        ForeColor = Color.FromArgb(216, 226, 236);
        Font = new Font("Segoe UI", 9f);

        var sceneLabel = new Label
        {
            Text = "SOURCE SCENE",
            Location = new Point(16, 14),
            Size = new Size(320, 16),
            ForeColor = Color.FromArgb(145, 181, 198),
            Font = new Font("Segoe UI Semibold", 7.5f)
        };

        _sceneBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _sceneBox.Location = new Point(16, 34);
        _sceneBox.Size = new Size(328, 26);
        _sceneBox.BackColor = Color.FromArgb(5, 17, 24);
        _sceneBox.ForeColor = Color.FromArgb(226, 235, 242);
        _sceneBox.FlatStyle = FlatStyle.Flat;
        foreach (var scene in _scenes.Where(s =>
                     !s.Id.Equals(_excludeSceneId, StringComparison.OrdinalIgnoreCase)))
            _sceneBox.Items.Add(scene);
        if (_sceneBox.Items.Count > 0) _sceneBox.SelectedIndex = 0;
        _sceneBox.SelectedIndexChanged += (_, _) => ReloadChannels();

        var listLabel = new Label
        {
            Text = "CHANNELS (check to copy)",
            Location = new Point(16, 68),
            Size = new Size(320, 16),
            ForeColor = Color.FromArgb(145, 181, 198),
            Font = new Font("Segoe UI Semibold", 7.5f)
        };

        _channels.Location = new Point(16, 88);
        _channels.Size = new Size(328, 250);
        _channels.BackColor = Color.FromArgb(5, 17, 24);
        _channels.ForeColor = Color.FromArgb(226, 235, 242);
        _channels.BorderStyle = BorderStyle.FixedSingle;
        _channels.CheckOnClick = true;

        _hint.Location = new Point(16, 344);
        _hint.Size = new Size(328, 28);
        _hint.ForeColor = Color.FromArgb(120, 150, 165);
        _hint.Font = new Font("Segoe UI", 7.5f);
        _hint.Text = "Copied as local channels of the current SCENE (new IDs).";

        var selectAll = MakeButton("All", 16, 378, 70);
        selectAll.Click += (_, _) => SetAllChecked(true);
        var selectNone = MakeButton("None", 92, 378, 70);
        selectNone.Click += (_, _) => SetAllChecked(false);
        var ok = MakeButton("Copy", 188, 378, 82);
        ok.BackColor = Color.FromArgb(196, 112, 39);
        ok.Click += (_, _) =>
        {
            SelectedChannels = _channels.CheckedItems
                .OfType<ChannelItem>()
                .Select(item => item.Channel.Clone())
                .ToArray();
            if (SelectedChannels.Count == 0)
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            foreach (var ch in SelectedChannels)
            {
                ch.Id = Guid.NewGuid().ToString("N");
                ch.Global = false;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = MakeButton("Cancel", 276, 378, 68);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([sceneLabel, _sceneBox, listLabel, _channels, _hint, selectAll, selectNone, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
        ReloadChannels();
    }

    private void ReloadChannels()
    {
        _channels.Items.Clear();
        if (_sceneBox.SelectedItem is not RxScene scene)
        {
            _hint.Text = _sceneBox.Items.Count == 0
                ? "No other SCENE to copy from."
                : "Select a SCENE.";
            return;
        }
        var channels = scene.FrequencyChannels ?? [];
        foreach (var ch in channels)
            _channels.Items.Add(new ChannelItem(ch), false);
        _hint.Text = channels.Count == 0
            ? $"“{scene.Name}” has no local channels."
            : $"{channels.Count} channel(s) · check and Copy";
    }

    private void SetAllChecked(bool on)
    {
        for (var i = 0; i < _channels.Items.Count; i++)
            _channels.SetItemChecked(i, on);
    }

    private static Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 28),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(47, 66, 78),
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 8f)
    };

    private sealed class ChannelItem(SceneFrequencyChannel channel)
    {
        public SceneFrequencyChannel Channel { get; } = channel;
        public override string ToString()
        {
            var name = string.IsNullOrWhiteSpace(Channel.Name)
                ? SceneFrequencyChannel.FormatMhz(Channel.Frequency)
                : Channel.Name;
            return $"{name}  ·  {Channel.Frequency / 1_000_000d:0.######} MHz  ·  {Channel.Mode}";
        }
    }
}
