/*
 * 文件职责：在真实 Windows 桌面上检查预览窗的焦点切换和圆角合成。
 * 测量方式：对自有窗体切换焦点、改变背板颜色，通过屏幕像素检查实际显示效果。
 * 使用方式：dotnet run --project tests/SideStage.App.FrameCheck；可附带一个路径输出验收截图，
 *   --horizontal 对比左右拖边和设置抽屉，--surface 对比上下拖边，--vertical 检查黑块和鼠标命中，
 *   --resize 测量布局耗时并检查右缘与纸面的相对位置。
 */
using System.Diagnostics;
using System.Runtime.InteropServices;
using SideStage.App;
using SideStage.Core.Sessions;
using SideStage.Windows.Platform.Windows;

namespace SideStage.App.FrameCheck;

internal static class Program
{
    /// <summary>显示测试背板、焦点目标和实际验收窗体，退出时统一释放。仅在交互式桌面运行。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        Rectangle initial = new(work.Left + (work.Width - 960) / 2, work.Top + (work.Height - 640) / 2, 960, 640);
        // 背板也覆盖最外一档拖动轨迹以外的采样点，确保读回的黑色确实来自测试窗。
        Rectangle backing = Rectangle.Inflate(initial, 240, 120);
        using Form back = new() { FormBorderStyle = FormBorderStyle.None, TopMost = true,
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = backing, BackColor = Color.Black };
        using Form other = new() { Text = "焦点切换测试", ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(backing.Left, backing.Top, 160, 45) };
        bool liveResize = args is ["--live-resize"] or ["--live-drawer-jump"];
        bool liveAudio = args is ["--live-audio"];
        var vault = liveResize || liveAudio ? new WindowsChildSessionCredentialStore() :
            new WindowsChildSessionCredentialStore($"SideStage.FrameCheck.{Guid.NewGuid():N}");
        string logDirectory = Path.Combine(Path.GetTempPath(), "SideStage.FrameCheck", Guid.NewGuid().ToString("N"));
        string? openedFolder = null;
        int folderOpenCount = 0;
        // 首次使用场景只改变内存中的开关，配合独立凭据项走正式首页的完整路径。
        var firstUse = args is ["--first-use", _] or ["--settings"] or ["--settings", _] ? new FirstUseSessions() : null;
        using ParchmentPreviewForm window = new(vault, logDirectory, folder => { openedFolder = folder; folderOpenCount++; },
            pollSessions: args is not ["--connected-resize"], sessionInspector: firstUse,
            enableChildSessions: firstUse is null ? null : firstUse.EnableAsync) { StartPosition = FormStartPosition.Manual, TopMost = true,
            Bounds = initial };
        back.Show();
        other.Show(back);
        window.Show(back);
        SetForegroundWindow(window.Handle);
        Settle();
        if (args is ["--first-use", var screenshot])
        {
            SettingsInteractionCheck.CheckFirstUse(window, screenshot);
            return 0;
        }
        if (liveAudio) return AudioReconnectCheck.Run(window);
        if (liveResize || args is ["--connected-resize"] or ["--drawer-jump"])
        {
            back.Bounds = work;
            back.BackColor = ConnectedResizePixels.Backdrop;
            back.Refresh();
            if (args is ["--drawer-jump"]) return DrawerExpansionCheck.Run(window);
            return ConnectedResizeCheck.Run(window, liveResize, args is ["--live-drawer-jump"]);
        }
        if (args is ["--settings"] or ["--settings", _])
        {
            try
            {
                int result = SettingsInteractionCheck.Run(window, args.ElementAtOrDefault(1));
                if (folderOpenCount != 2 || openedFolder != logDirectory ||
                    !File.ReadAllText(Directory.GetFiles(logDirectory, "*.log").Single()).Contains("测试日志"))
                    throw new InvalidOperationException("气泡链接与本次日志写入目录不符，或日志未写入。");
                return result;
            }
            finally { vault.Delete(); }
        }
        if (args is ["--surface"]) return SurfaceResizeCheck.Run(window);
        if (args is ["--horizontal"]) return SurfaceResizeCheck.Run(window, horizontal: true);
        if (args is ["--vertical"]) return VerticalResizeCheck.Run(window);
        if (args is ["--resize"]) return ResizeCheck.Run(window);
        bool passed = true;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            other.Activate();
            Settle();
            passed &= CheckRim(window, other, "失焦");
            window.Activate();
            Settle();
            passed &= CheckRim(window, window, "复焦");
        }

        window.WindowState = FormWindowState.Maximized;
        Settle(650);
        passed &= CheckRim(window, window, "最大化");
        window.WindowState = FormWindowState.Normal;
        window.Bounds = new Rectangle(initial.X - 12, initial.Y - 8, initial.Width + 24, initial.Height + 16);
        Settle(650);
        passed &= CheckRim(window, window, "还原并移动、缩放");

        // 同一个圆角分别叠在黑、白背板上，只有真正的半透明像素会跟随背板部分变亮。
        using Bitmap dark = Capture(window);
        back.BackColor = Color.White;
        back.Refresh();
        Settle();
        using Bitmap light = Capture(window);
        passed &= CheckCorners(dark, light, window.DeviceDpi);
        if (passed && args.Length == 1) light.Save(args[0], System.Drawing.Imaging.ImageFormat.Png);
        return passed ? 0 : 1;
    }

    /// <summary>向验收窗口提供尚未启用分身的首次使用状态，只在内存中处理启用请求。</summary>
    private sealed class FirstUseSessions : ISessionInspector
    {
        private bool _enabled;

        /// <summary>返回固定的已登录主桌面，保证截图和断言不受本机活动会话影响。</summary>
        public SessionSnapshot GetCurrentSession() =>
            new(new SessionId(1), SessionConnectionState.Active, false, DateTimeOffset.UtcNow) { IsLocked = false };

        /// <summary>首次配置尚未建立分身。</summary>
        public SessionSnapshot? TryGetChildSession() => null;

        /// <summary>提供用于首页回读的当前开关。</summary>
        public bool IsChildSessionsEnabled() => _enabled;

        /// <summary>只模拟系统接受启用请求。</summary>
        internal Task EnableAsync()
        {
            _enabled = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>在真实消息循环中等待重绘与桌面合成，防止误测窗口动画的中间帧。</summary>
    private static void Settle(int milliseconds = 200)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
        DwmFlush();
    }

    /// <summary>截取测试窗客户区在桌面上的合成结果，包含半透明曲线与背板混合后的颜色。</summary>
    private static Bitmap Capture(Form window)
    {
        Point border = window.PointToScreen(new Point(window.ClientSize.Width / 2, 4));
        nint occupant = WindowFromPoint(border);
        if (occupant != window.Handle)
        {
            GetWindowThreadProcessId(occupant, out int process);
            throw new InvalidOperationException($"采样点 {border} 被其他窗口遮挡（进程 {process}），无法测量实际窗框。");
        }
        Bitmap result = new(window.ClientSize.Width, window.ClientSize.Height);
        using Graphics graphics = Graphics.FromImage(result);
        graphics.CopyFromScreen(window.PointToScreen(Point.Empty), Point.Empty, result.Size);
        return result;
    }

    /// <summary>验证前台归属与四边中点处的实际铜色，能识别系统激活描边覆盖客户区的回归。</summary>
    private static bool CheckRim(Form window, Form expectedActive, string stage)
    {
        using Bitmap frame = Capture(window);
        int inset = (int)Math.Round(4 * window.DeviceDpi / 96f);
        int w = frame.Width, h = frame.Height;
        Color[] colors = [frame.GetPixel(w / 2, inset), frame.GetPixel(inset, h / 2),
            frame.GetPixel(w - 1 - inset, h / 2), frame.GetPixel(w / 2, h - 1 - inset)];
        bool ok = Form.ActiveForm == expectedActive &&
            colors.All(c => c.R is >= 95 and <= 180 && c.R - c.B >= 20);
        Console.WriteLine($"{stage} {ok} 焦点匹配={Form.ActiveForm == expectedActive} " +
            $"{string.Join(" ", colors.Select(c => $"{c.R:X2}{c.G:X2}{c.B:X2}"))}");
        return ok;
    }

    /// <summary>仅抽取四条理论圆弧边界上的像素，逐角统计黑白背板之间的中间亮度差。</summary>
    private static bool CheckCorners(Bitmap dark, Bitmap light, int dpi)
    {
        double radius = BronzeWindowFrame.CornerRadius * dpi / 96d;
        bool passed = true;
        for (int corner = 0; corner < 4; corner++)
        {
            int mixed = 0;
            for (int y = 0; y < radius; y++)
            {
                double dy = radius - (y + .5);
                int x = (int)Math.Floor(radius - Math.Sqrt(radius * radius - dy * dy));
                int px = (corner & 1) == 0 ? x : dark.Width - x - 1;
                int py = (corner & 2) == 0 ? y : dark.Height - y - 1;
                int delta = light.GetPixel(px, py).R - dark.GetPixel(px, py).R;
                if (delta is > 12 and < 240) mixed++;
            }
            Console.WriteLine($"圆角 {corner} 过渡像素 {mixed}/{radius:F0}");
            passed &= mixed >= radius / 3;
        }
        return passed;
    }

    /// <summary>等待桌面合成器提交上一轮窗口绘制。</summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>检查采样位置是否确由正在验收的窗体占据。</summary>
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    /// <summary>报告占用测试采样点的进程编号，便于区分窗口遮挡和错误的圆角裁切。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int process);

    /// <summary>请求把本进程的验收窗设为前台；系统拒绝时，焦点断言仍会明确报告未通过。</summary>
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
}
