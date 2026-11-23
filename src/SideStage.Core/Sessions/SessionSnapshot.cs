/*
 * 文件名称：SessionSnapshot.cs
 * 文件职责：
 *   定义一次会话观察结果，供日志、宿主 UI 和后续生命周期状态机使用。
 *
 * 设计边界：
 *   - 快照是某个时间点的事实，不承诺后续状态不变。
 *   - IsChildSession 只表示该快照来自子会话查询路径，不替代会话编号校验。
 *   - 本类型不保存密码、令牌或生产配置。
 */

namespace SideStage.Core.Sessions;

/// <summary>
/// 表示一次对 Windows 会话状态的不可变观察结果。
/// </summary>
/// <param name="Id">被观察的会话编号。</param>
/// <param name="ConnectionState">观察到的连接状态。</param>
/// <param name="IsChildSession">是否通过当前父会话的子会话查询得到。</param>
/// <param name="ObservedAtUtc">采集快照的 UTC 时间。</param>
public sealed record SessionSnapshot(
    SessionId Id,
    SessionConnectionState ConnectionState,
    bool IsChildSession,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>Windows 已分配编号但暂时无法查询连接状态时的原生错误码。</summary>
    public int? StateQueryError { get; init; }

    /// <summary>扩展查询失败后，基础连接状态查询返回的错误码。两次查询的诊断分别保留。</summary>
    public int? BasicStateQueryError { get; init; }

    /// <summary>
    /// 已经确认连接目标后，宿主可据此呈现 Windows 的人工登录画面。
    /// 在本机首次登录阶段，扩展查询可返回 1733，而基础查询只对等待登录的分身返回拒绝访问。
    /// 此结果不能用于启动进程、发送自动输入或截图。
    /// </summary>
    internal bool CanPresentSignIn => ConnectionState switch
    {
        SessionConnectionState.Connected or SessionConnectionState.ConnectQuery or SessionConnectionState.Init => true,
        SessionConnectionState.Active => IsLocked == true || (StateQueryError == 1733 && IsLocked is null),
        SessionConnectionState.Unknown => StateQueryError == 5 || (StateQueryError == 1733 && BasicStateQueryError == 5),
        _ => false,
    };

    /// <summary>Windows 返回的桌面锁定标志。无法确认时为空，不能据此放行自动操作。</summary>
    public bool? IsLocked { get; init; }

    /// <summary>会话已连接且桌面已解锁，此时才能尝试启动程序、输入或截图。</summary>
    public bool IsDesktopReady => ConnectionState == SessionConnectionState.Active && IsLocked == false;
}
