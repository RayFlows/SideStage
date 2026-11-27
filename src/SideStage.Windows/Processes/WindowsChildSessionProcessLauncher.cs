/*
 * 文件名称：WindowsChildSessionProcessLauncher.cs
 * 文件职责：
 *   将 Core 的跨会话进程启动契约绑定到 Windows 当前父会话的子会话。
 *
 * 实现策略：
 *   - 启动前重新查询当前父会话和子会话，拒绝过期或伪造的目标编号。
 *   - 通过 Windows 任务计划程序的指定会话运行能力启动目标程序，避免把
 *     调度器进程的父会话误当成目标子会话。
 *   - 启动后读取真实进程 SessionId，并在不匹配时终止错误进程且报告失败。
 *   - 不接受远程会话编号，不在校验失败时回退到主桌面。
 *
 * 线程安全：
 *   本类型不保存跨调用的可变状态；同一实例可以被多个线程共享。
 *
 * 维护要求：
 *   修改任务计划程序 COM 参数、会话校验顺序或进程发现窗口时，必须同步更新
 *   Windows 集成测试和 README 的失败语义说明。
 */

using System.ComponentModel;
using SideStage.Core.Processes;
using SideStage.Core.Platform.Windows;
using SideStage.Core.Sessions;

namespace SideStage.Windows.Processes;

/// <summary>
/// 在当前父会话的已确认子会话中启动并跟踪 Windows 进程。
/// </summary>
public sealed class WindowsChildSessionProcessLauncher : IChildSessionProcessLauncher
{
    private readonly ISessionInspector _sessionInspector;
    private readonly IWindowsChildSessionProcessStarter _processStarter;

    /// <summary>
    /// 创建使用系统会话观察器和任务计划程序启动后端的启动器。
    /// </summary>
    public WindowsChildSessionProcessLauncher()
        : this(new WindowsSessionInspector(), new WindowsTaskSchedulerChildSessionProcessStarter())
    {
    }

    /// <summary>
    /// 创建使用指定会话观察器的启动器，供宿主测试和诊断注入只读依赖。
    /// </summary>
    /// <param name="sessionInspector">用于核验父会话与子会话关系的观察器。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="sessionInspector"/> 为 null 时抛出。
    /// </exception>
    internal WindowsChildSessionProcessLauncher(ISessionInspector sessionInspector)
        : this(sessionInspector, new WindowsTaskSchedulerChildSessionProcessStarter())
    {
    }

    /// <summary>
    /// 创建使用指定观察器和启动后端的启动器。
    /// </summary>
    /// <param name="sessionInspector">用于核验父会话与子会话关系的观察器。</param>
    /// <param name="processStarter">负责在目标会话中启动并返回进程的后端。</param>
    /// <exception cref="ArgumentNullException">
    /// 任一依赖为 null 时抛出。
    /// </exception>
    internal WindowsChildSessionProcessLauncher(
        ISessionInspector sessionInspector,
        IWindowsChildSessionProcessStarter processStarter)
    {
        _sessionInspector = sessionInspector
            ?? throw new ArgumentNullException(nameof(sessionInspector));
        _processStarter = processStarter
            ?? throw new ArgumentNullException(nameof(processStarter));
    }

