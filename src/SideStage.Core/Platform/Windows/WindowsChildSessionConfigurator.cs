/*
 * 文件名称：WindowsChildSessionConfigurator.cs
 * 文件职责：
 *   封装 WTSEnableChildSessions，以显式改变当前用户会话的子会话功能开关。
 *
 * 设计边界：
 *   - 实例化本类型不会改变系统设置。
 *   - 只有调用 SetEnabled 才会尝试修改系统状态。
 *   - 原生 API 拒绝设置或执行失败时，保留错误码并抛出 Win32Exception。
 */

using System.ComponentModel;
using System.Runtime.InteropServices;
using SideStage.Core.Sessions;

namespace SideStage.Core.Platform.Windows;

/// <summary>
/// 通过 Windows Terminal Services API 配置子会话功能开关。
/// </summary>
public sealed class WindowsChildSessionConfigurator : IChildSessionConfigurator
{
    /// <summary>
    /// 显式设置 Windows 子会话功能开关。
    /// </summary>
    /// <param name="enabled">
    /// <see langword="true"/> 表示启用；<see langword="false"/> 表示禁用。
    /// </param>
    /// <exception cref="Win32Exception">
    /// Windows API 调用失败时抛出，并携带原始错误码。
    /// </exception>
    public void SetEnabled(bool enabled)
    {
        if (WTSEnableChildSessions(enabled) == 0)
        {
            int errorCode = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                errorCode,
                $"{nameof(WTSEnableChildSessions)} 调用失败（Win32 {errorCode}）：{new Win32Exception(errorCode).Message}");
        }
    }

    /// <summary>
    /// 启用或禁用 Windows 子会话功能。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enabled);
}
