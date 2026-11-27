/*
 * 文件名称：ParchmentPreviewForm.Frame.cs
 * 文件职责：绘制包住标题栏与 RDP 画面的古铜色窗口边框，管理标题栏布局。
 * 交互约定：保留可调整大小的标准顶层窗口样式，按鼠标位置向 Windows 报告标题、图标和八个缩放方向。
 * 绘制约定：使用同一套古铜外框几何裁切原生窗口、绘制明暗和排布矩形预览区。
 */

using System.Runtime.InteropServices;

namespace SideStage.App;

internal sealed partial class ParchmentPreviewForm
{
    private const int CaptionDip = 30;
    private const int CaptionButtonDip = 44;
    private const int WindowCalculateClient = 0x0083;
    private const int WindowHitTest = 0x0084;
    private const int WindowNonClientActivate = 0x0086;
    private const int WindowPositionChanging = 0x0046, WindowPositionChanged = 0x0047;
    private const uint NoSize = 0x0001, NoMove = 0x0002;
    private const int HitClient = 1, HitCaption = 2;
    private const int HitLeft = 10, HitRight = 11, HitTop = 12, HitTopLeft = 13, HitTopRight = 14;
    private const int HitBottom = 15, HitBottomLeft = 16, HitBottomRight = 17;

    private readonly Font _captionSymbols = new("Segoe MDL2 Assets", 9F);
    private Icon? _captionIcon;
    private Button? _minimizeButton, _maximizeButton, _closeButton;
    private (Size Size, int Dpi, bool Maximized, bool NativePreview) _frameGeometry;
    private BronzeCornerEdges? _cornerEdges;
    private bool _inSizeMove;
    private HomeResizeSurface? _homeSurface;
    private bool _homeCaptureFailed;
    private PreviewResizeSurface? _previewResize;
    private PreviewBorderDrag? _borderDrag;
    private bool _previewCaptureFailed;

    /// <summary>标记系统开始移动或调整窗口。等收到实际尺寸变化后，再按当前页面选择绘制方式。</summary>
    protected override void OnResizeBegin(EventArgs e)
    {
        _inSizeMove = true;
        _homeCaptureFailed = false;
        _previewCaptureFailed = false;
        base.OnResizeBegin(e);
    }

    /// <summary>用户松开边框后，恢复由宿主绘制的底边和原始纸纹。</summary>
    protected override void OnResizeEnd(EventArgs e)
    {
        _inSizeMove = false;
        RestorePaintedRim();
        base.OnResizeEnd(e);
    }

