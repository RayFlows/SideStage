/*
 * 文件名称：Program.cs
 * 文件职责：
 *   启动桌面分身库的 Windows Forms 人工验收宿主。
 *
 * 运行约束：
 *   - 使用 STA 线程创建并运行 Windows RDP ActiveX 控件。
 *   - 不在启动时创建、连接、启用或注销任何会话。
 *   - 主窗口按用户的会话恢复偏好处理已连接分身的远程断开通知。
 */

using System.Windows.Forms;

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
        ApplicationConfiguration.Initialize();

        // 两种界面的默认布局不同，分别记住各自的主窗口大小。
        bool classic = args.Length > 0 && args[0] == "--classic";
        using Form window = classic ? new ClassicPreviewForm() : new ParchmentPreviewForm();
        WindowSizeMemory.Attach(window, @"Software\SideStage", classic ? "ClassicWindowSize" : "WindowSize");
        Application.Run(window);
        return 0;
    }
}
