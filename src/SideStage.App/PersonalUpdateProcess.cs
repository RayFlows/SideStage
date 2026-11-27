/*
 * 文件职责：从当前窗口启动个人安装所用的独立更新器。管理员窗口直接办理交接，无需退出或再次授权。
 * 权限边界：缓存与安装目录属于桌面用户，Windows 在启动缓存内的程序之前就应用同一账户的普通令牌。
 */
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SideStage.App;

internal static class PersonalUpdateProcess
{
    /// <summary>仅传递本次随机管道名及主进程号。完整路径由调用方固定，工作进程不继承主窗口的任何句柄。</summary>
    internal static Process Start(string worker, string pipe)
    {
        string arguments = $"{Environment.ProcessId} {pipe}";
        string directory = Path.GetDirectoryName(worker)!;
        if (!StartupElevation.IsAdministrator())
            return Process.Start(new ProcessStartInfo(worker, arguments) { UseShellExecute = false,
                CreateNoWindow = true, WorkingDirectory = directory }) ?? throw new IOException("无法启动独立更新器。");

        using var identity = WindowsIdentity.GetCurrent();
        using var token = StartupUserAccess.CreatePersonalToken(identity);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder($"\"{worker}\" {arguments}");
        if (!CreateProcessAsUser(token, worker, command, 0, 0, false, 0x08000000, 0, directory, ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法为当前账户启动更新器。");
        using var process = new SafeProcessHandle(info.Process, true);
        using var thread = new SafeWaitHandle(info.Thread, true);
        var result = Process.GetProcessById(info.ProcessId);
        // 固定进程句柄，直到调用方完成对管道另一端身份的核对。
        _ = result.SafeHandle;
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, Columns, Rows, Fill, Flags;
        internal ushort Show, ReservedSize;
        internal nint ReservedData, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal nint Process, Thread; internal int ProcessId, ThreadId; }

    /// <summary>使用由调用方自身令牌派生的受限主令牌创建子进程。命令行缓冲区由 Windows 暂时改写。</summary>
    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder command,
        nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
}
