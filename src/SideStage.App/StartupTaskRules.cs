/*
 * 文件职责：读取并调整当前用户普通权限的登录计划任务，只替换一个明确的 EXE 动作。
 * 保留内容：重新读取任务定义，保留触发器、工作目录、条件和权限。任务由原账户运行，
 * 代理在放行时等待应用结束并返回退出码。系统目录、多动作、其他账户及提权任务只读。
 */
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using SideStage.StartupGate;

namespace SideStage.App;

internal sealed class StartupTaskRules : IDisposable
{
    private readonly dynamic _service;
    private readonly string _sid;
    private readonly string _account;
    private readonly string _directory;
    private string Gate => Path.Combine(_directory, "SideStage.StartupGate.exe");
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>连接本机计划任务服务。只接受当前 SID 或当前完整账户名。</summary>
    internal StartupTaskRules(string directory)
    {
        _directory = directory;
        using var user = WindowsIdentity.GetCurrent();
        _sid = user.User!.Value;
        _account = user.Name;
        _service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        _service.Connect();
    }

    /// <summary>按现有清单中的完整路径取实时定义，无法查询的登记保留只读状态。</summary>
    internal StartupEntry Inspect(StartupEntry seed)
    {
        try { return WithTask(seed, (_, task) => Describe(seed, XDocument.Parse((string)task.Xml), (bool)task.Enabled)); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return seed with { Fingerprint = null, WindowsEnabled = null, RepeatPolicy = StartupRepeatPolicy.Unknown,
                ReadOnlyReason = IsProtected(seed.Detail) ? MaintenanceReason(seed.Detail) : "无法读取任务定义" };
        }
    }

