/*
 * 文件职责：在个人安装向导启动前处理 Windows 授权，并把向导的退出码交还给调用者。
 * 运行环境：使用 Windows 10/11 随附的 .NET Framework。安装负载嵌入本文件生成的 EXE。
 * 兼容约定：旧版更新器按普通权限静默覆盖个人安装，等待本入口退出后才重新打开主窗口。
 */
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("SideStage Setup")]
[assembly: AssemblyProduct("SideStage")]

internal static class SetupEntry
{
    private const string OwnerArgument = "/SideStageSetupOwner=";

    /// <summary>双击先请求当前账户的管理员令牌。取消授权即退出，任何安装文件都还没有写入。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        bool update = Array.Exists(args, a => string.Equals(a, "/SIDESTAGEUPDATE=1", StringComparison.OrdinalIgnoreCase))
            && Array.Exists(args, a => string.Equals(a, "/SILENT", StringComparison.OrdinalIgnoreCase));
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                string owner = OwnerArgument + identity.User.Value;
                if (args.Length > 0 && args[0].StartsWith(OwnerArgument, StringComparison.OrdinalIgnoreCase))
                {
                    if (!elevated || !string.Equals(args[0], owner, StringComparison.Ordinal))
                        throw new InvalidOperationException("请使用打开安装包的 Windows 账户批准授权。");
                    var forwarded = new string[args.Length - 1];
                    Array.Copy(args, 1, forwarded, 0, forwarded.Length);
                    args = forwarded;
                }
                else if (!elevated && !update)
                {
                    var request = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                        owner + " " + CommandLine(args)) { UseShellExecute = true, Verb = "runas" };
                    return RunAndWait(request);
                }
                return RunSetup(args, elevated);
            }
        }
        catch (Win32Exception ex)
        {
            if (ex.NativeErrorCode == 1223) return 1223;
            if (!update) MessageBox.Show(ex.Message, "SideStage 安装", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return ex.NativeErrorCode;
        }
        catch (Exception ex)
        {
            if (!update) MessageBox.Show(ex.Message, "SideStage 安装", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>只释放内嵌的固定向导。授权后使用受保护的系统程序目录暂存，普通更新使用本账户的临时目录。</summary>
    private static int RunSetup(string[] args, bool elevated)
    {
        string root = elevated ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : Path.GetTempPath();
        string stage = Path.Combine(root, "SideStage-Setup-" + Guid.NewGuid().ToString("N"));
        string setup = Path.Combine(stage, "Setup.exe");
        Directory.CreateDirectory(stage);
        try
        {
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("SideStage.InnoSetup"))
            using (var output = new FileStream(setup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                payload.CopyTo(output);
            return RunAndWait(new ProcessStartInfo(setup, CommandLine(args))
                { UseShellExecute = false, WorkingDirectory = stage });
        }
        finally
        {
            // 只移除本次创建的文件和空目录，清理失败不覆盖向导实际退出码。
            try { File.Delete(setup); Directory.Delete(stage); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>逐项执行 Windows 命令行的引号和反斜杠转义，保留含空格的安装目录与日志路径。</summary>
    private static string CommandLine(string[] args)
    {
        var command = new StringBuilder();
        foreach (string arg in args)
        {
            if (command.Length > 0) command.Append(' ');
            command.Append('"');
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                command.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                command.Append(c);
                slashes = 0;
            }
            command.Append('\\', slashes * 2).Append('"');
        }
        return command.ToString();
    }

    /// <summary>等待实际子进程完成，把取消、安装失败或成功的结果完整传回资源管理器或旧更新器。</summary>
    private static int RunAndWait(ProcessStartInfo start)
    {
        using (var child = Process.Start(start))
        {
            child.WaitForExit();
            return child.ExitCode;
        }
    }
}
