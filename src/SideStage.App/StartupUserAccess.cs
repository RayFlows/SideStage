/*
 * 文件职责：以当前用户的普通令牌修改个人启动登记和备份。
 * 权限边界：主窗口可能已为会话恢复获得管理员权限；用户可写目录和快捷方式按普通用户权限操作。
 */
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SideStage.App;

internal static class StartupUserAccess
{
    /// <summary>在 UAC 分离令牌的管理员窗口中切回关联的受限令牌，普通窗口直接执行。</summary>
    internal static T AsUser<T>(Func<T> operation)
    {
        const int elevationType = 18, linkedToken = 19, full = 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetElevation(identity.AccessToken, elevationType, out int type, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (type != full) return operation();
        if (!GetLinked(identity.AccessToken, linkedToken, out var token, IntPtr.Size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var limited = new SafeAccessTokenHandle(token);
        return WindowsIdentity.RunImpersonated(limited, operation);
    }

    /// <summary>查询当前令牌是否属于 UAC 拆分后已提权的一半。</summary>
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetElevation(SafeAccessTokenHandle token, int info, out int value, int size, out int written);

    /// <summary>取得同一用户的关联令牌，返回的句柄由调用方释放。</summary>
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetLinked(SafeAccessTokenHandle token, int info, out IntPtr value, int size, out int written);
}
