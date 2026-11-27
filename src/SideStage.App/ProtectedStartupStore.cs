/*
 * 文件职责：为需要管理员写入的个人任务隔离代理和原动作。每个用户在 Program Files 有独立目录。
 * 权限约定：目录创建时即限定写入者；每次使用前检查所有者、权限和重解析点，拒绝接管已有的可疑目录。
 */
using System.Security.AccessControl;
using System.Security.Principal;

namespace SideStage.App;

internal static class ProtectedStartupStore
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>受保护路径来自 Windows 的程序目录和当前完整 SID，忽略扫描缓存及用户输入。</summary>
    internal static string Location
    {
        get
        {
            using var user = WindowsIdentity.GetCurrent();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "SideStage.StartupGate-" + user.User!.Value);
        }
    }

    /// <summary>原子创建受限目录；读取已有目录时同样核对权限，防止高权限操作使用个人可改写的备份。</summary>
    internal static void Validate(bool create)
    {
        var folder = new DirectoryInfo(Location);
        using var identity = WindowsIdentity.GetCurrent();
        var rights = new DirectorySecurity();
        rights.SetOwner(Administrators);
        rights.SetAccessRuleProtection(true, false);
        const InheritanceFlags descend = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var writer in new[] { Administrators, SystemAccount })
            rights.AddAccessRule(new(writer, FileSystemRights.FullControl, descend, PropagationFlags.None, AccessControlType.Allow));
        rights.AddAccessRule(new(identity.User!, FileSystemRights.ReadAndExecute, descend, PropagationFlags.None, AccessControlType.Allow));
        if (create) folder.Create(rights);
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("受保护的启动目录是重解析点。");
        var actual = folder.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!Administrators.Equals(actual.GetOwner(typeof(SecurityIdentifier))) ||
            actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access) != rights.GetSecurityDescriptorSddlForm(AccessControlSections.Access))
            throw new UnauthorizedAccessException("受保护的启动目录权限与预期不符。");
    }
}
