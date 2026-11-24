<#
.SYNOPSIS
    以随机安装标识跑通「下载已验证 → 更新器就绪 → 取消或授权 → 覆盖安装 → 启动新版」。
.DESCRIPTION
    使用无 RDP 能力的最小测试宿主和生产更新器、交接代码、Inno 配方。所有文件和卸载记录隔离，
    不关闭其他应用、不更改真实 SideStage；仅在明确匹配本轮标识后卸载。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Iscc)
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath "$PSScriptRoot/../..").FullName
$id = [guid]::NewGuid().ToString()
$run = Join-Path $root "artifacts/checks/handoff/$id"
$payload = Join-Path $run 'payload'
$dir = Join-Path $run 'installed'
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$id}_is1"
$compiler = (Get-Command $Iscc -CommandType Application).Source
if ((Test-Path -LiteralPath $run) -or (Test-Path -LiteralPath $key)) { throw '隔离路径已占用。' }
$null = New-Item -ItemType Directory -Path $payload
& dotnet publish (Join-Path $root 'tests/UpdaterFixture') -c Release -r win-x64 --self-contained false -o $payload
if ($LASTEXITCODE) { throw '验收宿主编译失败。' }
$worker = Join-Path $run 'worker'
& dotnet publish (Join-Path $root 'src/SideStage.Updater') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true -o $worker
if ($LASTEXITCODE) { throw '更新器编译失败。' }
Copy-Item -LiteralPath "$worker/SideStage.Updater.exe" -Destination $payload
$icon = Join-Path $root 'assets/branding/SideStage.ico'
$recipe = Join-Path $run 'fixture.iss'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SideStage.iss')).Replace('28C18EA6-CBC7-4C56-AFC2-A5307EAD9573', $id).
    Replace('{userprograms}\SideStage', '{app}\links\start').Replace('{userdesktop}\SideStage', '{app}\links\desktop').
    Replace('{localappdata}\Programs\SideStage', $dir).Replace('..\..\assets\branding\SideStage.ico', $icon)
if (!$source.Contains("AppId={{$id}") -or $source -match '\{user(desktop|programs)\}|28C18EA6') { throw '验收配方隔离失败。' }
[IO.File]::WriteAllText($recipe, $source, [Text.UTF8Encoding]::new($true))
foreach ($v in '1.0.0','1.1.0') {
    [IO.File]::WriteAllText("$payload/fixture-only.txt", $v)
    & $compiler /Q "/DAppVersion=$v" "/DFileVersion=$v.0" "/DPublishDir=$payload" "/DOutputDirPath=$run" `
        "/DChineseMessages=$root/artifacts/tooling/ChineseSimplified-6.5.4.isl" $recipe
    if ($LASTEXITCODE) { throw '验收安装包编译失败。' }
}
try {
    $first = Start-Process -FilePath "$run/SideStage-1.0.0-Setup-x64.exe" -ArgumentList "/VERYSILENT /NORESTART /SP- /DIR=`"$dir`"" -Wait -PassThru -WindowStyle Hidden
    if ($first.ExitCode -ne 0 -or (Get-ItemProperty -LiteralPath $key).InstallLocation.TrimEnd('\') -ne $dir) { throw '隔离安装未成功。' }
    $next = "$run/SideStage-1.1.0-Setup-x64.exe"
    $hash = (Get-FileHash -LiteralPath $next).Hash.ToLowerInvariant()
    foreach ($action in 'cancel','install') {
        $hostRun = Start-Process -FilePath "$dir/SideStage.exe" -ArgumentList "$action `"$next`" $hash" -Wait -PassThru -WindowStyle Hidden
        if ($hostRun.ExitCode -ne 0) { throw "交接失败：$action" }
        $expected = if ($action -eq 'cancel') { '1.0.0' } else { '1.1.0' }
        if ((Get-Content -Raw -LiteralPath "$dir/fixture-only.txt") -ne $expected -or
            (Get-ItemProperty -LiteralPath $key).DisplayVersion -ne $expected -or
            ((Test-Path -LiteralPath "$dir/restarted.txt") -ne ($action -eq 'install'))) { throw "版本或重启状态错误：$action" }
        Write-Output "$action 通过：安装记录、文件内容和重启状态均符合预期。"
    }
} finally {
    if (Test-Path -LiteralPath $key) {
        $item = Get-ItemProperty -LiteralPath $key
        $uninstall = [IO.Path]::GetFullPath($item.UninstallString.Trim('"'))
        if ($item.InstallLocation.TrimEnd('\') -ne $dir -or [IO.Path]::GetDirectoryName($uninstall) -ne $dir -or
            [IO.Path]::GetFileName($uninstall) -notmatch '^unins\d{3}\.exe$') { throw '卸载路径不属于隔离验收。' }
        $p = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES' -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0 -or (Test-Path -LiteralPath $key)) { throw '隔离卸载失败。' }
    }
}
