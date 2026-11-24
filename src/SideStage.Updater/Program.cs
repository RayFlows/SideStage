/*
 * 文件职责：从缓存目录运行，在校验安装包、取得原进程句柄并收到明确授权后，等待主程序退出。
 * 安装边界：仅调用当前用户的 Inno 安装包；不终止进程，不操作 RDP、凭据或 Windows 服务。
 * 失败处理：交接前的错误经管道显示在原窗口，交接后的错误由更新器显示，安装失败不启动新版。
 */
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SideStage.Updater;

internal static class Program
{
    /// <summary>与原窗口握手。原窗口崩溃、取消或关闭管道而没有提交 INSTALL，都不允许写入安装目录。</summary>
    private static int Main(string[] args)
    {
        bool committed = false;
        try
        {
            if (args is not [var pid, var name, var hash] || !int.TryParse(pid, CultureInfo.InvariantCulture, out int id) ||
                !Regex.IsMatch(name, @"\ASideStage-\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?-Setup-x64\.exe\z") ||
                !Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z") || !Console.IsInputRedirected || !Console.IsOutputRedirected) return 2;
            using var parent = Process.GetProcessById(id);
            string app = parent.MainModule?.FileName ?? throw new IOException("无法识别原程序。");
            if (!Path.GetFileName(app).Equals("SideStage.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新请求不是由 SideStage 主程序发起。");
            _ = parent.Handle; // 固定进程标识，不能误等复用同一个 PID 的其他进程。
            string directory = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string installer = Path.Combine(directory, name);
            // 将只读文件锁保持到安装结束，避免验证和执行之间有其他写入。
            using var file = File.OpenRead(installer);
            if (Convert.ToHexStringLower(SHA256.HashData(file)) != hash) throw new IOException("安装包复核失败，请重新下载。");
            Console.WriteLine("READY");
            Console.Out.Flush();
            if (Console.ReadLine() != "INSTALL") return 2;
            committed = true;
            if (!parent.WaitForExit(30000)) throw new IOException("原窗口没有退出，未开始安装。");
            string log = Path.Combine(directory, "install.log");
            var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = directory };
            foreach (string arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCLOSEAPPLICATIONS",
                "/SP-", "/SIDESTAGEUPDATE=1", $"/DIR={Path.GetDirectoryName(app)}", $"/LOG={log}" }) start.ArgumentList.Add(arg);
            using var setup = Process.Start(start) ?? throw new IOException("无法启动安装器。");
            setup.WaitForExit();
            if (setup.ExitCode != 0) throw new IOException($"安装未完成（退出码 {setup.ExitCode}）。请查看：\n{log}");
            using var restarted = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! });
            return 0;
        }
        catch (Exception ex)
        {
            if (committed) MessageBox(0, ex.Message, "SideStage 更新未完成", 0x10);
            else Console.WriteLine("ERROR");
            return 1;
        }
    }

    /// <summary>主窗口已退出时，使用系统错误提示展示可恢复的安装错误；不依赖正在替换的桌面运行库。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string title, uint flags);
}
