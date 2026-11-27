/*
 * 文件职责：使用隔离的注册表子树和临时启动目录，验证规则真正影响登录入口，
 * 而且保留参数、原始快捷方式、外部更新和 Windows 的禁用状态。
 * 测试负载：把固定的文本排序程序复制到临时目录，仅处理同目录内的测试文件。
 */
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;
using SideStage.App;

namespace SideStage.App.Tests;

[TestClass]
public sealed class StartupRuleTests
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private string Root => Path.Combine(Path.GetTempPath(), "SideStage.StartupTests", _id);
    private string Key => @"Software\SideStage.Tests\" + _id;
    private string Gate => Path.Combine(Root, "Gate");
    private string Folder => Path.Combine(Root, "Startup");
    private string Target => Path.Combine(Root, "fixture sort.exe");
    private string Output => Path.Combine(Root, "output.txt");
    private string Arguments => $"\"{Path.Combine(Root, "input.txt")}\" /o \"{Output}\"";
    private StartupRegistrations Rules => new(Key, Gate, Folder, Path.Combine(Root, "common"));

    /// <summary>每个测试只创建拥有独立标识的目录和测试登记。</summary>
    [TestInitialize]
    public void SetUp()
    {
        Directory.CreateDirectory(Folder);
        File.Copy(Path.Combine(Environment.SystemDirectory, "sort.exe"), Target);
        File.WriteAllText(Path.Combine(Root, "input.txt"), "b\r\na\r\n");
        using var key = Registry.CurrentUser.CreateSubKey(Key + @"\Run");
        key.SetValue("配置 A", $"\"{Target}\" {Arguments}", RegistryValueKind.ExpandString);
        key.SetValue("配置 B", $"\"{Target}\"");
    }

    /// <summary>只删除本测试的专用 GUID 登记及临时目录。</summary>
    [TestCleanup]
    public void TearDown()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Key, false);
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }

    /// <summary>执行真正的登录代理，分别验证普通桌面、确认的子会话、主窗口退出及原值恢复。</summary>
    [TestMethod]
    public void GateRunsOnlyOutsideConfirmedChildAndRestoresOriginal()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key + @"\Run")!;
        var rows = Rules.ReadEntries().ToArray();
        var blocked = Rules.SetAllowed(rows.Single(x => x.Name == "配置 A"), false);
        Assert.IsTrue(blocked.Blocked);
        Assert.IsFalse(Rules.ReadEntries().Single(x => x.Name == "配置 B").Blocked);
        string wrapped = (string)key.GetValue("配置 A")!;
        using (var broker = new StartupSessionBroker(Gate, () => (uint)Process.GetCurrentProcess().SessionId))
        {
            Execute(wrapped);
            Assert.IsFalse(File.Exists(Output), "确认子会话时不应启动负载。");
        }
        using (var broker = new StartupSessionBroker(Gate))
        {
            Execute(wrapped);
            Assert.IsTrue(SpinWait.SpinUntil(() => File.Exists(Output) && new FileInfo(Output).Length > 0, 3000));
        }
        File.Delete(Output);
        Execute(wrapped);
        Assert.IsTrue(SpinWait.SpinUntil(() => File.Exists(Output) && new FileInfo(Output).Length > 0, 3000));
        Rules.SetAllowed(blocked, true);
        Assert.AreEqual(RegistryValueKind.ExpandString, key.GetValueKind("配置 A"));
        Assert.AreEqual($"\"{Target}\" {Arguments}", key.GetValue("配置 A"));
    }

    /// <summary>同一快捷方式关闭再开启后，恢复原始字节和参数。</summary>
    [TestMethod]
    public void RestoresOriginalShortcutBytes()
    {
        string file = Path.Combine(Folder, "排序工具.lnk");
        StartupRegistrations.CreateLink(file, Target, Arguments);
        byte[] original = File.ReadAllBytes(file);
        var blocked = Rules.SetAllowed(Rules.ReadEntries().Single(x => x.Source == "Startup"), false);
        Assert.IsTrue(blocked.Blocked);
        Assert.IsFalse(Rules.SetAllowed(blocked, true).Blocked);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(file));
    }

    /// <summary>用独立登记覆盖重复批量操作、Windows 自行禁用、列表中途失败和厂商覆盖命令。</summary>
    [TestMethod]
    public void BulkActionsRespectWindowsAndContinueAfterStaleEntry()
    {
        using var run = Registry.CurrentUser.OpenSubKey(Key + @"\Run", true)!;
        run.SetValue("禁用项", $"\"{Target}\"");
        using var approval = Registry.CurrentUser.CreateSubKey(Key + @"\Explorer\StartupApproved\Run");
        approval.SetValue("禁用项", new byte[] { 3, 0, 0, 0 });
        var close = new StartupRuleBatch(Rules.ReadEntries(), false);
        Assert.AreEqual(2, close.Candidates.Length);
        var stale = close.Candidates[0];
        run.SetValue(stale.Name, $"\"{Target}\" /?");
        close.Apply(Rules);
        Assert.AreEqual(1, close.Changed.Count);
        Assert.AreEqual(1, close.Failures.Count);
        Assert.AreEqual(1, close.Skipped);
        var changed = close.Changed.Single();
        approval.SetValue(changed.Name, new byte[] { 3, 0, 0, 0 });
        var open = new StartupRuleBatch(Rules.ReadEntries(), true);
        open.Apply(Rules);
        Assert.AreEqual(false, open.Changed.Single().WindowsEnabled);
        Assert.AreEqual(2, open.Skipped);
        Assert.IsEmpty(new StartupRuleBatch(Rules.ReadEntries(), true).Candidates);
        Assert.AreEqual($"\"{Target}\" /?", run.GetValue(stale.Name));
    }

    /// <summary>卸载中模拟一份备份暂时不可用，验证剩余条目继续恢复，补回原备份后可重试。</summary>
    [TestMethod]
    public void UninstallCanRetryAfterOneMissingBackup()
    {
        var close = new StartupRuleBatch(Rules.ReadEntries(), false);
        close.Apply(Rules);
        Assert.AreEqual(2, close.Changed.Count);
        string backup = Directory.GetFiles(Gate, "*.expand").Single();
        byte[] original = File.ReadAllBytes(backup);
        File.Delete(backup);
        Assert.AreEqual(1, Rules.RestoreAll());
        Assert.IsTrue(File.ReadAllText(Path.Combine(Gate, "restore-errors.txt")).Contains("配置 A"));
        Assert.IsTrue(File.Exists(Path.Combine(Gate, "SideStage.StartupGate.exe")));
        Assert.AreEqual("配置 A", Rules.ReadEntries().Single(item => item.Blocked).Name);
        File.WriteAllBytes(backup, original);
        Assert.AreEqual(0, Rules.RestoreAll());
        Assert.IsFalse(Rules.ReadEntries().Any(item => item.Blocked));
        Assert.AreEqual(0, Rules.RestoreAll());
    }

    /// <summary>系统程序、系统已禁用入口以及已发生变化的旧快照都无法替换 Windows 的现有登记。</summary>
    [TestMethod]
    public void RejectsProtectedDisabledAndStaleEntries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key + @"\Run", true)!;
        key.SetValue("系统项", $"\"{Path.Combine(Environment.SystemDirectory, "sort.exe")}\"");
        using var approval = Registry.CurrentUser.CreateSubKey(Key + @"\Explorer\StartupApproved\Run");
        approval.SetValue("配置 B", new byte[] { 3, 0, 0, 0 }, RegistryValueKind.Binary);
        // Windows 设置关闭的个人启动项也可能使用 1，保护原因不能遮盖其禁用状态。
        approval.SetValue("系统项", new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        var rows = Rules.ReadEntries().ToArray();
        var system = rows.Single(row => row.Name == "系统项");
        Assert.AreEqual(false, system.WindowsEnabled);
        Assert.AreEqual("系统或驱动目录", system.ReadOnlyReason);
        Assert.AreEqual(true, rows.Single(row => row.Name == "配置 A").WindowsEnabled);
        Assert.AreEqual(false, rows.Single(row => row.Name == "配置 B").WindowsEnabled);
        foreach (var row in rows.Where(x => x.Name != "配置 A"))
        {
            Assert.IsNotNull(row.ReadOnlyReason);
            Assert.ThrowsExactly<IOException>(() => Rules.SetAllowed(row with { ReadOnlyReason = null }, false));
        }
        var changed = Rules.SetAllowed(rows.Single(x => x.Name == "配置 A"), false);
        key.SetValue("配置 A", $"\"{Target}\" /?");
        Assert.ThrowsExactly<IOException>(() => Rules.SetAllowed(changed, true));
        Assert.AreEqual($"\"{Target}\" /?", key.GetValue("配置 A"));
    }

    /// <summary>用任意任务名验证系统目录和管理员任务的限制均不会遮盖登录开关或重复触发规则。</summary>
    [TestMethod]
    [DataRow(null, "IgnoreNew")]
    [DataRow("IgnoreNew", "IgnoreNew")]
    [DataRow("Parallel", "Parallel")]
    [DataRow("Queue", "Queue")]
    [DataRow("StopExisting", "StopExisting")]
    [DataRow("Unrecognized", "Unknown")]
    public void ReportsTaskPolicyIndependentlyOfProtection(string? setting, string expected)
    {
        var doc = XDocument.Parse("""
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Settings/>
              <Principals><Principal><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Triggers><LogonTrigger><Enabled>false</Enabled></LogonTrigger></Triggers>
            </Task>
            """);
        var ns = doc.Root!.Name.Namespace;
        if (setting != null) doc.Root.Element(ns + "Settings")!.Add(new XElement(ns + "MultipleInstancesPolicy", setting));
        using var tasks = new StartupTaskRules(Gate);
        var seed = new StartupEntry("Task", _id, @"\Microsoft\Windows\Fixture\");
        var disabled = tasks.Describe(seed, doc, true);
        Assert.AreEqual(false, disabled.WindowsEnabled);
        Assert.AreEqual(expected, disabled.RepeatPolicy.ToString());
        Assert.AreEqual("Windows 维护任务", disabled.ReadOnlyReason);
        Assert.IsNull(disabled.Fingerprint);

        // 两个登录触发器中有一个启用时，该登录登记可用；关闭任务总开关时两者一并失效。
        doc.Root.Element(ns + "Triggers")!.Add(new XElement(ns + "LogonTrigger"));
        var admin = tasks.Describe(seed with { Detail = @"\" }, doc, true);
        Assert.AreEqual("管理员权限任务", admin.ReadOnlyReason);
        Assert.AreEqual(expected, admin.RepeatPolicy.ToString());
        Assert.AreEqual(true, admin.WindowsEnabled);
        Assert.AreEqual(false, tasks.Describe(seed, doc, false).WindowsEnabled);
    }

    /// <summary>相同的运行账户可以出现在不同权限的任务上，管理员组的允许项不能冒充普通用户的写入权。</summary>
    [TestMethod]
    public void ChecksEffectiveTaskWritePermission()
    {
        using var user = WindowsIdentity.GetCurrent();
        string sid = user.User!.Value;
        string administratorOwned = $"O:BAG:BAD:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;{sid})";
        string userWritable = $"O:{sid}G:BAD:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;{sid})";
        Assert.IsFalse(StartupTaskAccess.CanWrite(administratorOwned));
        Assert.IsTrue(StartupTaskAccess.CanWrite(userWritable));
    }

    /// <summary>在已授权的验收进程中创建独立的只读个人任务，验证管理员托管、普通用户读取和原权限恢复。</summary>
    [TestMethod]
    public void AdminOwnedPersonalTaskUsesProtectedBackup()
    {
        if (!StartupElevation.IsAdministrator())
        {
            // 普通进程也验证它不能通过伪造缓存标志进入管理员操作。
            var fake = new StartupEntry("Task", "SideStage-Test-" + _id, @"\", RequiresAdmin: true);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => new StartupRegistrations().SetAllowed(fake, false));
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value, name = "SideStage-Test-" + _id;
        string acl = $"O:BAG:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;{sid})";
        string xml = $"""
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><UserId>{sid}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="User"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Actions Context="User"><Exec><Command>{System.Security.SecurityElement.Escape(Target)}</Command></Exec></Actions>
            </Task>
            """;
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder(@"\");
            try
            {
                object created = folder.RegisterTask(name, xml, 2 | 16, sid, null, 3, acl);
                Marshal.FinalReleaseComObject(created);
                try
                {
                    var seed = new StartupEntry("Task", name, @"\");
                    using var ordinary = new StartupTaskRules(StartupRegistrations.DefaultGateDirectory);
                    using var admin = new StartupTaskRules(ProtectedStartupStore.Location, protectedStore: true);
                    Assert.IsTrue(ordinary.Inspect(seed).RequiresAdmin);
                    var initial = admin.Inspect(seed);
                    Assert.IsNull(initial.ReadOnlyReason);
                    var registrations = new StartupRegistrations();
                    var blocked = registrations.SetAllowed(initial, false);
                    Assert.IsTrue(blocked.Blocked);
                    Assert.IsTrue(StartupUserAccess.AsUser(() =>
                    {
                        ProtectedStartupStore.Validate(false);
                        using var view = new StartupTaskRules(ProtectedStartupStore.Location, protectedStore: true);
                        var readOnly = view.Inspect(seed);
                        return readOnly.Blocked && readOnly.Fingerprint == null;
                    }));
                    var restored = registrations.SetAllowed(blocked, true);
                    Assert.AreEqual(initial.Fingerprint, restored.Fingerprint);
                    dynamic live = folder.GetTask(name);
                    try { Assert.IsFalse(StartupTaskAccess.CanWrite((string)live.GetSecurityDescriptor(7))); }
                    finally { Marshal.FinalReleaseComObject(live); }
                }
                finally { folder.DeleteTask(name, 0); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    /// <summary>复现无效登录名的任务定义，拒绝未确认或过期确认；同意后只有触发器的账户变为当前 SID。</summary>
    [TestMethod]
    public void RepairsOnlyTheConfirmedUnmappedTrigger()
    {
        using var user = WindowsIdentity.GetCurrent();
        string[] account = user.Name.Split('\\');
        if (account.Length != 2 || !account[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("此用例需要本机账户。");
        string oldName = "OLD-" + _id[..8] + "\\" + account[1];
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var definition = new XDocument(new XElement(ns + "Task", new XElement(ns + "Triggers",
            new XElement(ns + "LogonTrigger", new XElement(ns + "UserId", oldName), new XElement(ns + "Delay", "PT1M")))));
        var original = new XDocument(definition);
        using var tasks = new StartupTaskRules(Gate);
        Assert.ThrowsExactly<StartupTriggerRepairRequired>(() => tasks.ApplyTriggerRepair(definition, null));
        Assert.ThrowsExactly<StartupTriggerRepairRequired>(() => tasks.ApplyTriggerRepair(definition, user.Name));
        Assert.IsTrue(XNode.DeepEquals(original, definition));
        original.Descendants(ns + "UserId").Single().Value = user.User!.Value;
        tasks.ApplyTriggerRepair(definition, oldName);
        Assert.IsTrue(XNode.DeepEquals(original, definition));
        tasks.ApplyTriggerRepair(definition, null);
        Assert.IsTrue(XNode.DeepEquals(original, definition));
        // 与当前登录名完全不同的失效引用同样需要用户明确选择，不能按应用名或用户名自动认领。
        string different = "OLD-" + _id[..8] + "\\retired-user";
        definition.Descendants(ns + "UserId").Single().Value = different;
        Assert.ThrowsExactly<StartupTriggerRepairRequired>(() => tasks.ApplyTriggerRepair(definition, oldName));
        tasks.ApplyTriggerRepair(definition, different);
        Assert.IsTrue(XNode.DeepEquals(original, definition));
    }

    /// <summary>在当前账户创建独立的普通登录任务，验证计划程序接受动作替换、放行及原样恢复。</summary>
    [TestMethod]
    public void PersonalTaskRoundTripsWithoutChangingTriggers()
    {
        string name = "SideStage-Test-" + _id;
        using var user = WindowsIdentity.GetCurrent();
        string sid = user.User!.Value;
        var doc = XDocument.Parse($"""
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><UserId>{sid}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="User"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings><Enabled>true</Enabled></Settings>
              <Actions Context="User"><Exec><Command/><Arguments/></Exec></Actions>
            </Task>
            """);
        XNamespace ns = doc.Root!.Name.Namespace;
        doc.Descendants(ns + "Command").Single().Value = Target;
        doc.Descendants(ns + "Arguments").Single().Value = Arguments;
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        try
        {
            service.Connect();
            dynamic folder = service.GetFolder(@"\");
            try
            {
                object original = folder.RegisterTask(name, doc.ToString(), 2, sid, null, 3, null);
                Marshal.FinalReleaseComObject(original);
                try
                {
                    using var tasks = new StartupTaskRules(Gate);
                    var initial = tasks.Inspect(new("Task", name, @"\"));
                    Assert.IsNull(initial.ReadOnlyReason);
                    var blocked = Rules.SetAllowed(initial, false);
                    Assert.IsTrue(blocked.Blocked);
                    string raw = $"\"{Path.Combine(Gate, "SideStage.StartupGate.exe")}\" {Path.GetFileNameWithoutExtension(Directory.GetFiles(Gate, "*.task").Single())}";
                    using (var broker = new StartupSessionBroker(Gate, () => null)) Execute(raw);
                    Assert.IsTrue(File.Exists(Output));
                    if (int.TryParse(Environment.GetEnvironmentVariable("SIDESTAGE_TEST_EXISTING_CHILD"), out int expected))
                        CheckExistingChild(folder, name, expected);
                    var restore = Rules.RestoreAll();
                    Assert.AreEqual(0, restore, restore == 0 ? "" : File.ReadAllText(Path.Combine(Gate, "restore-errors.txt")));
                    Assert.AreEqual(initial.Fingerprint, tasks.Inspect(initial).Fingerprint);
                    // Windows 系统目录以及提权的任务在相同动作下仍须保持只读。
                    doc.Descendants(ns + "RunLevel").Single().Value = "HighestAvailable";
                    Assert.AreEqual("管理员权限任务", tasks.Describe(initial, doc, true).ReadOnlyReason);
                    // 普通任务改为先停旧实例后，旧快照和新的只读快照均不能用来接管该任务。
                    doc.Descendants(ns + "RunLevel").Single().Value = "LeastPrivilege";
                    doc.Descendants(ns + "Settings").Single().AddFirst(new XElement(ns + "MultipleInstancesPolicy", "StopExisting"));
                    object updated = folder.RegisterTask(name, doc.ToString(), 4, sid, null, 3, null);
                    Marshal.FinalReleaseComObject(updated);
                    var stopExisting = tasks.Inspect(initial);
                    Assert.AreEqual("再次触发会先停止旧任务", stopExisting.ReadOnlyReason);
                    Assert.ThrowsExactly<IOException>(() => Rules.SetAllowed(initial, false));
                    Assert.ThrowsExactly<IOException>(() => Rules.SetAllowed(stopExisting, false));
                    Assert.AreEqual(stopExisting.Fingerprint, tasks.Inspect(initial).Fingerprint);
                }
                finally { folder.DeleteTask(name, 0); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }

    /// <summary>显式验收时在已确认的本机子会话启动同一个临时任务，先验证放行输出，再验证父会话判定跳过。</summary>
    private void CheckExistingChild(dynamic folder, string name, int expected)
    {
        var inspector = new SideStage.Core.Platform.Windows.WindowsSessionInspector();
        Assert.AreEqual((uint)expected, inspector.TryGetChildSession()?.Id.Value);
        dynamic registered = folder.GetTask(name);
        try
        {
            File.Delete(Output);
            object first = registered.RunEx(null, 4, expected, null);
            Marshal.FinalReleaseComObject(first);
            Assert.IsTrue(SpinWait.SpinUntil(() => File.Exists(Output) && (int)registered.State == 3, 12000), "子会话放行对照未完成。");
            File.Delete(Output);
            using var observed = new ManualResetEventSlim();
            using var broker = new StartupSessionBroker(Gate, () =>
            {
                uint? child = inspector.TryGetChildSession()?.Id.Value;
                observed.Set();
                return child;
            });
            object second = registered.RunEx(null, 4, expected, null);
            Marshal.FinalReleaseComObject(second);
            Assert.IsTrue(observed.Wait(12000), "真实登录代理未联系父桌面。");
            Assert.IsTrue(SpinWait.SpinUntil(() => (int)registered.State == 3, 8000));
            Assert.AreEqual(0, (int)registered.LastTaskResult);
            Assert.IsFalse(File.Exists(Output), "在实际子会话中执行了应当跳过的负载。");
        }
        finally { Marshal.FinalReleaseComObject(registered); }
    }

    /// <summary>从真实 Run 值启动代理，并等待有界的执行结果。</summary>
    private static void Execute(string raw)
    {
        Assert.IsTrue(SideStage.StartupGate.Dispatcher.TrySplit(raw, out var program, out var arguments));
        using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.IsTrue(process.WaitForExit(8000));
        Assert.AreEqual(0, process.ExitCode);
    }
}
