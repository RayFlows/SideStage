/*
 * 文件名称：SurfaceResizeCheck.cs
 * 文件职责：把拖边时的整窗与同一可见尺寸的静止画面逐帧对照，覆盖标题、按钮、
 *   中央表格和四角。单独采样底边无法识别这些区域的瞬时错位或擦除。
 * 测量边界：只操纵测试程序自己的未连接窗口；背板与参考帧来自同一桌面和同一构建。
 */
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SideStage.App.FrameCheck;

internal static class SurfaceResizeCheck
{
    private const int Stride = 12, Levels = 9, Margin = 112;

    /// <summary>在真实宿主上回放同一轴的两个方向。横向还检查打开抽屉后，右缘和被它遮挡的正文。</summary>
    internal static int Run(Form window, bool horizontal = false)
    {
        bool passed = Check(window, horizontal, false);
        passed &= Check(window, horizontal, true);
        if (horizontal)
        {
            SendMessage(window.Handle, 0x0112, 0x1200, 0);
            Settle();
            try { passed &= Check(window, true, false); }
            finally { SendMessage(window.Handle, 0x0112, 0x1200, 0); }
        }
        return passed ? 0 : 1;
    }

    /// <summary>先等待每个目标尺寸的绘制完成并保存参考画面，再让独立采样线程观察连续拖动。</summary>
    private static bool Check(Form window, bool horizontal, bool leading)
    {
        Rectangle start = window.Bounds;
        Rectangle capture = Rectangle.Inflate(start, horizontal ? Margin : 0, horizontal ? 0 : Margin);
        if (!Screen.FromControl(window).Bounds.Contains(capture))
            throw new InvalidOperationException("被测边两侧各需要 112 像素的可见背板。");
        int[][] references = new int[Levels][];
        using Bitmap bitmap = new(capture.Width, capture.Height, PixelFormat.Format32bppRgb);
        for (int i = 0; i < Levels; i++)
        {
            window.Bounds = Target(i);
            Settle();
            references[i] = new int[bitmap.Width * bitmap.Height];
            Capture(bitmap, capture.Location, references[i]);
        }
        // 从左边或上边测试时先把窗口放到最宽或最高，验证第一次操作就是收窄的情况。
        window.Bounds = Target(leading ? Levels - 1 : 0);
        Settle();
        using CancellationTokenSource stop = new();
        using ManualResetEventSlim ready = new();
        Task<bool> reader = Task.Run(() =>
        {
            try { return Sample(bitmap, capture.Location, start.Size, references, horizontal, leading, ready, stop.Token); }
            finally { ready.Set(); }
        });
        ready.Wait();
        try
        {
            SendMessage(window.Handle, 0x0231, 0, 0);
            for (int i = 1; i <= 96; i++)
            {
                int phase = i % (2 * (Levels - 1));
                int level = Math.Min(phase, 2 * (Levels - 1) - phase);
                window.Bounds = Target(leading ? Levels - 1 - level : level);
                Application.DoEvents();
            }
        }
        finally
        {
            SendMessage(window.Handle, 0x0232, 0, 0);
            stop.Cancel();
        }
        try { return reader.GetAwaiter().GetResult(); }
        finally { window.Bounds = start; Settle(); }

        // 固定对侧边缘，屏幕上可用同一套坐标比较所有像素。
        Rectangle Target(int level)
        {
            int dx = horizontal ? level * Stride : 0, dy = horizontal ? 0 : level * Stride;
            return new(start.X - (leading ? dx : 0), start.Y - (leading ? dy : 0), start.Width + dx, start.Height + dy);
        }
    }

