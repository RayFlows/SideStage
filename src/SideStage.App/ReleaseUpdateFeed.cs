/*
 * 文件职责：优先读取构建时指定的 CNB 国内发行库，不可用时查 GitHub；两者共用渠道与版本排序规则。
 * 信任边界：不读取用户凭据，不发送本机标识，不下载或运行程序；发布页由固定仓库和已验证的标签构造。
 * 发行约定：三段数字版本，可附 alpha.N、beta.N 或 rc.N。发布标签可带小写 v 前缀。
 */
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SideStage.App;

/// <summary>比较项目采用的发行版本；同一主版本内按 alpha、beta、rc、正式版及数字序号排序。</summary>
internal sealed record ReleaseVersion(Version Core, int Channel, int Serial, string Text) : IComparable<ReleaseVersion>
{
    /// <summary>解析仓库允许的版本格式；过大的数字、非标准标签和未知渠道不作为安装候选。</summary>
    internal static ReleaseVersion? Parse(string? value)
    {
        if (value is not { Length: > 0 and <= 64 }) return null;
        Match m = Regex.Match(value, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(alpha|beta|rc)\.(0|[1-9][0-9]*))?\z");
        if (!m.Success || !Version.TryParse(value.Split('-')[0], out Version? core) ||
            Math.Max(core.Major, Math.Max(core.Minor, core.Build)) > ushort.MaxValue) return null;
        int channel = m.Groups[4].Value switch { "alpha" => 0, "beta" => 1, "rc" => 2, _ => 3 };
        int serial = 0;
        if (channel != 3 && !int.TryParse(m.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out serial)) return null;
        return new(core, channel, serial, value);
    }

    /// <summary>按数字而不是字符串比较，以便 beta.10 正确排在 beta.2 之后。</summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int result = Core.CompareTo(other.Core);
        if (result == 0) result = Channel.CompareTo(other.Channel);
        return result != 0 ? result : Serial.CompareTo(other.Serial);
    }
}

/// <summary>同一标签的公开来源。根路径完全由构建仓库与校验过的标签组成，不采用发布说明内的链接。</summary>
internal sealed record UpdateSource(bool Domestic, Uri Assets);

/// <summary>界面展示的版本、纯文本说明与可选下载源；所有候选必须提供同一版本的签名安装包。</summary>
internal sealed record ReleaseOffer(ReleaseVersion Version, Uri Page, string Notes, IReadOnlyList<UpdateSource> Sources,
    string? ExpectedHash = null)
{
    internal string Installer => $"SideStage-{Version.Text}-Setup-x64.exe";
}

