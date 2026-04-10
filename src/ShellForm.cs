using System.Runtime.InteropServices;

namespace WinGroup;

public sealed class ShellForm : Form
{
    private const string WindowRegistryPath = @"Software\WinGroup\Window";
    private const string MonitorDeviceNameValue = "MonitorDeviceName";
    private const string XValue = "X";
    private const string YValue = "Y";
    private const string WidthValue = "Width";
    private const string HeightValue = "Height";
    private const string IsMaximizedValue = "IsMaximized";
    private const string AppTitle = "WinGroup";
    private const int FloatingBarTimerInterval = 50;
    private const int FloatingBarHoverHeight = 12;
    private const int FloatingBarTopMargin = 8;
    private const int FloatingBarHeight = 34;
    private const int FloatingBarWidth = 320;

    private readonly WindowEmbedder _windowEmbedder;
    private readonly WindowPicker _windowPicker;
    private readonly PaneManager _paneManager;
    private readonly System.Windows.Forms.Timer _floatingTitleBarTimer;
    private string? _currentMonitorDeviceName;
    private Panel? _floatingTitleBar;

    public ShellForm()
    {
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 24);

        var startupScreen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
        var startupBounds = startupScreen?.WorkingArea ?? new Rectangle(100, 100, 1280, 720);
        var startupMaximized = false;

        if (TryLoadWindowState(out var loadedState))
        {
            var loadedScreen = FindScreenByDeviceName(loadedState.MonitorDeviceName);
            startupScreen = loadedScreen ?? startupScreen;
            var workingArea = startupScreen?.WorkingArea ?? startupBounds;
            startupBounds = ClampBoundsToWorkingArea(new Rectangle(loadedState.X, loadedState.Y, loadedState.Width, loadedState.Height), workingArea);
            startupMaximized = loadedState.IsMaximized;
            _currentMonitorDeviceName = startupScreen?.DeviceName;
        }
        else
        {
            startupBounds = GetDefaultMonitorBounds(startupScreen, startupBounds);
            _currentMonitorDeviceName = startupScreen?.DeviceName;
        }

        Bounds = startupBounds;
        if (startupMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        _windowEmbedder = new WindowEmbedder(this);
        _windowPicker = new WindowPicker();
        _paneManager = new PaneManager(this, _windowEmbedder, _windowPicker);
        _floatingTitleBarTimer = new System.Windows.Forms.Timer { Interval = FloatingBarTimerInterval };
        _floatingTitleBarTimer.Tick += OnFloatingTitleBarTimerTick;

        Load += OnLoad;
        Resize += OnResize;
        Activated += OnActivated;
        DpiChanged += OnDpiChanged;
        FormClosing += OnFormClosing;
        FormClosed += OnFormClosed;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style &= ~Win32.WS_CAPTION;
            cp.Style |= Win32.WS_THICKFRAME;
            cp.ExStyle |= Win32.WS_EX_APPWINDOW;
            cp.ExStyle &= ~Win32.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        ApplyMainWindowTheme();
        _paneManager.Initialize();
        CreateFloatingTitleBar();
        UpdateFloatingTitleBarBounds();
        _floatingTitleBarTimer.Start();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            base.WndProc(ref m);
            AdjustClientTopInset(ref m);
            return;
        }

        base.WndProc(ref m);

        if (m.Msg == Win32.WM_NCHITTEST)
        {
            ApplyResizeHitTest(ref m);
        }

        if (m.Msg == Win32.WM_SETTINGCHANGE || m.Msg == Win32.WM_THEMECHANGED)
        {
            ApplyMainWindowTheme();
        }

