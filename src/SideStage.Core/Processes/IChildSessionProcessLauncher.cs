/*
 * 文件名称：IChildSessionProcessLauncher.cs
 * 文件职责：
 *   定义将 GUI 或其他 Windows 程序启动到已确认子会话的能力契约。
 *
 * 设计边界：
 *   - 目标 SessionId 必须是当前调用会话明确关联的子会话。
 *   - 实现必须在启动前和启动后检查会话身份以及实际进程 SessionId。
 *   - 任何校验或平台启动失败都必须明确失败，禁止回退到父桌面。
 */

using SideStage.Core.Sessions;

namespace SideStage.Core.Processes;

/// <summary>
/// 提供在当前父会话所拥有的子会话中启动进程的能力。
/// </summary>
public interface IChildSessionProcessLauncher
{
    /// <summary>
    /// 在指定且已核验的当前子会话中启动一个受管进程。
    /// </summary>
    /// <param name="targetSessionId">需要启动进程的当前子会话编号。</param>
    /// <param name="request">进程路径、工作目录和参数。</param>
    /// <param name="cancellationToken">取消尚未完成的启动握手。</param>
    /// <returns>可查询、等待或显式终止的进程管理句柄。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> 为 null 时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 目标会话不是当前父会话的子会话，或子会话关系在启动期间发生变化时抛出。
    /// </exception>
    /// <exception cref="ChildProcessLaunchException">
    /// Windows 启动代理、任务计划程序或目标程序启动失败时抛出。
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// 调用方在启动握手完成前取消操作时抛出。
    /// </exception>
    Task<IManagedChildProcess> StartAsync(
        SessionId targetSessionId,
        ChildProcessStartRequest request,
        CancellationToken cancellationToken = default);
}