    /// <summary>只有单次登录触发、单个普通 EXE 动作、当前交互用户三者同时成立时才允许托管。</summary>
    internal StartupEntry Describe(StartupEntry seed, XDocument doc, bool enabled)
    {
        var root = doc.Root!;
        var triggers = root.Element(Ns + "Triggers")?.Elements().ToArray() ?? [];
        var logonTriggers = triggers.Where(trigger => trigger.Name == Ns + "LogonTrigger").ToArray();
        // 一个任务可有多个登录触发器，其中任意一个启用即可。任务总开关优先。
        bool loginEnabled = enabled && logonTriggers.Any(trigger => (bool?)trigger.Element(Ns + "Enabled") != false);
        var policy = (string?)root.Element(Ns + "Settings")?.Element(Ns + "MultipleInstancesPolicy") switch
        {
            null or "IgnoreNew" => StartupRepeatPolicy.IgnoreNew,
            "Parallel" => StartupRepeatPolicy.Parallel,
            "Queue" => StartupRepeatPolicy.Queue,
            "StopExisting" => StartupRepeatPolicy.StopExisting,
            _ => StartupRepeatPolicy.Unknown
        };
        seed = seed with { WindowsEnabled = loginEnabled, RepeatPolicy = policy };
        if (IsProtected(seed.Detail))
            return seed with { ReadOnlyReason = MaintenanceReason(seed.Detail), Fingerprint = null, Blocked = false };
        var principals = root.Element(Ns + "Principals")?.Elements().ToArray() ?? [];
        var principal = principals.Length == 1 ? principals[0] : null;
        var actions = root.Element(Ns + "Actions")?.Elements().ToArray() ?? [];
        var exec = actions.Length == 1 && actions[0].Name == Ns + "Exec" ? actions[0] : null;
        var user = (string?)principal?.Element(Ns + "UserId");
        string? reason = (string?)principal?.Element(Ns + "RunLevel") == "HighestAvailable" ? "管理员权限任务"
            : !string.Equals(user, _sid, StringComparison.OrdinalIgnoreCase) && !string.Equals(user, _account, StringComparison.OrdinalIgnoreCase)
                || (string?)principal?.Element(Ns + "LogonType") != "InteractiveToken" ? "其他账户或共享任务"
            : triggers.Length != 1 || triggers[0].Name != Ns + "LogonTrigger" || exec == null ? "复合计划任务" : null;
        string target = ((string?)exec?.Element(Ns + "Command") ?? "").Trim('"');
        string args = (string?)exec?.Element(Ns + "Arguments") ?? "";
        bool managed = reason == null && target.Equals(Gate, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(args, "N", out _);
        if (managed)
        {
            var backup = ReadBackup(args, seed);
            if (backup == null) return seed with { Blocked = true, Fingerprint = null, ReadOnlyReason = "原始任务动作备份缺失" };
            target = ((string?)backup.Element(Ns + "Command") ?? "").Trim('"');
        }
        // Windows 在启动代理前已处理旧实例。已托管的旧登记仍允许还原原动作。
        if (policy == StartupRepeatPolicy.StopExisting)
            reason ??= "再次触发会先停止旧任务";
        if (policy == StartupRepeatPolicy.Unknown) reason ??= "无法识别任务的重复触发规则";
        target = Environment.ExpandEnvironmentVariables(target);
        reason ??= StartupRegistrations.TargetReason(target, Dispatcher.LocalPath(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (exec?.Value.Contains("$(", StringComparison.Ordinal) == true) reason ??= "运行时传参任务";
        if (!loginEnabled) reason ??= "登录入口已由 Windows 禁用";
        return seed with { IconPath = Dispatcher.LocalPath(target) ? target : seed.IconPath, Blocked = managed, ReadOnlyReason = reason,
            Fingerprint = StartupRegistrations.Hash(doc.ToString(SaveOptions.DisableFormatting)),
            DisplayName = reason == null && seed.Name.Length > 20 ? StartupRegistrations.ProductName(target) : null };
    }

    /// <summary>提交前再次核验完整定义，使用仅更新标志并保留已有权限，不重新触发登记事件。</summary>
    internal StartupEntry Change(StartupEntry snapshot, bool allowed, Func<string> prepare)
    {
        return WithTask(snapshot, (folder, task) =>
        {
            string raw = task.Xml;
            var doc = XDocument.Parse(raw);
            var live = Describe(snapshot, doc, (bool)task.Enabled);
            StartupRegistrations.Verify(snapshot, live, allowed);
            var action = doc.Root!.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
            string id;
            if (allowed)
            {
                id = action.Element(Ns + "Arguments")!.Value;
                action.ReplaceWith(ReadBackup(id, snapshot) ?? throw new IOException("原始任务动作备份缺失。"));
            }
            else
            {
                id = prepare();
                var original = new XElement("Original", new XAttribute("Folder", snapshot.Detail), new XAttribute("Name", snapshot.Name), new XElement(action));
                StartupRegistrations.WriteNew(BackupPath(id), Encoding.UTF8.GetBytes(original.ToString(SaveOptions.DisableFormatting)));
                action.Element(Ns + "Command")!.Value = Gate;
                if (action.Element(Ns + "Arguments") is { } args) args.Value = id;
                else action.Element(Ns + "Command")!.AddAfterSelf(new XElement(Ns + "Arguments", id));
            }
            try
            {
                // 从服务重新打开，防止用已经被厂商更新的旧 COM 快照提交。
                var latest = Inspect(snapshot);
                StartupRegistrations.Verify(snapshot, latest, allowed);
                const int updateOnly = 4, preserveAcl = 16, ignoreRegistrationTriggers = 32, interactive = 3;
                object updated = folder.RegisterTask(snapshot.Name, doc.ToString(SaveOptions.DisableFormatting),
                    updateOnly | preserveAcl | ignoreRegistrationTriggers, _sid, null, interactive, null);
                Marshal.FinalReleaseComObject(updated);
            }
            catch { if (!allowed) StartupRegistrations.Retire(BackupPath(id)); throw; }
            if (allowed) StartupRegistrations.Retire(BackupPath(id));
            return Inspect(snapshot);
        });
    }

    /// <summary>卸载时只通过自己的备份定位任务。已删除或已改写的任务不会被重新创建。</summary>
    internal IEnumerable<StartupEntry> Managed()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.task"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _)) continue;
            var backup = XElement.Load(file);
            if ((string?)backup.Attribute("Name") is not { } name || (string?)backup.Attribute("Folder") is not { } folder) continue;
            var entry = Inspect(new("Task", name, folder));
            if (entry.Blocked) yield return entry;
        }
    }

    /// <summary>核对备份中记录的路径，防止同名任务互相还原。</summary>
    private XElement? ReadBackup(string id, StartupEntry seed)
    {
        string path = BackupPath(id);
        if (!File.Exists(path)) return null;
        var backup = XElement.Load(path);
        return (string?)backup.Attribute("Folder") == seed.Detail && (string?)backup.Attribute("Name") == seed.Name
            ? backup.Element(Ns + "Exec") : null;
    }

    /// <summary>只用有效 GUID 定位代理目录中的任务备份。</summary>
    private string BackupPath(string id) => Path.Combine(_directory, Guid.ParseExact(id, "N").ToString("N") + ".task");

    /// <summary>微软的系统任务目录整体由 Windows 维护。</summary>
    private static bool IsProtected(string folder) => folder.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    /// <summary>区分 Windows 系统维护与 Office 等微软产品的维护任务。</summary>
    private static string MaintenanceReason(string folder) => folder.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase)
        ? "Windows 维护任务" : "Microsoft 产品维护";

    /// <summary>以本机任务文件夹和单个登记名打开任务，使用结束立即释放这次查询的 COM 引用。</summary>
    private T WithTask<T>(StartupEntry item, Func<dynamic, dynamic, T> work)
    {
        if (!item.Detail.StartsWith('\\') || item.Detail.Contains("..") || item.Name.IndexOfAny(['\\', '/']) >= 0) throw new IOException("任务路径无效。");
        // PowerShell 返回带尾分隔符的目录；计划任务 COM 接口只在根目录接受这个分隔符。
        string folderPath = item.Detail == @"\" ? item.Detail : item.Detail.TrimEnd('\\');
        dynamic folder = _service.GetFolder(folderPath);
        try
        {
            dynamic task = folder.GetTask(item.Name);
            try { return work(folder, task); }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        finally { Marshal.FinalReleaseComObject(folder); }
    }

    /// <summary>释放本轮查询使用的计划任务服务连接。</summary>
    public void Dispose() => Marshal.FinalReleaseComObject(_service);
}
