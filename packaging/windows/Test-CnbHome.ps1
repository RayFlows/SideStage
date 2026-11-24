<#
.SYNOPSIS
    用本地 Git 仓库验收国内下载首页，不访问 GitHub、CNB，也不使用真实的发布令牌。
.DESCRIPTION
    验证首次创建、重复执行、新版本、补发旧版本和手工分支保护。测试现场保留在忽略提交的
    artifacts/cnb-home-tests 下，可检查源码树、提交父链和工作区未提交文件。
#>
#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Join-Path "$PSScriptRoot/../../artifacts/cnb-home-tests" ([guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path $root
$root = (Resolve-Path $root).Path
$origin = Join-Path $root 'origin'
$mirror = Join-Path $root 'mirror.git'
$runner = Join-Path $root 'runner'
$script = Join-Path $PSScriptRoot 'Sync-CnbHome.ps1'
$savedToken = $env:CNB_TOKEN
$savedProtocols = $env:GIT_ALLOW_PROTOCOL

function Invoke-TestSync([string] $Version) {
    <# 地址与真实发布调用同形；调用仓库的 Git 配置把二者改写为本地路径，协议白名单禁止回退联网。 #>
    & $script -Tag $Version -GitHubRepository 'example/source' -CnbRepository 'example/mirror'
}

function Get-TestMain {
    <# 读取目标仓库本身的分支，用于验证实际推送结果而不是本地生成的中间提交。 #>
    git -C $mirror rev-parse refs/heads/main
}

Push-Location $root
try {
    $env:CNB_TOKEN = 'offline-test'
    $env:GIT_ALLOW_PROTOCOL = 'file'
    git init -q -b main $origin
    git init -q --bare -b main $mirror
    git init -q -b main $runner
    git -C $origin config user.name Test
    git -C $origin config user.email test@example.invalid
    [IO.File]::WriteAllText((Join-Path $origin 'released.txt'), 'first')
    git -C $origin add released.txt
    git -C $origin commit -qm 'old'
    git -C $origin tag v1.0.0-beta.2
    [IO.File]::WriteAllText((Join-Path $origin 'released.txt'), 'new')
    [IO.File]::WriteAllText((Join-Path $origin 'README.md'), "# SideStage`n`n原发行说明")
    git -C $origin add released.txt README.md
    git -C $origin commit -qm 'new'
    git -C $origin tag -a v1.0.0-beta.10 -m 'annotated'
    Set-Location $runner
    # POSIX 路径用普通 [uri] 转换可能变成相对 URI；显式指定 file 协议，两个平台都不能访问外网。
    $from = [UriBuilder]::new('file','',-1,$origin.Replace('\','/')).Uri.AbsoluteUri
    $to = [UriBuilder]::new('file','',-1,$mirror.Replace('\','/')).Uri.AbsoluteUri
    git config "url.$from.insteadOf" 'https://github.com/example/source.git'
    git config "url.$to.insteadOf" 'https://cnb.cool/example/mirror.git'
    if ((git ls-remote --get-url 'https://github.com/example/source.git') -cne $from -or
        (git ls-remote --get-url 'https://cnb.cool/example/mirror.git') -cne $to) { throw '离线 Git 地址映射未生效。' }
    [IO.File]::WriteAllText((Join-Path $runner 'local-only.txt'), 'not public')
    git add local-only.txt
    $index = git write-tree

    Invoke-TestSync 'v1.0.0-beta.2'
    $first = Get-TestMain
    $readme = (git -C $mirror show 'main:README.md') -join "`n"
    if (!$readme.Contains('https://cnb.cool/example/mirror/-/releases/download/v1.0.0-beta.2/SideStage-1.0.0-beta.2-Setup-x64.exe')) { throw '缺少首版下载链接。' }
    Invoke-TestSync 'v1.0.0-beta.2'
    if ((Get-TestMain) -ne $first) { throw '重复运行产生多余提交。' }
    Invoke-TestSync 'v1.0.0-beta.10'
    $latest = Get-TestMain
    $commit = git -C $origin rev-parse 'v1.0.0-beta.10^{commit}'
    if ((git -C $mirror rev-parse 'main^1') -ne $first -or (git -C $mirror rev-parse 'main^2') -ne $commit) { throw '镜像或发行提交的父链不正确。' }
    if ((git -C $mirror diff --name-only $commit main) -cne 'README.md') { throw '镜像包含了发行源码以外的改动。' }
    if (((git -C $mirror show 'main:README.md') -join "`n") -notmatch '原发行说明') { throw '未保留发行版原有的 README。' }
    Invoke-TestSync 'v1.0.0-beta.2'
    if ((Get-TestMain) -ne $latest -or (git write-tree) -ne $index) { throw '旧版本回退了首页或改变了工作索引。' }
    git -C $mirror -c user.name=Test -c user.email=test@example.invalid commit-tree 'main^{tree}' -p main -m 'manual' |
        ForEach-Object { git -C $mirror update-ref refs/heads/main $_ $latest }
    $manual = Get-TestMain
    try { Invoke-TestSync 'v1.0.0-beta.10'; throw '未拒绝覆盖手工分支。' }
    catch { if ($_.Exception.Message -notlike '*不是发布镜像维护的分支*') { throw } }
    if ((Get-TestMain) -ne $manual) { throw '手工分支被修改。' }
    Write-Output '镜像首页离线验收通过。'
} finally {
    Pop-Location
    $env:CNB_TOKEN = $savedToken
    $env:GIT_ALLOW_PROTOCOL = $savedProtocols
}
