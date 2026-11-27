/*
 * 文件职责：将随应用安装的独立更新器复制出安装目录，以一次性双向管道完成两阶段交接。
 * 安全约束：先确认本程序就是已登记的安装副本，再等待更新器复核成功；释放未提交的管道即取消。
 */
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SideStage.App;

internal sealed class UpdateHandoff : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _commands;

    /// <summary>持有已准备的子进程及其输入管道，直到原窗口确认关闭或放弃更新。</summary>
    private UpdateHandoff(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _commands = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>核对安装登记和工作进程的启动回执。开发目录和便携副本不可静默覆盖已安装应用。</summary>
    internal static async Task<UpdateHandoff> PrepareAsync(VerifiedUpdate package, CancellationToken cancel)
    {
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
        StartupUserAccess.AsUser(() =>
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "SideStage.Updater.exe"), workerPath, true);
            return true;
        });
        string name = $"SideStage.Update.{Guid.NewGuid():N}";
        using var worker = PersonalUpdateProcess.Start(workerPath, name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        // 更新器只需要验证连接者的 PID，不能借管道模拟管理员窗口。
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out int peer) || peer != worker.Id || worker.HasExited)
                throw new IOException("更新器身份核对失败，尚未退出 SideStage。");
            var ticket = new UpdateHandoff(pipe);
            // 普通更新器可能无权自行打开管理员进程。只交给它查询映像路径及等待退出所需的句柄。
            using var current = Process.GetCurrentProcess();
            if (!DuplicateHandle(current.SafeHandle, current.SafeHandle, worker.SafeHandle, out nint parent,
                0x00100000 | 0x1000, false, 0)) throw new IOException("无法将主窗口的退出通知交给更新器。");
            ticket._commands.WriteLine(parent.ToInt64().ToString(CultureInfo.InvariantCulture));
            ticket._commands.WriteLine(Path.GetFileName(package.FilePath));
            ticket._commands.WriteLine(package.Hash);
            using var reply = new StreamReader(pipe, leaveOpen: true);
            if (await reply.ReadLineAsync(timeout.Token) != "READY")
                throw new IOException("更新器未能复核安装包或识别当前进程，尚未退出 SideStage。");
            return ticket;
        }
        catch { pipe.Dispose(); throw; }
    }

    /// <summary>仅在主窗口确实完成关闭时授权写入；工作进程还会等待原进程退出。</summary>
    internal void Commit() => _commands.WriteLine("INSTALL");

    /// <summary>关闭管道使未获授权的更新器自行结束。工作进程已获授权时则与主程序独立运行。</summary>
    public void Dispose()
    {
        try { _commands.Dispose(); }
        finally { _pipe.Dispose(); }
    }

    /// <summary>从内核查询管道服务端的进程标识，仅接受本次实际启动的更新器。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int process);

    /// <summary>在本次更新器的句柄表中创建限定访问权的引用，交给工作进程在结束时释放。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(SafeProcessHandle sourceProcess, SafeProcessHandle source,
        SafeProcessHandle destinationProcess, out nint destination, uint access, bool inherit, uint options);
}
