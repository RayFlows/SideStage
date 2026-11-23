/*
 * 文件名称：ChildProcessSnapshot.cs
 * 文件职责：
 *   描述某个受管进程在单一观察时刻的身份、归属和运行状态。
 *
 * 设计边界：
 *   - 快照只表示采集时刻的事实，后续状态必须重新查询管理句柄。
 *   - SessionId 用于验证进程仍属于启动时确认的子会话。
 *   - 退出码仅在进程已退出后有值；不把“进程仍存活”解释为业务进度。
 */

using SideStage.Core.Sessions;

namespace SideStage.Core.Processes;

/// <summary>
/// 表示受管进程的一次不可变状态观察。
/// </summary>
/// <param name="ProcessId">Windows 进程编号。</param>
/// <param name="SessionId">启动时确认的子会话编号。</param>
/// <param name="ExecutablePath">启动请求中的规范化可执行文件路径。</param>
/// <param name="StartedAtUtc">Windows 报告的进程启动时间。</param>
/// <param name="State">观察到的进程状态。</param>
/// <param name="ExitCode">进程退出码；进程仍运行时为 null。</param>
/// <param name="ObservedAtUtc">采集此快照的 UTC 时间。</param>
public sealed record ChildProcessSnapshot(
    int ProcessId,
    SessionId SessionId,
    string ExecutablePath,
    DateTimeOffset StartedAtUtc,
    ChildProcessState State,
    int? ExitCode,
    DateTimeOffset ObservedAtUtc);
