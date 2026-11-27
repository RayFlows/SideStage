/*
 * 文件职责：在主桌面确认某个登录入口是否正在当前子会话中运行。
 * 通信约定：当前用户专用的只写管道只发送一个字节。会话号取自 Windows 管道句柄，
 * 客户端不能提交命令、进程号或会话号。主窗口退出后，登录入口自动放行。
 */
using System.IO.Pipes;
using System.Runtime.InteropServices;
using SideStage.StartupGate;
using Microsoft.Win32.SafeHandles;

namespace SideStage.App;

internal sealed class StartupSessionBroker : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;

    /// <summary>在真实主窗口创建前开始监听，覆盖刚建立子会话而预览尚未就绪的时间段。</summary>
    internal StartupSessionBroker(string? gateDirectory = null, Func<uint?>? currentChild = null)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var name = Dispatcher.PipeName(gateDirectory ?? StartupRegistrations.DefaultGateDirectory) + "." + process.SessionId;
        _worker = ServeAsync(name, currentChild ?? QueryChild, _lifetime.Token);
    }

    /// <summary>只接受由本机 WTS 从当前父桌面返回的有效会话号。</summary>
    private static uint? QueryChild() => WTSGetChildSessionId(out var id) && id != uint.MaxValue ? id : null;

    /// <summary>每次连接重新查询当前子会话。无法确定或 Windows 拒绝查询时返回放行决定。</summary>
    private static async Task ServeAsync(string name, Func<uint?> currentChild, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                bool child = GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var actual) && actual == currentChild();
                await pipe.WriteAsync(new byte[] { child ? (byte)1 : (byte)0 }, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100, stop).ConfigureAwait(false);
            }
        }
    }

    /// <summary>结束等待并释放取消句柄，不占用 UI 消息循环。</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        try { _worker.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    /// <summary>取发起当前连接的真实会话编号。</summary>
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);

    /// <summary>在主桌面查询 Windows 分配给当前父会话的子会话编号。</summary>
    [DllImport("wtsapi32.dll")] private static extern bool WTSGetChildSessionId(out uint session);
}
