<#
.SYNOPSIS
    构建可离线安装的 SideStage Windows x64 试装包。
.DESCRIPTION
    需要 .NET 10 SDK 和 Inno Setup 6.5 或以上的 6.x 编译器。每次发布到独立目录，
    核对主程序、会话代理、运行时及字体后才打包。仅构建本地产物，不上传、不安装。
.PARAMETER UpdateRepository
    可选的公开 GitHub 发布仓库，格式为 owner/repo；留空生成不联网检查更新的本地试装版。
#>
[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(alpha|beta|rc)\.(0|[1-9][0-9]*))?$')]
    [string] $Version = '0.1.0-beta.3',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$')]
    [string] $UpdateRepository,
    [string] $Iscc = 'ISCC.exe'
)
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath "$PSScriptRoot/../..").FullName
$compiler = (Get-Command $Iscc -CommandType Application -ErrorAction Stop).Source
$numericVersion = ($Version -split '-')[0] + '.0'
$parts = $Version -csplit '[-.]'
if (@($parts[0..2] | Where-Object { [long]$_ -gt 65535 }).Length -or
    ($parts.Length -gt 3 -and ($parts[3] -cnotin @('alpha', 'beta', 'rc') -or [long]$parts[4] -gt [int]::MaxValue))) {
    throw '版本需符合 Windows 文件版本范围和发行渠道约定。'
}
$release = Join-Path $root "artifacts/releases/$Version"
$stage = Join-Path $root ("artifacts/installer-build/" + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'app'
$agentPublish = Join-Path $stage 'agent'
$null = New-Item -ItemType Directory -Force -Path $release, $publish

# 语言文件锁定到构建工具的版本并校验摘要；终端用户安装时不需要网络。
$language = Join-Path $root 'artifacts/tooling/ChineseSimplified-6.5.4.isl'
if (-not (Test-Path -LiteralPath $language)) {
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $language)
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 45 -Uri 'https://raw.githubusercontent.com/jrsoftware/issrc/is-6_5_4/Files/Languages/Unofficial/ChineseSimplified.isl' -OutFile $language
}
if ((Get-FileHash -LiteralPath $language).Hash -ne 'FC1DBF303559CFEFD47CF59EEC7F1AD1C5FC6266EF8FA64569C13BC754ADFEB4') {
    throw '安装向导语言文件的 SHA-256 不匹配。'
}

$project = Join-Path $root 'src/SideStage.App/SideStage.App.csproj'
$publishArgs = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    "-p:Version=$Version", '-p:DebugType=None', '-p:DebugSymbols=false')
& dotnet publish $project @publishArgs -o $publish '-p:SideStagePublishAgentSeparately=true' "-p:SideStageUpdateRepository=$UpdateRepository"
if ($LASTEXITCODE -ne 0) { throw 'SideStage 发布失败。' }

# 两个入口共享同一份 Windows Desktop 运行时。只从代理的独立发布结果引入其四个入口文件，
# 不让 SDK 将框架依赖版的代理与自包含代理重复装入同一目录，也不覆盖主程序的依赖。
$agentProject = Join-Path $root 'src/SideStage.SessionAgent/SideStage.SessionAgent.csproj'
& dotnet publish $agentProject @publishArgs -o $agentPublish
if ($LASTEXITCODE -ne 0) { throw '会话代理发布失败。' }
foreach ($extension in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $agentPublish "SideStage.SessionAgent.$extension") -Destination $publish
}
# 发布给用户的目录必须同时支持主窗口和独立代理，不打入源码树、开发日志或保存项。
$required = @('SideStage.exe', 'SideStage.dll', 'SideStage.deps.json', 'SideStage.runtimeconfig.json',
    'SideStage.SessionAgent.exe', 'SideStage.SessionAgent.dll', 'SideStage.SessionAgent.deps.json',
    'SideStage.SessionAgent.runtimeconfig.json', 'SideStage.Core.dll', 'SideStage.Windows.dll',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Windows.Forms.dll',
    'Licenses/SideStage-LICENSE.txt',
    'Artwork/Fonts/HarmonyOS_Sans_SC_Regular.ttf', 'Artwork/Fonts/HarmonyOS_Sans_SC_Bold.ttf', 'Artwork/Fonts/LICENSE_Fonts.txt')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) { throw "发布目录缺少 $file" }
}
foreach ($app in @('SideStage', 'SideStage.SessionAgent')) {
    $config = Get-Content -Raw -LiteralPath (Join-Path $publish "$app.runtimeconfig.json") | ConvertFrom-Json
    if (-not $config.runtimeOptions.includedFrameworks) { throw "$app 未使用自包含运行时。" }
}

& $compiler '/Q' "/DAppVersion=$Version" "/DFileVersion=$numericVersion" "/DPublishDir=$publish" `
    "/DOutputDirPath=$release" "/DChineseMessages=$language" (Join-Path $PSScriptRoot 'SideStage.iss')
if ($LASTEXITCODE -ne 0) { throw '生成安装包失败。' }
$installer = Get-Item -LiteralPath (Join-Path $release "SideStage-$Version-Setup-x64.exe")
$digest = (Get-FileHash -LiteralPath $installer.FullName).Hash.ToLowerInvariant()
"$digest  $($installer.Name)" | Set-Content -Encoding ASCII -LiteralPath (Join-Path $release 'SHA256SUMS.txt')
Write-Output "安装包：$($installer.FullName)"
