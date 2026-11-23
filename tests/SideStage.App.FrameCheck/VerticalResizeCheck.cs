/*
 * 文件职责：在独立验收窗上按固定轨迹拉伸上下边，并检查放大后关闭按钮右侧的黑块。
 * 取样方法：按桌面合成节奏截取竖线，从黑色测试背板识别当时屏幕上的实际边界，
 *   将标题、纸面与同一位置的稳定帧比较，避免把整帧延迟误认为闪烁。
 */
using System.Runtime.InteropServices;

namespace SideStage.App.FrameCheck;

internal static class VerticalResizeCheck
{
    private const int EnterSizeMove = 0x0231, ExitSizeMove = 0x0232;

    /// <summary>先放大宽度检查右上角，再分别移动上下边，每段轨迹结束后恢复原尺寸。</summary>
    internal static int Run(Form window)
    {
        Rectangle start = window.Bounds;
        Button close = window.Controls.OfType<Button>().Single(button => button.AccessibleName == "关闭");
        int black = 0;
        for (int step = 0; step < 32; step++)
        {
            window.Width = start.Width + step % 8 * (18 + step / 8 * 8);
            Application.DoEvents();
            Marshal.ThrowExceptionForHR(DwmFlush());
            Point origin = close.PointToScreen(new Point(close.Width - 6, 3));
            using Bitmap patch = new(5, close.Height - 6);
            using Graphics g = Graphics.FromImage(patch);
            g.CopyFromScreen(origin, Point.Empty, patch.Size);
            if (Enumerable.Range(0, patch.Height).Any(y =>
                Enumerable.Range(0, patch.Width).Any(x => patch.GetPixel(x, y).ToArgb() == Color.Black.ToArgb())))
                black++;
        }
        window.Bounds = start;
        Application.DoEvents();
        Console.WriteLine($"横向放大后，关闭按钮右缘含纯黑像素 {black}/32 次");
        bool top = CheckEdge(window, true);
        bool bottom = CheckEdge(window, false);
        bool lifecycle = CheckRimLifetime(window);
        return black == 0 && top && bottom && lifecycle ? 0 : 1;
    }

    /// <summary>保持宽度及对侧边缘固定，交替回放慢速与快速拉伸轨迹。</summary>
    private static bool CheckEdge(Form window, bool moveTop)
    {
        Rectangle start = window.Bounds;
        int pad = BronzeWindowFrame.ContentInset(window.DeviceDpi / 96f, false);
        int title = window.Padding.Top;
        Rectangle screen = Screen.FromControl(window).Bounds;
        int margin = Math.Min(108, Math.Min(start.Top - screen.Top, screen.Bottom - start.Bottom));
        if (margin <= 48) throw new InvalidOperationException("验收窗口上下各需要超过 48 像素的屏幕空间。");
        Point origin = new(start.Left + start.Width / 4, start.Top - margin);
        int length = start.Height + 2 * margin;
        using CancellationTokenSource stop = new();
        using ManualResetEventSlim ready = new();
        Task<bool> reader = Task.Run(() =>
        {
            try { return Sample(origin, length, pad, title, moveTop, ready, stop.Token); }
            finally { ready.Set(); }
        });
        ready.Wait();
        try
        {
            // Bounds 用固定轨迹模拟鼠标；首尾消息使宿主经历与系统拖边相同的交互生命周期。
            SendMessage(window.Handle, EnterSizeMove, 0, 0);
            for (int step = 0; step < 96; step++)
            {
                int stride = step < 48 ? 4 : step < 72 ? 12 : 24;
                int halfPeriod = 48 / stride;
                int phase = (step + 1) % (2 * halfPeriod);
                int delta = stride * Math.Min(phase, 2 * halfPeriod - phase);
                window.Bounds = new(start.X, start.Y - (moveTop ? delta : 0), start.Width, start.Height + delta);
                Application.DoEvents();
            }
        }
        finally
        {
            SendMessage(window.Handle, ExitSizeMove, 0, 0);
            stop.Cancel();
        }
        return reader.GetAwaiter().GetResult();
    }

    /// <summary>分别统计标题带与纸面的高反差闪色，以及紧靠活动边缘的铜条错位。</summary>
    private static bool Sample(Point origin, int length, int pad, int title, bool moveTop,
        ManualResetEventSlim ready, CancellationToken stop)
    {
        using Bitmap line = new(1, length);
        using Graphics g = Graphics.FromImage(line);
        int frames = 0, caption = 0, paperTop = 0, paperBottom = 0, rim = 0;
        HashSet<int> positions = [];
        int rimOffset = (int)Math.Round(pad * 4d / 14);
        do
        {
            Marshal.ThrowExceptionForHR(DwmFlush());
            g.CopyFromScreen(origin, Point.Empty, line.Size);
            int first = 0, last = length - 1;
            while (first < length && line.GetPixel(0, first).R < 25) first++;
            while (last >= 0 && line.GetPixel(0, last).R < 25) last--;
            if (first == 0 || last == length - 1 || last - first < title + pad + 16)
                throw new InvalidOperationException($"竖线 {origin} 长 {length}，首末可见像素 {first}/{last}，没有同时覆盖窗口和两端背板。");
            frames++;
            positions.Add(moveTop ? first : last);
            if (line.GetPixel(0, first + title / 2).ToArgb() != Color.FromArgb(73, 55, 42).ToArgb()) caption++;
            if (line.GetPixel(0, first + title + 2).R < 230) paperTop++;
            if (line.GetPixel(0, last - pad - 2).R < 230) paperBottom++;
            if (line.GetPixel(0, moveTop ? first + rimOffset : last - rimOffset).ToArgb()
                != Color.FromArgb(145, 125, 100).ToArgb())
                rim++;
            ready.Set();
        } while (!stop.IsCancellationRequested);
        Console.WriteLine($"{(moveTop ? "上" : "下")}边 {positions.Count} 个位置，{frames} 次采样，" +
            $"标题异常 {caption}，纸面上缘异常 {paperTop}，纸面下缘异常 {paperBottom}，铜条异常 {rim}");
        return positions.Count >= 3 && caption == 0 && paperTop == 0 && paperBottom == 0 && rim == 0;
    }

