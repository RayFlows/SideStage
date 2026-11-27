/*
 * 文件职责：按计划任务公开的安全描述符，检查当前桌面账户在普通权限下能否写入任务。
 * Windows 负责处理用户组、拒绝项和继承。检查过程只读取权限，不提交任务定义。
 */
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SideStage.App;

internal static class StartupTaskAccess
{
    // 任务计划程序使用文件访问权限。检查替换任务数据所需的写入权。
    private const uint WriteData = 2;
    private struct GenericMapping
    {
        internal uint Read, Write, Execute, All;
    }

    /// <summary>由 Windows 按照实际写入时的普通用户身份判定权限，支持仅授予读取权限的个人登录任务。</summary>
    internal static bool CanWrite(string descriptor, bool ordinary = true)
    {
        var security = new RawSecurityDescriptor(descriptor);
        byte[] bytes = new byte[security.BinaryLength];
        security.GetBinaryForm(bytes, 0);
        return ordinary ? StartupUserAccess.AsUser(Check) : Check();

        // 仅在已授权的操作中检查当前进程令牌，其余情况按普通桌面用户判定。
        bool Check()
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
            if (!DuplicateToken(identity.AccessToken, 1, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200a0, All = 0x1f01ff };
                uint length = 64;
                var privileges = new byte[length];
                if (!AccessCheck(bytes, token, WriteData, ref mapping, privileges, ref length, out _, out bool granted))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return granted;
            }
        }
    }

    /// <summary>为仅检查权限的系统调用取得当前身份的标识级令牌。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateToken(SafeAccessTokenHandle existing, int level, out SafeAccessTokenHandle token);

    /// <summary>计算令牌在安全描述符上的有效权限，不打开或写入被检查的任务。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AccessCheck(byte[] descriptor, SafeAccessTokenHandle token, uint requested, ref GenericMapping mapping,
        [Out] byte[] privileges, ref uint length, out uint granted, out bool accessStatus);
}
