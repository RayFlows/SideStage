/*
 * 文件职责：安装成功后，将主程序交回当前用户的 Windows 桌面打开。
 * 权限约定：个人安装器的受限权限只用于安装。新窗口从桌面正常启动，自行读取启动偏好并请求授权。
 * 账户边界：只向同一账户的普通桌面提交已核实的主程序路径。没有合适的桌面时，提示从开始菜单打开。
 */
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SideStage.App;

internal static class DesktopRestart
{
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{28C18EA6-CBC7-4C56-AFC2-A5307EAD9573}_is1";
    internal const string PendingValue = "SideStagePendingDesktopRestart";

    /// <summary>
    /// 只在本次成功安装登记的目录领取一次桌面重启请求。先消费再打开，避免桌面启动的第二个进程循环重启。
    /// 旧版更新器同样会无参数启动新版，因而这条路径从首次升级便生效。
    /// </summary>
    internal static bool TryOpenPending(string registration, string app, WindowsIdentity caller)
    {
        using (var read = Registry.CurrentUser.OpenSubKey(registration))
        {
            if (read?.GetValue(PendingValue) is not 1 || read.GetValue("InstallLocation") is not string location ||
                !string.Equals(Path.TrimEndingDirectorySeparator(location), Path.GetDirectoryName(app), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(app), "SideStage.exe", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        using var key = Registry.CurrentUser.OpenSubKey(registration, writable: true)
            ?? throw new IOException("无法读取更新后的安装记录。");
        try { key.DeleteValue(PendingValue, throwOnMissingValue: true); }
        catch (ArgumentException) { return false; } // 同时打开的另一个窗口已经领取了请求。
        Open(app, caller);
        return true;
    }

    /// <summary>核对当前桌面的账户和权限，再把安装目录中的主程序作为单个路径参数交给系统资源管理器。</summary>
    private static void Open(string app, WindowsIdentity caller)
    {
        nint desktop = GetShellWindow();
        if (desktop == 0 || GetWindowThreadProcessId(desktop, out uint processId) == 0)
            throw new IOException("未找到当前桌面的资源管理器。");
        const uint queryLimitedInformation = 0x1000, queryToken = 0x0008;
        using var process = OpenProcess(queryLimitedInformation, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, queryToken, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        {
            // TokenElevation 只需查询权限，不需要复制资源管理器的令牌。
            const int tokenElevation = 20;
            if (!GetTokenInformation(token, tokenElevation, out int elevated, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (caller.User is null || owner.User != caller.User || elevated != 0)
                throw new IOException("当前桌面无法按此用户的普通启动流程打开应用。");
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var open = new ProcessStartInfo(Path.Combine(windows, "explorer.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(app)!
        };
        open.ArgumentList.Add(app);
        using var request = Process.Start(open) ?? throw new IOException("Windows 没有接受启动请求。");
    }

    /// <summary>查找本次交互桌面的 Shell 窗口，不枚举其他登录会话。</summary>
    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    /// <summary>取得桌面窗口的进程号，供读取其账户身份。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>仅以查询权限打开桌面进程。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    /// <summary>只读取桌面令牌，不复制令牌或用它创建进程。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    /// <summary>读取桌面令牌的实际提升状态，用于执行前核对启动环境。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int elevation, int size, out int length);
}
