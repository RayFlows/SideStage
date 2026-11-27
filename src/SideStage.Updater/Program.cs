/*
 * 文件职责：从缓存目录运行，在校验安装包、取得原进程句柄并收到明确授权后，等待主程序退出。
 * 安装边界：仅调用当前用户的 Inno 安装包；不终止进程，不操作 RDP、凭据或 Windows 服务。
 * 失败处理：交接前的错误经管道显示在原窗口，交接后的错误由更新器显示，安装失败不启动新版。
 */
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SideStage.Updater;

internal static class Program
{
    /// <summary>与原窗口握手。原窗口崩溃、取消或关闭管道而没有提交 INSTALL，都不允许写入安装目录。</summary>
    private static int Main(string[] args)
    {
        bool committed = false;
        bool installed = false;
        try
        {
            if (args is not [var pid, var pipeName] || !int.TryParse(pid, CultureInfo.InvariantCulture, out int id) ||
                !Regex.IsMatch(pipeName, @"\ASideStage\.Update\.[0-9a-f]{32}\z")) return 2;
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out int client) || client != id) return 2;
            using var input = new StreamReader(pipe, leaveOpen: true);
            using var output = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            if (!long.TryParse(input.ReadLine(), CultureInfo.InvariantCulture, out long handle) || handle <= 0) return 2;
            using var parent = new SafeProcessHandle((nint)handle, true);
            var path = new StringBuilder(32768);
            int size = path.Capacity;
            if (GetProcessId(parent) != id || !QueryFullProcessImageName(parent, 0, path, ref size))
                throw new IOException("无法识别原程序。");
            string app = path.ToString();
            if (!Path.GetFileName(app).Equals("SideStage.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新请求不是由 SideStage 主程序发起。");
            string name = input.ReadLine() ?? "", hash = input.ReadLine() ?? "";
            if (!Regex.IsMatch(name, @"\ASideStage-\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?-Setup-x64\.exe\z") ||
                !Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z")) return 2;
            string directory = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string installer = Path.Combine(directory, name);
            // 将只读文件锁保持到安装结束，避免验证和执行之间有其他写入。
            using var file = File.OpenRead(installer);
            if (Convert.ToHexStringLower(SHA256.HashData(file)) != hash) throw new IOException("安装包复核失败，请重新下载。");
            output.WriteLine("READY");
            if (input.ReadLine() != "INSTALL") return 2;
            committed = true;
            if (WaitForSingleObject(parent, 30000) != 0) throw new IOException("原窗口没有退出，未开始安装。");
            string log = Path.Combine(directory, "install.log");
            var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = directory };
            // 已在主窗口确认安装：跳过向导选择页，只显示真实的文件安装进度，不再弹出第二次确认。
            foreach (string arg in new[] { "/SILENT", "/NOCANCEL", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS",
                "/SP-", "/SIDESTAGEUPDATE=1", $"/DIR={Path.GetDirectoryName(app)}", $"/LOG={log}" }) start.ArgumentList.Add(arg);
            using var setup = Process.Start(start) ?? throw new IOException("无法启动安装器。");
            setup.WaitForExit();
            if (setup.ExitCode != 0) throw new IOException($"安装未完成（退出码 {setup.ExitCode}）。请查看：\n{log}");
            installed = true;
            // 新版入口会领取安装器留下的一次性桌面启动请求；旧版更新器也能以相同方式交接。
            using var restarted = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! })
                ?? throw new IOException("Windows 没有接受启动请求。");
            return 0;
        }
        catch (Exception ex)
        {
            if (installed)
            {
                // 原窗口已经退出，将启动失败原因保存在本次更新目录，方便从安装日志旁找到。
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "restart-error.log"), ex.Message); }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
                MessageBox(0, $"新版已安装。请从开始菜单打开 SideStage。\n\n{ex.Message}", "SideStage 未能自动重启", 0x30);
            }
            else if (committed) MessageBox(0, ex.Message, "SideStage 更新未完成", 0x10);
            return 1;
        }
    }

    /// <summary>核对收到的受限句柄属于正在与更新器通信的主程序。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetProcessId(SafeProcessHandle process);

    /// <summary>用有限查询权限读取可执行文件路径，不读取主窗口的进程内存。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int count);

    /// <summary>核对请求者就是已打开句柄的主窗口进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int pid);

    /// <summary>等待主程序释放安装目录中的文件后才开始安装。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    /// <summary>主窗口已退出时，使用系统错误提示展示可恢复的安装错误；不依赖正在替换的桌面运行库。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string title, uint flags);
}
