/*
 * 文件名称：WindowsProcessNativeMethods.cs
 * 文件职责：
 *   集中封装读取 Windows 进程会话编号和可执行文件路径所需的最小原生 API。
 *
 * 设计边界：
 *   - 这里只读进程身份，不创建、注入或修改进程。
 *   - 读取失败返回 null，由上层把“无法确认”视为失败而不是猜测成功。
 *   - 所有句柄都在本文件内关闭，避免启动核验过程泄漏内核资源。
 */

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SideStage.Windows.Processes;

/// <summary>
/// 提供 Windows 进程身份核验的原生调用。
/// </summary>
internal static class WindowsProcessNativeMethods
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// 读取进程的完整可执行文件路径；失败时返回 null。
    /// </summary>
    /// <param name="processId">目标进程编号。</param>
    /// <returns>规范化前的 Windows 进程路径，或 null。</returns>
    public static string? TryGetImagePath(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        IntPtr processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));

        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            StringBuilder pathBuffer = new(32768);
            uint pathLength = checked((uint)pathBuffer.Capacity);

            if (!QueryFullProcessImageName(
                    processHandle,
                    flags: 0,
                    pathBuffer,
                    ref pathLength))
            {
                return null;
            }

            return pathBuffer.ToString(0, checked((int)pathLength));
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    /// <summary>
    /// 打开具有受限查询权限的进程句柄。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    /// <summary>
    /// 查询进程的完整映像路径。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        uint flags,
        StringBuilder exeName,
        ref uint size);

    /// <summary>
    /// 关闭由 OpenProcess 返回的内核句柄。
    /// </summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
