/*
 * 文件职责：执行只读启动项扫描，并在当前用户目录保存最近一次成功的快照。
 * 扫描约定：每次重新读取登录登记和所有应用包清单，历史快照只用于展示。
 * 持久化约定：独立保存用户偏好及扫描结果。完整写入临时文件后替换，失败时保留上次结果。
 */
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SideStage.App;

/// <summary>控制是否在应用启动时刷新清单。旧配置的扫描方式字段由反序列化器忽略。</summary>
internal sealed record StartupScanPreferences(bool OnLaunch = false);

/// <summary>Windows 对同一条运行中任务再次触发时的处理方式。未知表示未取得有效定义。</summary>
internal enum StartupRepeatPolicy { Unknown, IgnoreNew, Parallel, Queue, StopExisting }

/// <summary>一条登录登记的扫描结果。WindowsEnabled 表示系统登记状态，Blocked 表示分身规则。</summary>
internal sealed record StartupEntry(string Source, string Name, string Detail, string? IconPath = null,
    string? Fingerprint = null, string? ReadOnlyReason = null, bool Blocked = false, string? DisplayName = null,
    bool? WindowsEnabled = null, StartupRepeatPolicy RepeatPolicy = StartupRepeatPolicy.Unknown, bool RequiresAdmin = false);

/// <summary>打包保存普通登记及按应用包版本索引的声明，包括没有声明的包。</summary>
internal sealed record StartupSnapshot(int Schema, DateTimeOffset Completed, StartupEntry[] Entries, Dictionary<string, StartupEntry[]> Packages)
{
    internal bool MatchesProcess => Elevated == StartupElevation.IsAdministrator();
    public bool Elevated { get; init; } = StartupElevation.IsAdministrator();
    internal IEnumerable<StartupEntry> Items => Entries.Concat(Packages.Values.SelectMany(items => items));
}

/// <summary>只读取写当前用户的快照与扫描偏好，可为验收指定隔离目录。</summary>
internal sealed class StartupInventory(string directory, StartupRegistrations? registrations = null)
{
    // 旧快照未核对任务写入权限。升级后首次打开时按当前普通账户重新分组。
    internal const int Schema = 6;
    internal StartupRegistrations Registrations { get; } = registrations ?? new();
    internal sealed record ScanOutput(StartupEntry[] Entries, Dictionary<string, StartupEntry[]> Packages);

    /// <summary>加载开关和方式。没有偏好或文件已损坏时使用首次安装的默认值。</summary>
    internal StartupScanPreferences Preferences() => Read<StartupScanPreferences>("preferences.json") ?? new();

    /// <summary>持久化启动时自动扫描开关，手动扫描始终可用。</summary>
    internal void SavePreferences(StartupScanPreferences preferences) => Write("preferences.json", preferences);

    /// <summary>只展示结构完整的最近成功快照，首次进入且没有有效快照时重新扫描。</summary>
    internal StartupSnapshot? Cached()
    {
        var cached = Read<StartupSnapshot>("inventory.json");
        return cached is { Schema: Schema, Entries: not null, Packages: not null, MatchesProcess: true }
            && cached.Entries.All(Valid) && cached.Packages.Values.All(items => items != null && items.All(Valid)) ? cached : null;
    }

    /// <summary>拒绝缓存中的空记录，避免渲染名称或分组时中断设置页。</summary>
    private static bool Valid(StartupEntry? entry) => entry is { Source: not null, Name: not null, Detail: not null };

    /// <summary>使用系统 Windows PowerShell 的只读组件，取消或超时时回收本次查询进程。</summary>
    internal async Task<StartupSnapshot> ScanAsync(CancellationToken token)
    {
        using var resource = typeof(StartupInventory).Assembly.GetManifestResourceStream("SideStage.StartupInventory.ps1")!;
        using var reader = new StreamReader(resource);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", await reader.ReadToEndAsync(token) })
            start.ArgumentList.Add(argument);
        start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules");
        using var process = Process.Start(start) ?? throw new IOException("无法启动系统查询。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0) throw new IOException("Windows 未能读取完整的启动项列表。");
            var scan = JsonSerializer.Deserialize<ScanOutput>(await output) ?? throw new InvalidDataException("扫描结果为空。");
            // 本地登记与开关共用同一读取器，显示状态以 Windows 正在使用的入口为准。
            using var tasks = new StartupTaskRules(Registrations.GateDirectory);
            using var protectedTasks = new StartupTaskRules(ProtectedStartupStore.Location, protectedStore: true);
            if (scan.Entries == null || scan.Packages == null || !scan.Entries.All(Valid)
                || !scan.Packages.Values.All(items => items != null && items.All(Valid)))
                throw new InvalidDataException("Windows 返回的启动项列表不完整。");
            var entries = Registrations.ReadEntries().Concat(scan.Entries.Select(seed =>
            {
                var entry = tasks.Inspect(seed);
                return Registrations.SupportsProtectedTasks && entry.RequiresAdmin ? protectedTasks.Inspect(seed) : entry;
            })).ToArray();
            var snapshot = new StartupSnapshot(Schema, DateTimeOffset.Now, entries, scan.Packages);
            token.ThrowIfCancellationRequested();
            Write("inventory.json", snapshot);
            return snapshot;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("扫描超时。"); }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }

    /// <summary>只更新被用户修改的那一行，保留扫描时间与其他缓存，浏览页面无需重新枚举。</summary>
    internal StartupSnapshot Remember(StartupSnapshot snapshot, StartupEntry changed)
        => Remember(snapshot, new[] { changed });

    /// <summary>批量完成后只替换一次缓存，保留原清单顺序及没有成功改动的登记。</summary>
    internal StartupSnapshot Remember(StartupSnapshot snapshot, IEnumerable<StartupEntry> changed)
    {
        var byLocation = changed.ToDictionary(item => (item.Source, item.Detail, item.Name));
        var result = snapshot with { Entries = snapshot.Entries.Select(entry =>
            byLocation.GetValueOrDefault((entry.Source, entry.Detail, entry.Name), entry)).ToArray() };
        Write("inventory.json", result);
        return result;
    }

    /// <summary>读取单个用户文件，缺失或损坏的内容视为没有可复用的数据。</summary>
    private T? Read<T>(string name) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(directory, name))); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>在同一目录以原子替换方式写入，防止读到另一个窗口尚未写完的 JSON。</summary>
    private void Write<T>(string name, T data)
    {
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(data));
            File.Move(temp, Path.Combine(directory, name), overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
