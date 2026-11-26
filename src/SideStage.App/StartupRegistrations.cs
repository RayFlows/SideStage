/*
 * 文件职责：读取 Windows 的 Run 和启动文件夹，按登记位置独立控制个人应用的分身登录行为。
 * 写入顺序：保存原始登记，准备稳定的登录入口，最后替换登记。恢复时先核对身份和内容。
 * 范围约束：全机登记、策略、一次性任务、系统目录及 Windows 已禁用项只读。
 */
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SideStage.StartupGate;

namespace SideStage.App;

internal sealed class StartupRegistrations
{
    private readonly string _versionKey;
    private readonly string _gateDirectory;
    private readonly string _personalFolder;
    private readonly string _commonFolder;
    internal static string DefaultGateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideStage", "StartupGate");
    private string Gate => Path.Combine(_gateDirectory, "SideStage.StartupGate.exe");
    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string[] Kinds = ["Run", "RunOnce", @"Policies\Explorer\Run"];
    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    /// <summary>实际使用当前账户的登记。验收可提供专用注册表树、启动文件夹和代理目录。</summary>
    internal StartupRegistrations(string? versionKey = null, string? gateDirectory = null, string? personalFolder = null, string? commonFolder = null)
    {
        _versionKey = versionKey ?? @"Software\Microsoft\Windows\CurrentVersion";
        _gateDirectory = gateDirectory ?? DefaultGateDirectory;
        _personalFolder = personalFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _commonFolder = commonFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
    }