    /// <summary>
    /// 在当前父会话所拥有且处于活动状态的子会话中启动受管进程。
    /// </summary>
    /// <param name="targetSessionId">需要启动进程的当前子会话编号。</param>
    /// <param name="request">目标可执行文件、工作目录和参数。</param>
    /// <param name="cancellationToken">取消尚未完成的启动握手。</param>
    /// <returns>可查询、等待或显式终止的进程句柄。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> 为 null 时抛出。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 目标会话不是当前父会话的活动子会话时抛出。
    /// </exception>
    /// <exception cref="ChildProcessLaunchException">
    /// Windows 无法把目标程序启动到已核验子会话，或启动后身份校验失败时抛出。
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// 调用方在启动握手完成前取消操作时抛出。
    /// </exception>
    public async Task<IManagedChildProcess> StartAsync(
        SessionId targetSessionId,
        ChildProcessStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateTargetChildSession(_sessionInspector, targetSessionId);

        System.Diagnostics.Process process = await _processStarter
            .StartAsync(targetSessionId, request, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            VerifyStartedProcess(process, targetSessionId, request);
            ValidateTargetChildSession(_sessionInspector, targetSessionId);
            return new WindowsManagedChildProcess(process, targetSessionId, request.ExecutablePath);
        }
        catch
        {
            // 启动后身份不可信时主动清理已创建进程，防止错误进程留在父桌面或其他会话。
            TryTerminateUnexpectedProcess(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 在启动前重新确认目标编号仍然是当前父会话的活动子会话。
    /// </summary>
    /// <param name="targetSessionId">调用方请求的目标会话编号。</param>
    /// <param name="inspector">宿主所在父会话的真实状态来源。</param>
    /// <exception cref="InvalidOperationException">
    /// 当前无子会话、会话编号不匹配、目标是父会话或目标未处于活动状态时抛出。
    /// </exception>
    internal static void ValidateTargetChildSession(ISessionInspector inspector, SessionId targetSessionId)
    {
        SessionSnapshot parentSession = inspector.GetCurrentSession();
        SessionSnapshot? childSession = inspector.TryGetChildSession();

        if (childSession is null)
        {
            throw new InvalidOperationException(
                $"拒绝启动进程：当前父会话 {parentSession.Id} 没有已建立的子会话。");
        }

        if (childSession.Id == parentSession.Id)
        {
            throw new InvalidOperationException(
                $"拒绝启动进程：Windows 报告的子会话 {childSession.Id} 与父会话相同。");
        }

        if (childSession.Id != targetSessionId)
        {
            throw new InvalidOperationException(
                $"拒绝启动进程：请求会话 {targetSessionId} 不是当前活动子会话 {childSession.Id}。");
        }

        if (!childSession.IsChildSession)
        {
            throw new InvalidOperationException(
                $"拒绝启动进程：会话 {targetSessionId} 未通过子会话查询路径确认。");
        }

        if (childSession.ConnectionState != SessionConnectionState.Active)
        {
            throw new InvalidOperationException(
                $"拒绝启动进程：子会话 {targetSessionId} 当前状态为 {childSession.ConnectionState}，不是活动状态。");
        }

        if (!childSession.IsDesktopReady)
        {
            throw new InvalidOperationException(childSession.IsLocked == true
                ? $"子会话 {targetSessionId} 已锁定，请在子会话预览中完成 Windows 解锁。"
                : $"无法确认子会话 {targetSessionId} 已解锁。");
        }
    }

    /// <summary>
    /// 校验启动后进程的路径、PID 和真实会话编号。
    /// </summary>
    /// <param name="process">启动后端返回的进程对象。</param>
    /// <param name="targetSessionId">预期的子会话编号。</param>
    /// <param name="request">原始启动请求。</param>
    /// <exception cref="ChildProcessLaunchException">
    /// 进程不存在、路径不匹配、读取会话编号失败或会话编号不匹配时抛出。
    /// </exception>
    private static void VerifyStartedProcess(
        System.Diagnostics.Process process,
        SessionId targetSessionId,
        ChildProcessStartRequest request)
    {
        if (process is null)
        {
            throw new ChildProcessLaunchException(
                "VerifyStartedProcess",
                "Windows 启动后端返回了空进程对象。");
        }

        int actualSessionId;
        try
        {
            actualSessionId = process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new ChildProcessLaunchException(
                "VerifyStartedProcess",
                $"无法读取目标进程 {process.Id} 的真实 Windows 会话编号。",
                ex is Win32Exception win32Exception ? win32Exception.NativeErrorCode : null,
                ex);
        }

        if (actualSessionId != checked((int)targetSessionId.Value))
        {
            throw new ChildProcessLaunchException(
                "VerifyStartedProcess",
                $"目标进程 {process.Id} 的会话编号为 {actualSessionId}，预期为 {targetSessionId}；已拒绝继续管理该进程。");
        }

        string? actualPath = WindowsProcessNativeMethods.TryGetImagePath(process.Id);
        if (actualPath is null
            || !string.Equals(
                Path.GetFullPath(actualPath),
                request.ExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ChildProcessLaunchException(
                "VerifyStartedProcess",
                $"目标进程 {process.Id} 的可执行文件路径无法确认为请求路径。");
        }
    }

    /// <summary>
    /// 尽力终止启动后身份校验失败的进程，不覆盖原始启动异常。
    /// </summary>
    /// <param name="process">需要清理的候选进程。</param>
    private static void TryTerminateUnexpectedProcess(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 清理失败不能掩盖“会话身份不可信”的主错误；调用方仍会得到失败结果。
        }
    }
}
