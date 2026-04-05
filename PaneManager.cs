namespace WinGroup;

internal sealed class PaneManager
{
    private readonly Form _host;
    private readonly WindowEmbedder _embedder;
    private readonly WindowPicker _windowPicker;
    private readonly List<Panel> _panes = new();
    private readonly Dictionary<Panel, Panel> _paneHosts = new();
    private readonly Dictionary<Panel, Panel> _paneOverlays = new();
    private readonly Dictionary<Panel, ListBox> _panePickers = new();
    private SplitterGuideOverlay? _splitterGuideOverlay;

    public PaneManager(Form host, WindowEmbedder embedder, WindowPicker windowPicker)
    {
        _host = host;
        _embedder = embedder;
        _windowPicker = windowPicker;
        _embedder.EmbeddedWindowDetached += OnEmbeddedWindowDetached;
    }

    public void Initialize()
    {
        _host.Controls.Clear();
        _panes.Clear();
        _paneHosts.Clear();
        _paneOverlays.Clear();
        _panePickers.Clear();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Orientation = Orientation.Vertical,
            BorderStyle = BorderStyle.None,
            SplitterWidth = 6,
            Panel1MinSize = 100,
            Panel2MinSize = 100,
            BackColor = Color.FromArgb(24, 24, 24)
        };

        split.Panel1.BackColor = split.BackColor;
        split.Panel2.BackColor = split.BackColor;
        split.SplitterMoving += (_, e) => OnSplitterMoving(split, e);
        split.SplitterMoved += (_, _) => OnSplitterMoved();
        split.MouseCaptureChanged += (_, _) => ClearSplitterGuide();
        split.Resize += (_, _) => _embedder.FitAllEmbeddedWindows();

        _host.Controls.Add(split);

        var leftPane = CreatePane();
        var rightPane = CreatePane();

        _panes.Add(leftPane);
        _panes.Add(rightPane);

        split.Panel1.Controls.Add(leftPane);
        split.Panel2.Controls.Add(rightPane);

        _host.BeginInvoke(new Action(() =>
        {
            if (!split.IsDisposed && split.Width > 2)
            {
                split.SplitterDistance = split.Width / 2;
            }

            _embedder.FitAllEmbeddedWindows();
        }));

