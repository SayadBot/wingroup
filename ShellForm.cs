namespace WinGroup;

public sealed class ShellForm : Form
{
    private readonly WindowEmbedder _windowEmbedder;
    private readonly WindowPicker _windowPicker;
    private readonly PaneManager _paneManager;

    public ShellForm()
    {
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 24);

        var workingArea = Screen.PrimaryScreen?.WorkingArea;
        Bounds = workingArea ?? new Rectangle(100, 100, 1280, 720);

        _windowEmbedder = new WindowEmbedder(this);
        _windowPicker = new WindowPicker();
        _paneManager = new PaneManager(this, _windowEmbedder, _windowPicker);

        Load += OnLoad;
        Resize += OnResize;
        FormClosed += OnFormClosed;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_APPWINDOW;
            cp.ExStyle &= ~Win32.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        _paneManager.Initialize();
    }

    private void OnResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            _windowEmbedder.HideAllChildren();
            return;
        }

        _windowEmbedder.ShowAllChildren();
        _paneManager.ResizeEmbeddedWindows();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _windowEmbedder.Dispose();
    }
}
