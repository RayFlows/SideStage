/*
 * 文件职责：使用临时发布密钥、内存 HTTP 和隔离缓存验证真实签名、缓存、错误线路及取消。
 * 测试边界：附件是普通字节，任何用例都不执行下载、不写生产注册表或访问网络。
 */
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SideStage.App;

namespace SideStage.App.Tests;

[TestClass]
public sealed class UpdateDownloadTests
{
    /// <summary>两个源使用完全相同的信任根；拒绝错误签名、版本、内容以及不安全的 CDN 跳转。</summary>
    [TestMethod]
    [DataRow("CNB")]
    [DataRow("GitHub")]
    [DataRow("signature")]
    [DataRow("version")]
    [DataRow("tamper")]
    [DataRow("different-build")]
    [DataRow("redirect")]
    [DataRow("missing")]
    [DataRow("cancel")]
    public async Task AuthenticatesBeforeOfferingInstall(string mode)
    {
        using var key = RSA.Create(2048);
        var sources = new UpdateSource[] { new(true, new("https://cnb.cool/a/b/-/releases/download/v1.2.0/")),
            new(false, new("https://github.com/a/b/releases/download/v1.2.0/")) };
        var offer = new ReleaseOffer(ReleaseVersion.Parse("1.2.0")!, new("https://github.com/a/b"), "修复", sources,
            mode == "different-build" ? new string('0', 64) : null);
        var source = sources[mode == "GitHub" ? 1 : 0];
        byte[] bytes = Encoding.UTF8.GetBytes("这不是可执行文件。");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        byte[] sums = Encoding.ASCII.GetBytes($"{hash}  {(mode == "version" ? "other.exe" : offer.Installer)}\r\n");
        var sig = key.SignData(sums, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (mode == "signature") sig[0] ^= 1;
        int packages = 0;
        using var http = new HttpClient(new Handler(uri =>
        {
            Assert.AreEqual(source.Assets.Host, uri.Host);
            string file = uri.Segments[^1];
            if (file == "SHA256SUMS.txt") return Blob(sums);
            if (file == "SHA256SUMS.sig") return mode == "missing" ? new(HttpStatusCode.NotFound) : Blob(sig);
            Assert.AreEqual(offer.Installer, file);
            packages++;
            if (mode == "redirect") return new(HttpStatusCode.Found) { Headers = { Location = new("http://example.org/file") } };
            return Blob(mode == "tamper" ? [1, 2, 3] : bytes);
        }));
        var root = Directory.CreateTempSubdirectory("SideStage.DownloadTest-");
        try
        {
            var download = new UpdateDownload(http, root.FullName);
            Task<VerifiedUpdate> Fetch() => download.FetchAsync(offer, source, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                new Progress<int>(), new CancellationToken(mode == "cancel"));
            if (mode is "CNB" or "GitHub")
            {
                var success = await Fetch();
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(success.FilePath));
                Assert.AreEqual(hash, (await Fetch()).Hash);
                Assert.AreEqual(1, packages);
                await File.WriteAllBytesAsync(success.FilePath, [1]); // 缓存被改写时，下一次取用必须重新下载。
                await Fetch();
                Assert.AreEqual(2, packages);
            }
            else
            {
                if (mode == "missing") await Assert.ThrowsAsync<HttpRequestException>(Fetch);
                else if (mode == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(Fetch);
                else await Assert.ThrowsAsync<InvalidDataException>(Fetch);
                Assert.AreEqual(mode is "tamper" or "redirect" ? 1 : 0, packages);
                Assert.IsEmpty(root.EnumerateFiles("*", SearchOption.AllDirectories).ToArray());
            }
        }
        finally { root.Delete(true); }
    }

    /// <summary>模拟发行附件，不提供网络传输。</summary>
    private static HttpResponseMessage Blob(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class Handler(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        /// <summary>仅在内存分发请求，取消时不产生任何附件。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request.RequestUri!));
        }
    }
}
