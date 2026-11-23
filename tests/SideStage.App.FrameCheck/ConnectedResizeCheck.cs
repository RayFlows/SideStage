/*
 * 文件名称：ConnectedResizeCheck.cs
 * 文件职责：通过窗口自己的系统尺寸命令回放四边和四角的拖动，记录 ActiveX 在松手前后的大小变化。
 * 测试边界：默认模拟已核验状态。显式使用 --live-resize 时经正常连接按钮进入本机子会话，结束后断开预览。
 */
using System.Runtime.InteropServices;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App.FrameCheck;

internal static class ConnectedResizeCheck
{
    /// <summary>切换到预览工作区并模拟打开侧栏，验证各方向的实时控件尺寸、提交结果及取消。</summary>
    internal static int Run(ParchmentPreviewForm window, bool live = false, bool drawerJump = false)
    {
        var panel = Flatten(window).OfType<ChildSessionPreviewPanel>().Single();
        Point cursor = Cursor.Position;
        try
        {
            if (live)
            {
                Flatten(window).OfType<Button>().Single(button => button.Visible && button.Text == "连接").PerformClick();
                long deadline = Environment.TickCount64 + 18000;
                int stableTicks = 0;
                while (stableTicks < 80 && Environment.TickCount64 < deadline)
                {
                    Application.DoEvents();
                    stableTicks = panel.PreviewControl.LoginCompleted && panel.PreviewControl.Enabled &&
                        panel.PreviewControl.VerifiedConnectionTarget != null ? stableTicks + 1 : 0;
                    Thread.Sleep(15);
                }
                if (stableTicks < 80)
                    throw new InvalidOperationException("登录或核验尚未完成，无法验收实时桌面。");
                Console.WriteLine("已通过 SideStage 的正常连接入口核验本机子会话。");
            }
            else
            {
                panel.SetPreviewState(RdpPreviewConnectionState.Connected, true, true, true);
                window.SwitchMainView(RdpPreviewConnectionState.Connected);
            }
            if (drawerJump) return DrawerExpansionCheck.Run(window);
            window.Size += new Size(96, 96);
            bool passed = true;
            for (int edge = 1; edge <= 8; edge++)
            {
                if (edge == 5) SendMessage(window.Handle, 0x0112, 0x1200, 0);
                passed &= Drag(window, panel, edge, false, 1, live);
                passed &= Drag(window, panel, edge, false, -1, live);
            }
            passed &= Drag(window, panel, 8, true, 1, live);
            return passed ? 0 : 1;
        }
        finally
        {
            Cursor.Position = cursor;
            if (live && panel.PreviewControl.GetConnectionState() != RdpPreviewConnectionState.Disconnected)
                panel.PreviewControl.DisconnectPreview();
        }
    }

    /// <summary>深度遍历当前窗口的子控件。</summary>
    private static IEnumerable<Control> Flatten(Control control) =>
        control.Controls.Cast<Control>().SelectMany(Flatten).Prepend(control);

