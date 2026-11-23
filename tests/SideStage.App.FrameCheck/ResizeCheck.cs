/*
 * 文件职责：在交互桌面反复拉伸实际的预览宿主，分别测量改变几何、主框绘制与子控件绘制。
 * 测量口径：每步让消息队列处理完毕；耗时是 UI 线程处理时间，不能换算为显示器帧率。
 * 操作范围：只改变本进程未连接窗口的尺寸，不登录子会话，不移动用户的鼠标。
 */
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SideStage.App.FrameCheck;

internal static class ResizeCheck
{
    /// <summary>沿固定的右下、左上往返轨迹预热后采样，让缩小、放大、边缘移动均被覆盖。</summary>
    internal static int Run(Form window)
    {
        Rectangle initial = window.Bounds;
        List<PaintProbe> probes = [];
        Observe(window, probes);
        try
        {
            List<double> totals = [], geometry = [], framePaint = [], childPaint = [];
            for (int pass = 0; pass < 5; pass++)
            {
                for (int step = 1; step <= 48; step++)
                {
                    int offset = step <= 24 ? step : 48 - step;
                    int dx = offset * 4, dy = offset * 2;
                    bool moveOrigin = (pass & 1) != 0;
                    Rectangle target = new(initial.X - (moveOrigin ? dx : 0), initial.Y - (moveOrigin ? dy : 0),
                        initial.Width + dx, initial.Height + dy);
                    foreach (PaintProbe probe in probes) probe.Ticks = 0;
                    long start = Stopwatch.GetTimestamp();
                    window.Bounds = target;
                    long afterBounds = Stopwatch.GetTimestamp();
                    Application.DoEvents();
                    if (window.Bounds != target) throw new InvalidOperationException("测试窗口无法到达预定尺寸。");
                    if (pass == 0) continue;
                    totals.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    geometry.Add(Stopwatch.GetElapsedTime(start, afterBounds).TotalMilliseconds);
                    framePaint.Add(probes[0].Ticks * 1000d / Stopwatch.Frequency);
                    childPaint.Add(probes.Skip(1).Sum(probe => probe.Ticks) * 1000d / Stopwatch.Frequency);
                }
            }
            Print("每步总耗时", totals);
            Print("设置位置和大小", geometry);
            Print("主窗口 WM_PAINT", framePaint);
            Print("子控件 WM_PAINT（含嵌套消息）", childPaint);
            return CheckMovingEdge(window) ? 0 : 1;
        }
        finally { foreach (PaintProbe probe in probes) probe.ReleaseHandle(); }
    }

    /// <summary>
    /// 在另一线程按桌面合成节奏截取一行像素，检查向右拖边期间铜条是否贴着纸面。
    /// 先从黑色背板辨认屏幕上真正显示的边界，再相对它采样，以免把整帧延迟误报为错位。
    /// </summary>
    private static bool CheckMovingEdge(Form window)
    {
        Rectangle start = window.Bounds;
        int dpi = window.DeviceDpi;
        int inset = BronzeWindowFrame.ContentInset(dpi / 96f, false);
        int rimSample = (int)Math.Round(4 * dpi / 96d);
        Point origin = new(start.Right - inset - 20, start.Top + start.Height / 2);
        int width = start.Right + 60 - origin.X;
        using CancellationTokenSource stop = new();
        using ManualResetEventSlim ready = new();
        Task<(int ValidPositions, int Count, int Misses)> sampler = Task.Run(() =>
        {
            ready.Set();
            using Bitmap strip = new(width, 1);
            using Graphics graphics = Graphics.FromImage(strip);
            HashSet<int> positions = [];
            int count = 0, misses = 0;
            while (!stop.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(DwmFlush());
                graphics.CopyFromScreen(origin, Point.Empty, strip.Size);
                int edge = width - 1;
                while (edge >= 0 && strip.GetPixel(edge, 0).R < 25) edge--;
                count++;
                if (edge < inset + 4 || edge == width - 1) { misses++; continue; }
                positions.Add(edge);
                Color rim = strip.GetPixel(edge - rimSample, 0);
                Color paper = strip.GetPixel(edge - inset - 4, 0);
                if (rim.ToArgb() != Color.FromArgb(145, 125, 100).ToArgb() ||
                    paper.R < 236 || paper.G < 230 || paper.B < 216)
                    misses++;
            }
            return (positions.Count, count, misses);
        }, stop.Token);
        try
        {
            ready.Wait();
            SendMessage(window.Handle, 0x0231, 0, 0);
            for (int step = 1; step <= 96; step++)
            {
                int phase = step % 24;
                window.Width = start.Width + 4 * Math.Min(phase, 24 - phase);
                Application.DoEvents();
            }
        }
        finally
        {
            SendMessage(window.Handle, 0x0232, 0, 0);
            stop.Cancel();
        }
        var (positions, count, misses) = sampler.GetAwaiter().GetResult();
        Console.WriteLine($"连续合成采样 {count} 帧，识别到 {positions} 个边界位置，铜边或纸面偏离 {misses} 帧");
        return positions >= 3 && misses == 0;
    }

    /// <summary>等待桌面合成器的刷新节奏，让像素采样独立于主窗口的消息处理。</summary>
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();

    /// <summary>回放系统开始拖边和松手两条消息，激活与人工横向拉伸相同的呈现路径。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    /// <summary>只挂接已经存在的窗口句柄，避免为了测量而提前创建设置面板或 ActiveX。</summary>
    private static void Observe(Control control, List<PaintProbe> probes)
    {
        if (control.IsHandleCreated) probes.Add(new PaintProbe(control.Handle));
        foreach (Control child in control.Controls) Observe(child, probes);
    }

    /// <summary>报告中位数、慢端分位数及超过 60 Hz 单帧时长的步数，保留长尾信息。</summary>
    private static void Print(string label, List<double> samples)
    {
        samples.Sort();
        Console.WriteLine($"{label}  中位 {samples[samples.Count / 2]:F2} ms  " +
            $"P95 {samples[(int)Math.Ceiling(samples.Count * .95) - 1]:F2} ms  " +
            $">16.7 ms {samples.Count(ms => ms > 1000d / 60)}/{samples.Count}");
    }

    /// <summary>在真实的原生绘制消息两端取时间戳，其他消息原样传回 WinForms。</summary>
    private sealed class PaintProbe : NativeWindow
    {
        internal long Ticks;

        /// <summary>挂接指定的宿主或子控件句柄，生命周期限定在单次测量内。</summary>
        internal PaintProbe(nint handle) => AssignHandle(handle);

        /// <summary>累计本次尺寸变化涉及的绘制时间，包含整个控件实际执行的绘制。</summary>
        protected override void WndProc(ref Message message)
        {
            if (message.Msg != 0x000F) { base.WndProc(ref message); return; }
            long start = Stopwatch.GetTimestamp();
            base.WndProc(ref message);
            Ticks += Stopwatch.GetTimestamp() - start;
        }
    }
}
