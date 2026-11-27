<#
.SYNOPSIS
    在随机安装标识下验证首次安装、覆盖升级、同版本重装、文件占用和拒绝降级。
.DESCRIPTION
    从正式 Inno 脚本生成只更换安装标识、默认路径和快捷方式位置的验收副本。
    测试负载只报告模拟的启动项恢复结果，用隔离目录验证卸载失败和重试。
.PARAMETER Iscc
    Inno Setup 6.x 编译器路径；运行 Build-Installer.ps1 后可复用已校验的中文语言文件。
.PARAMETER VerifyInteractiveElevation
    无参数打开正式安装入口。授权后由隔离向导报告实际权限，再退出，不安装程序。
#>
[CmdletBinding()]
param([string] $Iscc = 'ISCC.exe', [switch] $VerifyInteractiveElevation)
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath "$PSScriptRoot/../..").FullName
$compiler = (Get-Command $Iscc -CommandType Application).Source
$id = [guid]::NewGuid().ToString().ToUpperInvariant()
$run = Join-Path $root "artifacts/checks/wizard/$id"
$dir = Join-Path $run 'installed'
$payload = Join-Path $run 'fixture'
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$id}_is1"
$realId = '28C18EA6-CBC7-4C56-AFC2-A5307EAD9573'
if ((Test-Path -LiteralPath $run) -or (Test-Path -LiteralPath $key)) { throw '隔离测试位置已被占用。' }
$null = New-Item -ItemType Directory -Path $payload
$icon = Join-Path $root 'assets/branding/SideStage.ico'
# 独立的验收程序只认卸载命令；标记文件由本脚本控制，不访问实际启动登记。
$fixtureSource = @'
using System;
using System.IO;
internal class RestoreFixture {
    /// <summary>在隔离安装目录中模拟恢复命令的成功和失败。</summary>
    public static int Main(string[] args) {
        return args.Length == 2 && args[0] == "--restore-startup-items" && args[1] == "--silent" &&
            !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "restore-failure.test")) ? 0 : 1;
    }
}
'@
$fixtureFile = Join-Path $run 'RestoreFixture.cs'
[IO.File]::WriteAllText($fixtureFile, $fixtureSource)
& "$env:SystemRoot/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:winexe "/out:$payload/SideStage.exe" $fixtureFile
if ($LASTEXITCODE) { throw '卸载验收负载编译失败。' }
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SideStage.iss')).Replace($realId, $id).
    Replace('{userprograms}\SideStage', '{app}\links\start').Replace('{userdesktop}\SideStage', '{app}\links\desktop').
    Replace('{localappdata}\Programs\SideStage', $dir).Replace('..\..\assets\branding\SideStage.ico', $icon).
    Replace('OutputBaseFilename=SideStage-', 'OutputBaseFilename=Fixture-')
