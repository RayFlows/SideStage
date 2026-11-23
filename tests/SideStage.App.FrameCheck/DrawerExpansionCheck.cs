/*
 * 文件名称：DrawerExpansionCheck.cs
 * 文件职责：在高级设置展开时，从最小窗口一次拉到接近全屏，检查新增区域与松手后画面的实际合成像素。
 * 测试边界：通过宿主自己的尺寸消息复现大跨度拖动，屏幕采样仅用于本次比对，结束后归还窗口及侧栏状态。
 */
using System.Runtime.InteropServices;

namespace SideStage.App.FrameCheck;

internal static class DrawerExpansionCheck
{
    /// <summary>打开设置，分别验证只增宽、只增高和右下角同时放大，三种动作都从最小尺寸开始。</summary>
    internal static int Run(Form window)
    {
        Rectangle saved = window.Bounds;
        Rectangle work = Screen.FromControl(window).WorkingArea;
        Rectangle small = new(work.Left + 24, work.Top + 24, window.MinimumSize.Width, window.MinimumSize.Height);
        Size large = new(Math.Min(1920, work.Width - 48), Math.Min(1080, work.Height - 48));
        if (large.Width - small.Width < 240 || large.Height - small.Height < 120)
            throw new InvalidOperationException("当前屏幕放不下验收所需的大、小两档窗口。");
        window.Bounds = small;
        SendMessage(window.Handle, 0x0112, 0x1200, 0);
        try
        {
            bool passed = true;
            foreach (int edge in new[] { 2, 6, 8 })
            {
                Size size = new(edge == 6 ? small.Width : large.Width, edge == 2 ? small.Height : large.Height);
                passed &= CheckJump(window, small, new Rectangle(small.Location, size), edge);
            }
            return passed ? 0 : 1;
        }
        finally
        {
            SendMessage(window.Handle, 0x0112, 0x1200, 0);
            window.Bounds = saved;
        }
    }

    /// <summary>每次只发送一个大尺寸建议，持续采样首次显示、鼠标停住和释放阶段；重复同一轨迹捕捉偶发帧。</summary>
    private static bool CheckJump(Form window, Rectangle small, Rectangle large, int edge)
    {
        using var pixels = new ConnectedResizePixels(window, [small, large], checkDrawer: true);
        for (int i = 0; i < 6; i++)
        {
            // 缩回小窗只是准备下一次起点，采样从小窗画稳之后开始。
            window.Bounds = small;
            window.Refresh();
            Settle();
            using CancellationTokenSource stop = new();
            using ManualResetEventSlim ready = new();
            Task reader = Task.Run(() => pixels.Observe(ready, stop.Token));
            ready.Wait();
            try
            {
                if (i < 3)
                {
                    SendMessage(window.Handle, 0x0231, 0, 0);
                    try
                    {
                        NativeRect rect = new(large);
                        SendSizing(window.Handle, 0x0214, edge, ref rect);
                        // 系统会在 WM_SIZING 返回后应用建议矩形。
                        if (window.Bounds != large) window.Bounds = large;
                        Settle();
                    }
                    finally { SendMessage(window.Handle, 0x0232, 0, 0); }
                }
                else DragOnce(window, small, large, edge, hold: i == 5);
                Settle();
            }
            finally { stop.Cancel(); reader.GetAwaiter().GetResult(); }
        }
        pixels.CheckReleased(large);
        Console.WriteLine($"设置展开，方向 {edge}，{small.Size} 一步到 {large.Size}，{pixels}，通过 {pixels.Passed}");
        window.Bounds = small;
        return pixels.Passed;
    }

    /// <summary>从真实铜边的非客户区命中入口按下，只传一条移动消息到远端坐标，再立即释放或等到空闲恢复后释放。</summary>
    private static void DragOnce(Form window, Rectangle small, Rectangle large, int edge, bool hold)
    {
        int inset = window.LogicalToDeviceUnits(edge == 8 ? 10 : 4);
        Point start = new(edge == 6 ? small.Width / 2 : small.Width - inset,
            edge == 2 ? small.Height / 2 : small.Height - inset);
        Point end = start + (large.Size - small.Size);
        nint down = Pack(window.PointToScreen(start));
        if (SendMessage(window.Handle, 0x0084, 0, down) != 9 + edge)
            throw new InvalidOperationException("测试起点没有命中窗口抓手。");
        SendMessage(window.Handle, 0x00a1, 9 + edge, down);
        try
        {
            SendMessage(window.Handle, 0x0200, 1, Pack(end));
            if (window.Bounds != large) throw new InvalidOperationException("单次移动后窗口没有跟随到目标尺寸。");
            if (hold)
            {
                long deadline = Environment.TickCount64 + 180;
                do { Settle(); } while (Environment.TickCount64 < deadline);
            }
        }
        finally { SendMessage(window.Handle, 0x0202, 0, Pack(end)); }
    }

    /// <summary>按 Win32 鼠标消息约定打包两个有符号坐标。</summary>
    private static nint Pack(Point point) => (ushort)point.X | ((nint)(ushort)point.Y << 16);

    /// <summary>让绘制消息与桌面合成各推进几帧，独立采样线程继续检查每次尺寸跳变。</summary>
    private static void Settle()
    {
        for (int i = 0; i < 4; i++) { Application.DoEvents(); Marshal.ThrowExceptionForHR(DwmFlush()); }
    }

    /// <summary>对应系统尺寸消息使用的屏幕矩形。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(Rectangle bounds)
    {
        internal int Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom;
    }

    /// <summary>向测试窗口发送菜单命令以及尺寸循环的开始、结束消息。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint msg, nint w, nint l);
    /// <summary>传入可写的设备像素矩形，与 WM_SIZING 原生调用约定一致。</summary>
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendSizing(nint hwnd, uint msg, nint w, ref NativeRect rect);
    /// <summary>等待桌面合成。</summary>
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
