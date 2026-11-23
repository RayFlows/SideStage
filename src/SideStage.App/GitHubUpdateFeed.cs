/*
 * 文件职责：读取构建时指定的公开 GitHub Releases，只识别当前渠道可用的 Windows x64 安装包。
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

/// <summary>一个可以由用户主动打开的发布说明页，不是可执行文件的下载地址。</summary>
internal sealed record ReleaseOffer(ReleaseVersion Version, Uri Page);

/// <summary>仅桌面应用使用的更新源；离线试装版没有仓库配置，永远不发请求。</summary>
internal sealed class GitHubUpdateFeed
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        { MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly HttpClient _client;
    private readonly string? _repo;
    internal ReleaseVersion Current { get; }
    internal bool IsConfigured => _repo != null;

    /// <summary>从应用自身的程序集读取版本和发布时写入的仓库；不信任工作目录、环境变量或用户粘贴的网址。</summary>
    internal static GitHubUpdateFeed FromBuild()
    {
        Assembly assembly = typeof(GitHubUpdateFeed).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        string? repo = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SideStage.UpdateRepository")?.Value;
        return new(Client, version, repo);
    }

    /// <summary>绑定版本、固定仓库和 HTTP 客户端。测试可替换传输层，不需要联网。</summary>
    internal GitHubUpdateFeed(HttpClient client, string version, string? repo)
    {
        _client = client;
        var parsed = ReleaseVersion.Parse(version);
        Current = parsed ?? new ReleaseVersion(new Version(0, 0, 0), 0, 0, "开发版");
        _repo = parsed != null && repo != null && Regex.IsMatch(repo, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z") ? repo : null;
    }

    /// <summary>分页读取公开版本，返回最高的可安装新版本。未配置、不完整响应、限流和取消不等同于「已是最新版」。</summary>
    internal async Task<ReleaseOffer?> CheckAsync(CancellationToken cancellation)
    {
        if (_repo is null) throw new InvalidOperationException("此版本未配置公开发布仓库。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ReleaseOffer? best = null;
        const int pageSize = 50;
        for (int page = 1; page <= 20; page++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/repos/{_repo}/releases?per_page={pageSize}&page={page}");
            request.Headers.UserAgent.ParseAdd("SideStage-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var entries = await response.Content.ReadFromJsonAsync<Entry?[]>(timeout.Token).ConfigureAwait(false)
                ?? throw new JsonException("发布列表为空响应。");
            foreach (var entry in entries)
            {
                if (entry?.Tag is not { Length: > 0 } tag || entry.Draft != false || entry.Prerelease is null) continue;
                ReleaseVersion? version = ReleaseVersion.Parse(tag.StartsWith('v') ? tag[1..] : tag);
                if (version is null || version.CompareTo(Current) <= 0 || version.CompareTo(best?.Version) <= 0) continue;
                if (Current.Channel == 3 && (entry.Prerelease.Value || version.Channel != 3)) continue;
                // 只有上传完成且具有该版本的 x64 安装包才提示；源码标签、草稿和其他平台不算可更新版本。
                if (entry.Assets?.Any(a => a?.State == "uploaded" && a.Name == $"SideStage-{version.Text}-Setup-x64.exe") != true) continue;
                best = new(version, new Uri($"https://github.com/{_repo}/releases/tag/{Uri.EscapeDataString(tag)}"));
            }
            if (entries.Length < pageSize) return best;
        }
        throw new InvalidDataException("发布列表过长，未能完整检查。");
    }

    private sealed record Entry([property: JsonPropertyName("tag_name")] string? Tag, bool? Draft, bool? Prerelease, Asset?[]? Assets);
    private sealed record Asset(string? Name, string? State);
}
