/*
 * 文件名称：PaperLogFrame.cs
 * 文件职责：承载可选择、可复制的彩色活动日志，在纸面右缘绘制可拖动的细滑块。
 * 滚动约定：所有位置按富文本框实际排版后的显示行计算，长记录换行和窗口缩放均参与计算。
 * 交互约定：滑块只在内容溢出时出现，其鼠标命中区宽于可见宽度，保留正文的选择和键盘操作。
 */
using System.Runtime.InteropServices;

namespace SideStage.App;

internal sealed class PaperLogFrame : Panel
{
    private const int GetLineCount = 0x00BA, GetFirstVisibleLine = 0x00CE, LineScroll = 0x00B6;
    private readonly RichTextBox _log;
    private int _dragOffset;
    private bool _dragging;
    private int _wheelRemainder;

    /// <summary>设置文本留白与右缘命中区，订阅会改变可见行范围的原生编辑器事件。</summary>
    internal PaperLogFrame(RichTextBox log)
    {
        _log = log;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = new Padding(PaperFieldSurface.TextInset, 7, 12, 7);
        log.BackColor = PaperFieldSurface.Paper;
        log.BorderStyle = BorderStyle.None;
        log.Dock = DockStyle.Fill;
        log.VScroll += RefreshThumb;
        log.TextChanged += RefreshThumb;
        log.SelectionChanged += RefreshThumb;
        log.ContentsResized += RefreshThumb;
        log.MouseWheel += ScrollWheel;
        MouseWheel += ScrollWheel;
        log.KeyUp += RefreshThumb;
        Controls.Add(log);
    }

    /// <summary>排队刷新右侧滑块，编辑器自身负责文本绘制和选区。</summary>
    private void RefreshThumb(object? sender, EventArgs e) =>
        Invalidate(new Rectangle(Width - Padding.Right, 0, Padding.Right, Height));

    /// <summary>读取可见首行、总行数和页容量，用富文本本身的行距计算滑块几何。</summary>
    private (Rectangle Thumb, int First, int MaxFirst) MeasureScroll()
    {
        if (!_log.IsHandleCreated || _log.TextLength == 0) return default;
        int total = (int)SendMessage(_log.Handle, GetLineCount, 0, 0);
        if (total <= 1) return default;
        int first = (int)SendMessage(_log.Handle, GetFirstVisibleLine, 0, 0);
        int sample = Math.Clamp(first, 0, total - 2);
        int start = _log.GetFirstCharIndexFromLine(sample);
        int next = _log.GetFirstCharIndexFromLine(sample + 1);
        int lineHeight = next > start ? _log.GetPositionFromCharIndex(next).Y - _log.GetPositionFromCharIndex(start).Y : 0;
        if (lineHeight <= 0) lineHeight = _log.Font.Height;
        int page = Math.Max(1, _log.ClientSize.Height / lineHeight);
        int maxFirst = total - page;
        if (maxFirst <= 0) return default;
        return (PaperScrollThumb.Measure(this, Padding.Top, page, total, first), first, maxFirst);
    }

    /// <summary>按当前分页结果移动到给定首行。消息使用显示行增量，滚动不会移动日志选区。</summary>
    private void ScrollToLine(int target, int first, int maxFirst)
    {
        SendMessage(_log.Handle, LineScroll, 0, Math.Clamp(target, 0, maxFirst) - first);
        RefreshThumb(this, EventArgs.Empty);
    }

    /// <summary>正文与右缘共用系统滚轮行数，高精度滚轮的小步增量会累积至完整刻度。</summary>
    private void ScrollWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0) return;
        var (_, first, maxFirst) = MeasureScroll();
        if (maxFirst <= 0) return;
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        int wheelUnit = SystemInformation.MouseWheelScrollDelta;
        _wheelRemainder += e.Delta;
        int steps = Math.DivRem(_wheelRemainder, wheelUnit, out _wheelRemainder);
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines < 0) lines = (int)SendMessage(_log.Handle, GetLineCount, 0, 0) - maxFirst;
        if (steps != 0) ScrollToLine(first - steps * lines, first, maxFirst);
    }

    /// <summary>在同一纸面上绘制圆头细滑块，空白轨道直接露出日志底色。</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        PaperFieldSurface.Paint(e.Graphics, this, PaperFieldSurface.Paper);
        PaperScrollThumb.Draw(e.Graphics, MeasureScroll().Thumb, _dragging);
    }

    /// <summary>按住滑块开始拖动，点击滑块上下的空白则向相应方向翻一页。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var (thumb, first, maxFirst) = MeasureScroll();
        if (e.Button != MouseButtons.Left || thumb.IsEmpty || e.X < Width - Padding.Right) return;
        if (e.Y >= thumb.Top && e.Y < thumb.Bottom)
        {
            _dragOffset = e.Y - thumb.Top;
            Capture = true;
            _dragging = true;
            Invalidate();
            return;
        }
        int page = (int)SendMessage(_log.Handle, GetLineCount, 0, 0) - maxFirst;
        ScrollToLine(first + (e.Y < thumb.Top ? -page : page), first, maxFirst);
    }

    /// <summary>按滑块可移动距离映射首行，鼠标拖出纸面时仍可通过捕获到达头尾。</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || !Capture) return;
        var (thumb, first, maxFirst) = MeasureScroll();
        int travel = Height - Padding.Vertical - thumb.Height;
        if (thumb.IsEmpty || travel <= 0) return;
        int offset = Math.Clamp(e.Y - _dragOffset - Padding.Top, 0, travel);
        ScrollToLine((int)Math.Round((double)offset * maxFirst / travel), first, maxFirst);
    }

    /// <summary>结束手动拖动，释放鼠标并恢复滑块的普通颜色。</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        Capture = false;
    }

    /// <summary>鼠标松开或系统取消捕获时统一结束拖动，恢复滑块颜色。</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture) return;
        _dragging = false;
        Invalidate();
    }

    /// <summary>向日志自己的 RichEdit 窗口查询显示行或请求滚动。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
