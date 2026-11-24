<#
.SYNOPSIS
    将 GitHub 已发布的 SideStage 安装包原样同步到维护者指定的 CNB 公开仓库。
.DESCRIPTION
    使用 PowerShell 7。先校验 GitHub 附件，再非强制推送单个标签，上传到草稿并发布。
    中断后可以重跑；已公开的版本只核对文件，不覆盖。写入令牌仅来自 CNB_TOKEN 环境变量。
.PARAMETER CheckOnly
    仅匿名下载并校验 GitHub 发行附件，不需要 CNB 账号，不更改任何远端或本地 Git 引用。
#>
#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\Av?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(alpha|beta|rc)\.(0|[1-9]\d*))?\z')][string] $Tag,
    [ValidatePattern('\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z')][string] $GitHubRepository = 'RayFlows/SideStage',
    [ValidateLength(3,255)][ValidatePattern('\A(?:[A-Za-z0-9][A-Za-z0-9_.-]*/)+[A-Za-z0-9][A-Za-z0-9_.-]*\z')][string] $CnbRepository,
    [switch] $CheckOnly
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$version = $Tag -creplace '^v', ''
$installer = "SideStage-$version-Setup-x64.exe"
$files = @('SHA256SUMS.txt', $installer)
$headers = @{ Accept = 'application/json'; 'User-Agent' = 'SideStage-ReleaseMirror' }
$stage = Join-Path ([IO.Path]::GetTempPath()) ("sidestage-cnb-" + [guid]::NewGuid().ToString('N'))

function Invoke-CnbApi([string] $Method, [uri] $Uri, [hashtable] $Body, [switch] $AllowMissing) {
    <# 仅对固定仓库的 CNB API 发送令牌，拒绝跨域跳转。404 仅在查询可不存在的发行版时放行。 #>
    if ($Uri.GetLeftPart([UriPartial]::Authority) -cne 'https://api.cnb.cool' -or
        !$Uri.AbsolutePath.StartsWith("/$CnbRepository/-/releases", [StringComparison]::Ordinal)) { throw '拒绝向非发行 API 发送凭证。' }
    $options = @{ Method = $Method; Uri = $Uri; Headers = @{ Authorization = "Bearer $env:CNB_TOKEN"; Accept = 'application/json' }
        MaximumRedirection = 0; SkipHttpErrorCheck = $true; TimeoutSec = 30 }
    if ($Body) { $options.ContentType = 'application/json'; $options.Body = ConvertTo-Json -InputObject $Body -Compress }
    $result = Invoke-WebRequest @options
    if ($AllowMissing -and $result.StatusCode -eq 404) { return }
    if ($result.StatusCode -notin (200..299)) { throw "CNB 发行 API 返回 HTTP $($result.StatusCode)。" }
    if ($result.Content -and $result.Headers['Content-Type'] -match 'json') { $result.Content | ConvertFrom-Json }
}

function Assert-PublicCopy([string] $Root) {
    <# 从用户实际使用的公开入口回读两个附件并逐字节摘要核对，未免登录开放的镜像不会通过。 #>
    foreach ($file in $files) {
        $copy = Join-Path $stage 'public-copy'
        Invoke-WebRequest -Uri "$Root/download/$Tag/$file" -OutFile $copy -TimeoutSec 180
        if ((Get-FileHash -LiteralPath $copy).Hash -cne (Get-FileHash -LiteralPath (Join-Path $stage $file)).Hash) {
            throw "CNB 的 $file 与 GitHub 不同；不会覆盖已发布版本。"
        }
    }
}

