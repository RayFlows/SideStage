/*
 * 文件职责：在隔离用户目录中走真实侧栏入口，验证手动、启动时扫描和跨窗口的偏好持久化。
 * 测试仅查询本机启动登记，不连接子会话。
 */
using System.Diagnostics;
using System.Runtime.InteropServices;
using SideStage.Windows.Platform.Windows;
using SideStage.Windows.RemoteDesktop;

namespace SideStage.App.FrameCheck;

internal static class StartupInventoryCheck
{
    /// <summary>操作验收脚本创建的独立登录任务，核对两次真实按钮点击、任务定义、缓存和行距。</summary>
    internal static int CheckToggle(Guid id)
    {
        string name = $"SideStage-Frame-{id:N}";
        string root = Path.Combine(Path.GetTempPath(), "SideStage.FrameCheck", id.ToString("N"));
        string gate = Path.Combine(root, "Gate");
        var rules = new StartupRegistrations(@"Software\SideStage.Tests\" + id.ToString("N"), gate, root, root);
        var store = new StartupInventory(Path.Combine(root, "StartupInventory"), rules);
        using var tasks = new StartupTaskRules(gate);
        var entry = tasks.Inspect(new("Task", name, @"\"));
        if (entry.Blocked || entry.ReadOnlyReason != null || entry.Fingerprint == null) throw new InvalidOperationException("测试登录任务未就绪。");
        store.Remember(new(StartupInventory.Schema, DateTimeOffset.Now, [entry], []), entry);
        using var window = new ParchmentPreviewForm(new WindowsChildSessionCredentialStore(name), root,
            pollSessions: false, sessionInspector: new Program.FirstUseSessions(), startupInventory: store);
        window.Show();
        Application.DoEvents();
        OpenPage(window);
        var row = Descendants(window).OfType<StartupAppRow>().Single();
        var bounds = row.Bounds;
        foreach (bool blocked in new[] { true, false })
        {
            // 向本窗口的复选框发送原生点击，覆盖真实事件、Windows 回读和写入缓存的完整路径。
            SendMessage(row.Handle, 0x00F5, 0, 0);
            Application.DoEvents();
            if (row.Checked == blocked || tasks.Inspect(entry).Blocked != blocked || store.Cached()!.Items.Single().Blocked != blocked
                || bounds != row.Bounds)
                throw new InvalidOperationException(Descendants(window).OfType<RichTextBox>().Single().Text);
        }
        if (tasks.Inspect(entry).Fingerprint != entry.Fingerprint) throw new InvalidOperationException("原始登录任务未完整恢复。");
        window.Close();
        Console.WriteLine("启动项真实开关、缓存与行距通过。");
        return 0;
    }

    /// <summary>只触发指定验收控件的原生按钮事件，不移动桌面鼠标。</summary>
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    /// <summary>从首次使用起步，验证手动刷新、自动开关、导航和真实查询结果。</summary>
    internal static int Run(string? screenshot)
    {
        using var context = new ApplicationContext();
        Exception? failure = null;
        Application.Idle += Start;
        Application.Run(context);
        if (failure != null) throw new InvalidOperationException("启动项侧栏验收失败。", failure);
        return 0;

        // 保留与生产窗口相同的 UI 消息循环，在循环内依次创建、关闭和重开测试窗口。
        void Start(object? sender, EventArgs args)
        {
            Application.Idle -= Start;
            try { Verify(screenshot); }
            catch (Exception error) { failure = error; }
            finally { context.ExitThread(); }
        }
    }

    /// <summary>在活动的窗口消息循环中逐项操作扫描设置。</summary>
    private static void Verify(string? screenshot)
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "SideStage.FrameCheck", id);
        var store = new StartupInventory(Path.Combine(root, "StartupInventory"));
        var vault = new WindowsChildSessionCredentialStore("SideStage.FrameCheck." + id);
        using (var window = OpenWindow())
        {
            if (store.Cached() != null) throw new InvalidOperationException("未开启时不应在启动时扫描。");
            OpenPage(window);
            var refresh = Action(window, "立即扫描");
            Until(() => refresh.Enabled && store.Cached() != null, window);
            var status = Descendants(window).OfType<RichTextBox>().Single();
            if (!status.Text.Contains("启动项扫描完成")) throw new InvalidOperationException("首次扫描未建立缓存。");
            CheckReadOnlyRows(window, store.Cached()!);
            var toggle = Descendants(window).OfType<CheckBox>().Single(x => x.Text == "启动时自动扫描");
            if (toggle.Checked) throw new InvalidOperationException("首次安装不应自动勾选启动扫描。");
            refresh.PerformClick();
            var back = Descendants(window).OfType<Button>().Single(x => x.AccessibleName == "返回高级设置");
            back.PerformClick();
            Until(() => status.Lines.Count(line => line.Contains("启动项扫描完成")) == 2, window);
            foreach (var line in status.Lines.Where(line => line.Contains("启动项扫描完成"))) Console.WriteLine(line);
            Action(window, "子会话启动项").PerformClick();
            toggle = Descendants(window).OfType<CheckBox>().Single(x => x.Text == "启动时自动扫描");
            toggle.Checked = true;
            if (screenshot != null)
            {
                window.TopMost = true;
                window.Activate();
                Application.DoEvents();
                SettingsInteractionCheck.Capture(window, screenshot);
            }
            window.Close();
        }
        using (var window = OpenWindow())
        {
            var status = Descendants(window).OfType<RichTextBox>().Single();
            Until(() => status.Text.Contains("启动项扫描完成"), window);
            Console.WriteLine(status.Lines.Single(line => line.Contains("启动项扫描完成")));
            OpenPage(window);
            var toggle = Descendants(window).OfType<CheckBox>().Single(x => x.Text == "启动时自动扫描");
            if (!toggle.Checked) throw new InvalidOperationException("自动扫描开关没有跨窗口保存。");
            toggle.Checked = false;
            window.Close();
        }
        using (var window = OpenWindow())
        {
            OpenPage(window);
            if (!Action(window, "立即扫描").Enabled || store.Preferences().OnLaunch)
                throw new InvalidOperationException("关闭自动扫描后应显示缓存并保留手动入口。");
            CheckRefreshLayout(window);
            window.Close();
        }
        Console.WriteLine($"启动项真实扫描和设置验收通过，共 {store.Cached()!.Items.Count()} 条。");

        // 使用同一隔离目录重开窗口，沿用真实的 Shown 和关闭事件。
        ParchmentPreviewForm OpenWindow()
        {
            var window = new ParchmentPreviewForm(vault, root, pollSessions: false, sessionInspector: new Program.FirstUseSessions());
            window.Show();
            Application.DoEvents();
            return window;
        }
    }

    /// <summary>从缓存进入页面，再走修改失败时共用的完整刷新路径，逐行核对宽度、高度和相邻间距。</summary>
    private static void CheckRefreshLayout(Form window)
    {
        var before = Descendants(window).OfType<StartupAppRow>().Select(row => (row.Text, row.Bounds)).ToArray();
        var refresh = Action(window, "立即扫描");
        refresh.PerformClick();
        Until(() => refresh.Enabled, window);
        Application.DoEvents();
        var after = Descendants(window).OfType<StartupAppRow>().Select(row => (row.Text, row.Bounds)).ToArray();
        if (!before.SequenceEqual(after))
            throw new InvalidOperationException("重扫后启动项行距变化：" + string.Join("；", before.Zip(after)
                .Where(pair => pair.First != pair.Second).Take(4).Select(pair => $"{pair.First} → {pair.Second}")));
    }

    /// <summary>展开真实只读分组，确认缓存中的状态、只读原因和任务规则同时出现在行说明中。</summary>
    private static void CheckReadOnlyRows(Form window, StartupSnapshot snapshot)
    {
        var entries = snapshot.Items.Where(item => item.ReadOnlyReason != null && !item.Blocked).ToArray();
        if (entries.Length == 0) return;
        Action(window, $"查看只读项目（{entries.Length}）").PerformClick();
        var rows = Descendants(window).OfType<StartupAppRow>().ToArray();
        foreach (var item in entries)
        {
            string title = item.DisplayName ?? item.Name;
            string state = item.WindowsEnabled switch { true => "Windows 已启用", false => "Windows 已禁用", null => "Windows 启用状态未知" };
            if (!rows.Any(row => row.Text == title && row.AccessibleRole == AccessibleRole.StaticText
                && row.AccessibleDescription!.Contains(state) && row.AccessibleDescription.Contains("只读 · " + item.ReadOnlyReason)
                && (item.Source != "Task" || row.AccessibleDescription.Contains("触发"))))
                throw new InvalidOperationException($"只读行缺少对应的状态或任务说明：{title}");
        }
        Action(window, "收起只读项目").PerformClick();
    }

    /// <summary>通过首页高级设置和其子页入口进入扫描设置。</summary>
    private static void OpenPage(Form window)
    {
        Action(window, "高级设置").PerformClick();
        var preview = (ParchmentPreviewForm)window;
        var session = Descendants(window).OfType<Label>().Single(row => row.Text == "当前会话");
        preview.UpdateSessionCommands(false, true);
        if (session.Visible) throw new InvalidOperationException("窗口未连接时，即使子会话可注销也应隐藏当前会话。");
        preview.SwitchMainView(RdpPreviewConnectionState.Connecting);
        var picture = Descendants(window).OfType<Label>().Single(row => row.Text == "画面与声音");
        var login = Descendants(window).OfType<Label>().Single(row => row.Text == "子会话登录");
        if (!session.Visible || session.PointToScreen(Point.Empty).Y >= picture.PointToScreen(Point.Empty).Y
            || login.Margin.Top <= login.Margin.Bottom)
            throw new InvalidOperationException("连接时当前会话应排在首位，组间留白应大于标题与选项的距离。");
        preview.SwitchMainView(RdpPreviewConnectionState.Disconnected);
        if (session.Visible) throw new InvalidOperationException("断开连接后应立即收起当前会话。");
        preview.UpdateSessionCommands(false, false);
        if (session.Visible) throw new InvalidOperationException("没有可用操作时应收起当前会话。");
        Action(window, "子会话启动项").PerformClick();
        var scan = Action(window, "立即扫描");
        var auto = Descendants(window).OfType<CheckBox>().Single(row => row.Text == "启动时自动扫描");
        var items = Descendants(window).OfType<Label>().Single(row => row.Text == "子会话登录时启动");
        var warning = Descendants(window).OfType<PaperInfoButton>().Single(button => button.AccessibleName == "启动项权限提示");
        bool elevated = StartupElevation.IsAdministrator();
        if (warning.Visible == elevated) throw new InvalidOperationException("首次进入启动项页的提示与实际权限不符。");
        preview.DisplayPrivilegeCheck(false);
        if (!warning.Visible || warning.Parent != items.Parent || warning.Left < items.Right ||
            warning.Text != "\uE7BA" || warning.AccessibleDescription?.Contains("启动项开关已隐藏") != true)
            throw new InvalidOperationException("普通权限下应在标题旁说明需要管理员权限的开关已隐藏。");
        warning.PerformClick();
        if (warning.BubbleBounds.IsEmpty) throw new InvalidOperationException("未能打开启动项权限气泡。");
        preview.DisplayPrivilegeCheck(true);
        if (warning.Visible || !warning.BubbleBounds.IsEmpty) throw new InvalidOperationException("管理员状态下应收起提示和气泡。");
        preview.DisplayPrivilegeCheck(null);
        if (warning.Visible) throw new InvalidOperationException("权限未知时不能断言当前为普通权限。");
        preview.DisplayPrivilegeCheck(elevated);
        var openAll = Action(window, "全部打开");
        var closeAll = Action(window, "全部关闭");
        if (openAll.PointToScreen(Point.Empty).Y < items.PointToScreen(Point.Empty).Y ||
            closeAll.PointToScreen(Point.Empty).Y <= openAll.PointToScreen(Point.Empty).Y)
            throw new InvalidOperationException("两个批量开关应位于子会话登录设置的标题下方。");
        if (scan.PointToScreen(Point.Empty).Y >= auto.PointToScreen(Point.Empty).Y
            || auto.PointToScreen(Point.Empty).Y >= items.PointToScreen(Point.Empty).Y)
            throw new InvalidOperationException("扫描入口和自动开关应在启动项目列表之前。");
    }

    /// <summary>通过可见按钮标题执行用户操作，避免依赖字段或内部事件。</summary>
    private static Button Action(Form window, string text) =>
        Descendants(window).OfType<Button>().Single(button => button.Visible && button.Text == text);

    /// <summary>枚举真实窗口中的后代控件。</summary>
    private static IEnumerable<Control> Descendants(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(child => Descendants(child).Prepend(child));

    /// <summary>持续处理消息直到后台任务完成，同时捕获日志报告的真实查询失败。</summary>
    private static void Until(Func<bool> ready, Form window)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            Application.DoEvents();
            if (timer.Elapsed.TotalSeconds > 90 || Descendants(window).OfType<RichTextBox>().Any(x => x.Text.Contains("启动项扫描失败")))
                throw new InvalidOperationException(Descendants(window).OfType<RichTextBox>().Single().Text);
            Thread.Sleep(20);
        }
    }
}
