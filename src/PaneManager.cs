using System.Drawing.Drawing2D;

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
    private readonly Dictionary<ListBox, Panel> _pickerHeaderRows = new();
    private readonly Dictionary<ListBox, Label> _pickerHeaders = new();
    private readonly Dictionary<ListBox, Button> _pickerRefreshButtons = new();
    private readonly Dictionary<ListBox, int> _pickerHoverIndices = new();
    private SplitterGuideOverlay? _splitterGuideOverlay;

    public PaneManager(Form host, WindowEmbedder embedder, WindowPicker windowPicker)
    {
        _host = host;
        _embedder = embedder;
        _windowPicker = windowPicker;
        _embedder.EmbeddedWindowDetached += OnEmbeddedWindowDetached;
        _host.DpiChanged += (_, _) => RefreshPickerMetrics();
        _host.Resize += (_, _) => RefreshPickerMetrics();
    }

    public void Initialize()
    {
        _host.Controls.Clear();
        _panes.Clear();
        _paneHosts.Clear();
        _paneOverlays.Clear();
        _panePickers.Clear();
        _pickerHeaderRows.Clear();
        _pickerHeaders.Clear();
        _pickerRefreshButtons.Clear();
        _pickerHoverIndices.Clear();

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
            BackColor = Color.FromArgb(18, 18, 18)
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

        var overlayLayout = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(ScaleForDpi(14, _host.DeviceDpi)),
            BorderStyle = BorderStyle.None,
            BackColor = overlay.BackColor
        };

        var pickerHeaderRow = new Panel
        {
            Dock = DockStyle.Top,
            Height = ScaleForDpi(30, _host.DeviceDpi),
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BorderStyle = BorderStyle.None,
            BackColor = Color.Transparent
        };

        var pickerHeader = new Label
        {
            Dock = DockStyle.Left,
            Width = 220,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 4),
            Text = "Select a window",
            ForeColor = Color.FromArgb(226, 226, 226),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var refreshButton = new Button
        {
            Dock = DockStyle.Right,
            Margin = Padding.Empty,
            Padding = new Padding(10, 2, 10, 2),
            FlatStyle = FlatStyle.Flat,
            Text = "Refresh",
            BackColor = Color.FromArgb(42, 42, 42),
            ForeColor = Color.FromArgb(230, 230, 230),
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            TabStop = false,
            Cursor = Cursors.Hand,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        refreshButton.FlatAppearance.BorderSize = 1;
        refreshButton.FlatAppearance.BorderColor = Color.FromArgb(72, 72, 72);
        refreshButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 52, 52);
        refreshButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(62, 62, 62);
        refreshButton.Click += (_, _) => RefreshPanePickers();

        var pickerCard = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(ScaleForDpi(6, _host.DeviceDpi)),
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(23, 23, 23)
        };
        pickerCard.Paint += (_, e) =>
        {
            var rect = new Rectangle(0, 0, Math.Max(0, pickerCard.Width - 1), Math.Max(0, pickerCard.Height - 1));
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedPath(rect, ScaleForDpi(8, pickerCard.DeviceDpi));
            using var borderPen = new Pen(Color.FromArgb(54, 54, 54));
            e.Graphics.DrawPath(borderPen, path);
        };

        var picker = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = pickerCard.BackColor,
            ForeColor = Color.FromArgb(236, 236, 236),
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 46,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point)
        };
        UpdatePickerMetrics(picker);
        _pickerHoverIndices[picker] = -1;

        pickerCard.Controls.Add(picker);
        pickerHeaderRow.Controls.Add(refreshButton);
        pickerHeaderRow.Controls.Add(pickerHeader);
        overlayLayout.Controls.Add(pickerCard);
        overlayLayout.Controls.Add(pickerHeaderRow);
        overlay.Controls.Add(overlayLayout);
        pane.Controls.Add(hostSurface);
        pane.Controls.Add(overlay);

        _paneHosts[pane] = hostSurface;
        _paneOverlays[pane] = overlay;
        _panePickers[pane] = picker;
        _pickerHeaderRows[picker] = pickerHeaderRow;
        _pickerHeaders[picker] = pickerHeader;
        _pickerRefreshButtons[picker] = refreshButton;

        pane.MouseDown += (_, _) => _embedder.ActivateEmbeddedWindow(pane);
        hostSurface.MouseDown += (_, _) => _embedder.ActivateEmbeddedWindow(pane);
        hostSurface.EmbeddedChildMouseDown += () => _embedder.ActivateEmbeddedWindow(pane);
        overlay.MouseDown += (_, _) => _embedder.ActivateEmbeddedWindow(pane);
        picker.MouseDown += (_, _) => _embedder.ActivateEmbeddedWindow(pane);
        hostSurface.Resize += (_, _) => _embedder.FitEmbeddedWindow(pane);
        picker.MouseClick += (_, e) => OnPanePickerClicked(pane, picker, e);
        picker.DrawItem += OnPanePickerDrawItem;
        picker.MouseMove += (_, e) => OnPanePickerMouseMove(picker, e);
        picker.MouseLeave += (_, _) => OnPanePickerMouseLeave(picker);
        picker.FontChanged += (_, _) => UpdatePickerMetrics(picker);
        pickerHeaderRow.Resize += (_, _) => UpdateHeaderMetrics(picker);

        UpdateHeaderMetrics(picker);

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

    private void OnPanePickerDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox picker)
        {
            return;
        }

        e.DrawBackground();

        if (e.Index < 0 || e.Index >= picker.Items.Count)
        {
            return;
        }

        var hoveredIndex = _pickerHoverIndices.TryGetValue(picker, out var value) ? value : -1;
        var isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var isHovered = hoveredIndex == e.Index;

        var rowInset = ScaleForDpi(3, picker.DeviceDpi);
        var rowRect = Rectangle.Inflate(e.Bounds, -rowInset, -rowInset);
        var horizontalPadding = ScaleForDpi(10, picker.DeviceDpi);
        var topPadding = ScaleForDpi(5, picker.DeviceDpi);
        var titleHeight = ScaleForDpi(19, picker.DeviceDpi);
        var processTop = ScaleForDpi(24, picker.DeviceDpi);
        var processHeight = ScaleForDpi(15, picker.DeviceDpi);

        var fillColor = picker.BackColor;
        if (isSelected)
        {
            fillColor = Color.FromArgb(58, 58, 58);
        }
        else if (isHovered)
        {
            fillColor = Color.FromArgb(44, 44, 44);
        }

        using (var fillBrush = new SolidBrush(fillColor))
        using (var path = CreateRoundedPath(rowRect, ScaleForDpi(6, picker.DeviceDpi)))
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fillBrush, path);
        }

        if (picker.Items[e.Index] is WindowPicker.WindowItem item)
        {
            var textWidth = Math.Max(0, rowRect.Width - (horizontalPadding * 2));
            var titleRect = new Rectangle(rowRect.Left + horizontalPadding, rowRect.Top + topPadding, textWidth, titleHeight);
            var processRect = new Rectangle(rowRect.Left + horizontalPadding, rowRect.Top + processTop, textWidth, processHeight);
            using var detailsFont = new Font("Segoe UI", Math.Max(7.5f, picker.Font.SizeInPoints - 1.5f), FontStyle.Regular, GraphicsUnit.Point);

            TextRenderer.DrawText(
                e.Graphics,
                item.Title,
                picker.Font,
                titleRect,
                Color.FromArgb(242, 242, 242),
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(
                e.Graphics,
                item.ProcessName,
                detailsFont,
                processRect,
                isSelected ? Color.FromArgb(220, 220, 220) : Color.FromArgb(172, 172, 172),
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }

        e.DrawFocusRectangle();
    }

    private void OnPanePickerMouseMove(ListBox picker, MouseEventArgs e)
    {
        var hoveredIndex = picker.IndexFromPoint(e.Location);
        if (!_pickerHoverIndices.TryGetValue(picker, out var previousIndex))
        {
            previousIndex = -1;
        }

        if (previousIndex == hoveredIndex)
        {
            return;
        }

        _pickerHoverIndices[picker] = hoveredIndex;
        picker.Invalidate();
    }

    private void OnPanePickerMouseLeave(ListBox picker)
    {
        if (!_pickerHoverIndices.TryGetValue(picker, out var previousIndex) || previousIndex == -1)
        {
            return;
        }

        _pickerHoverIndices[picker] = -1;
        picker.Invalidate();
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

    private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(2, radius * 2);
        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void RefreshPickerMetrics()
    {
        foreach (var picker in _panePickers.Values)
        {
            UpdatePickerMetrics(picker);
            UpdateHeaderMetrics(picker);
        }
    }

    private static void UpdatePickerMetrics(ListBox picker)
    {
        var primaryTextHeight = TextRenderer.MeasureText("Ag", picker.Font).Height;
        using var detailsFont = new Font("Segoe UI", Math.Max(7.5f, picker.Font.SizeInPoints - 1.5f), FontStyle.Regular, GraphicsUnit.Point);
        var secondaryTextHeight = TextRenderer.MeasureText("Ag", detailsFont).Height;
        var verticalPadding = ScaleForDpi(16, picker.DeviceDpi);
        picker.ItemHeight = Math.Max(40, primaryTextHeight + secondaryTextHeight + verticalPadding);
        picker.Invalidate();
    }

    private void UpdateHeaderMetrics(ListBox picker)
    {
        if (!_pickerHeaderRows.TryGetValue(picker, out var pickerHeaderRow)
            || !_pickerHeaders.TryGetValue(picker, out var pickerHeader)
            || !_pickerRefreshButtons.TryGetValue(picker, out var refreshButton))
        {
            return;
        }

        var dpi = picker.DeviceDpi;
        var horizontalGap = ScaleForDpi(10, dpi);
        var bottomPadding = ScaleForDpi(8, dpi);
        var headerBaselinePadding = ScaleForDpi(2, dpi);
        var buttonMinHeight = ScaleForDpi(26, dpi);

        pickerHeaderRow.Padding = new Padding(0, 0, 0, bottomPadding);
        pickerHeader.Padding = new Padding(0, 0, 0, headerBaselinePadding);
        refreshButton.Padding = new Padding(ScaleForDpi(10, dpi), ScaleForDpi(2, dpi), ScaleForDpi(10, dpi), ScaleForDpi(2, dpi));
        refreshButton.MinimumSize = new Size(ScaleForDpi(84, dpi), buttonMinHeight);

        var buttonWidth = refreshButton.PreferredSize.Width;
        pickerHeader.Width = Math.Max(0, pickerHeaderRow.ClientSize.Width - buttonWidth - horizontalGap);

        var contentHeight = Math.Max(pickerHeader.PreferredHeight, refreshButton.PreferredSize.Height);
        pickerHeaderRow.Height = Math.Max(ScaleForDpi(30, dpi), contentHeight + bottomPadding);
        pickerHeaderRow.Invalidate();
    }

    private static int ScaleForDpi(int value, int dpi)
    {
        return (int)Math.Round(value * (dpi / 96d));
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
