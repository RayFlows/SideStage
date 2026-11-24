<#
.SYNOPSIS
    构建可离线安装的 SideStage Windows x64 试装包。
.DESCRIPTION
    需要 .NET 10 SDK 和 Inno Setup 6.5 或以上的 6.x 编译器。每次发布到独立目录，
    核对主程序、会话代理、运行时及字体后才打包。仅构建本地产物，不上传、不安装。
.PARAMETER UpdateRepository
    可选的公开 GitHub 发布仓库，格式为 owner/repo；与国内源均留空时生成离线试装版。
.PARAMETER CnbRepository
    CNB 公开仓库路径，允许子组织。仅打包 RayFlows/SideStage 时默认使用 fouramstage/SideStage；
    其他仓库需显式指定国内源，未配置任何源的本地试装包仍不联网。
.PARAMETER UpdateSigningThumbprint
    联网发行包必填。使用当前用户证书库的 RSA 签名证书，私钥不进入输出目录；
    可用 SIDESTAGE_UPDATE_SIGNER 环境变量复用同一指纹。离线试装不需要证书。
#>
[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(alpha|beta|rc)\.(0|[1-9][0-9]*))?$')]
    [string] $Version = '0.1.0-beta.3',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$')]
    [string] $UpdateRepository,
    [string] $Iscc = 'ISCC.exe',
    [ValidateLength(3, 255)]
    [ValidatePattern('\A(?:[A-Za-z0-9][A-Za-z0-9_.-]*/)+[A-Za-z0-9][A-Za-z0-9_.-]*\z')]
    [string] $CnbRepository,
    [string] $UpdateSigningThumbprint = $env:SIDESTAGE_UPDATE_SIGNER
)
$ErrorActionPreference = 'Stop'
# 只给官方 GitHub 发行包配对已确认的国内镜像，避免开发包或派生项目误查官方版本。
if ($UpdateRepository -eq 'RayFlows/SideStage' -and !$PSBoundParameters.ContainsKey('CnbRepository')) {
    $CnbRepository = 'fouramstage/SideStage'
}
$root = (Get-Item -LiteralPath "$PSScriptRoot/../..").FullName
$compiler = (Get-Command $Iscc -CommandType Application -ErrorAction Stop).Source
$numericVersion = ($Version -split '-')[0] + '.0'
$parts = $Version -csplit '[-.]'
if (@($parts[0..2] | Where-Object { [long]$_ -gt 65535 }).Length -or
    ($parts.Length -gt 3 -and ($parts[3] -cnotin @('alpha', 'beta', 'rc') -or [long]$parts[4] -gt [int]::MaxValue))) {
    throw '版本需符合 Windows 文件版本范围和发行渠道约定。'
}
# 离线试装不能覆盖同版本已签名的发行文件。只有配置发布源的构建进入 releases。
$release = if ($UpdateRepository -or $CnbRepository) { Join-Path $root "artifacts/releases/$Version" }
    else { Join-Path $root "artifacts/trials/installers/$Version" }
$buildRoot = Join-Path $root 'artifacts/build/installer'
$buildId = [guid]::NewGuid().ToString('N')
$stage = Join-Path $buildRoot $buildId
$publish = Join-Path $stage 'app'
$agentPublish = Join-Path $stage 'agent'
$workerPublish = Join-Path $stage 'updater'
$publicKey = ''
if ($UpdateRepository -or $CnbRepository) {
    if ($UpdateSigningThumbprint -notmatch '\A[0-9A-Fa-f]{40}\z') { throw '联网发行包必须指定固定的更新签名证书指纹（见 README）。' }
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$UpdateSigningThumbprint"
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert)
    try {
        if (!$cert.HasPrivateKey -or !$rsa -or $rsa.KeySize -lt 2048) { throw '更新签名需要带私钥的 RSA 证书（至少 2048 位）。' }
        $publicKey = [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
    } finally { if ($rsa) { $rsa.Dispose() } }
}
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
& dotnet publish $project @publishArgs -o $publish '-p:SideStagePublishAgentSeparately=true' `
    "-p:SideStageUpdateRepository=$UpdateRepository" "-p:SideStageCnbRepository=$CnbRepository" "-p:SideStageUpdatePublicKey=$publicKey"
if ($LASTEXITCODE -ne 0) { throw 'SideStage 发布失败。' }

# 两个入口共享同一份 Windows Desktop 运行时。只从代理的独立发布结果引入其四个入口文件，
# 不让 SDK 将框架依赖版的代理与自包含代理重复装入同一目录，也不覆盖主程序的依赖。
$agentProject = Join-Path $root 'src/SideStage.SessionAgent/SideStage.SessionAgent.csproj'
& dotnet publish $agentProject @publishArgs -o $agentPublish
if ($LASTEXITCODE -ne 0) { throw '会话代理发布失败。' }
foreach ($extension in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $agentPublish "SideStage.SessionAgent.$extension") -Destination $publish
}
# 更新器需要在主程序退出后独立存活，不可借用即将被覆盖的安装目录中的运行库。
& dotnet publish (Join-Path $root 'src/SideStage.Updater/SideStage.Updater.csproj') @publishArgs `
    '-p:PublishSingleFile=true' '-p:PublishTrimmed=true' '-p:IncludeNativeLibrariesForSelfExtract=true' -o $workerPublish