    /// <summary>枚举真实登记；当前用户共享的两个 Run 视图只列一次，全机的两个独立视图分别列出。</summary>
    internal IEnumerable<StartupEntry> ReadEntries()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in hive == RegistryHive.CurrentUser ? Views.Take(1) : Views)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            foreach (var kind in Kinds)
            {
                using var key = root.OpenSubKey(_versionKey + "\\" + kind);
                if (key == null) continue;
                foreach (string name in key.GetValueNames())
                    if (ReadRun(root, key, hive, view, kind, name) is { } row) yield return row;
            }
        }
        foreach (var (source, folder) in new[] { ("Startup", _personalFolder), ("CommonStartup", _commonFolder) })
        {
            if (!Dispatcher.LocalPath(folder) || !Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                yield return ReadFolder(source, path);
            }
        }
    }

    /// <summary>从原始值推导可操作性。缓存的图标路径、指纹和只读提示均不能作为写入授权。</summary>
    private StartupEntry? ReadRun(RegistryKey root, RegistryKey key, RegistryHive hive, RegistryView view, string kind, string name)
    {
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw) return null;
        var type = key.GetValueKind(name);
        string detail = $"{kind} ({view})", source = hive.ToString();
        string? id = GateId(raw);
        bool managed = id != null && hive == RegistryHive.CurrentUser && kind == "Run";
        string? original = managed ? BackupCommand(id!, out type) : raw;
        string command = type == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(original ?? "") : original ?? "";
        bool parsed = Dispatcher.TrySplit(command, out string target, out _);
        string? reason = kind switch { "RunOnce" => "一次性安装或维护", @"Policies\Explorer\Run" => "由系统策略管理", _ => null };
        reason ??= TargetReason(target, parsed);
        if (hive == RegistryHive.LocalMachine) reason ??= "全机共享登记";
        // RunOnce 和策略登记不使用普通 Run 的启用标记，同名条目也要分别判断。
        bool? enabled = kind == "Run" ? Approval(hive, view == RegistryView.Registry32 ? "Run32" : "Run", name) : null;
        reason ??= ApprovalReason(enabled);
        if (managed && original == null) reason = "原始登记备份缺失";
        return new(source, name, detail, parsed ? target : null, Hash($"{raw}\0{type}\0{original}"), reason, managed,
            parsed && name.Length > 20 ? ProductName(target) : null, enabled);
    }

    /// <summary>对很长的自动生成登记名，优先展示应用自身的产品名，完整登记名仍保留在提示中。</summary>
    internal static string? ProductName(string target)
    {
        try
        {
            string? name = File.Exists(target) ? FileVersionInfo.GetVersionInfo(target).ProductName?.Trim() : null;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>识别快捷方式的目标和开关状态，读取失败的文件仍然在清单中显示。</summary>
    private StartupEntry ReadFolder(string source, string path)
    {
        string name = Path.GetFileName(path);
        var row = new StartupEntry(source, name, "启动文件夹");
        try
        {
            var hive = source == "Startup" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            row = row with { WindowsEnabled = Approval(hive, "StartupFolder", name) };
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return row with { ReadOnlyReason = "仅支持普通应用快捷方式" };
            var link = ReadLink(path);
            string? id = link.Target.Equals(Gate, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(link.Arguments, "N", out var value)
                ? value.ToString("N") : null;
            string original = id == null ? path : Stem(id) + ".lnk";
            if (!File.Exists(original)) return row with { ReadOnlyReason = "原始快捷方式备份缺失" };
            if (id != null) link = ReadLink(original);
            string? reason = TargetReason(link.Target, Dispatcher.LocalPath(link.Target) && link.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            reason ??= source == "CommonStartup" ? "全机共享登记" : ApprovalReason(row.WindowsEnabled);
            return row with { IconPath = link.Target, Blocked = id != null, ReadOnlyReason = reason,
                Fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        { return row with { ReadOnlyReason = "无法读取快捷方式" }; }
    }

    /// <summary>按目标的实际位置识别 Windows 组件。文件缺失、系统命令及不明确的执行方式均保留原状。</summary>
    internal static string? TargetReason(string target, bool parsed)
    {
        if (!parsed) return "启动命令由 Windows 管理";
        string path = Path.GetFullPath(target);
        if (path.StartsWith(WindowsFolder + "\\", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(path).Equals("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase)
                ? "Windows 安全通知" : "系统或驱动目录";
        return File.Exists(path) ? null : "目标文件不可用";
    }

    /// <summary>从启动批准列表读取当前系统状态，32 位程序使用 Run32 分组。缺少覆盖值时沿用登记默认开启。</summary>
    private bool? Approval(RegistryHive hive, string bucket, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(_versionKey + @"\Explorer\StartupApproved\" + bucket);
            return key?.GetValue(name) switch
            {
                null or byte[] and [2 or 6, ..] => true,
                byte[] and [1 or 3 or 7, 0, 0, 0, ..] => false,
                _ => null
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { return null; }
    }

    /// <summary>没有其他保护原因时，只有 Windows 已启用的登记才允许设置分身规则。</summary>
    private static string? ApprovalReason(bool? enabled) => enabled switch
    {
        true => null, false => "登录入口已由 Windows 禁用", null => "无法确认登录入口状态"
    };

    /// <summary>在单次变更期间互斥，重新读取真实登记。两个窗口、过期扫描或应用修改都不能覆盖新命令。</summary>
    internal StartupEntry SetAllowed(StartupEntry snapshot, bool allowed) =>
        StartupUserAccess.AsUser(() => ChangeRegistration(snapshot, allowed));

    /// <summary>在普通用户令牌下先核验文件和登记，再提交一项可恢复的变更。</summary>
    private StartupEntry ChangeRegistration(StartupEntry snapshot, bool allowed)
    {
        using var mutex = new Mutex(false, @"Global\SideStage.StartupRules." + Hash(_gateDirectory));
        bool owned;
        try { owned = mutex.WaitOne(1500); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("其他窗口正在修改启动项。");
        try
        {
            if (snapshot.Source == "Task")
            {
                using var tasks = new StartupTaskRules(_gateDirectory);
                return tasks.Change(snapshot, allowed, PrepareGate);
            }
            if (snapshot.Source == "CurrentUser" && snapshot.Detail == "Run (Registry64)")
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = root.OpenSubKey(_versionKey + @"\Run", true) ?? throw Stale();
                StartupEntry? Current() => ReadRun(root, key, RegistryHive.CurrentUser, RegistryView.Registry64, "Run", snapshot.Name);
                Verify(snapshot, Current(), allowed);
                var raw = (string)key.GetValue(snapshot.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
                if (allowed)
                {
                    string id = GateId(raw)!;
                    string original = BackupCommand(id, out var type) ?? throw Stale();
                    key.SetValue(snapshot.Name, original, type);
                    Retire(Stem(id) + (type == RegistryValueKind.ExpandString ? ".expand" : ".run"));
                }
                else
                {
                    string id = PrepareGate();
                    string wrapper = $"\"{Gate}\" {id}";
                    if (wrapper.Length > 260) throw new IOException("登录入口路径超过 Windows Run 长度限制。");
                    var extension = key.GetValueKind(snapshot.Name) == RegistryValueKind.ExpandString ? ".expand" : ".run";
                    WriteNew(Stem(id) + extension, Encoding.UTF8.GetBytes(raw));
                    Verify(snapshot, Current(), allowed);
                    key.SetValue(snapshot.Name, wrapper, RegistryValueKind.String);
                }
                return Current() ?? throw Stale();
            }
            if (snapshot.Source != "Startup" || Path.GetFileName(snapshot.Name) != snapshot.Name ||
                !snapshot.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) throw Stale();
            string path = Path.Combine(_personalFolder, snapshot.Name);
            Verify(snapshot, ReadFolder("Startup", path), allowed);
            string staged = Path.Combine(_gateDirectory, Guid.NewGuid().ToString("N") + ".stage.lnk");
            try
            {
                string? backup = null;
                if (allowed)
                {
                    backup = Stem(ReadLink(path).Arguments) + ".lnk";
                    File.Copy(backup, staged);
                }
                else
                {
                    string id = PrepareGate();
                    File.Copy(path, Stem(id) + ".lnk");
                    CreateLink(staged, Gate, id);
                }
                Verify(snapshot, ReadFolder("Startup", path), allowed);
                // 在目标卷上原子替换；若个人启动目录重定向到其他卷，保留原件并报告失败。
                File.Replace(staged, path, null);
                if (backup != null) Retire(backup);
            }
            finally { File.Delete(staged); }
            return ReadFolder("Startup", path);
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>原登记已恢复才移除对应备份，登录期间文件被占用不应撤销已经成功的还原。</summary>
    internal static void Retire(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>卸载前只还原仍然指向本代理的个人登记。应用自行修改或移除的入口保持现状。</summary>
    internal int RestoreAll()
    {
        if (!Directory.Exists(_gateDirectory)) return 0;
        try
        {
            using var tasks = new StartupTaskRules(_gateDirectory);
            var items = ReadEntries().Where(item => item.Blocked).Concat(tasks.Managed()).ToArray();
            foreach (var item in items) SetAllowed(item, true);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or COMException
            or System.ComponentModel.Win32Exception) { return 1; }
    }

    /// <summary>安装独立的用户态代理。已存在文件必须与内嵌构建逐字节一致。</summary>
    private string PrepareGate()
    {
        Directory.CreateDirectory(_gateDirectory);
        using var embedded = typeof(StartupRegistrations).Assembly.GetManifestResourceStream("SideStage.StartupGate.exe")!;
        using var buffer = new MemoryStream();
        embedded.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        if (!File.Exists(Gate) || !File.ReadAllBytes(Gate).AsSpan().SequenceEqual(bytes))
        {
            string temp = Stem(Guid.NewGuid().ToString("N")) + ".tmp";
            try { WriteNew(temp, bytes); File.Move(temp, Gate, overwrite: true); }
            finally { File.Delete(temp); }
        }
        if (!File.ReadAllBytes(Gate).AsSpan().SequenceEqual(bytes)) throw new IOException("启动代理版本或文件内容不匹配。");
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>对已经托管的条目允许恢复，即使目标后来被卸载或被任务管理器禁用。</summary>
    internal static void Verify(StartupEntry prior, StartupEntry? live, bool allowed)
    {
        if (live == null || live.Fingerprint == null || prior.Fingerprint != live.Fingerprint ||
            live.Blocked != allowed || (!allowed && live.ReadOnlyReason != null)) throw Stale();
    }

    /// <summary>返回统一的竞态提示，由界面刷新当前行。</summary>
    private static IOException Stale() => new("启动登记已变化或此项只读，请重新查看。");

    /// <summary>严格识别本代理生成的命令，不接受来自缓存的文件名或任意参数。</summary>
    private string? GateId(string raw)
    {
        string prefix = $"\"{Gate}\" ";
        return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(raw[prefix.Length..], "N", out var id)
            ? id.ToString("N") : null;
    }

    /// <summary>只有 GUID 可以用作备份名，路径始终位于该用户的固定代理目录内。</summary>
    private string Stem(string id) => Path.Combine(_gateDirectory, Guid.ParseExact(id, "N").ToString("N"));

    /// <summary>按原值类型读取未经展开的命令，以便原样恢复环境变量。</summary>
    private string? BackupCommand(string id, out RegistryValueKind type)
    {
        string stem = Stem(id);
        type = File.Exists(stem + ".expand") ? RegistryValueKind.ExpandString : RegistryValueKind.String;
        string path = stem + (type == RegistryValueKind.String ? ".run" : ".expand");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>只创建新文件，同名或被占用时立即停止，禁止覆盖别的备份。</summary>
    internal static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    /// <summary>为登记内容生成固定长度的比较值，不把命令行写入扫描缓存。</summary>
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>读取快捷方式属性，在同一线程释放 COM 对象，不解析或启动目标应用。</summary>
    private static (string Target, string Arguments) ReadLink(string path)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            try { return ((string)link.TargetPath, (string)link.Arguments); }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>在暂存位置生成指向固定代理的快捷方式，原快捷方式则按原始字节备份。</summary>
    internal static void CreateLink(string path, string target, string arguments)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            try { link.TargetPath = target; link.Arguments = arguments; link.Save(); }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
