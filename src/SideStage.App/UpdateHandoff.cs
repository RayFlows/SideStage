/*
 * 文件职责：将随应用安装的独立更新器复制出安装目录，以标准管道完成两阶段交接。
 * 安全约束：先确认本程序就是已登记的安装副本，再等待更新器复核成功；释放未提交的管道即取消。
 */
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace SideStage.App;

internal sealed class UpdateHandoff : IDisposable
{
    private readonly Process _worker;

    /// <summary>持有已准备的子进程及其输入管道，直到原窗口确认关闭或放弃更新。</summary>
    private UpdateHandoff(Process worker) => _worker = worker;

    /// <summary>核对安装登记和工作进程的启动回执。开发目录和便携副本不可静默覆盖已安装应用。</summary>
    internal static async Task<UpdateHandoff> PrepareAsync(VerifiedUpdate package, CancellationToken cancel)
    {
        // 当前用户安装不需要提权，也不应由高权限进程执行用户可写缓存中的安装器。
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("请用普通权限打开 SideStage 后更新，无需以管理员身份运行。");
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{28C18EA6-CBC7-4C56-AFC2-A5307EAD9573}_is1";
        using var entry = Registry.CurrentUser.OpenSubKey(uninstall);
        return await PrepareAtPathAsync(package, entry?.GetValue("InstallLocation") as string, cancel);
    }

    /// <summary>仅从已登记目录复制随包的更新器。隔离验收使用随机安装记录，不读取或覆盖真实安装。</summary>
    internal static async Task<UpdateHandoff> PrepareAtPathAsync(VerifiedUpdate package, string? location, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(location) ||
            !string.Equals(Path.GetFullPath(Path.Combine(location, "SideStage.exe")), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此窗口不在登记的安装目录中。请从开始菜单打开已安装的 SideStage 后更新。");
        string directory = Path.GetDirectoryName(package.FilePath)!;
        string workerPath = Path.Combine(directory, "SideStage.Updater.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "SideStage.Updater.exe"), workerPath, true);
        var start = new ProcessStartInfo(workerPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, WorkingDirectory = directory };
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Path.GetFileName(package.FilePath));
        start.ArgumentList.Add(package.Hash);
        var worker = Process.Start(start) ?? throw new IOException("无法启动独立更新器。");
        var ticket = new UpdateHandoff(worker);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            if (await worker.StandardOutput.ReadLineAsync(timeout.Token) != "READY")
                throw new IOException("更新器未能复核安装包或识别当前进程，尚未退出 SideStage。");
            return ticket;
        }
        catch { ticket.Dispose(); throw; }
    }

    /// <summary>仅在主窗口确实完成关闭时授权写入；工作进程还会等待原进程退出。</summary>
    internal void Commit() => _worker.StandardInput.WriteLine("INSTALL");

    /// <summary>关闭管道使未获授权的更新器自行结束。工作进程已获授权时则与主程序独立运行。</summary>
    public void Dispose()
    {
        try { _worker.StandardInput.Close(); }
        finally { _worker.Dispose(); }
    }
}
