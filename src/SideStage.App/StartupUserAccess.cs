/*
 * 文件职责：为个人启动登记、当前用户更新缓存及安装进程提供同一账户的普通令牌。
 * 权限边界：主窗口保留已获授权，用户可改写的路径只交给该账户的普通权限处理。
 */
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SideStage.App;

internal static class StartupUserAccess
{
    /// <summary>普通窗口沿用自身权限。管理员窗口从本进程派生同一账户的普通用户令牌，仅在回调期间使用。</summary>
    internal static T AsUser<T>(Func<T> operation)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return operation();
        using var token = CreatePersonalToken(identity);
        return WindowsIdentity.RunImpersonated(token, operation);
    }

    /// <summary>从当前账户创建普通主令牌，既可用于个人文件读写，也可直接交给 Windows 启动当前用户安装器。</summary>
    internal static SafeAccessTokenHandle CreatePersonalToken(WindowsIdentity identity)
    {
        // UAC 的关联令牌可能只可识别身份，无法用于创建互斥锁或写入。SAFER 从自己的令牌派生可执行的普通权限上下文。
        const uint machineScope = 1, normalUser = 0x20000, openLevel = 1;
        if (!SaferCreateLevel(machineScope, normalUser, openLevel, out var level, IntPtr.Zero)) throw LastError();
        try
        {
            if (!SaferComputeTokenFromLevel(level, identity.AccessToken, out var token, 0, IntPtr.Zero)) throw LastError();
            try
            {
                SetMediumIntegrity(token);
                WindowsIdentity.RunImpersonated(token, () =>
                {
                    using var effective = WindowsIdentity.GetCurrent();
                    if (effective.User != identity.User || new WindowsPrincipal(effective).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new UnauthorizedAccessException("无法核实当前账户的个人操作权限。");
                });
                return token;
            }
            catch { token.Dispose(); throw; }
        }
        finally { SaferCloseLevel(level); }
    }

    /// <summary>将派生令牌的完整性限制在普通桌面级别，保证其新建的文件和内核对象可以由普通窗口继续操作。</summary>
    private static void SetMediumIntegrity(SafeAccessTokenHandle token)
    {
        var sid = new SecurityIdentifier(WellKnownSidType.WinMediumLabelSid, null);
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var label = new MandatoryLabel { Sid = pinned.AddrOfPinnedObject(), Attributes = 0x20 };
            const int tokenIntegrityLevel = 25;
            if (!SetTokenInformation(token, tokenIntegrityLevel, ref label, Marshal.SizeOf<MandatoryLabel>() + bytes.Length))
                throw LastError();
        }
        finally { pinned.Free(); }
    }

    /// <summary>在下一次系统调用覆盖错误码之前，记录 Windows 的失败原因。</summary>
    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct MandatoryLabel { internal IntPtr Sid; internal uint Attributes; }

    /// <summary>打开 Windows 定义的标准权限级别，由本类负责关闭。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferCreateLevel(uint scope, uint level, uint flags, out IntPtr handle, IntPtr reserved);

    /// <summary>从指定账户自己的访问令牌派生受限令牌，由安全句柄负责释放。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferComputeTokenFromLevel(IntPtr level, SafeAccessTokenHandle original,
        out SafeAccessTokenHandle token, uint flags, IntPtr reserved);

    /// <summary>释放权限级别句柄，结束本次降权操作。</summary>
    [DllImport("advapi32.dll")]
    private static extern bool SaferCloseLevel(IntPtr level);

    /// <summary>为专属的派生令牌写入完整性标签。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        ref MandatoryLabel label, int length);
}
