/*
 * 文件职责：从 SideStage 的真实主窗口体验、验收启动检查和侧栏手动检查，不直接创建更新弹窗。
 * 隔离边界：发行列表由内存提供，不配置签名公钥，不提供安装包；每次运行使用独立的偏好、日志及凭据项。
 * 验收约定：版本比较、检查开关、窗口激活、跳过判断及按钮事件均走正式代码。
 */
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SideStage.Windows.Platform.Windows;

namespace SideStage.App.FrameCheck;

internal static class UpdateEntryTrial
{
    private const string Current = "0.1.0-beta.5", Available = "0.1.0-beta.6";
    internal const string Notes = "这是本地模拟的新版本，用于测试启动检查和手动检查。没有可下载的安装包，也不会更新已安装的 SideStage。";

    /// <summary>打开可交互的主界面；两个场景只改变初始开关，不代替用户点击，也不强制显示弹窗。</summary>
    internal static int Run(bool startup)
    {
        WithHome(startup, skipped: false, (home, _) => Application.Run(home));
        return 0;
    }

    /// <summary>用独立的主窗依次验证冷启动、关闭启动检查后的首次手动检查，以及跳过版本后的手动查看。</summary>
    internal static int Verify()
    {
        VerifyRoute(startup: false, skipped: false);
        VerifyRoute(startup: true, skipped: false);
        VerifyRoute(startup: true, skipped: true);
        VerifySubscriptionPersistence();
        return 0;
    }

