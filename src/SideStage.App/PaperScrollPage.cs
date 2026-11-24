/*
 * 文件名称：PaperScrollPage.cs
 * 文件职责：为纸面页面提供双轴滚动视口，与活动日志共用滑块尺寸、颜色及轮廓。
 * 交互约定：内部面板负责原生滚轮、键盘焦点和内容布局，外侧滑块支持拖动和翻页。
 */
using System.Drawing.Drawing2D;

namespace SideStage.App;

internal class PaperScrollPage : Panel
{
    private readonly Panel _clip = new(), _scroll;
    private bool _arranging;
    private Orientation? _dragging;
    private int _grab, _wheelRemainder;

    /// <summary>建立滚动视口。单独裁切原生滚动区使系统继续管理表单焦点和实际滚动位置。</summary>
    internal PaperScrollPage() : this(new Panel { AutoScroll = true }) { }

    /// <summary>保留内容面板的原生滚动、焦点及纸纹，仅将系统滚动条移到裁切区外。</summary>
    internal PaperScrollPage(Panel surface)
    {
        _scroll = surface;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Dock = DockStyle.Fill;
        Controls.Add(_clip);
        _clip.Controls.Add(_scroll);
        _scroll.Layout += (_, _) => ArrangeViewport();
        _scroll.Scroll += (_, _) => Invalidate();
        _scroll.Dock = DockStyle.None;
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
            // 任一轴开始滚动都可能触发另一轴。用原生非客户区的实测尺寸补偿裁切区。
            for (int pass = 0; pass < 5; pass++)
            {
                int gutter = LogicalToDeviceUnits(12);
                Size visible = new(Math.Max(0, ClientSize.Width - (_scroll.VerticalScroll.Visible ? gutter : 0)),
                    Math.Max(0, ClientSize.Height - (_scroll.HorizontalScroll.Visible ? gutter : 0)));
                Size size = visible + (_scroll.Size - _scroll.ClientSize);
                if (_clip.Size == visible && _scroll.Size == size) break;
                _clip.Size = visible;
                _scroll.Size = size;
            }
            Invalidate();
        }
        finally { _arranging = false; }
    }

    /// <summary>读取内部面板的实际像素滚动范围，页面充分展开时范围为零。</summary>
    private int MaximumOffset(Orientation axis) => Math.Max(0, Length(_scroll.DisplayRectangle.Size, axis) - Length(_clip.Size, axis));

    /// <summary>把尺寸投影到当前操作轴，两个方向使用同一套拖动和翻页公式。</summary>
    private static int Length(Size size, Orientation axis) => axis == Orientation.Vertical ? size.Height : size.Width;

    /// <summary>读取指针或滚动位置在当前轴上的坐标。</summary>
    private static int Coordinate(Point point, Orientation axis) => axis == Orientation.Vertical ? point.Y : point.X;

    /// <summary>按照当前视口及首个可见像素计算右侧滑块。</summary>
    private Rectangle Thumb(Orientation axis)
    {
        bool visible = axis == Orientation.Vertical ? _scroll.VerticalScroll.Visible : _scroll.HorizontalScroll.Visible;
        if (!visible) return Rectangle.Empty;
        int page = Length(_clip.Size, axis);
        return PaperScrollThumb.Measure(this, LogicalToDeviceUnits(3), page, Length(_scroll.DisplayRectangle.Size, axis),
            -Coordinate(_scroll.AutoScrollPosition, axis), axis, page);
    }

    /// <summary>仅绘制右缘滑块，滑块外的命中区域沿用侧栏纸面底色。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaperScrollThumb.Draw(e.Graphics, Thumb(Orientation.Vertical), _dragging == Orientation.Vertical);
        PaperScrollThumb.Draw(e.Graphics, Thumb(Orientation.Horizontal), _dragging == Orientation.Horizontal);
    }

    /// <summary>在原生面板上滚动到目标像素，自动将请求限制到有效范围。</summary>
    private void ScrollTo(Orientation axis, int value)
    {
        Point current = _scroll.AutoScrollPosition;
        value = Math.Clamp(value, 0, MaximumOffset(axis));
        _scroll.AutoScrollPosition = axis == Orientation.Vertical ? new Point(-current.X, value) : new Point(value, -current.Y);
        Invalidate();
    }

    /// <summary>在滑块上按下开始拖动，点击其上下方则翻动一页。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Orientation axis = e.X >= _clip.Right ? Orientation.Vertical : Orientation.Horizontal;
        Rectangle thumb = Thumb(axis);
        if (e.Button != MouseButtons.Left || thumb.IsEmpty || _clip.Bounds.Contains(e.Location)) return;
        int position = Coordinate(e.Location, axis), start = Coordinate(thumb.Location, axis);
        if (position < start || position >= start + Length(thumb.Size, axis))
        {
            ScrollTo(axis, -Coordinate(_scroll.AutoScrollPosition, axis) +
                (position < start ? -1 : 1) * Length(_clip.Size, axis));
            return;
        }
        _grab = position - start;
        _dragging = axis;
        Capture = true;
        Invalidate();
    }

    /// <summary>把指针位移转换成滚动距离，使拖到任一端都能完整看到对应页面内容。</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int inset = LogicalToDeviceUnits(3);
        if (_dragging is not { } axis) return;
        int travel = Length(_clip.Size, axis) - 2 * inset - Length(Thumb(axis).Size, axis);
        if (travel <= 0) return;
        double fraction = Math.Clamp((double)(Coordinate(e.Location, axis) - _grab - inset) / travel, 0, 1);
        ScrollTo(axis, (int)Math.Round(fraction * MaximumOffset(axis)));
    }

    /// <summary>在松开或丢失鼠标捕获时结束拖动并恢复滑块颜色。</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture) return;
        _dragging = null;
        Invalidate();
    }

    /// <summary>右侧窄命中区也接受鼠标滚轮，累积高精度滚轮增量并遵循系统每档滚动量。</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Orientation axis = e.Y >= _clip.Bottom || MaximumOffset(Orientation.Vertical) == 0 ?
            Orientation.Horizontal : Orientation.Vertical;
        if (MaximumOffset(axis) == 0 || e is HandledMouseEventArgs { Handled: true }) return;
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        _wheelRemainder += e.Delta;
        int steps = Math.DivRem(_wheelRemainder, SystemInformation.MouseWheelScrollDelta, out _wheelRemainder);
        int lines = SystemInformation.MouseWheelScrollLines;
        ScrollTo(axis, -Coordinate(_scroll.AutoScrollPosition, axis) - steps *
            (lines < 0 ? Length(_clip.Size, axis) : lines * Font.Height));
    }
}

