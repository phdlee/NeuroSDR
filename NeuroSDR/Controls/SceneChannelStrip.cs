using NeuroSDR.Core;
using NeuroSDR.Settings;

namespace NeuroSDR.Controls;

/// <summary>SCENE frequency memories: ADD + scrollable chips (edit/delete via right-click or 1s hold).</summary>
internal sealed class SceneChannelStrip : Panel
{
    public const int LineHeight = 28;
    private const int LongPressMs = 1_000;
    private const int ButtonColumnWidth = 48;

    private readonly Button _addButton = new();
    private readonly Panel _scrollHost = new();
    private readonly FlowLayoutPanel _chips = new();
    private readonly ContextMenuStrip _channelMenu = new();
    private readonly ContextMenuStrip _emptyMenu = new();
    private readonly System.Windows.Forms.Timer _longPressTimer = new() { Interval = LongPressMs };
    private Button? _pressChip;
    private Point _pressScreen;
    private bool _suppressClick;
    private string? _selectedId;
    private int _lineCount = 1;
    private SceneFrequencyChannel? _menuChannel;

    public event Action? AddRequested;
    public event Action<SceneFrequencyChannel>? EditChannelRequested;
    public event Action<SceneFrequencyChannel>? DeleteChannelRequested;
    public event Action<SceneFrequencyChannel>? ChannelSelected;
    public event Action? LineCountChanged;
    public event Action? ClearAllRequested;
    public event Action? CopyFromSceneRequested;

    public int LineCount => _lineCount;
    public string? SelectedId => _selectedId;