        if (m.Msg == Win32.WM_EXITSIZEMOVE)
        {
            HandleExitSizeMove();
        }
    }

    private void OnResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            if (_floatingTitleBar != null)
            {
                _floatingTitleBar.Visible = false;
            }

            _windowEmbedder.HideAllChildren();
            return;
        }

        _windowEmbedder.ShowAllChildren();
        _paneManager.ResizeEmbeddedWindows();
        UpdateFloatingTitleBarBounds();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        BeginInvoke(new Action(_windowEmbedder.ActivateLastEmbeddedWindow));
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _floatingTitleBarTimer.Stop();
        _floatingTitleBarTimer.Dispose();
        _windowEmbedder.Dispose();
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        UpdateFloatingTitleBarBounds();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveWindowState();
    }

    private void ApplyMainWindowTheme()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var useDark = ShouldUseDarkTitleBar() ? 1u : 0u;
        Win32.DwmSetWindowAttribute(Handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, 4);
        Win32.DwmSetWindowAttribute(Handle, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, 4);
    }

    private static bool ShouldUseDarkTitleBar()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        return value is int intValue && intValue == 0;
    }

    private void HandleExitSizeMove()
    {
        _currentMonitorDeviceName = Screen.FromHandle(Handle).DeviceName;
        SaveWindowState();
    }

    private void CreateFloatingTitleBar()
    {
        if (_floatingTitleBar != null)
        {
            return;
        }

        var bar = new Panel
        {
            Visible = false,
            BackColor = Color.FromArgb(34, 34, 34),
            Padding = new Padding(8, 4, 8, 4)
        };

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = AppTitle,
            ForeColor = Color.FromArgb(235, 235, 235),
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var closeButton = CreateTitleButton("X");
        closeButton.Click += (_, _) => Close();

        var maximizeButton = CreateTitleButton("[]");
        maximizeButton.Click += (_, _) => ToggleMaximize();

        var minimizeButton = CreateTitleButton("_");
        minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;

        bar.Controls.Add(title);
        bar.Controls.Add(closeButton);
        bar.Controls.Add(maximizeButton);
        bar.Controls.Add(minimizeButton);

        title.MouseDown += OnFloatingTitleBarMouseDown;
        title.DoubleClick += OnFloatingTitleBarDoubleClick;
        bar.MouseDown += OnFloatingTitleBarMouseDown;
        bar.DoubleClick += OnFloatingTitleBarDoubleClick;

        Controls.Add(bar);
        bar.BringToFront();
        _floatingTitleBar = bar;
    }

    private static Button CreateTitleButton(string text)
    {
        var button = new Button
        {
            Dock = DockStyle.Right,
            Width = 34,
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
            Text = text,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(230, 230, 230),
            BackColor = Color.FromArgb(44, 44, 44),
            Margin = Padding.Empty
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 58, 58);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(68, 68, 68);
        return button;
    }

    private void UpdateFloatingTitleBarBounds()
    {
        if (_floatingTitleBar == null)
        {
            return;
        }

        var margin = ScaleForDpi(FloatingBarTopMargin, DeviceDpi);
        var height = ScaleForDpi(FloatingBarHeight, DeviceDpi);
        var width = Math.Min(Math.Max(ScaleForDpi(220, DeviceDpi), ScaleForDpi(FloatingBarWidth, DeviceDpi)), Math.Max(120, ClientSize.Width - (margin * 2)));
        var x = (ClientSize.Width - width) / 2;

        _floatingTitleBar.Bounds = new Rectangle(Math.Max(0, x), margin, width, height);
        _floatingTitleBar.BringToFront();
    }

    private void OnFloatingTitleBarTimerTick(object? sender, EventArgs e)
    {
        if (_floatingTitleBar == null || WindowState == FormWindowState.Minimized)
        {
            return;
        }

        var cursor = Cursor.Position;
        var windowBounds = Bounds;
        var insideWindow = windowBounds.Contains(cursor);
        var hoverHeight = ScaleForDpi(FloatingBarHoverHeight, DeviceDpi);
        var nearTop = insideWindow && cursor.Y <= windowBounds.Top + hoverHeight;
        var overBar = _floatingTitleBar.Visible && RectangleToScreen(_floatingTitleBar.Bounds).Contains(cursor);
        _floatingTitleBar.Visible = nearTop || overBar;
        if (_floatingTitleBar.Visible)
        {
            _floatingTitleBar.BringToFront();
        }
    }

    private void OnFloatingTitleBarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Win32.ReleaseCapture();
        Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
    }

    private void OnFloatingTitleBarDoubleClick(object? sender, EventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    private static int ScaleForDpi(int value, int dpi)
    {
        return (int)Math.Round(value * (dpi / 96f));
    }

    private void AdjustClientTopInset(ref Message m)
    {
        if (WindowState != FormWindowState.Normal || m.LParam == IntPtr.Zero)
        {
            return;
        }

        var parameters = Marshal.PtrToStructure<Win32.NCCALCSIZE_PARAMS>(m.LParam);
        var topInset = Win32.GetSystemMetrics(Win32.SM_CYSIZEFRAME) + Win32.GetSystemMetrics(Win32.SM_CXPADDEDBORDER);
        parameters.rgrc0.Top -= topInset;
        Marshal.StructureToPtr(parameters, m.LParam, false);
        m.Result = IntPtr.Zero;
    }

    private void ApplyResizeHitTest(ref Message m)
    {
        if (WindowState != FormWindowState.Normal || m.Result != (IntPtr)Win32.HTCLIENT)
        {
            return;
        }

        var raw = m.LParam.ToInt64();
        var x = unchecked((short)(raw & 0xFFFF));
        var y = unchecked((short)((raw >> 16) & 0xFFFF));
        var point = PointToClient(new Point(x, y));
        var border = Math.Max(4, Win32.GetSystemMetrics(Win32.SM_CXSIZEFRAME) + Win32.GetSystemMetrics(Win32.SM_CXPADDEDBORDER));

        var onLeft = point.X < border;
        var onRight = point.X >= ClientSize.Width - border;
        var onTop = point.Y < border;
        var onBottom = point.Y >= ClientSize.Height - border;

        if (onLeft && onTop)
        {
            m.Result = (IntPtr)Win32.HTTOPLEFT;
            return;
        }

        if (onRight && onTop)
        {
            m.Result = (IntPtr)Win32.HTTOPRIGHT;
            return;
        }

        if (onLeft && onBottom)
        {
            m.Result = (IntPtr)Win32.HTBOTTOMLEFT;
            return;
        }

        if (onRight && onBottom)
        {
            m.Result = (IntPtr)Win32.HTBOTTOMRIGHT;
            return;
        }

        if (onLeft)
        {
            m.Result = (IntPtr)Win32.HTLEFT;
            return;
        }

        if (onRight)
        {
            m.Result = (IntPtr)Win32.HTRIGHT;
            return;
        }

        if (onTop)
        {
            m.Result = (IntPtr)Win32.HTTOP;
            return;
        }

        if (onBottom)
        {
            m.Result = (IntPtr)Win32.HTBOTTOM;
        }
    }

    private void SaveWindowState()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var screen = Screen.FromRectangle(bounds);
        var clamped = ClampBoundsToWorkingArea(bounds, screen.WorkingArea);

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(WindowRegistryPath);
        if (key == null)
        {
            return;
        }

        key.SetValue(MonitorDeviceNameValue, screen.DeviceName, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue(XValue, clamped.X, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue(YValue, clamped.Y, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue(WidthValue, clamped.Width, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue(HeightValue, clamped.Height, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue(IsMaximizedValue, WindowState == FormWindowState.Maximized ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
    }

    private static bool TryLoadWindowState(out WindowStateSnapshot state)
    {
        state = default;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowRegistryPath);
        if (key == null)
        {
            return false;
        }

        var monitorDeviceName = key.GetValue(MonitorDeviceNameValue) as string;
        if (string.IsNullOrWhiteSpace(monitorDeviceName))
        {
            return false;
        }

        if (key.GetValue(XValue) is not int x
            || key.GetValue(YValue) is not int y
            || key.GetValue(WidthValue) is not int width
            || key.GetValue(HeightValue) is not int height)
        {
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var isMaximized = key.GetValue(IsMaximizedValue) is int maximized && maximized == 1;
        state = new WindowStateSnapshot(monitorDeviceName, x, y, width, height, isMaximized);
        return true;
    }

    private static Screen? FindScreenByDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        foreach (var screen in Screen.AllScreens)
        {
            if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return screen;
            }
        }

        return null;
    }

    private static Rectangle GetDefaultMonitorBounds(Screen? screen, Rectangle fallbackArea)
    {
        var workingArea = screen?.WorkingArea ?? fallbackArea;
        var width = Math.Max(1, (int)Math.Round(workingArea.Width * 0.8));
        var height = Math.Max(1, (int)Math.Round(workingArea.Height * 0.8));
        var x = workingArea.X + ((workingArea.Width - width) / 2);
        var y = workingArea.Y + ((workingArea.Height - height) / 2);
        return new Rectangle(x, y, width, height);
    }

    private static Rectangle ClampBoundsToWorkingArea(Rectangle bounds, Rectangle workingArea)
    {
        var width = Math.Clamp(bounds.Width, 1, workingArea.Width);
        var height = Math.Clamp(bounds.Height, 1, workingArea.Height);
        var minX = workingArea.Left;
        var minY = workingArea.Top;
        var maxX = workingArea.Right - width;
        var maxY = workingArea.Bottom - height;
        var x = Math.Clamp(bounds.X, minX, maxX);
        var y = Math.Clamp(bounds.Y, minY, maxY);
        return new Rectangle(x, y, width, height);
    }

    private readonly record struct WindowStateSnapshot(
        string MonitorDeviceName,
        int X,
        int Y,
        int Width,
        int Height,
        bool IsMaximized);
}