    /// <summary>连续创建多个版本的设置页并共用一份隔离偏好，模拟首次安装、升级和手动退订后的再次升级。</summary>
    private static void VerifySubscriptionPersistence()
    {
        string id = Guid.NewGuid().ToString("N");
        string key = $@"Software\SideStage.UpdateEntryTrial.{id}";
        var vault = new WindowsChildSessionCredentialStore($"SideStage.UpdateEntryTrial.{id}");
        using var client = new HttpClient(new ReleaseList());
        try
        {
            foreach (var (version, expected, turnOff) in new[] {
                ("1.0.0", false, false), ("1.1.0-beta.1", true, false),
                ("1.1.0", true, true), ("1.2.0-beta.1", false, false) })
            {
                using var window = new ParchmentPreviewForm(vault, Path.Combine(Path.GetTempPath(), "SideStage.UpdateEntryTrial", id),
                    pollSessions: false, sessionInspector: new Program.FirstUseSessions(),
                    updateFeed: new ReleaseUpdateFeed(client, version, "fixture/SideStage"), updatePreferenceKey: key);
                var toggle = UpdateWindowCheck.Enumerate(window).OfType<SettingsSwitch>().Single(s => s.Text == "接受测试版更新推送");
                if (toggle.Checked != expected) throw new InvalidOperationException($"{version} 未正确沿用测试版订阅。");
                if (turnOff) toggle.Checked = false;
            }
        }
        finally { Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false); }
        Console.WriteLine("通过：正式版默认关闭、首次安装 Beta 自动订阅、升级保留选择、手动退订持续有效。");
    }

    /// <summary>只替换数据来源与持久化位置；关闭时清理本轮创建的唯一键值，不接触已安装版本。</summary>
    private static void WithHome(bool startup, bool skipped, Action<ParchmentPreviewForm, ReleaseList> exercise,
        string? earlierSkip = null)
    {
        string id = Guid.NewGuid().ToString("N"), key = $@"Software\SideStage.UpdateEntryTrial.{id}";
        var vault = new WindowsChildSessionCredentialStore($"SideStage.UpdateEntryTrial.{id}");
        using var list = new ReleaseList();
        using var client = new HttpClient(list, disposeHandler: false);
        var feed = new ReleaseUpdateFeed(client, Current, "fixture/SideStage", "fixture/SideStage");
        try
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(key, true, RegistryOptions.Volatile))
            {
                // 自动场景走首次运行的默认值；手动场景明确关闭，便于反复从首页测试。
                if (!startup) settings.SetValue("CheckUpdatesOnStartup", 0, RegistryValueKind.DWord);
                if (skipped) settings.SetValue("SkippedUpdate", Available);
                else if (earlierSkip != null) settings.SetValue("SkippedUpdate", earlierSkip);
            }
            using var home = new ParchmentPreviewForm(vault, Path.Combine(Path.GetTempPath(), "SideStage.UpdateEntryTrial", id),
                pollSessions: false, sessionInspector: new Program.FirstUseSessions(),
                enableChildSessions: () => Task.FromException(new InvalidOperationException("更新入口测试不启用或连接真实子会话。")),
                updateFeed: feed, updatePreferenceKey: key);
            home.Shown += (_, _) => home.Text = startup ? "SideStage · 自动检查入口测试" : "SideStage · 手动检查入口测试";
            exercise(home, list);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
            vault.Delete();
        }
    }

    /// <summary>与正式入口一样运行持续的 UI 消息循环，让异步版本检查始终回到拥有主窗句柄的线程。</summary>
    private static void VerifyRoute(bool startup, bool skipped)
    {
        WithHome(startup, skipped, (home, feed) =>
        {
            Exception? failure = null;
            home.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Yield();
                    ActivateTestWindow(home);
                    await CheckRouteAsync(home, feed, startup, skipped);
                }
                catch (Exception ex) { failure = ex; }
                finally { home.Close(); }
            };
            Application.Run(home);
            if (failure != null) throw new InvalidOperationException("更新入口验收失败。", failure);
        }, earlierSkip: startup && !skipped ? Current : null);
    }

    /// <summary>分别从启动事件或真实侧栏按钮触发检查，核对弹窗归属、请求次数，以及「稍后」后重新打开的窗口。</summary>
    private static async Task CheckRouteAsync(ParchmentPreviewForm home, ReleaseList feed, bool startup, bool skipped)
    {
        var controls = UpdateWindowCheck.Enumerate(home).ToArray();
        var toggle = controls.OfType<SettingsSwitch>().Single(c => c.Text == "启动时检查更新");
        var row = controls.OfType<SettingsActionRow>().Single(c => c.Text == "立即检查更新" || c.Text == "查看可用更新");
        var settings = controls.OfType<Button>().Single(c => c.Text == "高级设置" || c.Text == "设置 · 有更新");
        if (toggle.Checked != startup) throw new InvalidOperationException("测试场景的启动检查开关不正确。");
        if (startup)
        {
            await WaitForAsync(() => row.Enabled && (skipped
                ? controls.OfType<Label>().Any(label => label.Text.Contains($"已跳过 {Available}"))
                : row.Text == "查看可用更新"), "启动事件没有完成版本检查。");
        }
        if (!startup || skipped)
        {
            if (feed.Requests != (startup ? 1 : 0) || VisibleOffer(home) != null)
                throw new InvalidOperationException("已关闭启动检查或跳过此版本时，不应自动弹窗。");
            settings.PerformClick();
            if (!row.Visible || !row.Enabled || VisibleOffer(home) != null)
                throw new InvalidOperationException("首页设置按钮应打开侧栏，而不是直接弹出更新窗口。");
            row.Select();
            row.PerformClick();
        }
        try { await WaitForAsync(() => VisibleOffer(home) != null && row.Enabled, "未显示可用更新。"); }
        catch (TimeoutException ex)
        {
            string windows = string.Join("，", Application.OpenForms.Cast<Form>().Select(f =>
                $"{f.Text}（可见={f.Visible}，属于主窗口={f.Owner == home}）"));
            Program.GetWindowThreadProcessId(GetForegroundWindow(), out int process);
            throw new InvalidOperationException($"入口未弹窗：请求 {feed.Requests} 次，设置行「{row.Text}」，主窗焦点={home.ContainsFocus}，前台进程={process}，验收进程={Environment.ProcessId}，活动窗={Form.ActiveForm?.Text}，当前窗口：{windows}", ex);
        }
        var offer = VisibleOffer(home)!;
        var fields = UpdateWindowCheck.Enumerate(offer).ToArray();
        if (!fields.OfType<ReleaseSourceOption>().Any(option => option.Visible && option.Enabled) ||
            !fields.OfType<Button>().Any(button => button.Text == "下载更新" && button.Enabled) ||
            fields.Any(control => control.Text == "以普通权限更新"))
            throw new InvalidOperationException("当前窗口应直接提供可用的下载源和下载按钮。");
        int expectedRequests = skipped ? 2 : 1;
        if (feed.Requests != expectedRequests || !UpdateWindowCheck.Enumerate(offer).OfType<Label>().Any(c => c.Text.Contains(Available)))
            throw new InvalidOperationException("更新窗口没有展示本次检查到的版本，或重复请求了发行源。");
        UpdateWindowCheck.Enumerate(offer).OfType<Button>().Single(c => c.Text == "稍后").PerformClick();
        if (offer.Visible) throw new InvalidOperationException("「稍后」未收起更新窗口。");
        if (!row.Visible) settings.PerformClick();
        row.PerformClick();
        if (VisibleOffer(home) != offer || feed.Requests != expectedRequests)
            throw new InvalidOperationException("「查看可用更新」应复用本次结果和窗口。");
        UpdateWindowCheck.Enumerate(offer).OfType<Button>().Single(b => b.Text == "跳过此版本").PerformClick();
        var subscription = controls.OfType<SettingsSwitch>().Single(c => c.Text == "接受测试版更新推送");
        if (!subscription.Checked || row.Text != "立即检查更新" || settings.Text != "高级设置" || VisibleOffer(home) != null)
            throw new InvalidOperationException("跳过本版后应保留订阅并撤下本版提示。");
        subscription.Checked = false;
        row.PerformClick();
        await WaitForAsync(() => row.Enabled && controls.OfType<Label>().Any(l => l.Text.Contains("未发现可用的新版本")),
            "关闭订阅后仍推荐测试包。");
        Console.WriteLine(skipped ? "通过：跳过的版本不自动弹出，仍可手动查看。" :
            startup ? "通过：冷启动自动检查并弹出，稍后可从侧栏重开。" : "通过：启动开关关闭时零请求，点侧栏「检查更新」才检查并弹出。");
    }

    /// <summary>等待可观察的 UI 状态，不阻塞界面线程或用第二个消息循环抢先处理窗口事件。</summary>
    private static async Task WaitForAsync(Func<bool> ready, string stage)
    {
        long deadline = Environment.TickCount64 + 10000;
        while (!ready())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException(stage);
            await Task.Delay(30);
        }
    }

    /// <summary>只认当前主窗口拥有且实际可见的更新窗，避免前一轮残留引起误判。</summary>
    private static ReleaseUpdateWindow? VisibleOffer(Form owner) =>
        owner.OwnedForms.OfType<ReleaseUpdateWindow>().SingleOrDefault(f => f.Visible);

    /// <summary>窗口管理器可能拒绝后台进程申请前台。只在确属本测试窗口的标题空白处模拟单击。</summary>
    private static void ActivateTestWindow(Form window)
    {
        window.TopMost = true;
        window.Activate();
        if (Form.ActiveForm == window) return;
        Point point = window.PointToScreen(new Point(window.ClientSize.Width / 2, window.LogicalToDeviceUnits(20)));
        if (Control.FromChildHandle(SettingsInteractionCheck.WindowFromPoint(point))?.FindForm() != window) return;
        Point original = Cursor.Position;
        try
        {
            Cursor.Position = point;
            mouse_event(2, 0, 0, 0, 0);
            mouse_event(4, 0, 0, 0, 0);
        }
        finally { Cursor.Position = original; }
    }

    /// <summary>向已核验的测试窗口发出左键按下与松开，让 Windows 自行完成激活。</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);

    /// <summary>读取前台窗口以便区分应用延后提示和外部程序占用前台。</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    /// <summary>内存发行源仅响应发行列表，不打开网络连接，也没有可执行附件可供安装。</summary>
    private sealed class ReleaseList : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);

        /// <summary>返回与正式镜像相同的发行列表字段，保留异步等待，以覆盖真实的启动及切回窗口时序。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (request.RequestUri is { Host: "api.github.com", AbsolutePath: "/repos/fixture/SideStage/releases" })
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) };
            if (request.RequestUri is not { Host: "cnb.cool", AbsolutePath: "/fixture/SideStage/-/releases" })
                throw new InvalidOperationException("测试发行源只接受版本检查。");
            Interlocked.Increment(ref _requests);
            await Task.Delay(650, cancel).ConfigureAwait(false);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new {
                tag_name = $"v{Available}", draft = false, prerelease = true,
                body = Notes,
                assets = new[] { $"SideStage-{Available}-Setup-x64.exe", "SHA256SUMS.txt", "SHA256SUMS.sig" }
                    .Select(name => new { name, size = 1 }).ToArray()
            } }) };
        }
    }
}
