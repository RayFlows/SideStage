/*
 * 文件职责：代替真实 RDP 窗口，验证安装进程交接、取消与升级后重启。
 * 隔离约束：仅在带专用标记的验收安装目录内运行，不访问真实卸载项、用户凭据或远程会话。
 */
using System.Security.Principal;
using System.Diagnostics;
using SideStage.App;

namespace SideStage.App
{
    /// <summary>与生产交接接口一致的测试附件，哈希由隔离安装脚本计算。</summary>
    internal sealed record VerifiedUpdate(string FilePath, string Hash);
}

internal static class Program
{
    /// <summary>安装后复用主程序的权限选择入口，只有获选显示窗口的进程才能留下重启回执。</summary>
    private static async Task<int> Main(string[] args)
    {
        string dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, "fixture-only.txt"))) return 2;
        // 隔离宿主不登记任何启动项；卸载器的恢复请求可以直接确认完成。
        if (args is ["--restore-startup-items", "--silent"]) return 0;
        if (args is ["--hold-installation"])
        {
            Console.WriteLine("READY");
            await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45));
            return 0;
        }
        // 等待中的启动器和测试窗口来自同一份安装；还原生产参数，验证只排除本窗口的启动器。
        if (args is ["--authorized-window", _, "--fixture-busy", var package, var digest, _])
            return await CheckBusyAsync(dir, new(package, digest));
        if (args.Length == 0 || args is ["--authorized-window", _, ..]) return CheckRestart(dir, args);
        if (args is [_, _, _, "--expect-elevated"])
        {
            if (!StartupElevation.IsAdministrator()) return 3;
            args = args[..3];
        }
        if (args is not [var action, var path, var hash]) return 2;
        if (action == "busy") return await RunWaitingLauncherAsync(path, hash);
        // 只在旧宿主的进程树设置标记。桌面正常启动的新版应取得桌面自己的环境。
        Environment.SetEnvironmentVariable("SIDESTAGE_FIXTURE_OLD_HOST", "1");
        using var ticket = await UpdateHandoff.PrepareAtPathAsync(new(path, hash), dir, CancellationToken.None);
        if (action == "install") ticket.Commit();
        return 0;
    }

    /// <summary>保持父进程存活，直到测试窗口放行本启动器、拦住另一个实例并成功重试。</summary>
    private static async Task<int> RunWaitingLauncherAsync(string path, string hash)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--authorized-window", identity.User!.Value, "--fixture-busy", path, hash,
                                    $"--waiting-launcher={Environment.ProcessId}" })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("无法启动隔离的更新窗口。");
        await child.WaitForExitAsync();
        return child.ExitCode;
    }

    /// <summary>打开同一份程序的另一个实例，检查交接在关闭当前宿主前被拒绝，并在占用解除后成功重试。</summary>
    private static async Task<int> CheckBusyAsync(string dir, VerifiedUpdate update)
    {
        using var other = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--hold-installation")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true })!;
        try
        {
            if (await other.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "READY") return 4;
            try { using var unexpected = await UpdateHandoff.PrepareAtPathAsync(update, dir, CancellationToken.None); return 5; }
            catch (IOException ex) when (ex.Message.Contains($"PID {other.Id}")) { }
        }
        finally
        {
            other.StandardInput.Close();
            await other.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        using var retry = await UpdateHandoff.PrepareAtPathAsync(update, dir, CancellationToken.None);
        return 0;
    }

    /// <summary>从本次验收专属的注册表项读取权限偏好，报告最终窗口的真实权限，而非启动器的权限。</summary>
    private static int CheckRestart(string dir, string[] args)
    {
        string? runId = Directory.GetParent(Path.TrimEndingDirectorySeparator(dir))?.Name;
        if (!Guid.TryParseExact(runId, "D", out _)) return 2;
        string key = $@"Software\SideStage.Tests\{runId}";
        string registration = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{{{runId}}}_is1";
        using var identity = WindowsIdentity.GetCurrent();
        if (args.Length == 0 && !StartupElevation.IsAdministrator() &&
            DesktopRestart.TryOpenPending(registration, Environment.ProcessPath!, identity)) return 0;
        if (Environment.GetEnvironmentVariable("SIDESTAGE_FIXTURE_OLD_HOST") == "1")
        {
            File.WriteAllText(Path.Combine(dir, "restarted.txt"), "inherited-updater-context");
            return 3;
        }
        bool elevated = StartupElevation.IsAdministrator();
        var route = StartupElevation.Choose(args, elevated, identity.User!.Value,
            () => StartupElevation.IsEnabled(key), (classic, sid) => StartupElevation.Request(classic, sid,
                message => File.WriteAllText(Path.Combine(dir, "authorization.txt"), message)));
        if (route == StartupRoute.DifferentAccount) return StartupElevation.DifferentAccountExitCode;
        if (route == StartupRoute.Home)
            File.WriteAllText(Path.Combine(dir, "restarted.txt"), elevated ? "administrator" : "standard");
        return 0;
    }
}
