/*
 * 文件职责：安装前向 Windows Restart Manager 查询这份安装中仍被占用的程序文件。
 * 使用边界：仅查询占用者。当前窗口及随它退出的授权启动器可以继续，其他进程由用户处理。
 */
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SideStage.App;

internal static class UpdateFileUse
{
    /// <summary>只登记安装目录里的实际文件。占用列表变化时重新取样，释放查询会话后仍由安装器作最终检查。</summary>
    internal static void EnsureAvailable(string directory, int waitingLauncher)
    {
        const int moreData = 234;
        string[] files = new[] { "SideStage.exe", "SideStage.SessionAgent.exe", "SideStage.Updater.exe" }
            .Select(name => Path.Combine(directory, name)).Where(File.Exists).ToArray();
        Check(RmStartSession(out uint session, 0, new StringBuilder(33)));
        try
        {
            Check(RmRegisterResources(session, (uint)files.Length, files, 0, 0, 0, 0));
            ProcessInfo[]? owners = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                uint count = (uint)(owners?.Length ?? 0);
                int error = RmGetList(session, out uint needed, ref count, owners, out _);
                if (error == moreData) { owners = new ProcessInfo[needed]; continue; }
                Check(error);
                var others = (owners ?? []).Take((int)count).Select(p => p.ProcessId)
                    .Where(pid => pid != Environment.ProcessId && pid != waitingLauncher).Distinct().ToArray();
                if (others.Length != 0)
                    throw new IOException($"此安装仍被其他进程使用（PID {string.Join("、", others)}）。请关闭其他 SideStage 窗口或等待相关任务结束，再点「安装并重启」。如需保留子会话，请先断开连接再关闭窗口。");
                return;
            }
            throw new IOException("安装目录的占用情况正在变化，请稍后再点「安装并重启」。");
        }
        finally { RmEndSession(session); }
    }

    /// <summary>把 Windows 查询失败和确有占用分开报告，保留原窗口供用户重试。</summary>
    private static void Check(int error)
    {
        if (error != 0) throw new IOException($"无法检查安装文件占用，请重试。{new Win32Exception(error).Message}");
    }

    // 固定布局与 RM_PROCESS_INFO 一致，前三个整数是进程号及创建时间 FILETIME。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        internal int ProcessId;
        private System.Runtime.InteropServices.ComTypes.FILETIME _start;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] private string _app;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] private string _service;
        private uint _type, _status, _session;
        private int _restartable;
    }

    /// <summary>开始只读查询，系统返回本轮会话句柄及唯一标识。</summary>
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, uint flags, [Out] StringBuilder key);

    /// <summary>按绝对路径登记待替换文件，其他安装目录的同名程序不计入占用。</summary>
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint count, string[] files, uint apps, nint processes, uint services, nint names);

    /// <summary>先查询记录数，再使用对应大小的缓冲区读取进程列表。</summary>
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? owners, out uint reasons);

    /// <summary>结束查询并释放 Restart Manager 的资源。</summary>
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmEndSession(uint session);
}
