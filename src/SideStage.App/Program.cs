/*
 * 文件名称：Program.cs
 * 文件职责：
 *   启动子会话库的 Windows Forms 人工验收宿主。
 *
 * 运行约束：
 *   - 使用 STA 线程创建并运行 Windows RDP ActiveX 控件。
 *   - 不在启动时创建、连接、启用或注销任何会话。
 *   - 主窗口按用户的会话恢复偏好处理已连接子会话的远程断开通知。
 */

using System.Security.Principal;
using System.Windows.Forms;
using SideStage.Windows.Platform.Windows;

namespace SideStage.App;

/// <summary>
/// SideStage.App 的进程入口。
/// </summary>
internal static class Program
{
    internal const string DisplayName = "SideStage";

    /// <summary>为两个预览入口设置同一应用名和多尺寸窗口图标，窗体释放时归还图标资源。</summary>
    internal static void ApplyIdentity(Form window)
    {
        using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("SideStage.App.SideStage.ico")
            ?? throw new InvalidDataException($"缺少 {DisplayName} 应用图标。");
        Icon icon = new(stream);
        window.Icon = icon;
        window.Text = DisplayName;
        window.Disposed += (_, _) => icon.Dispose();
    }

    /// <summary>
    /// 用户启动时显示预览窗口。处理启用按钮发出的固定命令时，只返回系统操作结果。
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is [ChildSessionEnableCommand.Argument]) return ChildSessionEnableCommand.EnableOnce();
        string sid;
        using (var identity = WindowsIdentity.GetCurrent())
            sid = identity.User?.Value ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        if (args is ["--restore-startup-items"] or ["--restore-startup-items", "--silent"])
            return RestoreForUninstall(sid, args.Length == 1);
        if (args is ["--authorized-restore", var expected])
            return expected == sid && StartupElevation.IsAdministrator() ? new StartupRegistrations().RestoreAll() : 1;
        var route = StartupElevation.Choose(args, StartupElevation.IsAdministrator(), sid,
            () => StartupElevation.IsEnabled(StartupElevation.SettingsKey), StartupElevation.Request);
        if (route == StartupRoute.Exit) return 0;
        if (route == StartupRoute.DifferentAccount) return StartupElevation.DifferentAccountExitCode;
        ApplicationConfiguration.Initialize();
        using var startupBroker = new StartupSessionBroker();
        using var protectedBroker = new StartupSessionBroker(ProtectedStartupStore.Location);

        // 两种界面的默认布局不同，分别记住各自的主窗口大小。
        bool classic = args.Contains("--classic");
        using Form window = classic ? new ClassicPreviewForm() : new ParchmentPreviewForm(new WindowsChildSessionCredentialStore());
        WindowSizeMemory.Attach(window, @"Software\SideStage", classic ? "ClassicWindowSize" : "WindowSize");
        Application.Run(window);
        return 0;
    }

    /// <summary>仅在已有受保护规则且普通恢复未能完成时，通过 Windows 正常授权重试。静默卸载返回失败供上层处理。</summary>
    private static int RestoreForUninstall(string sid, bool interactive)
    {
        int code = new StartupRegistrations().RestoreAll();
        if (code == 0 || !interactive || StartupElevation.IsAdministrator() || !Directory.Exists(ProtectedStartupStore.Location)) return code;
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SideStage.exe"))
        { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--authorized-restore");
        start.ArgumentList.Add(sid);
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process == null) return 1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return 1; }
    }
}
