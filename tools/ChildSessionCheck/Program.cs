/*
 * 本机子会话验收。默认打印调用方式，显式传入 --run 才建立 RDP 连接。
 * 已有子会话存在时退出，避免接管用户工作。只清理本次创建并核验的测试进程，
 * 窗口关闭只断开预览。命令行输出可用于区别 Windows 环境限制和库内错误。
 */
using SideStage.Core.Platform.Windows;
using SideStage.Core.Processes;
using SideStage.Core.Sessions;
using SideStage.Windows.Processes;
using SideStage.Windows.RemoteDesktop;

namespace ChildSessionCheck;

internal static class Program
{
    /// <summary>在 STA 消息循环中运行验收，独立测试进程模式仅显示一个固定图案窗口。</summary>
    /// <param name="args">--run 启动验收，--sample 由子会话启动器调用。</param>
    /// <returns>全部检查通过为零，失败为一。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args is ["--sample"])
        {
            using Form sample = new() { Text = "SideStage 验收", BackColor = Color.MediumSpringGreen, ClientSize = new Size(400, 300), StartPosition = FormStartPosition.CenterScreen };
            Application.Run(sample);
            return 0;
        }

        bool attached = args is ["--attached", _];
        SessionId? expected = args is ["--resume" or "--attached", var value] && uint.TryParse(value, out uint id)
            ? new SessionId(id) : null;
        if (args is not ["--run"] && expected is null)
        {
            Console.WriteLine("ChildSessionCheck --run | --resume <SessionId> | --attached <SessionId>");
            return 2;
        }

        WindowsSessionInspector inspector = new();
        if (inspector.TryGetChildSession()?.Id != expected)
        {
            Console.Error.WriteLine(expected.HasValue ? "目标编号与当前子会话不符。" : "已有子会话，验收退出。");
            return 1;
        }

        if (attached && expected is { } activeId)
        {
            // 沿用用户已打开的预览及其解锁状态，无需再连接一次。
            try
            {
                CheckInSessionAsync(activeId, null).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        using Form host = new() { Text = "SideStage · 本机验收", ClientSize = new Size(800, 450) };
        using ChildSessionPreviewControl preview = new();
        host.Controls.Add(preview);
        int result = 1;
        host.Shown += async (_, _) =>
        {
            try
            {
                await CheckAsync(inspector, preview, expected);
                result = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            finally
            {
                host.Close();
            }
        };
        Application.Run(host);
        return result;
    }

    /// <summary>核对创建、进程归属、断开和重连；按创建时保存的句柄回收测试程序。</summary>
    /// <param name="inspector">只读会话观察器。</param>
    /// <param name="preview">当前消息循环承载的预览控件。</param>
    /// <param name="expected">重连接受的测试子会话编号；新建时为空。</param>
    private static async Task CheckAsync(WindowsSessionInspector inspector, ChildSessionPreviewControl preview, SessionId? expected)
    {
        Console.WriteLine($"parent={inspector.GetCurrentSession().Id}");
        SessionSnapshot child = await preview.ConnectAndVerifyAsync(expected);
        Console.WriteLine($"child={child.Id}");
        await CheckInSessionAsync(child.Id, preview);
    }

    /// <summary>在指定子会话运行示例，核验像素并回收进程。仅在验收程序持有预览时测试其重连。</summary>
    /// <param name="session">启动时必须由库重新核验为活动且已解锁的子会话编号。</param>
    /// <param name="preview">验收程序拥有的连接，沿用外部预览时为空。</param>
    private static async Task CheckInSessionAsync(SessionId session, ChildSessionPreviewControl? preview)
    {
        WindowsChildSessionProcessLauncher launcher = new();
        using IManagedChildProcess handle = await launcher.StartAsync(session, new ChildProcessStartRequest(Environment.ProcessPath!, ["--sample"]));
        try
        {
            ChildProcessSnapshot snapshot = handle.GetSnapshot();
            Console.WriteLine($"pid={snapshot.ProcessId} session={snapshot.SessionId} state={snapshot.State}");
            await WaitForSampleFrameAsync(session, Color.MediumSpringGreen);
            Console.WriteLine("frame=1920x1080 sample=verified");
            if (preview is null)
            {
                return;
            }

            preview.DisconnectPreview();
            long start = Environment.TickCount64;
            while (preview.GetConnectionState() != RdpPreviewConnectionState.Disconnected)
            {
                if (Environment.TickCount64 - start > 10000)
                {
                    throw new TimeoutException("预览断开超时。");
                }

                await Task.Delay(100);
            }

            await preview.ConnectAndVerifyAsync(session);
            if (handle.GetSnapshot().State != ChildProcessState.Running)
            {
                throw new InvalidOperationException("测试进程未跨重连存活。");
            }

            Console.WriteLine($"reconnected={session} pid={snapshot.ProcessId}");
        }
        finally
        {
            Console.WriteLine($"cleanup={(await handle.TerminateAsync()).State}");
        }
    }

    /// <summary>
    /// 等到子会话本身绘出示例窗口。跨登录会话的 WaitForInputIdle 不能证明窗口可见，
    /// 因此以真实子会话截图中的已知像素作为就绪信号，并限制等待时间。
    /// </summary>
    /// <param name="session">此前核验的子会话编号。</param>
    /// <param name="color">示例窗口在该阶段应绘制的客户区颜色。</param>
    private static async Task WaitForSampleFrameAsync(SessionId session, Color color)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        while (true)
        {
            byte[] png;
            try
            {
                png = await ChildDesktopCapture.CapturePngAsync(session, deadline.Token);
            }
            catch (ChildProcessLaunchException ex) when (ex.Stage == "CaptureDesktop")
            {
                throw new InvalidOperationException($"子会话取图拒绝：{ex.Message}（原生错误 {ex.NativeErrorCode}）。", ex);
            }
            using MemoryStream stream = new(png);
            using Bitmap frame = new(stream);
            if (frame.Size != new Size(1920, 1080))
            {
                throw new InvalidDataException($"子会话截图尺寸为 {frame.Width}x{frame.Height}，预期 1920x1080。");
            }

            if (frame.GetPixel(960, 540).ToArgb() == color.ToArgb())
            {
                return;
            }

            await Task.Delay(250, deadline.Token);
        }
    }
}
