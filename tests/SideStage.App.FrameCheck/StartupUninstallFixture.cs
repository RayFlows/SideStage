/*
 * 文件职责：为安装验收准备一条在分身中关闭的独立 Run 登记，由同一个卸载恢复实现还原它。
 * 隔离范围：所有位置都从验收 GUID 派生，只操作本次试装的登记、程序副本和备份。
 */
using Microsoft.Win32;

namespace SideStage.App.FrameCheck;

internal static class StartupUninstallFixture
{
    /// <summary>分别在卸载前、卸载中和卸载后运行。最后核对类型和原命令，并清理专属测试位置。</summary>
    internal static int Run(string action, Guid id)
    {
        string root = Path.Combine(Path.GetTempPath(), "SideStage.UninstallFixture", id.ToString("N"));
        string key = @"Software\SideStage.Tests\" + id.ToString("N");
        string gate = Path.Combine(root, "Gate");
        string target = Path.Combine(root, "fixture.exe");
        string command = $"\"{target}\" /?";
        var rules = new StartupRegistrations(key, gate, Path.Combine(root, "Startup"), Path.Combine(root, "Common"));
        switch (action)
        {
            case "prepare":
                if (Directory.Exists(root)) throw new IOException("临时启动目录已被占用。");
                Directory.CreateDirectory(root);
                File.Copy(Path.Combine(Environment.SystemDirectory, "sort.exe"), target);
                using (var run = Registry.CurrentUser.CreateSubKey(key + @"\Run"))
                    run.SetValue("试装启动项", command, RegistryValueKind.ExpandString);
                return rules.SetAllowed(rules.ReadEntries().Single(), false).Blocked ? 0 : 1;
            case "restore":
                return rules.RestoreAll();
            case "verify":
                using (var run = Registry.CurrentUser.OpenSubKey(key + @"\Run"))
                {
                    if (run?.GetValue("试装启动项", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != command
                        || run.GetValueKind("试装启动项") != RegistryValueKind.ExpandString) return 1;
                }
                Registry.CurrentUser.DeleteSubKeyTree(key);
                Directory.Delete(root, recursive: true);
                return 0;
            default: return 2;
        }
    }
}
