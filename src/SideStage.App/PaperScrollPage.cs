/*
 * 文件名称：PaperScrollPage.cs
 * 文件职责：为设置和凭据页提供贴近右缘的滚动滑块，与活动日志共用尺寸、颜色及轮廓。
 * 交互约定：内部面板负责原生滚轮、键盘焦点和内容布局，外侧滑块支持拖动和翻页。
 */
using System.Drawing.Drawing2D;

namespace SideStage.App;

internal class PaperScrollPage : Panel
{
    private readonly Panel _clip = new(), _scroll = new() { AutoScroll = true };
    private bool _arranging, _dragging;
    private int _grab, _wheelRemainder;

    /// <summary>建立滚动视口。单独裁切原生滚动区使系统继续管理表单焦点和实际滚动位置。</summary>
    internal PaperScrollPage()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Dock = DockStyle.Fill;
        Controls.Add(_clip);
        _clip.Controls.Add(_scroll);
        _scroll.Layout += (_, _) => ArrangeViewport();
        _scroll.Scroll += (_, _) => Invalidate();
    }

    /// <summary>安装随宽度排版的页面，并跟踪键盘滚动或程序移位后的滑块位置。</summary>
    internal void SetPageContent(Control content)
    {
        _scroll.Controls.Add(content);
        content.LocationChanged += (_, _) => Invalidate();
        content.SizeChanged += (_, _) => ArrangeViewport();
        ArrangeViewport();
    }

    /// <summary>在高度或 DPI 变化时让滚动区重新分配可见宽度。</summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        ArrangeViewport();
    }

    /// <summary>溢出时把系统滚动条放到裁切区外，留出自绘滑块的命中区域。</summary>
    private void ArrangeViewport()
    {
        if (_arranging || _scroll == null || _scroll.Parent == null || Disposing) return;
        _arranging = true;
        try
        {
            // 视口高度先决定是否需要滚动条，宽度随后跟随实际出现的原生非客户区。
            _scroll.Height = ClientSize.Height;
            for (int pass = 0; pass < 2; pass++)
            {
                int gutter = _scroll.VerticalScroll.Visible ? LogicalToDeviceUnits(12) : 0;
                int native = _scroll.Width - _scroll.ClientSize.Width;
                int width = Math.Max(0, ClientSize.Width - gutter);
                _clip.Bounds = new Rectangle(0, 0, width, ClientSize.Height);
                _scroll.Width = width + native;
            }
            Invalidate();
        }
        finally { _arranging = false; }
    }

    /// <summary>读取内部面板的实际像素滚动范围，页面充分展开时范围为零。</summary>
    private int MaximumOffset => Math.Max(0, _scroll.DisplayRectangle.Height - _scroll.ClientSize.Height);

    /// <summary>按照当前视口及首个可见像素计算右侧滑块。</summary>
    private Rectangle Thumb => PaperScrollThumb.Measure(this, LogicalToDeviceUnits(3), Height,
        _scroll.DisplayRectangle.Height, -_scroll.AutoScrollPosition.Y);

    /// <summary>仅绘制右缘滑块，滑块外的命中区域沿用侧栏纸面底色。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaperScrollThumb.Draw(e.Graphics, Thumb, _dragging);
    }

    /// <summary>在原生面板上滚动到目标像素，自动将请求限制到有效范围。</summary>
    private void ScrollTo(int y)
    {
        _scroll.AutoScrollPosition = new Point(0, Math.Clamp(y, 0, MaximumOffset));
        Invalidate();
    }

    /// <summary>在滑块上按下开始拖动，点击其上下方则翻动一页。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Rectangle thumb = Thumb;
        if (e.Button != MouseButtons.Left || thumb.IsEmpty || e.X < _clip.Right) return;
        if (e.Y < thumb.Top || e.Y >= thumb.Bottom)
        {
            ScrollTo(-_scroll.AutoScrollPosition.Y + (e.Y < thumb.Top ? -Height : Height));
            return;
        }
        _grab = e.Y - thumb.Top;
        _dragging = true;
        Capture = true;
        Invalidate();
    }

    /// <summary>把指针位移转换成滚动距离，使拖到任一端都能完整看到对应页面内容。</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int inset = LogicalToDeviceUnits(3);
        int travel = Height - 2 * inset - Thumb.Height;
        if (!_dragging || travel <= 0) return;
        double fraction = Math.Clamp((double)(e.Y - _grab - inset) / travel, 0, 1);
        ScrollTo((int)Math.Round(fraction * MaximumOffset));
    }

    /// <summary>在松开或丢失鼠标捕获时结束拖动并恢复滑块颜色。</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture) return;
        _dragging = false;
        Invalidate();
    }

    /// <summary>右侧窄命中区也接受鼠标滚轮，累积高精度滚轮增量并遵循系统每档滚动量。</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (MaximumOffset == 0 || e is HandledMouseEventArgs { Handled: true }) return;
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        _wheelRemainder += e.Delta;
        int steps = Math.DivRem(_wheelRemainder, SystemInformation.MouseWheelScrollDelta, out _wheelRemainder);
        int lines = SystemInformation.MouseWheelScrollLines;
        ScrollTo(-_scroll.AutoScrollPosition.Y - steps * (lines < 0 ? Height : lines * Font.Height));
    }
}

/// <summary>设置页和日志的滚动单位不同，但共用同一滑块比例和视觉样式。</summary>
internal static class PaperScrollThumb
{
    /// <summary>根据可见量与总量计算滑块高度和位置，足以显示全部内容时返回空矩形。</summary>
    internal static Rectangle Measure(Control owner, int inset, int page, int total, int position)
    {
        int track = owner.Height - 2 * inset;
        if (total <= page || page <= 0 || track <= 0) return Rectangle.Empty;
        int height = Math.Clamp((int)Math.Round((double)track * page / total),
            Math.Min(track, owner.LogicalToDeviceUnits(18)), track);
        int y = inset + (int)Math.Round((track - height) * (double)Math.Clamp(position, 0, total - page) / (total - page));
        int width = owner.LogicalToDeviceUnits(4);
        return new Rectangle(owner.Width - owner.LogicalToDeviceUnits(3) - width, y, width, height);
    }

    /// <summary>绘制仅有圆头滑块的滚动指示，拖动时加深颜色。</summary>
    internal static void Draw(Graphics graphics, Rectangle thumb, bool dragging)
    {
        if (thumb.IsEmpty) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath shape = new();
        shape.AddRoundedRectangle(thumb, new SizeF(thumb.Width / 2f, thumb.Width / 2f));
        using SolidBrush ink = new(dragging ? PaperFieldSurface.Brass : Color.FromArgb(166, 149, 122));
        graphics.FillPath(ink, shape);
    }
}