if ($LASTEXITCODE -ne 0) { throw '独立更新器发布失败。' }
Copy-Item -LiteralPath (Join-Path $workerPublish 'SideStage.Updater.exe') -Destination $publish
# 发布给用户的目录必须同时支持主窗口和独立代理，不打入源码树、开发日志或保存项。
$required = @('SideStage.exe', 'SideStage.dll', 'SideStage.deps.json', 'SideStage.runtimeconfig.json',
    'SideStage.SessionAgent.exe', 'SideStage.SessionAgent.dll', 'SideStage.SessionAgent.deps.json',
    'SideStage.SessionAgent.runtimeconfig.json', 'SideStage.Core.dll', 'SideStage.Windows.dll',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Windows.Forms.dll',
    'SideStage.Updater.exe', 'Licenses/SideStage-LICENSE.txt',
    'Artwork/Fonts/HarmonyOS_Sans_SC_Regular.ttf', 'Artwork/Fonts/HarmonyOS_Sans_SC_Bold.ttf', 'Artwork/Fonts/LICENSE_Fonts.txt')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) { throw "发布目录缺少 $file" }
}
foreach ($app in @('SideStage', 'SideStage.SessionAgent')) {
    $config = Get-Content -Raw -LiteralPath (Join-Path $publish "$app.runtimeconfig.json") | ConvertFrom-Json
    if (-not $config.runtimeOptions.includedFrameworks) { throw "$app 未使用自包含运行时。" }
}
& (Join-Path $PSScriptRoot 'Test-PublishLayout.ps1') -Directory $publish

& $compiler '/Q' "/DAppVersion=$Version" "/DFileVersion=$numericVersion" "/DPublishDir=$publish" `
    "/DOutputDirPath=$release" "/DChineseMessages=$language" (Join-Path $PSScriptRoot 'SideStage.iss')
if ($LASTEXITCODE -ne 0) { throw '生成安装包失败。' }
$installer = Get-Item -LiteralPath (Join-Path $release "SideStage-$Version-Setup-x64.exe")
$digest = (Get-FileHash -LiteralPath $installer.FullName).Hash.ToLowerInvariant()
"$digest  $($installer.Name)" | Set-Content -Encoding ASCII -LiteralPath (Join-Path $release 'SHA256SUMS.txt')
$sig = Join-Path $release 'SHA256SUMS.sig'
if ($publicKey) {
    $signer = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    try {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $release 'SHA256SUMS.txt'))
        [IO.File]::WriteAllBytes($sig, $signer.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss))
    } finally { $signer.Dispose() }
} elseif (Test-Path -LiteralPath $sig) {
    # 同版本重新构建成离线包时，不能留下上一次联网构建的签名。
    Remove-Item -LiteralPath $sig
}
# 安装包与校验文件已成功写出，只移除本轮创建的暂存目录；失败的现场保留供诊断。
$completed = (Resolve-Path -LiteralPath $stage).ProviderPath
if ((Split-Path $completed -Parent) -ne (Resolve-Path -LiteralPath $buildRoot).ProviderPath -or
    (Split-Path $completed -Leaf) -cne $buildId -or ((Get-Item -LiteralPath $completed).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "打包完成，但暂存目录不属于本轮构建，未清理：$completed"
}
Remove-Item -LiteralPath $completed -Recurse -Force
Write-Output "安装包：$($installer.FullName)"
