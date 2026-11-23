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
public sealed class GitHubUpdateFeedTests
{
    /// <summary>即使测试版排序靠前、标签错误或没有可下载的资产，正式版也只看正式的 x64 包。</summary>
    [TestMethod]
    public async Task StableOnlyOffersPublishedStableInstaller()
    {
        var entries = new[] { Entry("v2.0.0-beta.3"), Entry("v1.5.0", prerelease: true), Entry("1.6.0", draft: true),
            Entry("1.8.0", asset: "Source.zip"), Entry("1.9.0", state: "open"), Entry("v1.1.0"), Entry("v../../invalid") };
        using HttpClient http = new(new Handler(_ => Json(entries)));
        var offer = await new GitHubUpdateFeed(http, "1.0.0", "example/SideStage").CheckAsync(default);
        Assert.AreEqual("https://github.com/example/SideStage/releases/tag/v1.1.0", offer?.Page.AbsoluteUri);
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
        var offer = await new GitHubUpdateFeed(http, "0.1.0-beta.2", "example/SideStage").CheckAsync(default);
        Assert.AreEqual(2, requests);
        Assert.AreEqual("0.1.0", offer?.Version.Text);
    }

    /// <summary>同一版本或较旧版本不重复提示。</summary>
    [TestMethod]
    public async Task EqualOrOlderHasNoOffer()
    {
        using HttpClient http = new(new Handler(_ => Json(new[] { Entry("v1.0.0"), Entry("v1.0.0-beta.9") })));
        Assert.IsNull(await new GitHubUpdateFeed(http, "1.0.0", "example/SideStage").CheckAsync(default));
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
        var feed = new GitHubUpdateFeed(http, version, repo);
        Assert.IsFalse(feed.IsConfigured);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => feed.CheckAsync(default));
    }

    /// <summary>限流不能返回「没有更新」，取消请求必须能离开等待。</summary>
    [TestMethod]
    public async Task FailureAndCancellationDoNotBecomeUpToDate()
    {
        using HttpClient http = new(new Handler(_ => new(HttpStatusCode.TooManyRequests)));
        var feed = new GitHubUpdateFeed(http, "1.0.0", "example/SideStage");
        await Assert.ThrowsAsync<HttpRequestException>(() => feed.CheckAsync(default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => feed.CheckAsync(new CancellationToken(true)));
    }

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
