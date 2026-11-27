/*
 * 文件名称：StartupElevation.cs
 * 文件职责：保存当前用户的启动权限偏好，并在普通启动器与经 UAC 授权的主窗口之间分派启动请求。
 * 交接约定：启动器只等待自己创建的进程，仅在取消授权或账户不匹配时继续显示主窗口。
 */
using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace SideStage.App;

internal enum StartupRoute { Home, Exit, DifferentAccount }

internal static class StartupElevation
{
    internal const string SettingsKey = @"Software\SideStage";
    private const string Preference = "RequestAdministratorOnStartup";
    private const string AuthorizedArgument = "--authorized-window";
    internal const int DifferentAccountExitCode = 21842;

    /// <summary>只检查当前进程有效的管理员角色，能区分管理员账户的普通令牌与已授权令牌。</summary>
    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>首次启动默认请求授权。注册表不可读或值已损坏时，允许普通启动。</summary>
    internal static bool IsEnabled(string path, Action<string>? report = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            object? value = key?.GetValue(Preference);
            if (value is null or 1) return true;
            if (value is not 0) report?.Invoke("启动权限设置值无效，本次以普通权限运行。");
            return false;
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            report?.Invoke($"无法读取启动权限设置，本次以普通权限运行。{ex.Message}");
            return false;
        }
    }

    /// <summary>只写当前用户的下次启动偏好，保留当前窗口和子会话。</summary>
    internal static void SetEnabled(string path, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(Preference, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 每次普通启动最多请求一次 UAC。返回的管理员进程必须仍属原用户，避免打开另一个账户的凭据或桌面。
    /// 单次普通启动和维护入口跳过授权，已授权窗口直接处理更新。
    /// </summary>
    internal static StartupRoute Choose(string[] args, bool elevated, string sid, Func<bool> enabled, Func<bool, string, int> request)
    {
        if (args is [AuthorizedArgument, var user, ..])
        {
            if (user != sid || !elevated) return StartupRoute.DifferentAccount;
            return StartupRoute.Home;
        }
        bool classic = args is ["--classic"];
        if (elevated || (args.Length != 0 && !classic) || !enabled()) return StartupRoute.Home;
        return request(classic, sid) == DifferentAccountExitCode ? StartupRoute.Home : StartupRoute.Exit;
    }

    /// <summary>通过 Windows 授权窗口启动同目录的固定宿主。取消或启动失败时原进程继续提供普通权限界面。</summary>
    internal static int Request(bool classic, string sid, Action<string> report)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SideStage.exe"))
        {
            UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory,
        };
        info.ArgumentList.Add(AuthorizedArgument);
        info.ArgumentList.Add(sid);
        if (classic) info.ArgumentList.Add("--classic");
        info.ArgumentList.Add($"--waiting-launcher={Environment.ProcessId}");
        Process? child;
        try { child = Process.Start(info); }
        catch (Win32Exception ex)
        {
            report(ex.NativeErrorCode == 1223
                ? "已取消 Windows 管理员授权，本次以普通权限运行。"
                : $"Windows 未能启动管理员窗口（错误 {ex.NativeErrorCode}）：{ex.Message}。本次以普通权限运行。");
            return DifferentAccountExitCode;
        }
        if (child is null)
        {
            report("Windows 没有返回管理员窗口进程，本次以普通权限运行。");
            return DifferentAccountExitCode;
        }
        using (child)
        {
            child.WaitForExit();
            if (child.ExitCode == DifferentAccountExitCode)
                report("授权窗口未取得当前用户的管理员权限，本次以普通权限运行。");
            return child.ExitCode;
        }
    }
}
