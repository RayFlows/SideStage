/*
 * 文件职责：从用户选择的固定发行库下载签名清单和安装包，不调用安装程序。
 * 信任边界：内置公钥验证清单原始字节，清单须精确绑定版本和文件名；镜像不能自行签发更新。
 * 文件生命周期：只在当前用户的独立暂存目录写入，取消、超时、摘要不符均不返回可安装对象。
 */
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SideStage.App;

/// <summary>仅表示已经校验的安装包；更新器在执行前还会重新核对同一摘要。</summary>
internal sealed record VerifiedUpdate(string FilePath, string Hash);

internal sealed class UpdateDownload
{
    private const int MaxPackage = 300 * 1024 * 1024;
    private static readonly HttpClient Network = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    internal static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SideStage", "Updates");
    private readonly HttpClient _http;
    private readonly string _root;

    /// <summary>生产使用无凭据的专用客户端；测试可以注入内存传输和自己的缓存目录。</summary>
    internal UpdateDownload(HttpClient? http = null, string? root = null) => (_http, _root) = (http ?? Network, root ?? Cache);

    /// <summary>先认证小清单，再流式写入并计算摘要；同一版本稍后重试时也会复验已有缓存。</summary>
    internal async Task<VerifiedUpdate> FetchAsync(ReleaseOffer offer, UpdateSource source, string? publicKey,
        IProgress<int> progress, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(publicKey) || !offer.Sources.Contains(source))
            throw new InvalidDataException("此构建没有可信的更新来源或发布公钥。");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromMinutes(12));
        var token = limit.Token;
        byte[] sums = await ReadSmallAsync(new(source.Assets, "SHA256SUMS.txt"), token).ConfigureAwait(false);
        byte[] signature = await ReadSmallAsync(new(source.Assets, "SHA256SUMS.sig"), token).ConfigureAwait(false);
        using RSA key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!key.VerifyData(sums, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("发布签名不正确，已停止更新。");
        string line = Encoding.ASCII.GetString(sums);
        var match = Regex.Match(line, @"\A([0-9a-fA-F]{64})  " + Regex.Escape(offer.Installer) + @"(?:\r?\n)?\z");
        if (!match.Success) throw new InvalidDataException("校验清单不属于所选版本。");
        string hash = match.Groups[1].Value.ToLowerInvariant();
        if (offer.ExpectedHash != null && !string.Equals(hash, offer.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("该线路的安装包与检查更新时的发行记录不同。");
        RemoveExpiredCaches(hash);
        string directory = Directory.CreateDirectory(Path.Combine(_root, hash)).FullName;
        string final = Path.Combine(directory, offer.Installer);
        if (File.Exists(final))
        {
            using var cached = File.OpenRead(final);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(cached, token).ConfigureAwait(false)) == hash)
                return new(final, hash);
        }
        string partial = Path.Combine(directory, $"{Guid.NewGuid():N}.part");
        try
        {
            using var response = await OpenAsync(new(source.Assets, offer.Installer), token).ConfigureAwait(false);
            long? length = response.Content.Headers.ContentLength;
            if (length is 0 or > MaxPackage) throw new InvalidDataException("安装包大小不符合预期。");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536];
                long total = 0;
                int last = -2, read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxPackage) throw new InvalidDataException("安装包超过大小限制。");
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    int value = length > 0 ? (int)Math.Min(99, total * 100 / length.Value) : -1;
                    if (value != last) { progress.Report(value); last = value; }
                }
                if (total == 0 || (length.HasValue && total != length) || Convert.ToHexStringLower(digest.GetHashAndReset()) != hash)
                    throw new InvalidDataException("安装包不完整或 SHA-256 不匹配，未安装。");
            }
            File.Move(partial, final, true);
            progress.Report(100);
            return new(final, hash);
        }
        finally { File.Delete(partial); }
    }

    /// <summary>下次下载时回收至少十四天未使用的旧缓存。只处理本程序固定格式的目录及附件，不遍历链接。</summary>
    private void RemoveExpiredCaches(string keep)
    {
        try
        {
            var root = new DirectoryInfo(_root);
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            foreach (var item in root.EnumerateDirectories())
            {
                if (item.Name == keep || !Regex.IsMatch(item.Name, @"\A[0-9a-f]{64}\z") ||
                    item.Attributes.HasFlag(FileAttributes.ReparsePoint) || item.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-14)) continue;
                foreach (var file in item.EnumerateFiles())
                    if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint) && (file.Name is "install.log" or "SideStage.Updater.exe" ||
                        Regex.IsMatch(file.Name, @"\A(?:[0-9a-f]{32}\.part|SideStage-\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?-Setup-x64\.exe)\z")))
                        file.Delete();
                if (!item.EnumerateFileSystemInfos().Any()) item.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 另一实例正在使用时留待下次。 */ }
    }

    /// <summary>限制清单和签名的实际读取字节，防止无长度响应将大量数据送入内存。</summary>
    private async Task<byte[]> ReadSmallAsync(Uri uri, CancellationToken token)
    {
        using var response = await OpenAsync(uri, token).ConfigureAwait(false);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] small = new byte[8193];
        int count = await input.ReadAtLeastAsync(small, small.Length, false, token).ConfigureAwait(false);
        if (count == 0 || count == small.Length) throw new InvalidDataException("发布清单或签名大小异常。");
        return small[..count];
    }

    /// <summary>支持发行平台的有限次 HTTPS CDN 跳转；不转发令牌，也不接受发布正文提供的目标。</summary>
    private async Task<HttpResponseMessage> OpenAsync(Uri uri, CancellationToken token)
    {
        for (int i = 0; i < 5; i++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !(uri.Host is "github.com" or "cnb.cool" or "api.cnb.cool" ||
                  uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal) ||
                  uri.Host.EndsWith(".myqcloud.com", StringComparison.Ordinal) ||
                  uri.Host.EndsWith(".cnb.cool", StringComparison.Ordinal)))
                throw new InvalidDataException("下载源返回了不受支持的跳转地址。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SideStage-Updater");
            var reply = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (reply.IsSuccessStatusCode) return reply;
            using (reply)
            {
                if ((int)reply.StatusCode is 301 or 302 or 303 or 307 or 308 && reply.Headers.Location is { } next)
                { uri = new(uri, next); continue; }
                reply.EnsureSuccessStatusCode();
            }
        }
        throw new InvalidDataException("下载源跳转次数过多。");
    }
}