    /// <summary>
    /// 在铜条显示期间检查拖边命中、最小化和隐藏。在真实设置抽屉放置一块高对比色，
    /// 从显示器读回内容区最后几个像素，检查打开抽屉、继续拖动和松手时的显示范围。
    /// </summary>
    private static bool CheckRimLifetime(Form window)
    {
        Rectangle start = window.Bounds;
        Panel workspace = window.Controls.OfType<Panel>().Single(panel => panel.Controls.OfType<PaperSurface>().Any());
        PaperSurface paper = workspace.Controls.OfType<PaperSurface>().Single();
        Panel drawer = workspace.Controls.OfType<Panel>().Single(panel => panel.GetType() == typeof(Panel));
        Button settings = paper.Controls.OfType<TableLayoutPanel>().Single()
            .Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(button => button.Text == "高级设置");
        using Panel marker = new() { BackColor = Color.Lime, Dock = DockStyle.Fill };
        drawer.Controls.Add(marker);
        marker.BringToFront();
        SendMessage(window.Handle, EnterSizeMove, 0, 0);
        try
        {
            window.Height += 12;
            Point dragPoint = new(window.Left + window.Width / 2, window.Bottom - 3);
            nint packed = (ushort)dragPoint.X | ((nint)(ushort)dragPoint.Y << 16);
            bool canDrag = WindowFromPoint(dragPoint) == window.Handle &&
                SendMessage(window.Handle, 0x0084, 0, packed) == 15;
            Point paperPoint = workspace.PointToScreen(new Point(workspace.Width / 2, workspace.Height - 2));
            window.Hide();
            bool hidden = WaitForPixels(Color.Black, paperPoint, dragPoint);
            window.Show();
            window.Height += 4;
            window.WindowState = FormWindowState.Minimized;
            bool minimized = WaitForPixels(Color.Black, paperPoint, dragPoint);
            window.WindowState = FormWindowState.Normal;
            window.Height += 4;
            SendMessage(window.Handle, ExitSizeMove, 0, 0);
            Application.DoEvents();
            Point buttonPoint = settings.PointToScreen(new Point(settings.Width / 2, settings.Height / 2));
            bool clickable = window.Opacity == 1 && WindowFromPoint(buttonPoint) == settings.Handle;
            SendMessage(window.Handle, EnterSizeMove, 0, 0);
            window.Height -= 4;
            settings.PerformClick();
            bool exposed = MarkerIsVisible();
            window.Height += 12;
            exposed &= MarkerIsVisible();
            SendMessage(window.Handle, ExitSizeMove, 0, 0);
            bool released = MarkerIsVisible();
            Console.WriteLine($"底边命中宿主 {canDrag}，隐藏无残留 {hidden}，最小化无残留 {minimized}，" +
                $"首页松手后按钮可点击 {clickable}，抽屉下缘完整 {exposed}，松手后完整 {released}");
            return canDrag && hidden && minimized && clickable && exposed && released;
        }
        finally
        {
            SendMessage(window.Handle, ExitSizeMove, 0, 0);
            if (drawer.Visible) settings.PerformClick();
            window.Bounds = start;
        }

        // 从实际屏幕采样，窗口命中测试本身无法识别一张鼠标穿透但仍遮挡画面的图。
        bool MarkerIsVisible() => WaitForPixels(marker.BackColor, marker.PointToScreen(new Point(marker.Width / 2, marker.Height - 2)));
    }

    /// <summary>等待系统完成隐藏或最小化动画，检查同一轮采样的所有指定位置是否都呈现预期颜色。</summary>
    private static bool WaitForPixels(Color expected, params Point[] points)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using Bitmap pixel = new(1, 1);
        using Graphics g = Graphics.FromImage(pixel);
        do
        {
            Application.DoEvents();
            Marshal.ThrowExceptionForHR(DwmFlush());
            if (points.All(point => Sample(point) == expected.ToArgb())) return true;
        } while (elapsed.ElapsedMilliseconds < 1200);
        Console.WriteLine($"等待颜色 {expected.Name} 超时，屏幕实测 " +
            string.Join("、", points.Select(point => $"{point} #{Sample(point):X8}")));
        return false;

        // 复用单像素位图，避免轮询动画时不断分配 GDI 资源。
        int Sample(Point point)
        {
            g.CopyFromScreen(point, Point.Empty, pixel.Size);
            return pixel.GetPixel(0, 0).ToArgb();
        }
    }

    /// <summary>等待桌面完成一次合成，再从真实显示画面读取采样点。</summary>
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();

    /// <summary>检查显示在最上面的底边是否把鼠标交给了窗口本身。</summary>
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    /// <summary>向宿主查询屏幕采样点的原生命中测试结果。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
