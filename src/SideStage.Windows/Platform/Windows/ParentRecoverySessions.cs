/*
 * 文件名称：ParentRecoverySessions.cs
 * 文件职责：给一次父桌面恢复提供同账户、原分身和物理控制台的核验，以及一次 Windows 桌面切换。
 * 安全边界：只接受本进程的会话作为源，目标必须是已取样的空控制台编号。读取失败即停止。
 * 权限约定：使用进程现有权限，由 Windows 决定能否查询和接回，不保存密码或请求后台提权。
 */
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SideStage.Core.Platform.Windows;
using SideStage.Core.Sessions;

namespace SideStage.Windows.Platform.Windows;

internal sealed class ParentRecoverySessions(SessionId child)
{
    private readonly WindowsSessionInspector _inspector = new();

    /// <summary>从同一批 WTS 枚举取得账户与占用，再核对关联分身及物理控制台编号。</summary>
    internal uint RequireIdleConsole()
    {
        using var identity = WindowsIdentity.GetCurrent();
        SessionSnapshot parent = _inspector.GetCurrentSession();
        if (parent.ConnectionState != SessionConnectionState.Disconnected || !OriginalChildReady())
            throw new InvalidOperationException("父桌面未断开，或原来的分身已变化或锁定。");
        uint console = SelectConsole(ReadSessions(), identity.Name, parent.Id.Value, child.Value, WTSGetActiveConsoleSessionId());
        // 枚举中的空用户名可能是权限不足；再按编号查询，只有成功且明确为空才视为无人占用。
        if (!WTSQuerySessionInformationW(0, console, 5, out var user, out uint bytes))
            throw LastWindowsError();
        try
        {
            if (user == 0 || bytes < sizeof(char) || Marshal.ReadInt16(user) != 0)
                throw new InvalidOperationException("未能确认物理控制台无人登录。");
        }
        finally { if (user != 0) WTSFreeMemory(user); }
        return console;
    }

    /// <summary>
    /// 用完整域和账户名核对父子归属，只允许已连接但还没有用户的物理控制台。
    /// 活动、正在连接或被远程控制的第三方会话会使本次恢复退出。
    /// </summary>
    internal static uint SelectConsole(IReadOnlyList<Station> stations, string account,
        uint parent, uint childId, uint physicalConsole)
    {
        Station? source = stations.SingleOrDefault(s => s.Id == parent);
        Station? target = stations.SingleOrDefault(s => s.Id == childId);
        Station? console = stations.SingleOrDefault(s => s.Id == physicalConsole);
        if (parent == 0 || childId == 0 || parent == childId || source is not { State: 4 } ||
            target is not { State: 0 } || !source.BelongsTo(account) || !target.BelongsTo(account))
            throw new InvalidOperationException("未能确认父桌面和活动分身均属于当前账户。");
        if (physicalConsole is 0 or uint.MaxValue || console is not { State: 1 } || console.Id == childId ||
            !string.Equals(console.Name, "console", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(console.User))
            throw new InvalidOperationException("本机控制台已有用户或状态未知。");
        if (stations.Any(s => s.Id != 0 && s.Id != childId && s.Id != physicalConsole && s.State is >= 0 and <= 3))
            throw new InvalidOperationException("还有其他活动或正在连接的会话。");
        return physicalConsole;
    }

    /// <summary>把本进程自己的会话接到已核验编号的控制台。让 Windows 按当前身份校验权限。</summary>
    internal void ConnectOwnSession(uint console)
    {
        using Process own = Process.GetCurrentProcess();
        if (!WTSConnectSessionW((uint)own.SessionId, console, string.Empty, false))
            throw LastWindowsError();
    }

    /// <summary>复检输入前后都核对物理控制台已归还给当前进程，并且原来的分身仍可操作。</summary>
    internal bool IsRestored()
    {
        SessionSnapshot parent = _inspector.GetCurrentSession();
        return parent.ConnectionState == SessionConnectionState.Active
            && WTSGetActiveConsoleSessionId() == parent.Id.Value && OriginalChildReady();
    }

    /// <summary>从本进程的父会话重新向 Windows 查询关联分身，排除刚注销或新建的目标。</summary>
    private bool OriginalChildReady() =>
        _inspector.TryGetChildSession() is { IsChildSession: true, IsDesktopReady: true } current && current.Id == child;

    /// <summary>在释放原生数组前复制需要的站名、账户和连接状态。没有登录用户时账户字段为空。</summary>
    private static List<Station> ReadSessions()
    {
        uint level = 1;
        if (!WTSEnumerateSessionsExW(0, ref level, 0, out var data, out uint count))
            throw LastWindowsError();
        try
        {
            List<Station> result = [];
            int size = Marshal.SizeOf<StationInfo>();
            for (int i = 0; i < checked((int)count); i++)
            {
                var row = Marshal.PtrToStructure<StationInfo>(data + checked(i * size));
                result.Add(new(row.Id, row.State, Marshal.PtrToStringUni(row.Name),
                    Marshal.PtrToStringUni(row.Domain), Marshal.PtrToStringUni(row.User)));
            }
            return result;
        }
        finally { if (data != 0) WTSFreeMemoryExW(2, data, count); }
    }

    /// <summary>保留失败的 Win32 错误码，权限不足时给出可操作的提示。</summary>
    private static Win32Exception LastWindowsError()
    {
        int code = Marshal.GetLastPInvokeError();
        var error = new Win32Exception(code);
        string hint = code is 5 or 1314 or 7045 ? "可尝试以管理员身份启动 SideStage。" : "";
        return new Win32Exception(code, $"Windows {code}：{error.Message}{hint}");
    }

    /// <summary>从 WTS_SESSION_INFO_1W 复制的单个会话，State 保留系统的 WTS_CONNECTSTATE_CLASS 数值。</summary>
    internal sealed record Station(uint Id, int State, string? Name, string? Domain, string? User)
    {
        /// <summary>拒绝仅凭相同短用户名认领另一个域的会话。</summary>
        internal bool BelongsTo(string account) => !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(Domain)
            && string.Equals($"{Domain}\\{User}", account, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>WTS_SESSION_INFO_1W 的原生布局，字符串属于枚举返回的缓冲区。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StationInfo
    {
        internal uint Environment;
        internal int State;
        internal uint Id;
        internal nint Name, Host, User, Domain, Farm;
    }

    /// <summary>获取当前物理控制台编号，切换中的无效值不能作为目的地。</summary>
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    /// <summary>枚举本机的扩展会话资料。</summary>
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool WTSEnumerateSessionsExW(nint server, ref uint level, uint filter, out nint data, out uint count);
    /// <summary>按扩展枚举对应的类型和数量释放缓冲区。</summary>
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern bool WTSFreeMemoryExW(int type, nint data, uint count);
    /// <summary>单独查询控制台的用户名，区分查询失败和真正的空值。</summary>
    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint session, int info, out nint buffer, out uint bytes);
    /// <summary>释放单项查询返回的缓冲区。</summary>
    [DllImport("wtsapi32.dll", ExactSpelling = true)] private static extern void WTSFreeMemory(nint buffer);
    /// <summary>向系统提交同一账户的桌面切换请求，按已核对的目标编号路由显示。</summary>
    [DllImport("wtsapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSConnectSessionW(uint source, uint destination, string password, bool wait);
}