if (-not $source.Contains("AppId={{$id}") -or $source.Contains($realId) -or $source -match '\{user(desktop|programs)\}') {
    throw '测试向导尚未完全隔离，拒绝继续。'
}
# 无参数才记录向导实际拿到的令牌，随后停止在任何安装写入之前。授权调用保持生产实现。
$requestFile = Join-Path $run 'wizard-elevation.txt'
$beforeRead = "  if not RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation'"
if (!$source.Contains($beforeRead)) { throw '未找到隔离向导的权限采样位置。' }
$source = $source.Replace($beforeRead, @"
  if ParamCount = 0 then begin
    if IsAdmin then SaveStringToFile('$($requestFile.Replace("'","''"))', 'administrator', False)
    else SaveStringToFile('$($requestFile.Replace("'","''"))', 'standard', False);
    Result := False;
    exit;
  end;
$beforeRead
"@).Replace('DisableWelcomePage=no', "ShowLanguageDialog=no`r`nDisableWelcomePage=no")
$recipe = Join-Path $run 'fixture.iss'
[IO.File]::WriteAllText($recipe, $source, [Text.UTF8Encoding]::new($true))
$language = Join-Path $root 'artifacts/tooling/ChineseSimplified-6.5.4.isl'
# 合成负载用自己的校验清单；正式发行文件的真实摘要另由 Test-Installer 的跨版验收覆盖。
$legacyManifest = Join-Path $run 'LegacyFlatFiles.sha256'
$digest = (Get-FileHash -LiteralPath $icon).Hash
$rows = foreach ($culture in 'cs', 'de', 'fr', 'zh-Hans') { "$culture\System.Windows.Forms.resources.dll|$digest" }
[IO.File]::WriteAllLines($legacyManifest, [string[]]$rows)
foreach ($version in '0.1.0-beta.2','0.1.0-beta.10') {
    & $compiler '/Q' "/DAppVersion=$version" '/DFileVersion=0.1.0.0' "/DPublishDir=$payload" "/DOutputDirPath=$run" "/DChineseMessages=$language" "/DLegacyManifest=$legacyManifest" $recipe
    if ($LASTEXITCODE -ne 0) { throw '隔离安装包编译失败。' }
}

<#
.SYNOPSIS
    对隔离安装运行一次静默操作，同时核对向导分支、退出状态和卸载记录的最终版本。
#>
function Test-Mode([string] $Name, [string] $Version, [string] $Mode, [string] $ExpectedVersion, [bool] $Success) {
    $log = Join-Path $run "$Name.log"
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=zhcn /DIR=`"$dir`" /LOG=`"$log`""
    $proc = Start-Process -FilePath (Join-Path $run "Fixture-$Version-Setup-x64.exe") -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ((($proc.ExitCode -eq 0) -ne $Success) -or -not (Select-String -LiteralPath $log -SimpleMatch "SideStage mode: $Mode" -Quiet)) {
        throw "向导分支或退出码不正确：$Name；日志 $log"
    }
    $entry = Get-ItemProperty -LiteralPath $key
    if ($entry.DisplayVersion -ne $ExpectedVersion -or $entry.InstallLocation.TrimEnd('\') -ne $dir) { throw "安装记录不正确：$Name" }
    Write-Output "$Name 通过，已安装版本：$ExpectedVersion"
}

try {
    if ($VerifyInteractiveElevation) {
        $entryPoint = Join-Path $run 'SideStage-ElevationCheck.exe'
        & "$PSScriptRoot/New-SetupEntry.ps1" -Payload "$run/Fixture-0.1.0-beta.2-Setup-x64.exe" -Destination $entryPoint `
            -Version '0.1.0-beta.2' -FileVersion '0.1.0.0'
        $probe = Start-Process -FilePath $entryPoint -PassThru -WindowStyle Hidden
        $probe.WaitForExit()
        if ($probe.ExitCode -eq 1223) { throw 'Windows 授权已取消。' }
        if (!(Test-Path -LiteralPath $requestFile) -or (Get-Content -Raw -LiteralPath $requestFile) -cne 'administrator' -or (Test-Path -LiteralPath $key)) {
            throw "安装向导未取得管理员权限，入口退出码 $($probe.ExitCode)。"
        }
        Write-Output '无参数打开安装包后，隔离向导实际取得了管理员权限。'
    }
    Test-Mode '首次安装' '0.1.0-beta.2' 'install' '0.1.0-beta.2' $true
    # 原样文件应清除；同名但已修改的文件、用户文件、空目录和指向目录外的联接必须保留。
    foreach ($name in 'cs','de','zh-Hans') {
        $null = New-Item -ItemType Directory -Path (Join-Path $dir $name)
        Copy-Item -LiteralPath $icon -Destination (Join-Path $dir "$name/System.Windows.Forms.resources.dll")
    }
    Copy-Item -LiteralPath $icon -Destination (Join-Path $dir 'de/keep.txt')
    [IO.File]::WriteAllText("$dir/zh-Hans/System.Windows.Forms.resources.dll", 'modified by user')
    $null = New-Item -ItemType Directory -Path "$dir/es","$run/user-files"
    Copy-Item -LiteralPath $icon -Destination "$run/user-files/System.Windows.Forms.resources.dll"
    $null = New-Item -ItemType Junction -Path "$dir/fr" -Target "$run/user-files"
    # 占用文件时，升级应在写入前停止，仍保持原版本。
    $lock = [IO.File]::OpenRead((Join-Path $dir 'SideStage.exe'))
    try { Test-Mode '文件占用' '0.1.0-beta.10' 'update' '0.1.0-beta.2' $false }
    finally { $lock.Dispose() }
    if (!(Test-Path -LiteralPath "$dir/cs/System.Windows.Forms.resources.dll")) { throw '升级被阻止时不应提前清理旧版。' }
    Test-Mode '向前升级' '0.1.0-beta.10' 'update' '0.1.0-beta.10' $true
    if ((Test-Path -LiteralPath "$dir/cs") -or (Test-Path -LiteralPath "$dir/de/System.Windows.Forms.resources.dll") -or
        !(Test-Path -LiteralPath "$dir/de/keep.txt") -or !(Test-Path -LiteralPath "$dir/zh-Hans/System.Windows.Forms.resources.dll") -or
        !(Test-Path -LiteralPath "$dir/es" -PathType Container) -or !(Test-Path -LiteralPath "$dir/fr/System.Windows.Forms.resources.dll")) {
        throw '覆盖升级未精确迁移旧语言目录。'
    }
    Test-Mode '同版重装' '0.1.0-beta.10' 'reinstall' '0.1.0-beta.10' $true
    Test-Mode '拒绝降级' '0.1.0-beta.2' 'blocked (Downgrade)' '0.1.0-beta.10' $false
}
finally {
    if (Test-Path -LiteralPath $key) {
        $entry = Get-ItemProperty -LiteralPath $key
        $uninstall = [IO.Path]::GetFullPath($entry.UninstallString.Trim('"'))
        if ($entry.InstallLocation.TrimEnd('\') -ne $dir -or [IO.Path]::GetDirectoryName($uninstall) -ne $dir -or
            [IO.Path]::GetFileName($uninstall) -notmatch '^unins[0-9]{3}\.exe$') { throw '卸载记录不属于本次验收，不执行卸载。' }
        $marker = Join-Path $dir 'restore-failure.test'
        try {
            [IO.File]::WriteAllText($marker, 'failure')
            $attempt = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru -WindowStyle Hidden
            if (!(Test-Path -LiteralPath $key) -or !(Test-Path -LiteralPath "$dir/SideStage.exe") -or $attempt.ExitCode -eq 0) {
                throw '恢复失败后卸载器未报告失败或提前移除了安装文件。'
            }
        } finally { if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker } }
        $p = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0 -or (Test-Path -LiteralPath $key)) { throw '隔离验收记录未清理。' }
        Write-Output '启动项恢复失败时停止卸载，通过重试后正常卸载。'
    }
}