if (!$CheckOnly -and (!$CnbRepository -or !$env:CNB_TOKEN)) { throw '发布需要明确的 CNB 仓库和 CNB_TOKEN；仅检查时使用 -CheckOnly。' }
$null = New-Item -ItemType Directory -Path $stage
try {
    $gh = Invoke-RestMethod -Uri "https://api.github.com/repos/$GitHubRepository/releases/tags/$Tag" -Headers $headers -TimeoutSec 30
    if ($gh.tag_name -cne $Tag -or $gh.draft -ne $false -or ($version.Contains('-') -and !$gh.prerelease)) { throw 'GitHub 标签、草稿状态或测试渠道不符合发行约定。' }
    # 保留历史版本的双附件格式。新版本签名与正文一样从 GitHub 校验后原样镜像。
    if ($gh.assets.name -ccontains 'SHA256SUMS.sig') { $files += 'SHA256SUMS.sig' }
    elseif ($version -cnotin @('0.1.0-beta.1','0.1.0-beta.2','0.1.0-beta.3','0.1.0-beta.4')) {
        throw '新发行版缺少 SHA256SUMS.sig，不能同步为可更新版本。'
    }
    foreach ($file in $files) {
        $asset = @($gh.assets | Where-Object { $_.name -ceq $file -and $_.state -ceq 'uploaded' })
        if ($asset.Count -ne 1 -or $asset[0].size -le 0) { throw "GitHub 尚无完整的 $file。" }
        $path = Join-Path $stage $file
        Invoke-WebRequest -Uri "https://github.com/$GitHubRepository/releases/download/$Tag/$file" -OutFile $path -TimeoutSec 180
        $hash = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
        if ("sha256:$hash" -cne $asset[0].digest) { throw "GitHub 附件 $file 的 SHA-256 不匹配。" }
    }
    $sums = (Get-Content -LiteralPath (Join-Path $stage $files[0]) -Raw).TrimEnd("`r","`n")
    $installerHash = (Get-FileHash -LiteralPath (Join-Path $stage $installer)).Hash.ToLowerInvariant()
    if ($sums -cne "$installerHash  $installer") { throw '安装包与 SHA256SUMS.txt 不匹配。' }
    if ($CheckOnly) { Write-Output "$Tag：GitHub 的发行附件校验通过；未发布。"; return }

    $api = "https://api.cnb.cool/$CnbRepository/-/releases"
    $public = "https://cnb.cool/$CnbRepository/-/releases"
    $release = Invoke-CnbApi GET "$api/tags/$Tag" -AllowMissing
    if ($release -and !$release.draft) {
        if ($release.prerelease -ne $gh.prerelease) { throw '已公开镜像的发行渠道与 GitHub 不一致。' }
        Assert-PublicCopy $public
        Write-Output "$Tag 已同步，无需重复发布。"
        return
    }

    # 从实际 GitHub 源取标签，不信任同名本地引用；只推该标签，从不强推或改动 CNB 默认分支。
    $ref = "refs/tags/$Tag"
    git fetch --no-tags "https://github.com/$GitHubRepository.git" "${ref}:${ref}"
    $commit = git rev-parse --verify "${ref}^{commit}"
    $basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("cnb:$env:CNB_TOKEN"))
    $env:SIDESTAGE_CNB_GIT_HEADER = "Authorization: Basic $basic"
    try { git --config-env=http.https://cnb.cool/.extraHeader=SIDESTAGE_CNB_GIT_HEADER -c http.followRedirects=false push "https://cnb.cool/$CnbRepository.git" "${ref}:${ref}" }
    finally { Remove-Item Env:SIDESTAGE_CNB_GIT_HEADER }

    if (!$release) {
        $release = Invoke-CnbApi POST $api @{ tag_name = $Tag; target_commitish = $commit; name = $gh.name
            body = $gh.body; draft = $true; prerelease = [bool]$gh.prerelease }
    }
    if ($release.draft -ne $true -or $release.tag_name -cne $Tag -or $release.id -notmatch '\A[0-9]+\z') { throw '未取得对应标签的发行草稿。' }
    foreach ($file in $files) {
        $path = Get-Item -LiteralPath (Join-Path $stage $file)
        $ticket = Invoke-CnbApi POST "$api/$($release.id)/asset-upload-url" @{ asset_name = $file; size = $path.Length; overwrite = $true; ttl = 0 }
        $upload = [uri]$ticket.upload_url
        if (!$upload.IsAbsoluteUri -or $upload.Scheme -cne 'https') { throw 'CNB 未返回 HTTPS 上传地址。' }
        # 对象存储使用一次性签名地址，绝不转发 CNB 令牌，也不在错误输出里打印带签名的 URL。
        try { $null = Invoke-WebRequest -Method Put -Uri $upload -InFile $path.FullName -ContentType 'application/octet-stream' -MaximumRedirection 0 -TimeoutSec 180 }
        catch { throw "上传 $file 失败；草稿可在重跑时继续。" }
        if (!$ticket.verify_url) { throw 'CNB 未提供附件确认地址。' }
        $confirm = [uri]::new([uri]'https://api.cnb.cool/', [string]$ticket.verify_url)
        $null = Invoke-CnbApi POST $confirm
    }
    $null = Invoke-CnbApi PATCH "$api/$($release.id)" @{ draft = $false; prerelease = [bool]$gh.prerelease }
    Assert-PublicCopy $public
    Write-Output "已校验国内公开下载：$public/tag/$Tag"
} finally {
    # 仅清理本次创建的几个固定文件，不递归操作临时目录。
    foreach ($file in @($files) + 'public-copy') { Remove-Item -LiteralPath (Join-Path $stage $file) -ErrorAction SilentlyContinue }
    [IO.Directory]::Delete($stage)
}
