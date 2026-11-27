/*
 * 文件职责：为侧栏的批量开关制定变更清单，逐项记录回读结果、失败原因和待确认的账户修复。
 * 计数约定：已在目标状态和只读项目计入跳过。只有从 Windows 回读成功才计入成功。
 */
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml;

namespace SideStage.App;

internal sealed class StartupRuleBatch
{
    internal sealed record Repair(StartupEntry Entry, string Previous, string Current)
    {
        /// <summary>确认窗口展示完整任务路径及两端账户，便于区分同名应用的不同入口。</summary>
        public override string ToString() => $"{Entry.Detail.TrimEnd('\\')}\\{Entry.Name}   {Previous} → {Current}";
    }

    internal StartupEntry[] Candidates { get; }
    internal List<StartupEntry> Changed { get; } = [];
    internal List<string> Failures { get; } = [];
    internal List<Repair> Repairs { get; } = [];
    internal int Skipped { get; private set; }
    private readonly bool _allowed;

    /// <summary>根据同一份清单计算确认前的影响范围，实际写入仍须逐项核验。</summary>
    internal StartupRuleBatch(IEnumerable<StartupEntry> items, bool allowed)
    {
        _allowed = allowed;
        var rows = items.ToArray();
        Candidates = rows.Where(item => item.Blocked == allowed && item.Fingerprint != null &&
            (allowed || item.ReadOnlyReason == null)).ToArray();
        Skipped = rows.Length - Candidates.Length;
    }

    /// <summary>首次执行暂存需要修复的项目，其他项目即使相邻项目失败也继续处理。</summary>
    internal void Apply(StartupRegistrations registrations)
    {
        foreach (var item in Candidates) Change(registrations, item, null, collectRepairs: true);
    }

    /// <summary>仅对明确勾选的任务重试，未勾选及取消修复均算跳过。</summary>
    internal void FinishRepairs(StartupRegistrations registrations, IReadOnlySet<Repair> approved)
    {
        foreach (var repair in Repairs)
        {
            if (approved.Contains(repair)) Change(registrations, repair.Entry, repair.Previous, collectRepairs: false);
            else Skipped++;
        }
    }

    /// <summary>与单项开关共享写入和回读。失败信息只记录任务名和系统原因。</summary>
    private void Change(StartupRegistrations registrations, StartupEntry item, string? previous, bool collectRepairs)
    {
        try { Changed.Add(registrations.SetAllowed(item, _allowed, previous)); }
        catch (StartupTriggerRepairRequired repair) when (collectRepairs)
        { Repairs.Add(new(item, repair.Previous, repair.Current)); }
        catch (Exception error) when (Recoverable(error))
        { Failures.Add($"{item.Detail}\\{item.Name}：{error.Message}"); }
    }

    /// <summary>隔离可归因于单个登记、备份或系统访问的故障，进程级错误照常传播。</summary>
    internal static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException
        or SecurityException or COMException or Win32Exception or ArgumentException or XmlException or InvalidDataException;
}
