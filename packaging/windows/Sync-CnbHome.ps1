<#
.SYNOPSIS
    将已发布标签的源码和国内下载入口展示在 CNB 的 main 分支。
.DESCRIPTION
    由发版流程在公开附件校验通过后调用。直接从 Git 对象生成首页，不检出标签、不读取本地 README
    或暂存区。镜像提交保留发布标签对应的提交为父提交；不修改标签，不强推，也不接管手工维护的分支。
#>
#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(alpha|beta|rc)\.(0|[1-9][0-9]*))?\z')][string] $Tag,
    [Parameter(Mandatory)][ValidatePattern('\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z')][string] $GitHubRepository,
    [Parameter(Mandatory)][ValidatePattern('\A(?:[A-Za-z0-9][A-Za-z0-9_.-]*/)+[A-Za-z0-9][A-Za-z0-9_.-]*\z')][string] $CnbRepository
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (!$env:CNB_TOKEN) { throw '更新镜像首页需要 CNB_TOKEN。' }
$version = $Tag -creplace '^v', ''
$cnb = "https://cnb.cool/$CnbRepository"
$github = "https://github.com/$GitHubRepository"
$marker = 'SideStage-Release: '

function New-HomeTree([string] $Commit, [string] $MainTag, [string] $StableTag, [string] $PreviewTag) {
    <# 从发布树只替换 README.md；如标签已经带有说明，则保留原文放在下载入口下方。使用临时索引隔离宿主。 #>
    $buttons = @(
        if ($StableTag) {
            $number = $StableTag -creplace '^v', ''
            "**[下载正式版（$StableTag）]($cnb/-/releases/download/$StableTag/SideStage-$number-Setup-x64.exe)**"
        }
        if ($PreviewTag) {
            $number = $PreviewTag -creplace '^v', ''
            "[试用测试版（$PreviewTag）]($cnb/-/releases/download/$PreviewTag/SideStage-$number-Setup-x64.exe)"
        }
    ) -join ' · '
    $readme = @"
# SideStage

$buttons

[本版说明与 SHA-256 校验文件]($cnb/-/releases/tag/$MainTag) · [全部发行版]($cnb/-/releases) · [GitHub 开发主仓]($github)

SideStage 把本机的 Windows RDP 子会话放在独立窗口里，让主桌面继续使用。此页展示的是 **$MainTag** 的源码。
"@
    if (git ls-tree --name-only $Commit -- README.md) {
        $original = (git show "${Commit}:README.md") -join "`n"
        $readme += "`n`n" + ($original -creplace '\A# SideStage[ \t]*\n+', '')
    }
    $tmp = (New-Item -ItemType Directory -Path ([IO.Path]::Combine([IO.Path]::GetTempPath(), [guid]::NewGuid().ToString('N')))).FullName
    $oldIndex = $env:GIT_INDEX_FILE
    try {
        $env:GIT_INDEX_FILE = Join-Path $tmp 'index'
        $file = Join-Path $tmp 'README.md'
        [IO.File]::WriteAllText($file, ($readme.TrimEnd() -replace "`r`n","`n") + "`n", [Text.UTF8Encoding]::new($false))
        $blob = git hash-object -w -- $file
        git read-tree $Commit
        git update-index --add --cacheinfo "100644,$blob,README.md"
        git write-tree
    } finally {
        $env:GIT_INDEX_FILE = $oldIndex
        foreach ($name in 'index','index.lock','README.md') { [IO.File]::Delete((Join-Path $tmp $name)) }
        [IO.Directory]::Delete($tmp)
    }
}

# 使用独立来源的标签，不以当前 HEAD、工作目录或本地同名标签为准。
git fetch --no-tags "$github.git" "refs/tags/$Tag"
$source = git rev-parse --verify 'FETCH_HEAD^{commit}'

