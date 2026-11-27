/*
 * 文件职责：验证本地版本排序、测试渠道、发布包筛选、分页以及离线或接口失败时的行为。
 * 测试边界：请求只送往自定义内存处理器；测试不读取凭据、不调用浏览器，也不启动安装包。
 */
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.App;

namespace SideStage.App.Tests;

[TestClass]
public sealed class ReleaseUpdateFeedTests
{
    /// <summary>即使测试版排序靠前、标签错误或没有可下载的资产，正式版也只看正式的 x64 包。</summary>
    [TestMethod]
    public async Task StableOnlyOffersPublishedStableInstaller()
    {
        var entries = new[] { Entry("v2.0.0-beta.3"), Entry("v1.5.0", prerelease: true), Entry("1.6.0", draft: true),
            Entry("1.8.0", asset: "Source.zip"), Entry("1.9.0", state: "open"), Entry("v1.1.0"), Entry("v../../invalid") };
        using HttpClient http = new(new Handler(_ => Json(entries)));
        var offer = await new ReleaseUpdateFeed(http, "1.0.0", "example/SideStage").CheckAsync(default);
        Assert.AreEqual("https://github.com/example/SideStage/releases/tag/v1.1.0", offer?.Page.AbsoluteUri);
    }

    /// <summary>两个发布源按显式订阅筛选。正式版可以试用测试包，取消订阅的测试版只向更高正式版升级。</summary>
    [TestMethod]
    [DataRow("1.0.0", true, "1.2.0-beta.1")]
    [DataRow("1.0.0-beta.1", false, "1.1.0")]
    [DataRow("1.2.0-beta.1", false, null)]
    public async Task SubscriptionOverridesInstalledChannel(string current, bool subscribe, string? expected)
    {
        foreach (bool domestic in new[] { false, true })
        {
            using HttpClient http = new(new Handler(_ => domestic
                ? Json(new[] { MirrorEntry("1.2.0-beta.1", prerelease: true), MirrorEntry("1.1.0") })
                : Json(new[] { Entry("v1.2.0-beta.1", prerelease: true), Entry("v1.1.0") })));
            var feed = new ReleaseUpdateFeed(http, current, domestic ? null : "a/b", domestic ? "c/d" : null);
            Assert.AreEqual(expected, (await feed.CheckAsync(default, subscribe))?.Version.Text);
        }
    }

    /// <summary>第一页填满时继续请求后续页；测试渠道按数字序号比较，且可升级到同主版本正式版。</summary>
    [TestMethod]
    public async Task BetaPaginatesAndCanGraduateToStable()
    {
        int requests = 0;
        using HttpClient http = new(new Handler(request =>
        {
            requests++;
            Assert.AreEqual($"https://api.github.com/repos/example/SideStage/releases?per_page=50&page={requests}", request.RequestUri!.AbsoluteUri);
            return requests == 1 ? Json(Enumerable.Repeat(Entry("0.1.0-beta.10", prerelease: true), 50))
                                  : Json(new[] { Entry("v0.1.0"), Entry("0.1.0-rc.1") });
        }));
        Assert.IsGreaterThan(0, ReleaseVersion.Parse("0.1.0-beta.10")!.CompareTo(ReleaseVersion.Parse("0.1.0-beta.2")));
        var offer = await new ReleaseUpdateFeed(http, "0.1.0-beta.2", "example/SideStage").CheckAsync(default);
        Assert.AreEqual(2, requests);
        Assert.AreEqual("0.1.0", offer?.Version.Text);
    }

    /// <summary>同一版本或较旧版本不重复提示。</summary>
    [TestMethod]
    public async Task EqualOrOlderHasNoOffer()
    {
        using HttpClient http = new(new Handler(_ => Json(new[] { Entry("v1.0.0"), Entry("v1.0.0-beta.9") })));
        Assert.IsNull(await new ReleaseUpdateFeed(http, "1.0.0", "example/SideStage").CheckAsync(default));
    }

