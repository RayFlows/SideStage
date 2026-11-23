/*
 * 文件名称：IManagedChildProcess.cs
 * 文件职责：
 *   定义一个已启动子会话进程的查询、等待和显式终止能力。
 *
 * 设计边界：
 *   - Dispose 只释放本库持有的系统句柄，不会终止目标进程。
 *   - TerminateAsync 只终止此次启动得到的进程，不递归结束其子进程。
 *   - 等待被取消时进程保持运行；取消等待不等于取消或终止进程。
 */

namespace SideStage.Core.Processes;

/// <summary>
/// 提供对单个受管子会话进程的生命周期操作。
/// </summary>
public interface IManagedChildProcess : IDisposable
{
    /// <summary>
    /// 重新读取进程状态和退出码。
    /// </summary>
    /// <returns>采集时刻的进程状态快照。</returns>
    /// <exception cref="ObjectDisposedException">
    /// 管理句柄已释放时抛出。
    /// </exception>
    ChildProcessSnapshot GetSnapshot();

    /// <summary>
    /// 等待进程退出，并在退出后返回最终快照。
    /// </summary>
    /// <param name="cancellationToken">仅取消本次等待的令牌。</param>
    /// <returns>进程退出后的最终状态快照。</returns>
    /// <exception cref="OperationCanceledException">
    /// 等待被调用方取消时抛出；进程本身不会因此终止。
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// 管理句柄已释放时抛出。
    /// </exception>
    Task<ChildProcessSnapshot> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 显式终止此次启动的进程，并等待 Windows 确认它已退出。
    /// </summary>
    /// <param name="cancellationToken">取消终止后的等待；不会撤销已发出的终止请求。</param>
    /// <returns>确认退出后的最终状态快照。</returns>
    /// <exception cref="OperationCanceledException">
    /// 发出终止请求后，等待进程退出的阶段被取消时抛出。
    /// </exception>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// Windows 拒绝终止进程或无法确认其退出时抛出。
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// 管理句柄已释放时抛出。
    /// </exception>
    Task<ChildProcessSnapshot> TerminateAsync(CancellationToken cancellationToken = default);
}
