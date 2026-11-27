/*
 * 文件名称：WindowsParentSessionRecovery.cs
 * 文件职责：在宿主收到本人远程桌面断开通知后，检查原有子会话的输入，必要时接回父桌面。
 * 操作范围：只处理本进程所在的父会话和它已核验的子会话。调用方负责通知来源、用户偏好与取消。
 * 结果语义：只有原子会话代理实际投递输入成功，才报告输入可用。一次调用最多发出一次接回请求。
 */
using SideStage.Core.Sessions;
using SideStage.Windows.Processes;

namespace SideStage.Windows.Platform.Windows;

/// <summary>提供与具体调度器、应用和游戏无关的父桌面恢复能力。</summary>
public static class WindowsParentSessionRecovery
{
    /// <summary>
    /// 使用进程已有权限，尝试把自己的父桌面接回空闲控制台。应在真实的 RDP 断开通知后调用。
    /// 取消可以阻止尚未发出的请求，已由 Windows 接受的桌面切换不能撤销。
    /// </summary>
    /// <param name="child">远程断开前已经核验的子会话编号。</param>
    /// <param name="token">关闭设置、重新连接或退出时用来取消检查的令牌。</param>
    /// <returns>原子会话的输入测试结果，以及可供日志记录的中文说明。</returns>
    public static Task<(bool InputReady, string Detail)> RecoverAsync(SessionId child, CancellationToken token)
    {
        var sessions = new ParentRecoverySessions(child);
        var agent = new WindowsTaskSchedulerChildSessionProcessStarter();
        return Task.Run(() => RunAsync(sessions.RequireIdleConsole, sessions.ConnectOwnSession,
            sessions.IsRestored, ct => agent.ProbeInputAsync(child, ct), Task.Delay, token), token);
    }

    /// <summary>
    /// 先排除控制台占用和输入仍可用的情况。探测期间可能发生重连，因此执行前重新取样并核对目标。
    /// 请求发出后只等待原会话和输入恢复，不重复切换控制台。
    /// </summary>
    internal static async Task<(bool InputReady, string Detail)> RunAsync(Func<uint> requireConsole,
        Action<uint> connect, Func<bool> isRestored, Func<CancellationToken, Task<bool>> probe,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token)
    {
        await delay(TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        uint console = requireConsole();
        if (await probe(token).ConfigureAwait(false)) return (true, "原有子会话输入正常，已保留当前会话状态。");

        if (requireConsole() != console) return (false, "等待期间本机控制台已变化，已跳过接回。");
        token.ThrowIfCancellationRequested();
        connect(console);

        bool reachedConsole = false;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                if (!isRestored()) continue;
                reachedConsole = true;
                if (await probe(token).ConfigureAwait(false) && isRestored())
                    return (true, "父桌面已接回本机控制台，原有子会话输入复检通过。");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, $"Windows 已接受接回请求，子会话输入复检未完成。{ex.Message}");
        }
        return (false, reachedConsole ? "父桌面已接回，但原有子会话输入复检未通过。" :
            "Windows 已接受接回请求，等待超时，尚未确认父桌面和原有子会话均已就绪。");
    }
}
