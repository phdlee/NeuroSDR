namespace NeuroSDR.Controls;

using System.Runtime.InteropServices;

/// <summary>
/// Reusable pop-out host: starts as a normal window on the owner screen;
/// F12 toggles true fullscreen. Chrome can be external (RF overlay) or built-in.
/// </summary>
internal sealed class PopOutHostForm : Form
{
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Panel _chrome = new()
    {
        Height = 32,
        Dock = DockStyle.Top,
        BackColor = Color.FromArgb(220, 10, 28, 38),
        Visible = false
    };
    private readonly Button _dockButton = new();
    private readonly Button _fullscreenButton = new();
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 3_500 };
    private FormBorderStyle _windowedBorder = FormBorderStyle.Sizable;
    private Rectangle _restoreBounds;
    private bool _fullscreen;
    private bool _externalChrome;
    private bool _raising;

    public event Action? DockRequested;
    public event Action? FullscreenChanged;
    /// <summary>Fired after move / resize / maximize / fullscreen changes (for SCENE auto-save).</summary>
    public event Action? LayoutStateChanged;

    public Panel HostPanel => _host;
    public bool IsFullscreen => _fullscreen;
    /// <summary>When true, built-in dock/fullscreen bar stays hidden (owner supplies hover chrome).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ExternalChrome
    {
        get => _externalChrome;
        set
        {
            _externalChrome = value;
            if (value)
            {
                _chrome.Visible = false;
                _hideTimer.Stop();
            }
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string TitleText { get => Text; set => Text = value; }

    private Form? _zOrderOwner;
    private bool _chromeDisposed;

    public PopOutHostForm(string title, Form? zOrderOwner = null)
    {
        Text = title;
        _zOrderOwner = zOrderOwner;
        FormBorderStyle = FormBorderStyle.Sizable;
        KeyPreview = true;
        ShowInTaskbar = true;
        ShowIcon = true;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(480, 320);
        // Stay above the main SDR window without covering other applications.
        Owner = zOrderOwner is { IsDisposed: false } ? zOrderOwner : null;

        ConfigureChromeButton(_dockButton, "↩ NeuroSDR", 8);
        _dockButton.Click += (_, _) => DockRequested?.Invoke();
        ConfigureChromeButton(_fullscreenButton, "Fullscreen (F12)", 116);
        _fullscreenButton.Width = 150;
        _fullscreenButton.Click += (_, _) => ToggleFullscreen();
        _chrome.Controls.Add(_dockButton);
        _chrome.Controls.Add(_fullscreenButton);

        Controls.Add(_host);
        Controls.Add(_chrome);
        _chrome.BringToFront();

        MouseEnter += (_, _) => ShowChrome();
        _host.MouseEnter += (_, _) => ShowChrome();
        _chrome.MouseEnter += (_, _) => ShowChrome();
        MouseLeave += (_, _) => ScheduleHideChrome();
        _host.MouseLeave += (_, _) => ScheduleHideChrome();
        _chrome.MouseLeave += (_, _) => ScheduleHideChrome();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_externalChrome || _chromeDisposed || IsDisposed || Disposing || _chrome.IsDisposed)
                return;
            try
            {
                if (!_chrome.IsHandleCreated) return;
                if (!_chrome.ClientRectangle.Contains(_chrome.PointToClient(Cursor.Position)))
                    _chrome.Visible = false;
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F12)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && _fullscreen)
            {
                SetFullscreen(false);
                e.Handled = true;
            }
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            // Cancel dispose; owner docks hosted controls then disposes this form.
            e.Cancel = true;
            var dock = DockRequested;
            BeginInvoke(() => dock?.Invoke());
        };
        LocationChanged += (_, _) =>
        {
            if (!_fullscreen) _restoreBounds = Bounds;
            LayoutStateChanged?.Invoke();
        };
        SizeChanged += (_, _) =>
        {
            if (!_fullscreen && WindowState == FormWindowState.Normal) _restoreBounds = Bounds;
            LayoutStateChanged?.Invoke();
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Maximized || WindowState == FormWindowState.Normal)
                LayoutStateChanged?.Invoke();
        };
        Activated += (_, _) =>
        {
            if (_raising || !Visible || IsDisposed || !IsHandleCreated) return;
            if (Owner is not null) return;
            RaiseToFront();
        };
    }

    /// <summary>Own taskbar button; never a tool window owned by the main form.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExAppWindow = 0x00040000;
            const int WsExToolWindow = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExAppWindow;
            cp.ExStyle &= ~WsExToolWindow;
            return cp;
        }
    }

    public void ShowWindowed(Rectangle bounds)
    {
        BindZOrderOwner();
        _fullscreen = false;
        FormBorderStyle = _windowedBorder;
        WindowState = FormWindowState.Normal;
        if (bounds.Width >= 400 && bounds.Height >= 280 &&
            Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            Bounds = bounds;
        else
        {
            var screen = Screen.FromPoint(bounds.Location);
            Bounds = new Rectangle(
                screen.WorkingArea.Left + 40,
                screen.WorkingArea.Top + 40,
                Math.Min(1100, screen.WorkingArea.Width - 80),
                Math.Min(700, screen.WorkingArea.Height - 80));
        }
        _restoreBounds = Bounds;
        UpdateFullscreenCaption();
        Show();
        RaiseToFront();
        ShowChrome();
    }

    public void ShowFullscreen(Screen screen)
    {
        BindZOrderOwner();
        // Seed restore bounds before first show if still empty.
        if (_restoreBounds.Width < 400)
        {
            var wa = screen.WorkingArea;
            _restoreBounds = new Rectangle(wa.Left + 40, wa.Top + 40,
                Math.Min(1100, wa.Width - 80), Math.Min(700, wa.Height - 80));
        }
        SetFullscreen(true, screen);
        Show();
        RaiseToFront();
        ShowChrome();
    }

    public void ToggleFullscreen() => SetFullscreen(!_fullscreen);

    public void SetFullscreen(bool fullscreen, Screen? screen = null)
    {
        if (fullscreen)
        {
            if (!_fullscreen && WindowState == FormWindowState.Normal && Bounds.Width >= 400)
                _restoreBounds = Bounds;
            else if (!_fullscreen && _restoreBounds.Width < 400)
            {
                var s = screen ?? (IsHandleCreated ? Screen.FromControl(this) : Screen.PrimaryScreen!);
                var wa = s.WorkingArea;
                _restoreBounds = new Rectangle(wa.Left + 40, wa.Top + 40,
                    Math.Min(1100, wa.Width - 80), Math.Min(700, wa.Height - 80));
            }
            _fullscreen = true;
            var target = screen ?? (IsHandleCreated ? Screen.FromControl(this) : Screen.PrimaryScreen!);
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = target.Bounds;
        }
        else
        {
            _fullscreen = false;
            FormBorderStyle = _windowedBorder;
            WindowState = FormWindowState.Normal;
            if (_restoreBounds.Width >= 400 && _restoreBounds.Height >= 280)
                Bounds = _restoreBounds;
            else
            {
                var s = IsHandleCreated ? Screen.FromControl(this) : Screen.PrimaryScreen!;
                Bounds = new Rectangle(s.WorkingArea.Left + 40, s.WorkingArea.Top + 40, 1000, 640);
            }
        }
        UpdateFullscreenCaption();
        FullscreenChanged?.Invoke();
        LayoutStateChanged?.Invoke();
        ShowChrome();
    }

    public void RaiseToFront()
    {
        if (IsDisposed) return;
        BindZOrderOwner();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        if (_fullscreen && IsHandleCreated)
            Bounds = Screen.FromControl(this).Bounds;

        if (Owner is not null)
        {
            try { BringToFront(); } catch (ObjectDisposedException) { }
            return;
        }

        NativeRaise(clearTopMostLater: true);
    }

    private void BindZOrderOwner()
    {
        if (_zOrderOwner is { IsDisposed: false } owner && !ReferenceEquals(Owner, owner))
            Owner = owner;
    }

    private void NativeRaise(bool clearTopMostLater)
    {
        if (!IsHandleCreated || IsDisposed || _raising) return;
        _raising = true;
        try
        {
            var hWnd = Handle;
            ShowWindow(hWnd, SwRestore);
            // HWND_TOPMOST briefly, then HWND_NOTOPMOST — above the main form, not sticky.
            SetWindowPos(hWnd, HwndTopMost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
            BringWindowToTop(hWnd);

            var fore = GetForegroundWindow();
            var foreThread = GetWindowThreadProcessId(fore, IntPtr.Zero);
            var appThread = GetWindowThreadProcessId(hWnd, IntPtr.Zero);
            var attached = false;
            if (fore != IntPtr.Zero && fore != hWnd && foreThread != 0 && foreThread != appThread)
                attached = AttachThreadInput(appThread, foreThread, true);
            try
            {
                SetForegroundWindow(hWnd);
                Activate();
                Focus();
            }
            finally
            {
                if (attached) AttachThreadInput(appThread, foreThread, false);
            }

            // Demote from sticky topmost while remaining above the main form.
            SetWindowPos(hWnd, HwndNoTopMost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);

            if (!clearTopMostLater) return;

            // Main-form layout often steals Z-order right after Show; re-assert once.
            BeginInvoke(() =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                var h = Handle;
                SetWindowPos(h, HwndTopMost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
                SetWindowPos(h, HwndNoTopMost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
                BringWindowToTop(h);
                SetForegroundWindow(h);
            });
        }
        finally
        {
            _raising = false;
        }
    }

    public string CaptureScreenDeviceName() =>
        IsHandleCreated ? Screen.FromControl(this).DeviceName : Screen.PrimaryScreen!.DeviceName;

    public Rectangle CaptureRestoreBounds() =>
        _fullscreen ? (_restoreBounds.Width >= 400 ? _restoreBounds : Bounds)
            : (WindowState == FormWindowState.Normal ? Bounds : RestoreBounds);

    /// <summary>True fullscreen (F12) or OS-maximized — both count as “full” for SCENE.</summary>
    public bool CaptureFullscreenState() =>
        _fullscreen || WindowState == FormWindowState.Maximized;

    public void ShowChrome()
    {
        if (_externalChrome || _chromeDisposed || IsDisposed || _chrome.IsDisposed) return;
        _hideTimer.Stop();
        _chrome.Visible = true;
        _chrome.BringToFront();
    }

    /// <summary>Forward hover from hosted content so chrome appears over filled Dock panels.</summary>
    public void AttachHoverTargets(Control root)
    {
        void Wire(Control control)
        {
            control.MouseEnter -= HoverShow;
            control.MouseLeave -= HoverHide;
            control.MouseEnter += HoverShow;
            control.MouseLeave += HoverHide;
            control.ControlAdded -= OnChildAdded;
            control.ControlAdded += OnChildAdded;
            foreach (Control child in control.Controls)
                Wire(child);
        }

        void OnChildAdded(object? sender, ControlEventArgs e)
        {
            if (e.Control is not null) Wire(e.Control);
        }

        void HoverShow(object? sender, EventArgs e) => ShowChrome();
        void HoverHide(object? sender, EventArgs e) => ScheduleHideChrome();

        Wire(root);
        Wire(_host);
    }

    public void StopChrome()
    {
        _chromeDisposed = true;
        try { _hideTimer.Stop(); } catch { }
        try { Owner = null; } catch { }
    }

    private void ScheduleHideChrome()
    {
        if (_externalChrome || _chromeDisposed || IsDisposed) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void UpdateFullscreenCaption() =>
        _fullscreenButton.Text = _fullscreen ? "Exit Fullscreen (F12)" : "Fullscreen (F12)";

    private static void ConfigureChromeButton(Button button, string text, int left)
    {
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI Semibold", 8f);
        button.ForeColor = Color.FromArgb(255, 193, 69);
        button.BackColor = Color.FromArgb(35, 66, 83);
        button.FlatAppearance.BorderColor = Color.FromArgb(86, 130, 151);
        button.Size = new Size(100, 24);
        button.Location = new Point(left, 4);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chromeDisposed = true;
            try { _hideTimer.Stop(); } catch { }
            _hideTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private const int SwRestore = 9;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