# 不同标签可以同时发布。每次推送都只允许快进，遇到并发写入就重读最新首页再按版本比较。
for ($try = 1; $try -le 3; $try++) {
    $tip = git ls-remote --heads "$cnb.git" refs/heads/main
    $parents = @('-p', $source)
    $stable = $null
    $preview = $null
    $primarySource = $source
    if ($tip) {
        git fetch --no-tags "$cnb.git" refs/heads/main
        $previous = git rev-parse --verify 'FETCH_HEAD^{commit}'
        $subject = git show --no-patch --format=%s $previous
        if (!$subject.StartsWith($marker, [StringComparison]::Ordinal)) { throw 'CNB main 不是发布镜像维护的分支，未覆盖。' }
        $oldTag = $subject.Substring($marker.Length)
        # 旧版镜像只记录主标签和源码，首次同步时也能保留原来的正式版按钮。
        $primarySource = git show -s '--format=%(trailers:key=Release-Commit,valueonly)' $previous | Select-Object -First 1
        if ($primarySource -cnotmatch '^[0-9a-f]{40,64}$') { throw '镜像首页缺少有效的发行提交。' }
        if ($oldTag.Contains('-')) { $preview = $oldTag } else { $stable = $oldTag }
        $previewSource = $primarySource
        if ($stable) {
            $preview = git show -s '--format=%(trailers:key=Preview-Tag,valueonly)' $previous | Select-Object -First 1
            $previewSource = git show -s '--format=%(trailers:key=Preview-Commit,valueonly)' $previous | Select-Object -First 1
        }
        $isPreview = $version.Contains('-')
        $sameChannel = if ($isPreview) { $preview } else { $stable }
        if ($sameChannel) {
            $order = ([semver]$version).CompareTo([semver]($sameChannel -creplace '^v', ''))
            if ($order -lt 0) { return }
            $recordedSource = if ($isPreview) { $previewSource } else { $primarySource }
            if ($order -eq 0 -and $recordedSource -cne $source) { throw '同一版本对应了不同源码，拒绝改写首页。' }
        }
        if ($isPreview -and $stable -and [semver]$version -le [semver]($stable -creplace '^v', '')) { return }
        $parents = @('-p', $previous, '-p', $source)
    }
    if ($version.Contains('-')) {
        $preview = $Tag
        $previewSource = $source
        if (!$stable) { $primarySource = $source }
    } else {
        $stable = $Tag
        $primarySource = $source
        if ($preview -and [semver]($preview -creplace '^v', '') -le [semver]$version) { $preview = $null }
    }
    $primaryTag = if ($stable) { $stable } else { $preview }
    $tree = New-HomeTree $primarySource $primaryTag $stable $preview
    $body = "Release-Commit: $primarySource"
    if ($stable -and $preview) { $body += "`nPreview-Tag: $preview`nPreview-Commit: $previewSource" }
    if ($tip -and (git rev-parse "${previous}^{tree}") -ceq $tree -and
        ((git show -s --format=%b $previous) -join "`n").Trim() -ceq $body) { return }
    $next = git -c user.name=SideStage -c user.email=mirror@users.noreply.github.com commit-tree $tree @parents -m "$marker$primaryTag" -m $body
    $oldHeader = $env:SIDESTAGE_CNB_GIT_HEADER
    $env:SIDESTAGE_CNB_GIT_HEADER = 'Authorization: Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("cnb:$env:CNB_TOKEN"))
    try {
        git --config-env=http.https://cnb.cool/.extraHeader=SIDESTAGE_CNB_GIT_HEADER -c http.followRedirects=false push "$cnb.git" "${next}:refs/heads/main"
        Write-Output "CNB main 已展示 $primaryTag 的源码，并更新国内下载入口。"
        return
    } catch {
        $now = git ls-remote --heads "$cnb.git" refs/heads/main
        if ($try -eq 3 -or $tip -ceq $now) { throw }
    } finally { $env:SIDESTAGE_CNB_GIT_HEADER = $oldHeader }
}