    /// <summary>切换到其他窗口时立即还原真实页面，防止拖动中的旧画面继续覆盖宿主。</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        _inSizeMove = false;
        _borderDrag?.Finish(cancel: false);
        RestorePaintedRim();
        base.OnDeactivate(e);
    }

    /// <summary>页面、抽屉或拖动状态改变时撤下临时图层，并补绘其覆盖的窗框。</summary>
    private void RestorePaintedRim()
    {
        if (_previewResize is { } preview)
        {
            _previewResize = null;
            try
            {
                Opacity = 1;
                Refresh();
                DwmFlush();
            }
            finally { preview.Dispose(); }
            PlaceCornerEdges();
        }
        if (_homeSurface is { } surface)
        {
            _homeSurface = null;
            // 先恢复已排版的真实控件，再撤走它们上方的拖动画面。
            try
            {
                Opacity = 1;
                PlaceCornerEdges();
            }
            finally { surface.Dispose(); }
        }
        if (_cornerEdges?.BottomActive != true) return;
        _cornerEdges.HideResizeRim();
        Invalidate(new Rectangle(0, ClientSize.Height - Padding.Bottom, ClientSize.Width, Padding.Bottom));
        Update();
    }

    /// <summary>建立三个可通过辅助技术识别的窗口按钮，并留出纸面和远程画面共用的空间。</summary>
    private void BuildWindowFrame()
    {
        _minimizeButton = CreateCaptionButton("最小化", "\uE921", (_, _) => WindowState = FormWindowState.Minimized);
        _maximizeButton = CreateCaptionButton("最大化", "\uE922", (_, _) =>
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized);
        _closeButton = CreateCaptionButton("关闭", "\uE8BB", (_, _) => Close());
        _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(165, 58, 48);
        Controls.AddRange([_minimizeButton, _maximizeButton, _closeButton]);
        UpdateFrameGeometry();
    }

    /// <summary>使用系统窗口符号并提供中文辅助名称，使标题栏按钮支持鼠标及辅助技术调用。</summary>
    private Button CreateCaptionButton(string name, string glyph, EventHandler action)
    {
        CaptionButton button = new()
        {
            AccessibleName = name, Text = glyph, Font = _captionSymbols,
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(101, 79, 60) },
            ForeColor = CardLight, BackColor = FrameDark, FaceColor = FrameDark, TabStop = false,
            Margin = Padding.Empty, Padding = Padding.Empty,
        };
        button.Click += action;
        return button;
    }

    /// <summary>窗口命令共用纸面按钮的外观，但鼠标操作应像系统标题栏一样保留内容区焦点。</summary>
    private sealed class CaptionButton : PaperCommandButton
    {
        /// <summary>仅在处理左键按下时跳过 WinForms 的自动聚焦；松开、辅助技术调用和程序触发仍走标准按钮流程。</summary>
        protected override void WndProc(ref Message message)
        {
            const int leftButtonDown = 0x0201, leftButtonDoubleClick = 0x0203;
            if (message.Msg is not (leftButtonDown or leftButtonDoubleClick))
            {
                base.WndProc(ref message);
                return;
            }

            // TabStop 只管 Tab 导航。鼠标若把标题键变成活动控件，还原窗口后它会继续显示为默认按钮。
            bool selectable = GetStyle(ControlStyles.Selectable);
            SetStyle(ControlStyles.Selectable, false);
            try { base.WndProc(ref message); }
            finally { SetStyle(ControlStyles.Selectable, selectable); }
        }
    }

    /// <summary>在尺寸、缩放或最大化状态改变时更新真实窗形，同时给标题按钮和 RDP 预留内圆弧空间。</summary>
    private void UpdateFrameGeometry()
    {
        if (!IsHandleCreated || Disposing || IsDisposed) return;
        if (WindowState == FormWindowState.Minimized || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            PlaceCornerEdges();
            return;
        }
        bool maximized = WindowState == FormWindowState.Maximized;
        bool nativePreview = _centerCard?.Visible == false && Visible;
        var geometry = (ClientSize, DeviceDpi, maximized, nativePreview);
        if (geometry == _frameGeometry) { PlaceCornerEdges(); return; }
        // 设置 Region 和 Padding 都可能触发布局，先记下本次几何以避免重复分配原生区域。
        float scale = DeviceDpi / 96f;
        if (_cornerEdges is null || _frameGeometry.Dpi != DeviceDpi)
        {
            _cornerEdges?.Dispose();
            _cornerEdges = new BronzeCornerEdges(Handle, scale);
        }
        bool frameChanged = nativePreview != _frameGeometry.NativePreview;
        bool fullRedraw = frameChanged || _frameGeometry.Maximized != maximized || _frameGeometry.Dpi != DeviceDpi;
        bool expandedWidth = ClientSize.Width > _frameGeometry.Size.Width;
        _frameGeometry = geometry;
        _cornerEdges.ApplySolidRegion(Handle, ClientSize, maximized, fullRedraw || NeedsFullResizeFrame);
        int edge = BronzeWindowFrame.ContentInset(scale, maximized);
        Padding = new Padding(edge, edge + LogicalToDeviceUnits(CaptionDip), edge, edge);
        PlaceCornerEdges();
        // 按钮可能先被移到旧圆角裁切范围之外。新窗形生效后重新填满按钮，包括新露出的右端。
        if (expandedWidth)
        {
            _minimizeButton?.Invalidate();
            _maximizeButton?.Invalidate();
            _closeButton?.Invalidate();
        }
    }

    /// <summary>以客户区屏幕原点为基准同步四角，保持拖动、边框绘制和实际显示范围一致。</summary>
    private void PlaceCornerEdges()
    {
        if (_cornerEdges is null || !IsHandleCreated) return;
        _cornerEdges.Place(RectangleToScreen(ClientRectangle),
            _homeSurface == null && _previewResize == null && Visible && WindowState == FormWindowState.Normal);
    }

    /// <summary>窗口平移时只移动四角，复用已有透明像素。</summary>
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        PlaceCornerEdges();
    }

    /// <summary>随主窗口一起显示或隐藏弧线边缘。</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) RestorePaintedRim();
        PlaceCornerEdges();
    }

    /// <summary>窗口句柄重建或关闭时释放依附在该句柄上的边缘资源。</summary>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterSessionChanges();
        _borderDrag?.Dispose();
        _borderDrag = null;
        _previewResize?.Dispose();
        _previewResize = null;
        _homeSurface?.Dispose();
        _homeSurface = null;
        _cornerEdges?.Dispose();
        _cornerEdges = null;
        _frameGeometry = default;
        base.OnHandleDestroyed(e);
    }

    /// <summary>拖动窗口或切换最大化时，把原生可见区域随实际客户区尺寸一起更新。</summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!IsHandleCreated) return;
        if (WindowState != FormWindowState.Normal) RestorePaintedRim();
        UpdateFrameGeometry();
        // 布局完成后提交已失效的铜边，缩短旧边框停留在新窗口尺寸上的时间。
        if (WindowState == FormWindowState.Normal) Update();
    }

    /// <summary>跨显示器后，以新 DPI 重新计算铜边和标题栏占用的设备像素。</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        _borderDrag?.Finish(cancel: false);
        RestorePaintedRim();
        base.OnDpiChanged(e);
        UpdateFrameGeometry();
        Invalidate();
    }

    /// <summary>把三个按钮排在右上方的铜边之内。每次还原或最大化后同步按钮图形与名称。</summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_minimizeButton is null || _maximizeButton is null || _closeButton is null) return;
        int width = LogicalToDeviceUnits(CaptionButtonDip);
        int edge = Padding.Right;
        int height = LogicalToDeviceUnits(CaptionDip);
        int right = ClientSize.Width - edge;
        _minimizeButton.SetBounds(right - 3 * width, edge, width, height);
        _maximizeButton.SetBounds(right - 2 * width, edge, width, height);
        _closeButton.SetBounds(right - width, edge, width, height);
        bool maximized = WindowState == FormWindowState.Maximized;
        _maximizeButton.Text = maximized ? "\uE923" : "\uE922";
        _maximizeButton.AccessibleName = maximized ? "还原" : "最大化";
    }

    /// <summary>绘制铜边、标题栏图标与连接状态。Logo 和文字共用标题栏原有的拖动区域。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle title = Rectangle.FromLTRB(Padding.Left + LogicalToDeviceUnits(8), Padding.Left,
            _minimizeButton?.Left ?? ClientSize.Width, Padding.Top);
        BronzeWindowFrame.Paint(e.Graphics, ClientSize, DeviceDpi / 96f, WindowState == FormWindowState.Maximized,
            _workspace?.Bounds ?? Rectangle.Empty, title.Bottom, FrameDark);
        if (_cornerEdges?.BottomActive == true && WindowState == FormWindowState.Normal)
        {
            // 铜条作为整条移动时，旧位置应直接露出内容底色，否则可见一条残留的铜线。
            int radius = (int)Math.Round(BronzeWindowFrame.CornerRadius * DeviceDpi / 96d);
            using SolidBrush backing = new(ResizeBackingColor);
            e.Graphics.FillRectangle(backing, radius, ClientSize.Height - Padding.Bottom, ClientSize.Width - 2 * radius, Padding.Bottom);
        }
        int iconSize = LogicalToDeviceUnits(24);
        if (Icon != null && title.Width >= iconSize)
        {
            if (_captionIcon?.Width != iconSize)
            {
                _captionIcon?.Dispose();
                _captionIcon = new Icon(Icon, iconSize, iconSize);
            }
            e.Graphics.DrawIcon(_captionIcon, new Rectangle(title.Left, title.Top + (title.Height - iconSize) / 2, iconSize, iconSize));
            title = Rectangle.FromLTRB(title.Left + iconSize + LogicalToDeviceUnits(4), title.Top, title.Right, title.Bottom);
        }
        // 取与标题同字级的实际控件字体，运行中切换显示器后仍与标题栏图标保持比例。
        TextRenderer.DrawText(e.Graphics, Text, _checkRdpIcon?.Font ?? _labelFont ?? Font, title, CardLight,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>连接状态改变窗口标题时，只刷新本地标题栏这一条区域。</summary>
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (_homeSurface != null) RestorePaintedRim();
        Invalidate(new Rectangle(0, 0, ClientSize.Width, Padding.Top));
    }

    /// <summary>把完整窗口交给客户区绘制。最大化时只占当前屏幕工作区，避免装饰被屏幕边缘裁掉。</summary>
    private bool HandleWindowFrameMessage(ref Message message)
    {
        if (_borderDrag != null && _borderDrag.HandleMessage(ref message)) return true;
        if (message.Msg == 0x00a1 && NeedsFullResizeFrame && WindowState == FormWindowState.Normal &&
            (int)message.WParam is >= HitLeft and <= HitBottomRight)
        {
            ReleaseFieldFocus();
            long packedPoint = message.LParam.ToInt64();
            Point start = new(unchecked((short)packedPoint), unchecked((short)(packedPoint >> 16)));
            _previewCaptureFailed = false;
            _borderDrag = new PreviewBorderDrag(this, (int)message.WParam, start);
            Capture = true;
            return true;
        }
        if (message.Msg == 0x0214 && NeedsFullResizeFrame && WindowState == FormWindowState.Normal)
        {
            NativeRect rect = Marshal.PtrToStructure<NativeRect>(message.LParam);
            PresentPreviewSize(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            message.Result = 1;
            return true;
        }
        // 点击自绘标题栏也结束表单输入，再由系统开始移动或调整窗口。
        if (message.Msg == 0x00A1) ReleaseFieldFocus(); // WM_NCLBUTTONDOWN
        if (message.Msg is WindowPositionChanging or WindowPositionChanged)
        {
            SyncResizeFrame(message);
        }
        if (message.Msg == WindowNonClientActivate)
        {
            // 通知系统更新激活状态。传 -1 保留已由客户区绘制的铜边像素。
            message.Result = DefWindowProc(message.HWnd, message.Msg, message.WParam, -1);
            return true;
        }
        if (message.Msg == WindowCalculateClient)
        {
            // 两种 WM_NCCALCSIZE 参数都以待计算的屏幕矩形开头，只修改这第一个 RECT。
            if (IsZoomed(message.HWnd))
            {
                NativeRect rect = Marshal.PtrToStructure<NativeRect>(message.LParam);
                Rectangle window = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                Rectangle area = Rectangle.Intersect(window, Screen.FromRectangle(window).WorkingArea);
                if (!area.IsEmpty)
                    Marshal.StructureToPtr(new NativeRect(area), message.LParam, false);
            }
            message.Result = IntPtr.Zero;
            return true;
        }
        if (message.Msg != WindowHitTest) return false;

        // 屏幕左侧或上方的副屏可能使用负坐标，两个分量都必须按有符号短整数解包。
        long packed = message.LParam.ToInt64();
        Point point = PointToClient(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
        message.Result = HitTestFrame(point);
        return true;
    }

    /// <summary>远程画面和展开的设置侧栏都需要按新尺寸排版，才能绘制新露出的整片区域。</summary>
    private bool NeedsFullResizeFrame => _frameGeometry.NativePreview || _settingsDrawer?.Visible == true;

    /// <summary>按 Windows 的尺寸建议布局真实控件，再一次提交整幅画面。系统继续处理抓手、尺寸限制和取消。</summary>
    private void PresentPreviewSize(Rectangle target)
    {
        if (Bounds == target) return;
        if (_previewCaptureFailed) { Bounds = target; return; }
        try
        {
            if (_previewResize == null)
            {
                _previewResize = new PreviewResizeSurface(this);
                _previewResize.Present(this);
                // 先显示完整首帧，再隐去正在排版的控件；一个透明度灰阶保留宿主的鼠标命中。
                Opacity = 1d / 255;
                PlaceCornerEdges();
            }
            Bounds = target;
            _previewResize?.Present(this);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 内存图或合成层暂时不可用时，交还给不透明的原生预览。
            _previewCaptureFailed = true;
            RestorePaintedRim();
            Bounds = target;
        }
    }

    /// <summary>按实际位置和宽高提交首页整帧。设置抽屉在水平拖边时靠右合成，实时预览沿用原生控件。</summary>
    private void SyncResizeFrame(Message message)
    {
        if (!_inSizeMove || _cornerEdges == null || !Visible || WindowState != FormWindowState.Normal ||
            _frameGeometry.NativePreview || _previewResize != null) return;
        NativePosition next = Marshal.PtrToStructure<NativePosition>(message.LParam);
        bool resized = (next.Flags & NoSize) == 0;
        if (!resized)
        {
            if ((next.Flags & NoMove) == 0 && new Point(next.X, next.Y) != Location) RestorePaintedRim();
            return;
        }
        bool widthChanged = next.Width != _frameGeometry.Size.Width;
        bool heightChanged = next.Height != _frameGeometry.Size.Height;
        if (!widthChanged && !heightChanged) return;
        Point origin = (next.Flags & NoMove) != 0 ? Location : new(next.X, next.Y);
        Rectangle target = new(origin, new Size(next.Width, next.Height));
        PaperSurface? paper = _homePaper;
        Control? drawer = _settingsDrawer?.Visible == true ? _settingsDrawer : null;
        bool unclippedPaper = paper?.Visible == true && !paper.HorizontalScroll.Visible && !paper.VerticalScroll.Visible;
        bool canPresentHome = unclippedPaper && paper!.Controls.Count == 1 && _minimizeButton != null &&
            (drawer == null || !heightChanged);
        if (!_homeCaptureFailed && canPresentHome)
        {
            if (message.Msg != WindowPositionChanging && _homeSurface != null) return;
            try
            {
                _homeSurface ??= new HomeResizeSurface(this, paper!.Controls[0], _minimizeButton!.Left, drawer, _drawerTab);
                if (_homeSurface.Present(target))
                {
                    // 透明度保留一个灰阶，原窗口仍接收系统拖边；旧几何不会透出临时窗的透明圆角。
                    if (Opacity == 1)
                    {
                        Opacity = 1d / 255;
                        PlaceCornerEdges();
                    }
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // 桌面无法分配临时表面时，本次拖动继续走原来的窗口绘制。
            }
            _homeCaptureFailed = true;
            RestorePaintedRim();
        }
        else if (_homeSurface != null) RestorePaintedRim();
        if (widthChanged)
        {
            if (_cornerEdges.BottomActive) RestorePaintedRim();
            return;
        }
        if (message.Msg == WindowPositionChanging && next.Height < _frameGeometry.Size.Height) return;
        int paperDepth = 0;
        if (unclippedPaper && _settingsDrawer?.Visible == false && paper!.Controls.Count == 1)
        {
            // 首页的表格垂直居中。即使高 DPI 下空白已不足，也不能盖住最后一行按钮。
            int tableHeight = paper.Controls[0].Height;
            int blank = (Math.Min(ClientSize.Height, next.Height) - Padding.Vertical - tableHeight) / 2;
            paperDepth = Math.Clamp(blank, 0, LogicalToDeviceUnits(48));
        }
        _cornerEdges.PlaceVerticalResizeRim(target, paperDepth > 0 ? paper : null, paperDepth);
    }

    /// <summary>尺寸过渡时从两层之间透出的底色，与当前纸页或预览面板相同。</summary>
    private Color ResizeBackingColor => _centerCard?.Visible == true ? CardLight : _previewPanel.BackColor;
    /// <summary>先判断八向拖边，再判断图标及标题拖动区，其余坐标正常交给客户区和子控件。</summary>
    private int HitTestFrame(Point point)
    {
        if (WindowState == FormWindowState.Normal)
        {
            int edge = Padding.Left;
            bool left = point.X < edge, right = point.X >= ClientSize.Width - edge;
            bool top = point.Y < edge, bottom = point.Y >= ClientSize.Height - edge;
            if (top) return left ? HitTopLeft : right ? HitTopRight : HitTop;
            if (bottom) return left ? HitBottomLeft : right ? HitBottomRight : HitBottom;
            if (left) return HitLeft;
            if (right) return HitRight;
        }
        return point.Y < Padding.Top ? HitCaption : HitClient;
    }

    /// <summary>由窗体区域决定圆角，避免系统在铜条之外再包一层较小的圆角或强调色。</summary>
    private void ConfigureFrameCompositor()
    {
        // 不支持这两个 Windows 11 属性的系统返回 HRESULT，客户区自绘仍然有效。
        const int cornerPreference = 33, borderColor = 34;
        int useWindowRegion = 1;
        int hideSystemBorder = unchecked((int)0xfffffffe);
        DwmSetWindowAttribute(Handle, cornerPreference, ref useWindowRegion, sizeof(int));
        DwmSetWindowAttribute(Handle, borderColor, ref hideSystemBorder, sizeof(int));
    }
 
    /// <summary>随窗口关闭日志维护计时器与文件租约，并释放标题栏绘图资源。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logMaintenance.Dispose();
            _activityLog?.Dispose();
            _captionIcon?.Dispose();
            _captionSymbols.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>表示 Win32 RECT 的四个连续有符号坐标，适用于原位更新客户区边界。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(Rectangle bounds)
    {
        public int Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom;
    }

    /// <summary>Windows 即将应用的位置和尺寸，坐标以设备像素计。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePosition
    {
        public nint Window, After;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    /// <summary>在 Windows 处理尺寸消息的过程中读取原生最大化标志。</summary>
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);

    /// <summary>调用 Windows 的默认激活逻辑，并单独控制非客户区是否重绘。</summary>
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern nint DefWindowProc(nint window, int message, nint wParam, nint lParam);

    /// <summary>仅设置窗口外观属性，由桌面合成器负责阴影和屏幕合成。</summary>
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    /// <summary>等待恢复不透明的原生控件提交画面，再撤下拖动期间的显示层。</summary>
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
