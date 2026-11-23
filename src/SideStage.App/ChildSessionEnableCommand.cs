/*
 * 文件名称：ChildSessionEnableCommand.cs
 * 文件职责：完成首页用户发起的 Windows 子会话启用请求。
 * 权限范围：先使用当前权限。Windows 要求管理员权限时，通过 UAC 启动同目录的宿主，
 *   使用固定命令执行一次启用操作并退出。原窗口继续用原有权限处理预览和凭据。
 */

using System.ComponentModel;
using System.Diagnostics;
using SideStage.Core.Platform.Windows;

namespace SideStage.App;

internal static class ChildSessionEnableCommand
{
    internal const string Argument = "--enable-child-sessions";

    /// <summary>尝试直接启用。权限不足时交给系统授权窗口，等待辅助进程并保留其 Win32 错误码。</summary>
    internal static async Task EnableAsync()
    {
        int result = EnableOnce();
        if (result is 5 or 740 or 1314)
        {
            // 使用本程序集对应的 apphost；开发运行与安装版本均调用 SideStage 自身，而不是 dotnet.exe。
            string host = typeof(ChildSessionEnableCommand).Assembly.GetName().Name + ".exe";
            ProcessStartInfo info = new(Path.Combine(AppContext.BaseDirectory, host))
            {
                UseShellExecute = true, Verb = "runas", Arguments = Argument,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using Process helper = Process.Start(info) ?? throw new InvalidOperationException("未能启动 Windows 授权窗口。");
            await helper.WaitForExitAsync();
            result = helper.ExitCode;
        }
        if (result != 0) throw new Win32Exception(result);
    }

    /// <summary>执行固定的系统开关操作。返回零表示请求成功，原窗口将独立读回系统状态。</summary>
    internal static int EnableOnce()
    {
        try
        {
            new WindowsChildSessionConfigurator().SetEnabled(true);
            return 0;
        }
        catch (Win32Exception ex) { return ex.NativeErrorCode; }
        catch (EntryPointNotFoundException) { return 127; }
        catch (DllNotFoundException) { return 126; }
    }
}