/// <summary>设置页和日志的滚动单位不同，但共用同一滑块比例和视觉样式。</summary>
internal static class PaperScrollThumb
{
    /// <summary>根据可见量与总量计算滑块高度和位置，足以显示全部内容时返回空矩形。</summary>
    internal static Rectangle Measure(Control owner, int inset, int page, int total, int position,
        Orientation axis = Orientation.Vertical, int? trackLength = null)
    {
        int track = (trackLength ?? (axis == Orientation.Vertical ? owner.Height : owner.Width)) - 2 * inset;
        if (total <= page || page <= 0 || track <= 0) return Rectangle.Empty;
        int height = Math.Clamp((int)Math.Round((double)track * page / total),
            Math.Min(track, owner.LogicalToDeviceUnits(18)), track);
        int y = inset + (int)Math.Round((track - height) * (double)Math.Clamp(position, 0, total - page) / (total - page));
        int width = owner.LogicalToDeviceUnits(4);
        int edge = owner.LogicalToDeviceUnits(3) + width;
        return axis == Orientation.Vertical ? new Rectangle(owner.Width - edge, y, width, height)
            : new Rectangle(y, owner.Height - edge, height, width);
    }

    /// <summary>绘制仅有圆头滑块的滚动指示，拖动时加深颜色。</summary>
    internal static void Draw(Graphics graphics, Rectangle thumb, bool dragging)
    {
        if (thumb.IsEmpty) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath shape = new();
        float radius = Math.Min(thumb.Width, thumb.Height) / 2f;
        shape.AddRoundedRectangle(thumb, new SizeF(radius, radius));
        using SolidBrush ink = new(dragging ? PaperFieldSurface.Brass : Color.FromArgb(166, 149, 122));
        graphics.FillPath(ink, shape);
    }
}