    /// <summary>缺少仓库、外部网址及不受支持的本地版本都不会向网络发送请求。</summary>
    [TestMethod]
    [DataRow("1.0.0", null)]
    [DataRow("1.0.0", "https://example.org/download")]
    [DataRow("1.0.0", "example/..")]
    [DataRow("development", "example/SideStage")]
    public async Task UnconfiguredNeverSends(string version, string? repo)
    {
        using HttpClient http = new(new Handler(_ => throw new AssertFailedException("不应发起请求。")));
        var feed = new ReleaseUpdateFeed(http, version, repo);
        Assert.IsFalse(feed.IsConfigured);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => feed.CheckAsync(default));
    }

    /// <summary>限流不能返回「没有更新」，取消请求必须能离开等待。</summary>
    [TestMethod]
    public async Task FailureAndCancellationDoNotBecomeUpToDate()
    {
        using HttpClient http = new(new Handler(_ => new(HttpStatusCode.TooManyRequests)));
        var feed = new ReleaseUpdateFeed(http, "1.0.0", "example/SideStage");
        await Assert.ThrowsAsync<HttpRequestException>(() => feed.CheckAsync(default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => feed.CheckAsync(new CancellationToken(true)));
    }

    /// <summary>模拟 CNB 真实响应：不含 GitHub 的上传状态；匿名读取，用本地构造的标签页而非响应内的链接。</summary>
    [TestMethod]
    [DataRow("1.0.0", "1.1.0", "1.1.0")]
    [DataRow("1.1.0", "1.1.0", null)]
    [DataRow("1.0.0-beta.1", "1.1.0-beta.2", "1.1.0-beta.2")]
    public async Task HealthyMirrorNeverContactsGitHub(string current, string tag, string? expected)
    {
        using HttpClient http = new(new Handler(request =>
        {
            Assert.AreEqual("https://cnb.cool/example/sub/SideStage/-/releases?page_size=50&page=1", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("application/json", request.Headers.Accept.Single().MediaType);
            Assert.IsNull(request.Headers.Authorization);
            return Json(new[] { MirrorEntry("2.0.0-beta.1", draft: true), MirrorEntry(tag) });
        }));
        var offer = await new ReleaseUpdateFeed(http, current, "example/SideStage", "example/sub/SideStage").CheckAsync(default);
        Assert.AreEqual(expected == null ? null : $"https://cnb.cool/example/sub/SideStage/-/releases/tag/v{expected}", offer?.Page.AbsoluteUri);
    }

    /// <summary>镜像限流、断流、只剩旧版、缺少附件或返回登录页面时，用备用源的真实结果和下载页。</summary>
    [TestMethod]
    [DataRow("rate")]
    [DataRow("timeout")]
    [DataRow("html")]
    [DataRow("empty")]
    [DataRow("old")]
    [DataRow("partial")]
    [DataRow("checksum")]
    public async Task UnusableMirrorFallsBackOnce(string failure)
    {
        List<string> hosts = [];
        using HttpClient http = new(new Handler(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "api.github.com") return Json(new[] { Entry("v1.2.0") });
            return failure switch
            {
                "rate" => new(HttpStatusCode.TooManyRequests),
                "timeout" => throw new TaskCanceledException(),
                "html" => new(HttpStatusCode.OK) { Content = new StringContent("<html>登录</html>") },
                "empty" => Json(Array.Empty<object>()),
                "old" => Json(new[] { MirrorEntry("0.9.0") }),
                "partial" => Json(new[] { MirrorEntry("1.0.0"), MirrorEntry("1.2.0", size: 0) }),
                _ => Json(new[] { MirrorEntry("1.2.0", checksum: false) }),
            };
        }));
        var offer = await new ReleaseUpdateFeed(http, "1.0.0", "origin/SideStage", "mirror/SideStage").CheckAsync(default);
        CollectionAssert.AreEqual(new[] { "cnb.cool", "api.github.com" }, hosts);
        Assert.AreEqual("https://github.com/origin/SideStage/releases/tag/v1.2.0", offer?.Page.AbsoluteUri);
    }

    /// <summary>国内源同样翻页；正式用户不会收到渠道字段为 true 或标签自带 beta 后缀的测试包。</summary>
    [TestMethod]
    public async Task MirrorPaginatesAndFiltersStable()
    {
        int page = 0;
        using HttpClient http = new(new Handler(request =>
        {
            Assert.AreEqual($"?page_size=50&page={++page}", request.RequestUri!.Query);
            return page == 1 ? Json(Enumerable.Repeat(MirrorEntry("2.0.0", prerelease: true), 50))
                : Json(new[] { MirrorEntry("3.0.0-beta.4"), MirrorEntry("1.1.0") });
        }));
        var offer = await new ReleaseUpdateFeed(http, "1.0.0", null, "mirror/SideStage").CheckAsync(default);
        Assert.AreEqual("1.1.0", offer?.Version.Text);
        Assert.AreEqual(2, page);
    }

    /// <summary>取消来自窗口而不是镜像时，不再访问备用源；两边都限流则向界面报告失败。</summary>
    [TestMethod]
    public async Task ExplicitCancelAndDoubleFailurePropagate()
    {
        using var stop = new CancellationTokenSource();
        using HttpClient cancel = new(new Handler(request =>
        {
            Assert.AreEqual("cnb.cool", request.RequestUri!.Host);
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
            return Json(Array.Empty<object>());
        }));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ReleaseUpdateFeed(cancel, "1.0.0", "a/b", "c/d").CheckAsync(stop.Token));
        int requests = 0;
        using HttpClient failed = new(new Handler(_ => { requests++; return new(HttpStatusCode.Forbidden); }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ReleaseUpdateFeed(failed, "1.0.0", "a/b", "c/d").CheckAsync(default));
        Assert.AreEqual(2, requests);
    }

    /// <summary>国内路径必须是固定组织下的仓库名。拒绝绝对 URL、路径穿越、查询串和无版本的开发包。</summary>
    [TestMethod]
    [DataRow("1.0.0", "https://cnb.cool/a/b")]
    [DataRow("1.0.0", "a/../b")]
    [DataRow("1.0.0", "a/b?redirect=x")]
    [DataRow("development", "a/b")]
    public void InvalidMirrorIsNotConfigured(string version, string repo)
    {
        using HttpClient http = new(new Handler(_ => throw new AssertFailedException("不应发起请求。")));
        Assert.IsFalse(new ReleaseUpdateFeed(http, version, null, repo).IsConfigured);
    }

    /// <summary>联网构建只有签名附件齐备才推荐；正文为纯文本，两个候选路径固定到完全相同的版本标签。</summary>
    [TestMethod]
    public async Task SignedReleasePairsMirrorsAndIgnoresIncompleteNewerVersion()
    {
        var signed = new { tag_name = "v1.1.0", draft = false, prerelease = false, body = "发行说明",
            assets = new[] { "SideStage-1.1.0-Setup-x64.exe", "SHA256SUMS.txt", "SHA256SUMS.sig" }
                .Select(name => new { name, state = "uploaded" }).ToArray() };
        using HttpClient http = new(new Handler(request => request.RequestUri!.Host == "cnb.cool" ?
            new(HttpStatusCode.ServiceUnavailable) : Json(new[] { Entry("v1.2.0"), signed })));
        var offer = await new ReleaseUpdateFeed(http, "1.0.0", "org/app", "mirror/app", "pinned").CheckAsync(default);
        Assert.IsNotNull(offer);
        Assert.AreEqual("发行说明", offer.Notes);
        CollectionAssert.AreEqual(new[] { "https://cnb.cool/mirror/app/-/releases/download/v1.1.0/",
            "https://github.com/org/app/releases/download/v1.1.0/" }, offer.Sources.Select(s => s.Assets.AbsoluteUri).ToArray());
    }

    /// <summary>构造 CNB 已关联的附件列表；允许模拟空文件、漏传摘要、草稿和预发布。</summary>
    private static object MirrorEntry(string version, long size = 42, bool checksum = true, bool draft = false, bool prerelease = false) =>
        new { tag_name = $"v{version}", draft, prerelease, assets = new[] {
            new { name = $"SideStage-{version}-Setup-x64.exe", size },
            new { name = checksum ? "SHA256SUMS.txt" : "other.txt", size = 96L } } };

    /// <summary>生成一个类似发布 API 的条目，并故意附带不可信的外部链接以验证程序不会打开它。</summary>
    private static object Entry(string tag, bool draft = false, bool prerelease = false, string? asset = null, string state = "uploaded") =>
        new { tag_name = tag, draft, prerelease, html_url = "https://example.invalid/not-the-release",
            assets = new[] { new { state, name = asset ?? $"SideStage-{tag.TrimStart('v')}-Setup-x64.exe" } } };

    /// <summary>使用结构化序列化构造 HTTP 响应。</summary>
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = JsonContent.Create(data) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        /// <summary>仅在内存中响应请求，并模拟正常的取消语义。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request));
        }
    }
}