    public SceneChannelStrip()
    {
        Height = LineHeight;
        BackColor = Color.FromArgb(16, 38, 50);
        StyleButton(_addButton, "ADD", 44);
        _addButton.Click += (_, _) => AddRequested?.Invoke();

        BuildChannelMenu();
        BuildEmptyMenu();

        _scrollHost.Location = new Point(ButtonColumnWidth, 0);
        _scrollHost.Height = LineHeight;
        _scrollHost.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _scrollHost.AutoScroll = true;
        _scrollHost.BackColor = Color.FromArgb(8, 24, 33);

        _chips.AutoSize = true;
        _chips.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _chips.WrapContents = true;
        _chips.FlowDirection = FlowDirection.LeftToRight;
        _chips.Padding = new Padding(2, 1, 2, 1);
        _chips.BackColor = Color.FromArgb(8, 24, 33);
        _scrollHost.Controls.Add(_chips);

        Controls.Add(_addButton);
        Controls.Add(_scrollHost);
        WireEmptyRightClick(this);
        WireEmptyRightClick(_scrollHost);
        WireEmptyRightClick(_chips);
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer.Stop();
            if (_pressChip?.Tag is not SceneFrequencyChannel channel) return;
            _suppressClick = true;
            ShowChannelMenu(channel, _pressChip.PointToScreen(new Point(_pressChip.Width / 2, _pressChip.Height)));
        };
        Resize += (_, _) => LayoutStrip();
    }

    public void SetChannels(IReadOnlyList<SceneFrequencyChannel> channels, string? selectedId = null)
    {
        _selectedId = selectedId;
        CancelLongPress();
        _chips.SuspendLayout();
        _chips.Controls.Clear();
        foreach (var channel in channels)
            _chips.Controls.Add(MakeChip(channel));
        _chips.ResumeLayout(true);
        LayoutStrip();
    }

    public void SelectId(string? id)
    {
        _selectedId = id;
        foreach (Control c in _chips.Controls)
        {
            if (c is not Button btn || c.Tag is not SceneFrequencyChannel ch) continue;
            var on = !string.IsNullOrEmpty(id) &&
                     ch.Id.Equals(id, StringComparison.OrdinalIgnoreCase);
            btn.BackColor = on ? Color.FromArgb(70, 95, 55) : Color.FromArgb(28, 52, 66);
            btn.FlatAppearance.BorderColor = on
                ? Color.FromArgb(255, 193, 69)
                : Color.FromArgb(70, 110, 130);
        }
    }

    private void BuildChannelMenu()
    {
        var editItem = new ToolStripMenuItem("Edit…") { ForeColor = Color.FromArgb(230, 238, 245) };
        var delItem = new ToolStripMenuItem("Delete") { ForeColor = Color.FromArgb(255, 180, 180) };
        editItem.Click += (_, _) =>
        {
            if (_menuChannel is not null) EditChannelRequested?.Invoke(_menuChannel);
        };
        delItem.Click += (_, _) =>
        {
            if (_menuChannel is not null) DeleteChannelRequested?.Invoke(_menuChannel);
        };
        _channelMenu.Items.Add(editItem);
        _channelMenu.Items.Add(delItem);
        StyleMenu(_channelMenu);
    }

    private void BuildEmptyMenu()
    {
        var clearItem = new ToolStripMenuItem("Clear all") { ForeColor = Color.FromArgb(255, 180, 180) };
        var copyItem = new ToolStripMenuItem("Copy from another scene…")
        {
            ForeColor = Color.FromArgb(230, 238, 245)
        };
        clearItem.Click += (_, _) => ClearAllRequested?.Invoke();
        copyItem.Click += (_, _) => CopyFromSceneRequested?.Invoke();
        _emptyMenu.Items.Add(clearItem);
        _emptyMenu.Items.Add(copyItem);
        StyleMenu(_emptyMenu);
    }

    private void WireEmptyRightClick(Control control)
    {
        control.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var at = control.PointToClient(Cursor.Position);
            var child = control.GetChildAtPoint(at, GetChildAtPointSkip.Invisible);
            if (child is Button) return;
            _emptyMenu.Show(control, e.Location);
        };
    }

    private Button MakeChip(SceneFrequencyChannel channel)
    {
        var text = channel.ToString();
        if (text.Length > 14) text = text[..13] + "…";
        var btn = new Button
        {
            Tag = channel,
            Text = text,
            AutoSize = false,
            Size = new Size(Math.Max(72, TextRenderer.MeasureText(text, Font).Width + 14), 22),
            Margin = new Padding(2, 2, 2, 2),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 7.2f),
            ForeColor = Color.FromArgb(220, 232, 240),
            BackColor = Color.FromArgb(28, 52, 66),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(70, 110, 130);
        btn.FlatAppearance.BorderSize = 1;
        var on = !string.IsNullOrEmpty(_selectedId) &&
                 channel.Id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase);
        if (on)
        {
            btn.BackColor = Color.FromArgb(70, 95, 55);
            btn.FlatAppearance.BorderColor = Color.FromArgb(255, 193, 69);
        }

        btn.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _suppressClick = true;
                CancelLongPress();
                ShowChannelMenu(channel, btn.PointToScreen(e.Location));
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            _suppressClick = false;
            _pressChip = btn;
            _pressScreen = Cursor.Position;
            _longPressTimer.Stop();
            _longPressTimer.Start();
        };
        btn.MouseMove += (_, _) =>
        {
            if (_pressChip != btn) return;
            var dx = Math.Abs(Cursor.Position.X - _pressScreen.X);
            var dy = Math.Abs(Cursor.Position.Y - _pressScreen.Y);
            if (dx > 6 || dy > 6) CancelLongPress();
        };
        btn.MouseUp += (_, _) => CancelLongPress();
        btn.MouseLeave += (_, _) => CancelLongPress();
        btn.Click += (_, _) =>
        {
            if (_suppressClick)
            {
                _suppressClick = false;
                return;
            }
            _selectedId = channel.Id;
            SelectId(channel.Id);
            ChannelSelected?.Invoke(channel);
        };
        return btn;
    }

    private void ShowChannelMenu(SceneFrequencyChannel channel, Point screenPoint)
    {
        _menuChannel = channel;
        _selectedId = channel.Id;
        SelectId(channel.Id);
        _channelMenu.Show(screenPoint);
    }

    private void CancelLongPress()
    {
        _longPressTimer.Stop();
        _pressChip = null;
    }

    private void LayoutStrip()
    {
        var needed = EstimateLines();
        var nextLines = Math.Clamp(needed, 1, 2);
        if (nextLines != _lineCount)
        {
            _lineCount = nextLines;
            LineCountChanged?.Invoke();
        }

        Height = _lineCount * LineHeight;
        // Stretch ADD to the full channel-strip height (1 or 2 lines).
        _addButton.SetBounds(2, 2, 44, Math.Max(20, Height - 4));

        _scrollHost.Left = ButtonColumnWidth;
        _scrollHost.Top = 0;
        _scrollHost.Width = Math.Max(40, Width - _scrollHost.Left);
        _scrollHost.Height = Height;

        var gutter = SystemInformation.VerticalScrollBarWidth + 4;
        _chips.MaximumSize = new Size(Math.Max(40, _scrollHost.ClientSize.Width - gutter), 0);
        _chips.PerformLayout();

        if (_lineCount >= 2 && needed > 2)
            _scrollHost.AutoScrollMinSize = new Size(0, Math.Max(Height + 1, _chips.PreferredSize.Height + 4));
        else
            _scrollHost.AutoScrollMinSize = Size.Empty;
    }

    private int EstimateLines()
    {
        if (_chips.Controls.Count == 0) return 1;
        var availWide = Math.Max(40, Width - ButtonColumnWidth - 8);
        var x = 0;
        var lines = 1;
        foreach (Control chip in _chips.Controls)
        {
            var w = chip.Width + chip.Margin.Horizontal;
            if (x > 0 && x + w > availWide)
            {
                lines++;
                x = 0;
            }
            x += w;
        }
        return lines;
    }

    private static void StyleMenu(ContextMenuStrip menu)
    {
        menu.BackColor = Color.FromArgb(18, 36, 48);
        menu.ForeColor = Color.FromArgb(230, 238, 245);
        menu.ShowImageMargin = false;
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
    }

    private static void StyleButton(Button button, string text, int width)
    {
        button.Text = text;
        button.Size = new Size(width, 22);
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI Semibold", 7.2f);
        button.ForeColor = Color.FromArgb(222, 233, 240);
        button.BackColor = Color.FromArgb(35, 66, 83);
        button.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        button.FlatAppearance.BorderSize = 1;
        button.TabStop = false;
        button.Margin = Padding.Empty;
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(45, 80, 100);
        public override Color MenuItemBorder => Color.FromArgb(86, 130, 151);
        public override Color MenuBorder => Color.FromArgb(70, 110, 130);
        public override Color ToolStripDropDownBackground => Color.FromArgb(18, 36, 48);
        public override Color ImageMarginGradientBegin => Color.FromArgb(18, 36, 48);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(18, 36, 48);
        public override Color ImageMarginGradientEnd => Color.FromArgb(18, 36, 48);
        public override Color SeparatorDark => Color.FromArgb(60, 90, 110);
        public override Color SeparatorLight => Color.FromArgb(40, 65, 80);
    }
}
