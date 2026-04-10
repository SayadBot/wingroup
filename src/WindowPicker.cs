using System.Diagnostics;
using System.Text;

namespace WinGroup;

internal sealed class WindowPicker
{
    private readonly List<WindowItem> _items = new();
    private IntPtr _shellHandle;
    private HashSet<IntPtr> _excludedWindows = new();

    public IReadOnlyList<WindowItem> GetAvailableWindows(IntPtr shellHandle, HashSet<IntPtr> excludedWindows)
    {
        _shellHandle = shellHandle;
        _excludedWindows = excludedWindows;

        _items.Clear();
        Win32.EnumWindows(EnumWindow, IntPtr.Zero);

        return _items
            .OrderBy(item => item.Title)
            .ThenBy(item => item.ProcessName)
            .ToList();
    }

    private bool EnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        if (hwnd == _shellHandle || _excludedWindows.Contains(hwnd))
        {
            return true;
        }

        if (!Win32.IsWindowVisible(hwnd))
        {
            return true;
        }

        if (Win32.GetParent(hwnd) != IntPtr.Zero)
        {
            return true;
        }

        if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero)
        {
            return true;
        }

        var exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0 || (exStyle & Win32.WS_EX_NOACTIVATE) != 0)
        {
            return true;
        }

        var length = Win32.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return true;
        }

        var buffer = new StringBuilder(length + 1);
        Win32.GetWindowText(hwnd, buffer, buffer.Capacity);
        var title = buffer.ToString().Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        Win32.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return true;
        }

        string processName;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            processName = "Unknown";
        }

        _items.Add(new WindowItem(hwnd, title, processName));
        return true;
    }

    internal sealed record WindowItem(IntPtr Hwnd, string Title, string ProcessName)
    {
        public string DisplayText => $"{Title} ({ProcessName})";
        public override string ToString() => DisplayText;
    }
}