        RefreshPanePickers();
    }

    public void ResizeEmbeddedWindows()
    {
        _embedder.FitAllEmbeddedWindows();
    }

    private Panel CreatePane()
    {
        var pane = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(24, 24, 24)
        };

        var hostSurface = new PaneHostPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BorderStyle = BorderStyle.None,
            BackColor = pane.BackColor
        };

        var overlay = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BorderStyle = BorderStyle.None,
            BackColor = pane.BackColor
        };

        var picker = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = overlay.BackColor,
            ForeColor = Color.Gainsboro,
            IntegralHeight = false
        };

        overlay.Controls.Add(picker);
        pane.Controls.Add(hostSurface);
        pane.Controls.Add(overlay);

        _paneHosts[pane] = hostSurface;
        _paneOverlays[pane] = overlay;
        _panePickers[pane] = picker;

        pane.MouseDown += (_, _) => _embedder.ActivateEmbeddedWindow(pane);
        hostSurface.MouseDown += (_, _) => _embedder.ActivateEmbeddedWindow(pane);
        hostSurface.EmbeddedChildMouseDown += () => _embedder.ActivateEmbeddedWindow(pane);
        overlay.MouseDown += (_, _) => _embedder.ActivateEmbeddedWindow(pane);
        picker.MouseDown += (_, _) => _embedder.ActivateEmbeddedWindow(pane);
        hostSurface.Resize += (_, _) => _embedder.FitEmbeddedWindow(pane);
        picker.MouseClick += (_, e) => OnPanePickerClicked(pane, picker, e);

        return pane;
    }

    private void RefreshPanePickers()
    {
        var windows = _windowPicker.GetAvailableWindows(_host.Handle, _embedder.GetEmbeddedWindowHandles());

        foreach (var pane in _panes)
        {
            if (!_panePickers.TryGetValue(pane, out var picker) || !_paneOverlays.TryGetValue(pane, out var overlay))
            {
                continue;
            }

            if (_embedder.HasEmbeddedWindow(pane))
            {
                overlay.Visible = false;
                continue;
            }

            picker.BeginUpdate();
            picker.Items.Clear();
            foreach (var window in windows)
            {
                picker.Items.Add(window);
            }

            picker.EndUpdate();
            if (picker.Items.Count > 0)
            {
                picker.SelectedIndex = 0;
            }

            overlay.Visible = true;
            overlay.BringToFront();
        }
    }

    private void AssignWindowToPane(Panel pane, IntPtr hwnd)
    {
        if (!_paneHosts.TryGetValue(pane, out var hostSurface))
        {
            return;
        }

        if (!_embedder.TryEmbedWindow(pane, hwnd, hostSurface.Handle, out var error))
        {
            MessageBox.Show(error ?? "this window cannot be embedded", "WinGroup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshPanePickers();
            return;
        }

        if (_paneOverlays.TryGetValue(pane, out var overlay))
        {
            overlay.Visible = false;
        }

        _embedder.ActivateEmbeddedWindow(pane);
        RefreshPanePickers();
    }

    private void OnPanePickerClicked(Panel pane, ListBox picker, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var index = picker.IndexFromPoint(e.Location);
        if (index < 0)
        {
            return;
        }

        if (picker.Items[index] is not WindowPicker.WindowItem item)
        {
            return;
        }

        picker.SelectedIndex = index;
        AssignWindowToPane(pane, item.Hwnd);
    }

    private void OnEmbeddedWindowDetached(Panel pane)
    {
        if (pane.IsDisposed || !_panes.Contains(pane))
        {
            return;
        }

        RefreshPanePickers();
    }

    private void OnSplitterMoving(SplitContainer split, SplitterCancelEventArgs e)
    {
        ShowSplitterGuide(GetSplitterGuideScreenRect(split, e));
    }

    private void OnSplitterMoved()
    {
        ClearSplitterGuide();
        _embedder.FitAllEmbeddedWindows();
    }

    private void ShowSplitterGuide(Rectangle guideRect)
    {
        if (guideRect.IsEmpty)
        {
            return;
        }

        if (_splitterGuideOverlay == null || _splitterGuideOverlay.IsDisposed)
        {
            _splitterGuideOverlay = new SplitterGuideOverlay();
        }

        if (!_splitterGuideOverlay.Visible)
        {
            _splitterGuideOverlay.Bounds = guideRect;
            _splitterGuideOverlay.Show(_host);
            return;
        }

        if (_splitterGuideOverlay.Bounds != guideRect)
        {
            _splitterGuideOverlay.Bounds = guideRect;
        }
    }

    private void ClearSplitterGuide()
    {
        if (_splitterGuideOverlay == null)
        {
            return;
        }

        if (!_splitterGuideOverlay.IsDisposed)
        {
            _splitterGuideOverlay.Close();
            _splitterGuideOverlay.Dispose();
        }

        _splitterGuideOverlay = null;
    }

    private static Rectangle GetSplitterGuideScreenRect(SplitContainer split, SplitterCancelEventArgs e)
    {
        var thickness = Math.Max(2, split.SplitterWidth);
        if (split.Orientation == Orientation.Vertical)
        {
            var x = Math.Clamp(e.SplitX, 0, Math.Max(0, split.ClientSize.Width - thickness));
            return split.RectangleToScreen(new Rectangle(x, 0, thickness, Math.Max(1, split.ClientSize.Height)));
        }

        var y = Math.Clamp(e.SplitY, 0, Math.Max(0, split.ClientSize.Height - thickness));
        return split.RectangleToScreen(new Rectangle(0, y, Math.Max(1, split.ClientSize.Width), thickness));
    }

    private sealed class PaneHostPanel : Panel
    {
        public event Action? EmbeddedChildMouseDown;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_PARENTNOTIFY)
            {
                var childMessage = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (childMessage == Win32.WM_LBUTTONDOWN
                    || childMessage == Win32.WM_RBUTTONDOWN
                    || childMessage == Win32.WM_MBUTTONDOWN
                    || childMessage == Win32.WM_XBUTTONDOWN)
                {
                    EmbeddedChildMouseDown?.Invoke();
                }
            }

            base.WndProc(ref m);
        }
    }

    private sealed class SplitterGuideOverlay : Form
    {
        public SplitterGuideOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            Opacity = 0.4;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Win32.WS_EX_TOOLWINDOW;
                cp.ExStyle |= Win32.WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }
}
