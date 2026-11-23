/*
 * 文件名称：IWindowsChildSessionProcessStarter.cs
 * 文件职责：
 *   隔离 Windows 任务计划程序启动后端，避免启动器同时承担 COM 编排和会话校验。
 *
 * 设计边界：
 *   - 该接口是 Windows 程序集内部实现接缝，不属于 Core 的跨平台契约。
 *   - 后端只负责返回实际创建的进程；目标会话的最终安全核验仍由启动器执行。
 *   - 后端失败必须保留启动阶段，禁止把未找到进程转换成成功。
 */

using System.Diagnostics;
using SideStage.Core.Processes;
using SideStage.Core.Sessions;

namespace SideStage.Windows.Processes;

/// <summary>
/// 在指定 Windows 会话中启动目标进程的内部后端。
/// </summary>
internal interface IWindowsChildSessionProcessStarter
{
    /// <summary>
    /// 启动目标程序并返回新创建的实际进程对象。
    /// </summary>
    /// <param name="targetSessionId">已经由上层核验的目标子会话编号。</param>
    /// <param name="request">目标程序启动参数。</param>
    /// <param name="cancellationToken">取消进程发现握手。</param>
    /// <returns>在目标会话内发现的新进程对象。</returns>
    /// <exception cref="ChildProcessLaunchException">
    /// 任务计划程序拒绝启动或无法发现目标进程时抛出。
    /// </exception>
    Task<Process> StartAsync(
        SessionId targetSessionId,
        ChildProcessStartRequest request,
        CancellationToken cancellationToken);
}
