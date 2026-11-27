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
    private readonly bool _protectedStore;
    private string Gate => Path.Combine(_directory, "SideStage.StartupGate.exe");
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>连接本机计划任务服务。只接受当前 SID 或当前完整账户名。</summary>
    internal StartupTaskRules(string directory, bool protectedStore = false)
    {
        if (protectedStore && !directory.Equals(ProtectedStartupStore.Location, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("管理员任务只能使用固定的受保护目录。");
        _protectedStore = protectedStore;
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
        try { return WithTask(seed, (_, task) => ReadTask(seed, task, XDocument.Parse((string)task.Xml))); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return seed with { Fingerprint = null, WindowsEnabled = null, RepeatPolicy = StartupRepeatPolicy.Unknown,
                ReadOnlyReason = IsProtected(seed.Detail) ? MaintenanceReason(seed.Detail) : "无法读取任务定义" };
        }
    }

    /// <summary>先识别任务的用途与运行账户，再核对实际修改任务的权限。两种账户可以不同。</summary>
    private StartupEntry ReadTask(StartupEntry seed, dynamic task, XDocument doc)
    {
        var entry = Describe(seed, doc, (bool)task.Enabled);
        if (entry.ReadOnlyReason != null && !entry.Blocked) return entry;
        try
        {
            const int ownerGroupDacl = 7;
            string acl = task.GetSecurityDescriptor(ownerGroupDacl);
            bool ordinary = StartupTaskAccess.CanWrite(acl);
            if (_protectedStore)
            {
                if (ordinary && !entry.Blocked) return entry with { ReadOnlyReason = "请按普通用户任务处理", Fingerprint = null };
                if (!StartupElevation.IsAdministrator()) return entry with { ReadOnlyReason = "需要管理员权限修改此个人任务", Fingerprint = null };
                return StartupTaskAccess.CanWrite(acl, ordinary: false) ? entry
                    : entry with { ReadOnlyReason = "任务权限不允许当前管理员写入", Fingerprint = null };
            }
            if (ordinary && !entry.RequiresAdmin) return entry;
            return entry with { ReadOnlyReason = "当前账户没有此计划任务的写入权限", Fingerprint = null,
                RequiresAdmin = !entry.Blocked };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception
            or ArgumentException or System.Security.SecurityException)
        { return entry with { ReadOnlyReason = "无法确认此计划任务的写入权限", Fingerprint = null }; }
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
        seed = seed with { WindowsEnabled = loginEnabled, RepeatPolicy = policy, RequiresAdmin = _protectedStore,
            ReadOnlyReason = null, Blocked = false };
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
        if (!_protectedStore && target.Equals(Path.Combine(ProtectedStartupStore.Location, "SideStage.StartupGate.exe"), StringComparison.OrdinalIgnoreCase))
            return seed with { RequiresAdmin = true, ReadOnlyReason = "需要核对受保护的原动作", Fingerprint = null };
        if (_protectedStore && target.Equals(Path.Combine(StartupRegistrations.DefaultGateDirectory, "SideStage.StartupGate.exe"), StringComparison.OrdinalIgnoreCase))
            return seed with { ReadOnlyReason = "个人目录的原动作只允许由普通权限恢复", Fingerprint = null };
        bool managed = reason == null && target.Equals(Gate, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(args, "N", out _);
        if (managed)
        {
            if (_protectedStore) ProtectedStartupStore.Validate(false);
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
    internal StartupEntry Change(StartupEntry snapshot, bool allowed, Func<string> prepare, string? confirmedTriggerUser = null)
    {
        return WithTask(snapshot, (folder, task) =>
        {
            string raw = task.Xml;
            var doc = XDocument.Parse(raw);
            StartupEntry live = ReadTask(snapshot, task, doc);
            StartupRegistrations.Verify(snapshot, live, allowed);
            if (_protectedStore != live.RequiresAdmin) throw new IOException("任务写入权限已变化，请重新扫描。");
            ApplyTriggerRepair(doc, confirmedTriggerUser);
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
            {
                // 从服务重新打开，防止用已经被厂商更新的旧 COM 快照提交。
                var latest = Inspect(snapshot);
                StartupRegistrations.Verify(snapshot, latest, allowed);
                const int updateOnly = 4, preserveAcl = 16, ignoreRegistrationTriggers = 32, interactive = 3;
                object updated = folder.RegisterTask(snapshot.Name, doc.ToString(SaveOptions.DisableFormatting),
                    updateOnly | preserveAcl | ignoreRegistrationTriggers, _sid, null, interactive, null);
                Marshal.FinalReleaseComObject(updated);
            }
            var result = WithTask(snapshot, (_, updated) =>
            {
                var actual = XDocument.Parse((string)updated.Xml);
                if (!XNode.DeepEquals(ActionContent(actual), ActionContent(doc)))
                    throw new IOException("未能核实计划任务的实际动作，已保留备份。");
                return ReadTask(snapshot, updated, actual);
            });
            if (allowed) StartupRegistrations.Retire(BackupPath(id));
            return result;
        });
    }

    /// <summary>动作从独立备份转入任务时可能自带 xmlns 声明。比较展开后的 XML 名称、属性和内容。</summary>
    private static XElement ActionContent(XDocument document)
    {
        var actions = new XElement(document.Root!.Element(Ns + "Actions")!);
        actions.DescendantsAndSelf().Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Remove();
        return actions;
    }

    /// <summary>所有通过个人任务核验的登记都按同一规则解析登录触发器。只有 Windows 无法映射且用户确认旧值时才修复。</summary>
    internal void ApplyTriggerRepair(XDocument doc, string? confirmedTriggerUser)
    {
        var trigger = doc.Root!.Element(Ns + "Triggers")?.Element(Ns + "LogonTrigger")?.Element(Ns + "UserId");
        string? triggerUser = (string?)trigger;
        if (string.IsNullOrWhiteSpace(triggerUser) || string.Equals(triggerUser, _sid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(triggerUser, _account, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (triggerUser.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                _ = new SecurityIdentifier(triggerUser).Translate(typeof(NTAccount));
            else _ = new NTAccount(triggerUser).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            if (!string.Equals(confirmedTriggerUser, triggerUser, StringComparison.Ordinal))
                throw new StartupTriggerRepairRequired(triggerUser, _account);
            trigger!.Value = _sid;
        }
    }

    /// <summary>独立检查每份备份和实时任务；随后在可见任务中补查丢失备份或被移动的引用。</summary>
    internal bool RestoreManaged(Action<StartupEntry> restore, List<string> failures)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.Exists(_directory) ? Directory.EnumerateFiles(_directory, "*.task") : [])
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _)) continue;
            try
            {
                var backup = XElement.Load(file);
                if ((string?)backup.Attribute("Name") is not { } name || (string?)backup.Attribute("Folder") is not { } folder)
                    throw new InvalidDataException("找不到原任务的位置。");
                var seed = new StartupEntry("Task", name, folder);
                WithTask(seed, (_, task) => { Check(seed, task); return true; });
            }
            catch (Exception error) when (error.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
            { /* 原任务已被应用移除。 */ }
            catch (Exception error) when (StartupRuleBatch.Recoverable(error)) { failures.Add($"{Path.GetFileName(file)}：{error.Message}"); }
        }
        bool complete = true;
        dynamic root = _service.GetFolder(@"\");
        try { Walk(root); }
        finally { Marshal.FinalReleaseComObject(root); }
        return complete;

        // 先匹配实时动作再读取备份。即使厂商重用同名任务，也不会覆盖其新动作。
        void Check(StartupEntry seed, dynamic task)
        {
            string path = task.Path;
            if (!visited.Add(path)) return;
            var doc = XDocument.Parse((string)task.Xml);
            if (!doc.Descendants(Ns + "Command").Any(e => e.Value.Trim('"').Equals(Gate, StringComparison.OrdinalIgnoreCase))) return;
            var item = ReadTask(seed, task, doc);
            if (!item.Blocked || item.Fingerprint == null)
                throw new IOException($"{path} 仍引用启动代理，{item.ReadOnlyReason ?? "任务定义已变化"}。");
            restore(item);
        }

        // 不可读的无关目录只阻止代理清理；已知托管任务的读取失败在上一步单独报告。
        void Walk(dynamic folder)
        {
            VisitCollection(() => folder.GetTasks(1), task =>
            {
                string path = task.Path;
                string name = task.Name;
                Check(new StartupEntry("Task", name, path[..^name.Length]), task);
            });
            VisitCollection(() => folder.GetFolders(0), child =>
            {
                string path = child.Path;
                if (!IsProtected(path + "\\")) Walk(child);
            });
        }

        // Windows 的 COM 集合从 1 计数，每个对象在访问结束时立即释放。
        void VisitCollection(Func<dynamic> open, Action<dynamic> visit)
        {
            try
            {
                dynamic collection = open();
                try
                {
                    int count = collection.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        try
                        {
                            dynamic item = collection.Item(i);
                            try { visit(item); }
                            finally { Marshal.FinalReleaseComObject(item); }
                        }
                        catch (Exception error) when (StartupRuleBatch.Recoverable(error))
                        {
                            complete = false;
                            if (error is IOException or InvalidDataException) failures.Add(error.Message);
                        }
                    }
                }
                finally { Marshal.FinalReleaseComObject(collection); }
            }
            catch (Exception error) when (StartupRuleBatch.Recoverable(error)) { complete = false; }
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

/// <summary>通知界面旧登录用户无法解析，必须取得用户对本次账户修复的明确同意。</summary>
internal sealed class StartupTriggerRepairRequired(string previous, string current) : IOException("登录触发器中的账户名称已失效。")
{
    internal string Previous { get; } = previous;
    internal string Current { get; } = current;
}
