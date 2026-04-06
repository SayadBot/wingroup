using System.Runtime.InteropServices;

namespace WinGroup;

internal sealed class WindowEmbedder : IDisposable
{
    private readonly Control _uiControl;
    private readonly Dictionary<Panel, EmbeddedWindowState> _paneToWindow = new();
    private readonly Dictionary<IntPtr, Panel> _windowToPane = new();
    private readonly Win32.WinEventDelegate _windowEventCallback;
    private readonly IntPtr _moveSizeAndMinimizeHook;
    private readonly IntPtr _destroyHook;

    public event Action<Panel>? EmbeddedWindowDetached;

    public WindowEmbedder(Control uiControl)
    {
        _uiControl = uiControl;
        _windowEventCallback = OnWindowEvent;
        _moveSizeAndMinimizeHook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_MOVESIZESTART,
            Win32.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero,
            _windowEventCallback,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        _destroyHook = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_DESTROY,
            Win32.EVENT_OBJECT_DESTROY,
            IntPtr.Zero,
            _windowEventCallback,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    public bool TryEmbedWindow(Panel pane, IntPtr hwnd, IntPtr hostHandle, out string? error)
    {
        error = null;
        var failureCode = 0;

        if (hwnd == IntPtr.Zero || hostHandle == IntPtr.Zero || !Win32.IsWindow(hwnd) || !Win32.IsWindow(hostHandle))
        {
            error = "this window cannot be embedded";
            return false;
        }

        if (_windowToPane.ContainsKey(hwnd))
        {
            error = "this window cannot be embedded";
            return false;
        }

        if (_paneToWindow.ContainsKey(pane))
        {
            ReleaseEmbeddedWindow(pane);
        }

        var state = CaptureState(hwnd, hostHandle);
        var embeddedStyle = (state.OriginalStyle | Win32.WS_CHILD)
            & ~Win32.WS_POPUP
            & ~Win32.WS_THICKFRAME
            & ~Win32.WS_CAPTION
            & ~Win32.WS_BORDER
            & ~Win32.WS_DLGFRAME;
        var embeddedExStyle = (state.OriginalExStyle & ~Win32.WS_EX_APPWINDOW)
            & ~Win32.WS_EX_WINDOWEDGE
            & ~Win32.WS_EX_CLIENTEDGE
            & ~Win32.WS_EX_STATICEDGE;

        try
        {
            Win32.SetLastError(0);
            var styleResult = Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, embeddedStyle);
            failureCode = Marshal.GetLastWin32Error();
            if (styleResult == 0 && failureCode != 0)
            {
                throw new InvalidOperationException();
            }

            Win32.SetLastError(0);
            var exStyleResult = Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, embeddedExStyle);
            failureCode = Marshal.GetLastWin32Error();
            if (exStyleResult == 0 && failureCode != 0)
            {
                throw new InvalidOperationException();
            }

            if (!ApplyFrameChange(hwnd, out failureCode))
            {
                throw new InvalidOperationException();
            }

            Win32.SetLastError(0);
            var previousParent = Win32.SetParent(hwnd, hostHandle);
            failureCode = Marshal.GetLastWin32Error();
            if (previousParent == IntPtr.Zero && failureCode != 0)
            {
                throw new InvalidOperationException();
            }

            ApplyEmbeddedBorderStyle(hwnd);
            state.UpdateClientInsets();

            _paneToWindow[pane] = state;
            _windowToPane[hwnd] = pane;

            if (!FitWindowToHost(state, true, out failureCode))
            {
                throw new InvalidOperationException();
            }

            Win32.ShowWindow(hwnd, Win32.SW_SHOW);
            return true;
        }
        catch
        {
            _windowToPane.Remove(hwnd);
            _paneToWindow.Remove(pane);
            RestoreWindowState(state);
            error = "this window cannot be embedded";
            return false;
        }
    }

    public void ReleaseEmbeddedWindow(Panel pane)
    {
        if (!_paneToWindow.TryGetValue(pane, out var state))
        {
            return;
        }

        _paneToWindow.Remove(pane);
        _windowToPane.Remove(state.Hwnd);
        RestoreWindowState(state);
    }

    public void FitEmbeddedWindow(Panel pane)
    {
        if (!_paneToWindow.TryGetValue(pane, out var state))
        {
            return;
        }

        if (!Win32.IsWindow(state.Hwnd) || !Win32.IsWindow(state.HostHandle))
        {
            return;
        }

        FitWindowToHost(state, false, out _);
    }

    public void FitAllEmbeddedWindows()
    {
        foreach (var pane in _paneToWindow.Keys.ToList())
        {
            FitEmbeddedWindow(pane);
        }
    }

    public HashSet<IntPtr> GetEmbeddedWindowHandles()
    {
        return _windowToPane.Keys.ToHashSet();
    }

    public bool HasEmbeddedWindow(Panel pane)
    {
        return _paneToWindow.ContainsKey(pane);
    }

    public void ActivateEmbeddedWindow(Panel pane)
    {
        if (!_paneToWindow.TryGetValue(pane, out var state) || !Win32.IsWindow(state.Hwnd))
        {
            return;
        }

        var shellHandle = _uiControl.FindForm()?.Handle ?? _uiControl.Handle;
        var currentThread = Win32.GetCurrentThreadId();
        var targetThread = Win32.GetWindowThreadProcessId(state.Hwnd, out _);
        var shellThread = shellHandle != IntPtr.Zero ? Win32.GetWindowThreadProcessId(shellHandle, out _) : 0;
        var foregroundWindow = Win32.GetForegroundWindow();
        var foregroundThread = foregroundWindow != IntPtr.Zero ? Win32.GetWindowThreadProcessId(foregroundWindow, out _) : 0;
        var attachedThreads = new List<(uint From, uint To)>();

        try
        {
            AttachThreadInput(currentThread, targetThread, attachedThreads);
            AttachThreadInput(currentThread, shellThread, attachedThreads);
            AttachThreadInput(currentThread, foregroundThread, attachedThreads);
            AttachThreadInput(shellThread, targetThread, attachedThreads);
            AttachThreadInput(foregroundThread, targetThread, attachedThreads);

            if (shellHandle != IntPtr.Zero && Win32.IsWindow(shellHandle))
            {
                Win32.SetForegroundWindow(shellHandle);
                Win32.SetActiveWindow(shellHandle);
            }

            Win32.SetFocus(state.Hwnd);
        }
        finally
        {
            for (var i = attachedThreads.Count - 1; i >= 0; i--)
            {
                var pair = attachedThreads[i];
                Win32.AttachThreadInput(pair.From, pair.To, false);
            }
        }
    }

    public void HideAllChildren()
    {
        foreach (var state in _paneToWindow.Values)
        {
            if (Win32.IsWindow(state.Hwnd))
            {
                Win32.ShowWindow(state.Hwnd, Win32.SW_HIDE);
            }
        }
    }

    public void ShowAllChildren()
    {
        foreach (var entry in _paneToWindow)
        {
            if (!Win32.IsWindow(entry.Value.Hwnd))
            {
                continue;
            }

            Win32.ShowWindow(entry.Value.Hwnd, Win32.SW_SHOW);
            FitEmbeddedWindow(entry.Key);
        }
    }

    private void OnWindowEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint dwmsEventTime)
    {
        if (_uiControl.IsDisposed || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_windowToPane.TryGetValue(hwnd, out _))
        {
            return;
        }

        if (_uiControl.InvokeRequired)
        {
            _uiControl.BeginInvoke(new Action(() => HandleWindowEvent(eventType, hwnd, idObject, idChild)));
            return;
        }

        HandleWindowEvent(eventType, hwnd, idObject, idChild);
    }

    private void HandleWindowEvent(uint eventType, IntPtr hwnd, int idObject, int idChild)
    {
        if (!_windowToPane.TryGetValue(hwnd, out var pane)
            || !_paneToWindow.TryGetValue(pane, out var state))
        {
            return;
        }

        if (eventType == Win32.EVENT_SYSTEM_MOVESIZESTART
            || eventType == Win32.EVENT_SYSTEM_MINIMIZESTART
            || eventType == Win32.EVENT_SYSTEM_MINIMIZEEND)
        {
            DetachTrackedWindow(hwnd, restoreWindowState: true);
            return;
        }

        if (eventType == Win32.EVENT_SYSTEM_MOVESIZEEND)
        {
            state.IsInMoveSizeSession = false;
            FitWindowToHost(state, true, out _);
            return;
        }

        if (eventType == Win32.EVENT_OBJECT_DESTROY
            && idObject == Win32.OBJID_WINDOW
            && idChild == 0)
        {
            HandleTrackedWindowDestroyed(hwnd);
        }
    }

    private void HandleTrackedWindowDestroyed(IntPtr hwnd)
    {
        DetachTrackedWindow(hwnd, restoreWindowState: false);
    }

    private void DetachTrackedWindow(IntPtr hwnd, bool restoreWindowState)
    {
        if (!_windowToPane.TryGetValue(hwnd, out var pane)
            || !_paneToWindow.TryGetValue(pane, out var state))
        {
            return;
        }

        _windowToPane.Remove(hwnd);
        _paneToWindow.Remove(pane);

        if (restoreWindowState)
        {
            RestoreWindowState(state);
        }

        EmbeddedWindowDetached?.Invoke(pane);
    }

    public void Dispose()
    {
        if (_moveSizeAndMinimizeHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_moveSizeAndMinimizeHook);
        }

        if (_destroyHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_destroyHook);
        }
    }

    private static EmbeddedWindowState CaptureState(IntPtr hwnd, IntPtr hostHandle)
    {
        var originalStyle = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        var originalExStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        var originalParent = Win32.GetParent(hwnd);
        var wasVisible = Win32.IsWindowVisible(hwnd);

        var placement = new Win32.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>()
        };

        var hasPlacement = Win32.GetWindowPlacement(hwnd, ref placement);
        var hasRect = Win32.GetWindowRect(hwnd, out var rect);

        return new EmbeddedWindowState(hwnd, hostHandle, originalParent, originalStyle, originalExStyle, hasPlacement, placement, hasRect, rect, wasVisible);
    }

    private static void RestoreWindowState(EmbeddedWindowState state)
    {
        if (!Win32.IsWindow(state.Hwnd))
        {
            return;
        }

        Win32.SetParent(state.Hwnd, state.OriginalParent);

        Win32.SetLastError(0);
        Win32.SetWindowLong(state.Hwnd, Win32.GWL_STYLE, state.OriginalStyle);
        Win32.SetLastError(0);
        Win32.SetWindowLong(state.Hwnd, Win32.GWL_EXSTYLE, state.OriginalExStyle);

        ApplyFrameChange(state.Hwnd, out _);
        RestoreDefaultBorderStyle(state.Hwnd);

        if (state.HasPlacement)
        {
            var placement = state.OriginalPlacement;
            placement.length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>();
            Win32.SetWindowPlacement(state.Hwnd, ref placement);
        }
        else if (state.HasRect)
        {
            var width = Math.Max(1, state.OriginalRect.Right - state.OriginalRect.Left);
            var height = Math.Max(1, state.OriginalRect.Bottom - state.OriginalRect.Top);
            Win32.SetWindowPos(
                state.Hwnd,
                IntPtr.Zero,
                state.OriginalRect.Left,
                state.OriginalRect.Top,
                width,
                height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        Win32.ShowWindow(state.Hwnd, state.WasVisible ? Win32.SW_SHOW : Win32.SW_HIDE);
    }

    private bool FitWindowToHost(EmbeddedWindowState state, bool force, out int errorCode)
    {
        errorCode = 0;

        if (!Win32.IsWindow(state.HostHandle))
        {
            return false;
        }

        Win32.SetLastError(0);
        if (!Win32.GetClientRect(state.HostHandle, out var hostClientRect))
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        var hostWidth = Math.Max(0, hostClientRect.Right - hostClientRect.Left);
        var hostHeight = Math.Max(0, hostClientRect.Bottom - hostClientRect.Top);
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return true;
        }

        var targetX = -state.ClientInsetLeft;
        var targetY = -state.ClientInsetTop;
        var targetWidth = Math.Max(1, hostWidth + state.ClientInsetLeft + state.ClientInsetRight);
        var targetHeight = Math.Max(1, hostHeight + state.ClientInsetTop + state.ClientInsetBottom);

        if (!force
            && state.LastX == targetX
            && state.LastY == targetY
            && state.LastWidth == targetWidth
            && state.LastHeight == targetHeight)
        {
            return true;
        }

        Win32.SetLastError(0);
        var moved = Win32.MoveWindow(state.Hwnd, targetX, targetY, targetWidth, targetHeight, true);
        errorCode = Marshal.GetLastWin32Error();
        if (!moved && errorCode != 0)
        {
            return false;
        }

        state.LastX = targetX;
        state.LastY = targetY;
        state.LastWidth = targetWidth;
        state.LastHeight = targetHeight;
        return true;
    }

    private static void AttachThreadInput(uint from, uint to, List<(uint From, uint To)> attachedThreads)
    {
        if (from == 0 || to == 0 || from == to)
        {
            return;
        }

        for (var i = 0; i < attachedThreads.Count; i++)
        {
            if (attachedThreads[i].From == from && attachedThreads[i].To == to)
            {
                return;
            }
        }

        if (Win32.AttachThreadInput(from, to, true))
        {
            attachedThreads.Add((from, to));
        }
    }

    private static void ApplyEmbeddedBorderStyle(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var color = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref color, 4);
    }

    private static void RestoreDefaultBorderStyle(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var color = Win32.DWMWA_COLOR_DEFAULT;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref color, 4);
    }

    private static bool ApplyFrameChange(IntPtr hwnd, out int errorCode)
    {
        Win32.SetLastError(0);
        var updated = Win32.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        errorCode = Marshal.GetLastWin32Error();
        return updated || errorCode == 0;
    }

    private sealed class EmbeddedWindowState
    {
        public EmbeddedWindowState(
            IntPtr hwnd,
            IntPtr hostHandle,
            IntPtr originalParent,
            int originalStyle,
            int originalExStyle,
            bool hasPlacement,
            Win32.WINDOWPLACEMENT originalPlacement,
            bool hasRect,
            Win32.RECT originalRect,
            bool wasVisible)
        {
            Hwnd = hwnd;
            HostHandle = hostHandle;
            OriginalParent = originalParent;
            OriginalStyle = originalStyle;
            OriginalExStyle = originalExStyle;
            HasPlacement = hasPlacement;
            OriginalPlacement = originalPlacement;
            HasRect = hasRect;
            OriginalRect = originalRect;
            WasVisible = wasVisible;
            IsInMoveSizeSession = false;
            LastX = int.MinValue;
            LastY = int.MinValue;
            LastWidth = int.MinValue;
            LastHeight = int.MinValue;
            ClientInsetLeft = 0;
            ClientInsetTop = 0;
            ClientInsetRight = 0;
            ClientInsetBottom = 0;
        }

        public IntPtr Hwnd { get; }
        public IntPtr HostHandle { get; }
        public IntPtr OriginalParent { get; }
        public int OriginalStyle { get; }
        public int OriginalExStyle { get; }
        public bool HasPlacement { get; }
        public Win32.WINDOWPLACEMENT OriginalPlacement { get; }
        public bool HasRect { get; }
        public Win32.RECT OriginalRect { get; }
        public bool WasVisible { get; }
        public bool IsInMoveSizeSession { get; set; }
        public int LastX { get; set; }
        public int LastY { get; set; }
        public int LastWidth { get; set; }
        public int LastHeight { get; set; }
        public int ClientInsetLeft { get; private set; }
        public int ClientInsetTop { get; private set; }
        public int ClientInsetRight { get; private set; }
        public int ClientInsetBottom { get; private set; }

        public void UpdateClientInsets()
        {
            if (!Win32.GetWindowRect(Hwnd, out var windowRect))
            {
                ClientInsetLeft = 0;
                ClientInsetTop = 0;
                ClientInsetRight = 0;
                ClientInsetBottom = 0;
                return;
            }

            if (!Win32.GetClientRect(Hwnd, out var clientRect))
            {
                ClientInsetLeft = 0;
                ClientInsetTop = 0;
                ClientInsetRight = 0;
                ClientInsetBottom = 0;
                return;
            }

            var clientTopLeft = new Win32.POINT
            {
                X = clientRect.Left,
                Y = clientRect.Top
            };
            var clientBottomRight = new Win32.POINT
            {
                X = clientRect.Right,
                Y = clientRect.Bottom
            };

            if (!Win32.ClientToScreen(Hwnd, ref clientTopLeft) || !Win32.ClientToScreen(Hwnd, ref clientBottomRight))
            {
                ClientInsetLeft = 0;
                ClientInsetTop = 0;
                ClientInsetRight = 0;
                ClientInsetBottom = 0;
                return;
            }

            ClientInsetLeft = Math.Max(0, clientTopLeft.X - windowRect.Left);
            ClientInsetTop = Math.Max(0, clientTopLeft.Y - windowRect.Top);
            ClientInsetRight = Math.Max(0, windowRect.Right - clientBottomRight.X);
            ClientInsetBottom = Math.Max(0, windowRect.Bottom - clientBottomRight.Y);
        }
    }
}