    /// <summary>将鼠标移动和释放交给系统拖边循环，按实际起始和终止边界判断是否完成预期拖动。</summary>
    private static bool Drag(Form window, ChildSessionPreviewPanel preview, int edge, bool cancel, int direction, bool live)
    {
        Rectangle start = window.Bounds;
        bool left = edge is 1 or 4 or 7, right = edge is 2 or 5 or 8;
        bool top = edge is 3 or 4 or 5, bottom = edge is 6 or 7 or 8;
        int inset = window.LogicalToDeviceUnits((left || right) && (top || bottom) ? 10 : 4);
        Point origin = new(left ? start.Left + inset : right ? start.Right - inset : start.Left + start.Width / 2,
            top ? start.Top + inset : bottom ? start.Bottom - inset : start.Top + start.Height / 2);
        for (int i = 0; i < 3; i++) { Application.DoEvents(); Marshal.ThrowExceptionForHR(DwmFlush()); }
        Rectangle viewport = preview.RectangleToScreen(preview.ClientRectangle);
        Point inner = new(viewport.Left + viewport.Width / 4, viewport.Top + viewport.Height / 2);
        int[] offsets = [0, 16, 80, -32, 64, 0, -16, 48];
        Rectangle[] targets = offsets.Select(distance => Rectangle.FromLTRB(
            start.Left - (left ? direction * distance : 0), start.Top - (top ? direction * distance : 0),
            start.Right + (right ? direction * distance : 0), start.Bottom + (bottom ? direction * distance : 0))).ToArray();
        using var pixels = live ? new ConnectedResizePixels(window, targets) : null;
        Rectangle expected = cancel ? start : targets[^1];
        using CancellationTokenSource stop = new();
        using ManualResetEventSlim ready = new(!live);
        Task? sampling = pixels == null ? null : Task.Run(() => pixels.Observe(ready, stop.Token));
        ready.Wait();
        int step = 0;
        Point pointer = origin;
        HashSet<Size> actualSizes = [];
        using System.Windows.Forms.Timer timer = new() { Interval = 16 };
        timer.Tick += (_, _) =>
        {
            nint target = GetCapture();
            if (target == 0) target = window.Handle;
            actualSizes.Add(window.Size);
            if (++step < offsets.Length)
            {
                int delta = offsets[step] * direction;
                pointer = new Point(origin.X + delta * (left ? -1 : right ? 1 : 0),
                    origin.Y + delta * (top ? -1 : bottom ? 1 : 0));
                Point local = window.PointToClient(pointer);
                SendMessage(target, 0x0200, 1, (ushort)local.X | ((nint)(ushort)local.Y << 16));
            }
            else
            {
                timer.Stop();
                Point local = window.PointToClient(pointer);
                PostMessage(target, cancel ? 0x0100 : 0x0202, cancel ? 27 : 0,
                    cancel ? 0 : (ushort)local.X | ((nint)(ushort)local.Y << 16));
            }
        };
        try
        {
            Application.DoEvents();
            nint coordinates = (ushort)origin.X | ((nint)(ushort)origin.Y << 16);
            if (WindowFromPoint(origin) != window.Handle || SendMessage(window.Handle, 0x0084, 0, coordinates) != 9 + edge)
                throw new InvalidOperationException($"方向 {edge} 的抓手未命中测试窗口。");
            timer.Start();
            PostMessage(window.Handle, 0x00a1, 9 + edge, coordinates);
            long deadline = Environment.TickCount64 + 4000;
            do { Application.DoEvents(); Thread.Sleep(1); } while (timer.Enabled && Environment.TickCount64 < deadline);
            Application.DoEvents();
        }
        finally
        {
            timer.Stop();
            stop.Cancel();
            sampling?.GetAwaiter().GetResult();
        }
        // 第一帧就检查松手后的新增面积，并持续到布局和远程绘制均有机会完成。
        pixels?.CheckReleased(expected);
        nint hit = WindowFromPoint(inner);
        nint ax = preview.PreviewControl.Handle;
        bool inputReady = (hit == ax || IsChild(ax, hit)) && preview.TryMapToRemotePoint(preview.PointToClient(inner)) != null;
        bool passed = step == offsets.Length && actualSizes.Count >= 3 && window.Bounds == expected &&
            inputReady && (pixels?.Passed ?? true);
        Console.WriteLine($"方向 {edge} 变化 {direction} 取消 {cancel}，松手前 {actualSizes.Count} 档，" +
            $"终点 {window.Bounds == expected}，输入 {inputReady}，{pixels} 通过 {passed}");
        window.Bounds = start;
        window.Refresh();
        return passed;
    }

    /// <summary>等待新的桌面合成帧，避免重复扫描同一批像素。</summary>
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    /// <summary>调用被测窗口的系统菜单命令，覆盖与真实拖边相同的 Windows 尺寸循环。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int msg, nint w, nint l);
    /// <summary>将非客户区按下、释放或取消送至验收窗口及它实际持有的系统鼠标捕获窗口。</summary>
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, int msg, nint w, nint l);
    /// <summary>获取当前界面线程在系统拖边循环中的捕获窗口。</summary>
    [DllImport("user32.dll")] private static extern nint GetCapture();
    /// <summary>检查松手后画面内的鼠标命中窗口。</summary>
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    /// <summary>原生远程画面可有多层子窗口，命中其中任一层都属于预览。</summary>
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
}
