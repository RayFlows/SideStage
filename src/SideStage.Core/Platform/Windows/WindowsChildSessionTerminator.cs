/*
 * 文件名称：WindowsChildSessionTerminator.cs
 * 文件职责：
 *   通过 Windows Terminal Services API 注销当前子会话。
 *
 * 安全约束：
 *   - 注销目标始终来自 Windows 子会话查询，调用方提供的已确认编号仅用于比对。
 *   - 注销前重新查询子会话编号，并与当前父进程会话编号进行比对。
 *   - 预览断开必须使用 RDP 控件的 Disconnect；本类型只用于明确的会话注销动作。
 *
 * 操作影响：
 *   注销会结束子会话中的用户进程。调用方必须将此操作呈现为独立且明确的命令。
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SideStage.Core.Sessions;

namespace SideStage.Core.Platform.Windows;

/// <summary>
/// 仅允许注销当前父会话所拥有的子会话。
/// </summary>
public sealed class WindowsChildSessionTerminator : IChildSessionTerminator
{
    /// <summary>
    /// 注销当前子会话，并等待 Windows 接受注销请求。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 当前没有子会话，或者查询到的子会话编号与当前会话相同。
    /// </exception>
    /// <exception cref="Win32Exception">
    /// Windows 拒绝注销请求或注销 API 调用失败时抛出。
    /// </exception>
    public void LogoffCurrentChildSession() => LogoffCurrentChildSessionCore(null);

    /// <summary>仅当 Windows 当前报告的子会话仍为用户刚刚确认的编号时才注销。</summary>
    public void LogoffCurrentChildSession(SessionId confirmedChild) => LogoffCurrentChildSessionCore(confirmedChild);

    /// <summary>在同一次调用中重新查询目标、核对确认编号和父会话，再提交注销请求。</summary>
    private static void LogoffCurrentChildSessionCore(SessionId? confirmedChild)
    {
        WindowsSessionInspector inspector = new();
        SessionSnapshot? childSnapshot = inspector.TryGetChildSession();

        if (childSnapshot is null)
        {
            throw new InvalidOperationException("当前没有可注销的子会话。");
        }

        if (childSnapshot.StateQueryError is int error)
        {
            throw new InvalidOperationException($"无法确认子会话 {childSnapshot.Id} 的状态（Win32 {error}），已取消注销。");
        }
        if (confirmedChild.HasValue && confirmedChild.Value != childSnapshot.Id)
            throw new InvalidOperationException("确认期间子会话编号已变化，请重新确认。");

        using Process currentProcess = Process.GetCurrentProcess();
        uint parentSessionId = checked((uint)currentProcess.SessionId);

        // 最后一道保护：即使平台 API 返回意外结果，也绝不把当前父会话传入注销 API。
        if (childSnapshot.Id.Value == parentSessionId)
        {
            throw new InvalidOperationException(
                $"拒绝注销会话 {childSnapshot.Id}：它与当前父会话编号相同。");
        }

        if (!WTSLogoffSession(IntPtr.Zero, childSnapshot.Id.Value, wait: true))
        {
            int errorCode = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                errorCode,
                $"{nameof(WTSLogoffSession)} 注销 {childSnapshot.Id} 失败（Win32 {errorCode}）：{new Win32Exception(errorCode).Message}");
        }
    }

    /// <summary>
    /// 请求本机 Windows 注销指定终端服务会话。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(
        IntPtr serverHandle,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);
}
