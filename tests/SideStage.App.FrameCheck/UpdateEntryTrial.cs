/*
 * 文件职责：从 SideStage 的真实主窗口体验、验收启动检查和侧栏手动检查，不直接创建更新弹窗。
 * 隔离边界：发行列表由内存提供，不配置签名公钥，不提供安装包；每次运行使用独立的偏好、日志及凭据项。
 * 验收约定：版本比较、检查开关、窗口激活、跳过判断及按钮事件均走正式代码。
 */
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
        VerifyPermissionSwitch();
        return 0;
    }

    /// <summary>只替换数据来源与持久化位置；关闭时清理本轮创建的唯一键值，不接触已安装版本。</summary>
    private static void WithHome(bool startup, bool skipped, Action<ParchmentPreviewForm, ReleaseList> exercise,
        bool canReturnToLauncher = false, bool openUpdate = false)
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
            }
            using var home = new ParchmentPreviewForm(vault, Path.Combine(Path.GetTempPath(), "SideStage.UpdateEntryTrial", id),
                pollSessions: false, sessionInspector: new Program.FirstUseSessions(),
                enableChildSessions: () => Task.FromException(new InvalidOperationException("更新入口测试不启用或连接真实子会话。")),
                updateFeed: feed, updatePreferenceKey: key, canReturnToLauncher: canReturnToLauncher, openUpdate: openUpdate);
            home.Shown += (_, _) => home.Text = startup ? "SideStage · 自动检查入口测试" : "SideStage · 手动检查入口测试";
            exercise(home, list);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
            vault.Delete();
        }
    }

    /// <summary>
    /// 用真实窗口分两次走完启动器交接。管理员窗口退出并交还更新意图，普通窗口即使关闭了自动检查也能打开下载页。
    /// 验收使用独立偏好、模拟权限来源和离线版本数据。
    /// </summary>
    private static void VerifyPermissionSwitch()
    {
        foreach (bool firstWindow in new[] { true, false })
        {
            WithHome(startup: firstWindow, skipped: !firstWindow, (home, _) =>
            {
                Exception? failure = null;
                home.Shown += async (_, _) =>
                {
                    try
                    {
                        await Task.Yield();
                        home.Activate();
                        await WaitForAsync(() => VisibleOffer(home) != null, "切换权限时没有出现更新窗口。");
                        var buttons = UpdateWindowCheck.Enumerate(VisibleOffer(home)!).OfType<Button>().ToArray();
                        if (firstWindow) buttons.Single(b => b.Text == "以普通权限更新").PerformClick();
                        else if (!buttons.Any(b => b.Text == "下载更新"))
                            throw new InvalidOperationException("普通窗口没有提供下载入口。");
                        if (firstWindow && !home.ContinueUpdateInLauncher)
                            throw new InvalidOperationException("管理员窗口没有将更新请求交还启动器。");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { if (!home.IsDisposed) home.Close(); }
                };
                Application.Run(home);
                if (failure != null) throw new InvalidOperationException("更新权限交接失败。", failure);
            }, canReturnToLauncher: firstWindow, openUpdate: !firstWindow);
        }
        Console.WriteLine("通过：管理员窗口退出后，普通启动器进入下载页。");
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
                    home.Activate();
                    await CheckRouteAsync(home, feed, startup, skipped);
                }
                catch (Exception ex) { failure = ex; }
                finally { home.Close(); }
            };
            Application.Run(home);
            if (failure != null) throw new InvalidOperationException("更新入口验收失败。", failure);
        });
    }

    /// <summary>分别从启动事件或真实侧栏按钮触发检查，核对弹窗归属、请求次数，以及「稍后」后重新打开的窗口。</summary>
    private static async Task CheckRouteAsync(ParchmentPreviewForm home, ReleaseList feed, bool startup, bool skipped)
    {
        var controls = UpdateWindowCheck.Enumerate(home).ToArray();
        var toggle = controls.OfType<SettingsSwitch>().Single(c => c.Text == "启动时检查更新");
        var row = controls.OfType<SettingsActionRow>().Single(c => c.Text == "检查更新" || c.Text == "查看可用更新");
        var settings = controls.OfType<Button>().Single(c => c.Text == "高级设置" || c.Text == "设置 · 有更新");
        if (toggle.Checked != startup) throw new InvalidOperationException("测试场景的启动检查开关不正确。");
        if (startup)
        {
            await WaitForAsync(() => row.Text == "查看可用更新" && row.Enabled, "启动事件没有完成版本检查。");
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
            throw new InvalidOperationException($"入口未弹窗：请求 {feed.Requests} 次，设置行「{row.Text}」，主窗焦点={home.ContainsFocus}，活动窗={Form.ActiveForm?.Text}，当前窗口：{windows}", ex);
        }
        var offer = VisibleOffer(home)!;
        if (feed.Requests != 1 || !UpdateWindowCheck.Enumerate(offer).OfType<Label>().Any(c => c.Text.Contains(Available)))
            throw new InvalidOperationException("更新窗口没有展示本次检查到的版本，或重复请求了发行源。");
        UpdateWindowCheck.Enumerate(offer).OfType<Button>().Single(c => c.Text == "稍后").PerformClick();
        if (offer.Visible) throw new InvalidOperationException("「稍后」未收起更新窗口。");
        if (!row.Visible) settings.PerformClick();
        row.PerformClick();
        if (VisibleOffer(home) != offer || feed.Requests != 1)
            throw new InvalidOperationException("「查看可用更新」应复用本次结果和窗口。");
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

    /// <summary>内存发行源仅响应发行列表，不打开网络连接，也没有可执行附件可供安装。</summary>
    private sealed class ReleaseList : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);

        /// <summary>返回与正式镜像相同的发行列表字段，保留异步等待，以覆盖真实的启动及切回窗口时序。</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
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