/// <summary>仅桌面应用使用的更新源；离线试装版没有仓库配置，永远不发请求。</summary>
internal sealed class ReleaseUpdateFeed
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        { MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly HttpClient _client;
    private readonly string? _github;
    private readonly string? _cnb;
    internal string? SigningKey { get; }
    internal ReleaseVersion Current { get; }
    internal bool IsConfigured => _github != null || _cnb != null;

    /// <summary>从应用自身的程序集读取版本和发布时写入的仓库；不信任工作目录、环境变量或用户粘贴的网址。</summary>
    internal static ReleaseUpdateFeed FromBuild()
    {
        Assembly assembly = typeof(ReleaseUpdateFeed).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        return new(Client, version, metadata.FirstOrDefault(a => a.Key == "SideStage.UpdateRepository")?.Value,
            metadata.FirstOrDefault(a => a.Key == "SideStage.CnbRepository")?.Value,
            metadata.FirstOrDefault(a => a.Key == "SideStage.UpdatePublicKey")?.Value);
    }

    /// <summary>绑定版本、固定仓库和 HTTP 客户端。测试可替换传输层，不需要联网。</summary>
    internal ReleaseUpdateFeed(HttpClient client, string version, string? github, string? cnb = null, string? signingKey = null)
    {
        _client = client;
        SigningKey = signingKey;
        var parsed = ReleaseVersion.Parse(version);
        Current = parsed ?? new ReleaseVersion(new Version(0, 0, 0), 0, 0, "开发版");
        if (parsed == null) return;
        _github = github != null && Regex.IsMatch(github, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z") ? github : null;
        _cnb = cnb is { Length: <= 255 } && Regex.IsMatch(cnb, @"\A(?:[A-Za-z0-9][A-Za-z0-9_.-]*/)+[A-Za-z0-9][A-Za-z0-9_.-]*\z") ? cnb : null;
    }

    /// <summary>先查国内源。只有镜像能确认当前或更高版本齐备时才采用其结果；关闭窗口取消所有后续请求。</summary>
    internal async Task<ReleaseOffer?> CheckAsync(CancellationToken cancellation, bool? includePrereleases = null)
    {
        if (!IsConfigured) throw new InvalidOperationException("此版本未配置公开发布仓库。");
        bool previews = includePrereleases ?? Current.Channel != 3;
        if (_cnb != null)
        {
            try
            {
                var mirror = await ReadLatestAsync(_cnb, true, previews, cancellation).ConfigureAwait(false);
                if (mirror == null && _github == null) return null;
                if (mirror == null) throw new InvalidDataException("国内源尚无此渠道的完整发行包。");
                int comparison = mirror.Version.CompareTo(Current);
                if (comparison > 0) return mirror;
                if (comparison == 0 || _github == null) return null;
            }
            catch (Exception ex) when (_github != null && !cancellation.IsCancellationRequested &&
                ex is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
            {
                // 仅忽略国内源自身的失败。备用源有独立超时；若也失败，交给界面显示检查失败。
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var latest = await ReadLatestAsync(_github!, false, previews, cancellation).ConfigureAwait(false);
        return latest != null && latest.Version.CompareTo(Current) > 0 ? latest : null;
    }

    /// <summary>分页读取一个固定来源，找出该渠道最高版本。两个来源的分页参数、附件完成条件和发布页各不相同。</summary>
    private async Task<ReleaseOffer?> ReadLatestAsync(string repo, bool cnb, bool includePrereleases, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(cnb ? 6 : 15));
        ReleaseOffer? best = null;
        ReleaseVersion? newestMirrorTag = null;
        const int pageSize = 50;
        for (int page = 1; page <= 20; page++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, cnb
                ? $"https://cnb.cool/{repo}/-/releases?page_size={pageSize}&page={page}"
                : $"https://api.github.com/repos/{repo}/releases?per_page={pageSize}&page={page}");
            request.Headers.UserAgent.ParseAdd("SideStage-UpdateCheck");
            // CNB 公共站点在同一路径上按 Accept 区分 HTML 和匿名 JSON；客户端无需携带写入令牌。
            request.Headers.Accept.ParseAdd(cnb ? "application/json" : "application/vnd.github+json");
            using HttpResponseMessage response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var entries = await response.Content.ReadFromJsonAsync<Entry?[]>(timeout.Token).ConfigureAwait(false)
                ?? throw new JsonException("发布列表为空响应。");
            foreach (var entry in entries)
            {
                if (entry?.Tag is not { Length: > 0 } tag || entry.Draft != false || entry.Prerelease is null) continue;
                ReleaseVersion? version = ReleaseVersion.Parse(tag.StartsWith('v') ? tag[1..] : tag);
                if (version is null || version.CompareTo(best?.Version) <= 0) continue;
                if (!includePrereleases && (entry.Prerelease.Value || version.Channel != 3)) continue;
                // 只有上传完成且具有该版本的 x64 安装包才提示；源码标签、草稿和其他平台不算可更新版本。
                if (cnb && version.CompareTo(newestMirrorTag) > 0) newestMirrorTag = version;
                string installer = $"SideStage-{version.Text}-Setup-x64.exe";
                var asset = entry.Assets?.FirstOrDefault(a => a?.Name == installer && (cnb ? a.Size > 0 : a.State == "uploaded"));
                if (asset == null || entry.Assets == null) continue;
                if (cnb && !entry.Assets.Any(a => a?.Name == "SHA256SUMS.txt" && a.Size > 0)) continue;
                // 联网发行包不推荐签名附件尚未上传完毕的版本。下载时仍须验证签名内容。
                if (!string.IsNullOrEmpty(SigningKey) && new[] { "SHA256SUMS.txt", "SHA256SUMS.sig" }.Any(name =>
                    !entry.Assets.Any(a => a?.Name == name && (cnb ? a.Size > 0 : a.State == "uploaded")))) continue;
                string pages = cnb ? $"https://cnb.cool/{repo}/-/releases" : $"https://github.com/{repo}/releases";
                var sources = new List<UpdateSource>(2);
                if (_cnb != null) sources.Add(new(true, new($"https://cnb.cool/{_cnb}/-/releases/download/{tag}/")));
                if (_github != null) sources.Add(new(false, new($"https://github.com/{_github}/releases/download/{tag}/")));
                string notes = entry.Body?.Trim() ?? "";
                string? hash = cnb ? (asset.HashAlgo == "sha256" ? asset.HashValue : null) :
                    (asset.Digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? asset.Digest[7..] : null);
                best = new(version, new Uri($"{pages}/tag/{tag}"), notes.Length > 16000 ? notes[..16000] : notes, sources, hash);
            }
            if (entries.Length < pageSize)
            {
                if (newestMirrorTag?.CompareTo(best?.Version) > 0)
                    throw new InvalidDataException("国内源的最新发行附件尚未同步完成。");
                return best;
            }
        }
        throw new InvalidDataException("发布列表过长，未能完整检查。");
    }

    private sealed record Entry([property: JsonPropertyName("tag_name")] string? Tag, bool? Draft, bool? Prerelease, Asset?[]? Assets, string? Body);
    private sealed record Asset(string? Name, string? State, long? Size, string? Digest,
        [property: JsonPropertyName("hash_algo")] string? HashAlgo, [property: JsonPropertyName("hash_value")] string? HashValue);
}
