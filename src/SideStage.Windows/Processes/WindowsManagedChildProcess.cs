/*
 * 文件名称：WindowsManagedChildProcess.cs
 * 文件职责：
 *   持有一次跨会话启动得到的 Windows 进程句柄，并实现 Core 进程生命周期契约。
 *
 * 生命周期约束：
 *   - Dispose 只释放本库的 Process 包装，不自动终止目标程序。
 *   - 每次读取快照都重新读取进程存活状态；不缓存“运行中”结论。
 *   - 只有 Kill 调用成功后记录 Terminated；自然退出始终报告 Exited。
 *   - 进程仍存活时如果会话编号发生变化，立即报告不可信，不返回伪造的目标会话快照。
 */

using System.ComponentModel;
using System.Diagnostics;
using SideStage.Core.Processes;
using SideStage.Core.Sessions;

namespace SideStage.Windows.Processes;

/// <summary>
/// 基于 System.Diagnostics.Process 的受管子会话进程实现。
/// </summary>
internal sealed class WindowsManagedChildProcess : IManagedChildProcess
{
    private readonly Process _process;
    private readonly SessionId _targetSessionId;
    private readonly string _executablePath;
    private readonly DateTimeOffset _startedAtUtc;
    private int _isDisposed;
    private int _wasTerminated;

    /// <summary>
    /// 创建一个已完成身份核验的受管进程句柄。
    /// </summary>
    /// <param name="process">目标进程对象。</param>
    /// <param name="targetSessionId">启动时核验的目标子会话编号。</param>
    /// <param name="executablePath">目标可执行文件绝对路径。</param>
    public WindowsManagedChildProcess(
        Process process,
        SessionId targetSessionId,
        string executablePath)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _targetSessionId = targetSessionId;
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));

        try
        {
            _startedAtUtc = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new ChildProcessLaunchException(
                "ReadProcessStartTime",
                $"无法读取目标进程 {process.Id} 的启动时间。",
                ex is Win32Exception win32Exception ? win32Exception.NativeErrorCode : null,
                ex);
        }
    }

    /// <summary>
    /// 读取当前进程状态，并核对子会话归属仍然有效。
    /// </summary>
    /// <returns>采集时刻的进程状态快照。</returns>
    /// <exception cref="ObjectDisposedException">
    /// 当前管理句柄已释放时抛出。
    /// </exception>
    /// <exception cref="ChildProcessLaunchException">
    /// 进程仍存活但无法确认其会话归属时抛出。
    /// </exception>
    public ChildProcessSnapshot GetSnapshot()
    {
        ThrowIfDisposed();

        bool hasExited = HasExitedSafely();
        int? exitCode = hasExited ? ReadExitCodeSafely() : null;

        if (!hasExited)
        {
            VerifyLiveProcessSession();
        }

        ChildProcessState state = hasExited
            ? Volatile.Read(ref _wasTerminated) != 0
                ? ChildProcessState.Terminated
                : ChildProcessState.Exited
            : ChildProcessState.Running;

        return new ChildProcessSnapshot(
            _process.Id,
            _targetSessionId,
            _executablePath,
            _startedAtUtc,
            state,
            exitCode,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 等待目标进程退出并返回最终快照。
    /// </summary>
    /// <param name="cancellationToken">仅取消本次等待的令牌。</param>
    /// <returns>进程退出后的最终状态。</returns>
    /// <exception cref="ObjectDisposedException">
    /// 当前管理句柄已释放时抛出。
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// 等待被调用方取消时抛出。
    /// </exception>
    public async Task<ChildProcessSnapshot> WaitForExitAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <summary>
    /// 终止目标进程并等待 Windows 确认其退出。
    /// </summary>
    /// <param name="cancellationToken">取消终止后的等待。</param>
    /// <returns>确认退出后的最终状态。</returns>
    /// <exception cref="ObjectDisposedException">
    /// 当前管理句柄已释放时抛出。
    /// </exception>
    /// <exception cref="Win32Exception">
    /// Windows 拒绝终止进程时抛出。
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// 终止请求发出后等待退出被取消时抛出。
    /// </exception>
    public async Task<ChildProcessSnapshot> TerminateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!HasExitedSafely())
        {
            try
            {
                _process.Kill(entireProcessTree: false);
                Volatile.Write(ref _wasTerminated, 1);
            }
            catch (InvalidOperationException)
            {
                // 进程可能刚好在 HasExited 与 Kill 之间自然退出，继续读取最终状态即可。
            }
            catch (Win32Exception)
            {
                throw;
            }
        }

        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return GetSnapshot();
    }

    /// <summary>
    /// 释放当前管理句柄持有的 Process 包装，不终止目标进程。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _process.Dispose();
        }
    }

    /// <summary>
    /// 安全读取目标进程是否已经退出。
    /// </summary>
    /// <returns>已退出或 Windows 无法继续读取时返回 true。</returns>
    private bool HasExitedSafely()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// 读取进程退出码；进程句柄失效时返回 null，避免伪造退出码。
    /// </summary>
    /// <returns>Windows 返回的退出码，或无法读取时的 null。</returns>
    private int? ReadExitCodeSafely()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 重新读取存活进程的会话编号，并拒绝会话漂移或身份不可确认。
    /// </summary>
    private void VerifyLiveProcessSession()
    {
        int actualSessionId;
        try
        {
            actualSessionId = _process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new ChildProcessLaunchException(
                "VerifyManagedProcessSession",
                $"无法读取受管进程 {_process.Id} 的当前会话编号。",
                ex is Win32Exception win32Exception ? win32Exception.NativeErrorCode : null,
                ex);
        }

        if (actualSessionId != checked((int)_targetSessionId.Value))
        {
            throw new ChildProcessLaunchException(
                "VerifyManagedProcessSession",
                $"受管进程 {_process.Id} 已不在目标子会话 {_targetSessionId} 中，当前会话为 {actualSessionId}。");
        }
    }

    /// <summary>
    /// 在所有公开操作前确认对象尚未释放。
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _isDisposed) != 0,
            this);
    }
}
