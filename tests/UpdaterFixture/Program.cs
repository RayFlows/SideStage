/*
 * 文件职责：代替真实 RDP 窗口，验证安装进程交接、取消与升级后重启。
 * 隔离约束：仅在带专用标记的验收安装目录内运行，不访问真实卸载项、用户凭据或远程会话。
 */
using SideStage.App;

namespace SideStage.App
{
    /// <summary>与生产交接接口一致的测试附件，哈希由隔离安装脚本计算。</summary>
    internal sealed record VerifiedUpdate(string FilePath, string Hash);
}

internal static class Program
{
    /// <summary>无参数代表安装器已完成重启；其他模式分别模拟确认安装和取消交接。</summary>
    private static async Task<int> Main(string[] args)
    {
        string dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, "fixture-only.txt"))) return 2;
        // 隔离宿主不登记任何启动项；卸载器的恢复请求可以直接确认完成。
        if (args is ["--restore-startup-items", "--silent"]) return 0;
        if (args.Length == 0) { File.WriteAllText(Path.Combine(dir, "restarted.txt"), "ok"); return 0; }
        if (args is [_, _, _, "--expect-elevated"])
        {
            if (!StartupElevation.IsAdministrator()) return 3;
            args = args[..3];
        }
        if (args is not [var action, var path, var hash]) return 2;
        using var ticket = await UpdateHandoff.PrepareAtPathAsync(new(path, hash), dir, CancellationToken.None);
        if (action == "install") ticket.Commit();
        return 0;
    }
}
