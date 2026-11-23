/*
 * 文件名称：Program.cs
 * 文件职责：
 *   启动桌面分身库的 Windows Forms 人工验收宿主。
 *
 * 运行约束：
 *   - 使用 STA 线程创建并运行 Windows RDP ActiveX 控件。
 *   - 不在启动时创建、连接、启用或注销任何会话。
 *   - 所有会改变会话状态的操作都必须由用户在窗口中明确触发。
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

        // 使用 --classic 参数可以启动旧版界面
        if (args.Length > 0 && args[0] == "--classic")
        {
            Application.Run(new ClassicPreviewForm());
        }
        else
        {
            Application.Run(new ParchmentPreviewForm());
        }
        return 0;
    }
}