    /// <summary>按屏幕显示的实际边界选择参考帧。每三像素取一点，忽略轻微纸纹变化，单独统计四个区域。</summary>
    private static bool Sample(Bitmap bitmap, Point origin, Size initial, int[][] references, bool horizontal, bool leading,
        ManualResetEventSlim ready, CancellationToken stop)
    {
        int width = bitmap.Width;
        int extent = horizontal ? bitmap.Width : bitmap.Height;
        int initialLength = horizontal ? initial.Width : initial.Height;
        int[] frame = new int[width * bitmap.Height], misses = new int[4], max = new int[4];
        int frames = 0, unaligned = 0;
        HashSet<int> positions = [];
        do
        {
            Marshal.ThrowExceptionForHR(DwmFlush());
            Capture(bitmap, origin, frame);
            ready.Set();
            int first = 0, last = extent - 1;
            while (first < last && Dark(first)) first++;
            while (last > first && Dark(last)) last--;
            int delta = leading ? Margin - first : last - (Margin + initialLength - 1);
            if (delta < 0 || delta % Stride != 0 || delta / Stride >= Levels)
            {
                if (unaligned == 0)
                {
                    Point point = new(origin.X + width / 4, origin.Y + Margin);
                    GetWindowThreadProcessId(WindowFromPoint(point), out int pid);
                    Console.WriteLine($"边界 {first}/{last}，预期初始 {Margin}/{Margin + initialLength - 1}，" +
                        $"顶部颜色 {frame[Margin * width + width / 4]:X8}，采样点所在进程 {pid}，验收进程 {Environment.ProcessId}");
                }
                unaligned++;
                continue;
            }
            positions.Add(delta);
            frames++;
            int[] reference = references[delta / Stride];
            Span<int> differences = new int[4];
            int left = horizontal ? first : 0, right = horizontal ? last : width - 1;
            int top = horizontal ? 0 : first, bottom = horizontal ? bitmap.Height - 1 : last;
            for (int y = Math.Max(0, top - 4); y < Math.Min(bitmap.Height, bottom + 5); y += 3)
            for (int x = Math.Max(0, left - 4); x < Math.Min(width, right + 5); x += 3)
            {
                int actual = frame[y * width + x], expected = reference[y * width + x];
                if (Math.Abs((actual >> 16 & 255) - (expected >> 16 & 255)) < 32 &&
                    Math.Abs((actual >> 8 & 255) - (expected >> 8 & 255)) < 32 &&
                    Math.Abs((actual & 255) - (expected & 255)) < 32) continue;
                int area = x - left < 32 || right - x < 32 ? 0 : y - top < 48 ? 1 : bottom - y < 64 ? 2 : 3;
                differences[area]++;
            }
            for (int i = 0; i < differences.Length; i++)
            {
                if (differences[i] >= 16) misses[i]++;
                max[i] = Math.Max(max[i], differences[i]);
            }
        } while (!stop.IsCancellationRequested);
        string edge = horizontal ? (leading ? "左" : "右") : (leading ? "上" : "下");
        Console.WriteLine($"{edge}边 {frames} 帧，{positions.Count} 档，未匹配边界 {unaligned} 帧。" +
            $"两侧及四角/标题/底部/正文偏离帧数 {string.Join('/', misses)}，最大偏离点数 {string.Join('/', max)}");
        return positions.Count >= 4 && unaligned == 0 && misses.All(count => count == 0);

        // 黑色背板用于区分真实边界与消息队列里已经请求、尚未呈现的位置。
        bool Dark(int offset)
        {
            int index = horizontal ? bitmap.Height / 4 * width + offset : offset * width + width / 4;
            return (frame[index] >> 16 & 255) < 25;
        }
    }

    /// <summary>等待上一轮位置变更的子控件绘制和桌面合成结束，得到静止参考帧。</summary>
    private static void Settle()
    {
        for (int i = 0; i < 6; i++)
        {
            Application.DoEvents();
            Marshal.ThrowExceptionForHR(DwmFlush());
        }
    }

    /// <summary>按固定的 32 位像素格式读取测试窗及黑色背板，复用缓冲区以免采样本身造成停顿。</summary>
    private static void Capture(Bitmap bitmap, Point origin, int[] pixels)
    {
        using (Graphics g = Graphics.FromImage(bitmap)) g.CopyFromScreen(origin, Point.Empty, bitmap.Size);
        BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>等待桌面合成下一帧。</summary>
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    /// <summary>检查进程外窗口是否覆盖屏幕采样点。</summary>
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    /// <summary>报告被采样窗口的进程编号。</summary>
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    /// <summary>让被测窗进入或结束系统拖边生命周期。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
