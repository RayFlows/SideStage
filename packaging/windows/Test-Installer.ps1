<#
.SYNOPSIS
    在当前 Windows x64 交互式桌面验收本地 SideStage 安装包。
.DESCRIPTION
    使用一次性目录，验证首次安装、快捷方式、首页启动、代理握手、重复安装和卸载。
    单包模式仅适用于没有 SideStage 的机器；跨版模式重新封装真实负载并隔离安装标识与
    快捷方式，可在已有安装的开发机上运行。不发送点击或按键，不连接子会话。
.PARAMETER Installer
    由同目录 Build-Installer.ps1 生成的可信安装包。验收记录保存在 artifacts/checks/installer。
.PARAMETER PreviousInstaller
    指定上一版可信安装包时，同时验证旧版文件迁移。需要 Iscc 和 Unpacker（innounp）。
#>
[CmdletBinding(DefaultParameterSetName='CleanMachine')]
param([Parameter(Mandatory)][string] $Installer,
    [Parameter(Mandatory, ParameterSetName='IsolatedUpgrade')][string] $PreviousInstaller,
    [Parameter(Mandatory, ParameterSetName='IsolatedUpgrade')][string] $Unpacker,
    [Parameter(Mandatory, ParameterSetName='IsolatedUpgrade')][string] $Iscc)
$ErrorActionPreference = 'Stop'
$isolated = $PSCmdlet.ParameterSetName -eq 'IsolatedUpgrade'
$root = (Get-Item -LiteralPath "$PSScriptRoot/../..").FullName
$id = [guid]::NewGuid().ToString()
$run = Join-Path $root "artifacts/checks/installer/$id"
$dir = Join-Path $run 'SideStage 试装'
$setup = (Get-Item -LiteralPath $Installer).FullName
$releaseId = '28C18EA6-CBC7-4C56-AFC2-A5307EAD9573'
$identity = if ($isolated) { $id } else { $releaseId }
$key = "Software\Microsoft\Windows\CurrentVersion\Uninstall\{$identity}_is1"
$userKey = "HKCU:\$key"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'SideStage.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'SideStage.lnk'
$defaultDir = Join-Path $env:LOCALAPPDATA 'Programs/SideStage'
if ($isolated) {
    $shortcut = Join-Path $run 'links/start.lnk'
    $desktop = Join-Path $run 'links/desktop.lnk'
    $defaultDir = $run
}
if (-not [Environment]::Is64BitProcess) { throw '请使用 64 位 PowerShell，以核对正确的卸载记录。' }
foreach ($existing in @($userKey, "HKLM:\$key", $shortcut, $desktop, $defaultDir)) {
    if (Test-Path -LiteralPath $existing) { throw "已存在正式安装或快捷方式，跳过验收，不会覆盖：$existing" }
}

$exe = Join-Path $dir 'SideStage.exe'
$idleTitle = "SideStage $([char]0x2014) 本窗口未连接"
$null = New-Item -ItemType Directory -Path $run

<#
.SYNOPSIS
    从可信安装包直接解出 {app}，只替换试装标识、路径与快捷方式后重新封装。
    不启动原安装包；若配方仍含正式标识或用户快捷方式常量则中止。
#>
function New-IsolatedSetup([string] $Package, [string] $Name) {
    $sourceDir = Join-Path $run $Name
    $null = New-Item -ItemType Directory -Path $sourceDir
    & $Unpacker -x -q -b "-d$sourceDir" $Package '{app}\*' | Out-Null
    if ($LASTEXITCODE) { throw "无法解出 $Name 安装负载。" }
    $payload = Join-Path $sourceDir '{app}'
    $info = (Get-Item -LiteralPath (Join-Path $payload 'SideStage.exe')).VersionInfo
    $version = ($info.ProductVersion -split '\+')[0]
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-(alpha|beta|rc)\.\d+)?$') { throw "不支持的版本：$version" }
    $recipe = Join-Path $sourceDir 'fixture.iss'
    $definition = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SideStage.iss')).Replace($releaseId, $id).
        Replace('{userprograms}\SideStage', (Join-Path $run 'links/start')).Replace('{userdesktop}\SideStage', (Join-Path $run 'links/desktop')).
        Replace('{localappdata}\Programs\SideStage', $dir).Replace('..\..\assets\branding\SideStage.ico', (Join-Path $root 'assets/branding/SideStage.ico'))
    if (!$definition.Contains("AppId={{$id}") -or $definition.Contains($releaseId) -or $definition -match '\{user(desktop|programs)\}') {
        throw '隔离配方仍可能影响正式安装。'
    }
    [IO.File]::WriteAllText($recipe, $definition, [Text.UTF8Encoding]::new($true))
    $language = Join-Path $root 'artifacts/tooling/ChineseSimplified-6.5.4.isl'
    & $Iscc /Q "/DAppVersion=$version" "/DFileVersion=$(($version -split '-')[0]).0" "/DPublishDir=$payload" `
        "/DOutputDirPath=$sourceDir" "/DChineseMessages=$language" "/DLegacyManifest=$PSScriptRoot/LegacyFlatFiles.sha256" $recipe | Out-Host
    if ($LASTEXITCODE) { throw '隔离安装包编译失败。' }
    return (Get-Item -LiteralPath (Join-Path $sourceDir "SideStage-$version-Setup-x64.exe")).FullName
}

<#
.SYNOPSIS
    仅让本次安装的代理回传一次性管道令牌；不发送截图或启动请求，不接触真实子会话。
#>
function Test-AgentHandshake {
    $nonce = [guid]::NewGuid().ToString('N')
    $pipe = [IO.Pipes.NamedPipeServerStream]::new("SideStage.Trial.$nonce", [IO.Pipes.PipeDirection]::InOut, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    $agent = $null
    $timeout = [Threading.CancellationTokenSource]::new(15000)
    try {
        $agent = Start-Process -FilePath (Join-Path $dir 'SideStage.SessionAgent.exe') -WorkingDirectory $dir `
            -ArgumentList "--pipe-name SideStage.Trial.$nonce --authentication-token $nonce" -WindowStyle Hidden -PassThru
        $pipe.WaitForConnectionAsync($timeout.Token).GetAwaiter().GetResult()
        $expected = [Text.Encoding]::UTF8.GetBytes("`"$nonce`"")
        $frame = [byte[]]::new(4 + $expected.Length)
        $read = 0
        while ($read -lt $frame.Length) {
            $count = $pipe.ReadAsync($frame, $read, $frame.Length - $read, $timeout.Token).GetAwaiter().GetResult()
            if (!$count) { throw '代理提前断开。' }
            $read += $count
        }
        if ([BitConverter]::ToInt32($frame, 0) -ne $expected.Length -or
            [Text.Encoding]::UTF8.GetString($frame, 4, $expected.Length) -cne "`"$nonce`"") { throw '代理握手响应不符。' }
    } finally {
        $pipe.Dispose()
        $timeout.Dispose()
        if ($agent) {
            try { if (!$agent.WaitForExit(20000)) { throw '管道关闭后试装代理未退出。' } }
            finally { $agent.Dispose() }
        }
    }
}

<#
.SYNOPSIS
    同步执行安装或卸载，拒绝非零退出码，保留安装器日志供定位。
#>
function Invoke-Setup([string] $File, [string] $Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    try {
        if ($process.ExitCode -ne 0) { throw "安装器退出码：$($process.ExitCode)，验收记录：$run" }
    }
    finally { $process.Dispose() }
}

<#
.SYNOPSIS
    从当前用户的卸载记录读取卸载器；只有安装位置和卸载程序均属于本次随机测试目录才允许使用。
#>
function Get-OwnedUninstaller {
    $entry = Get-ItemProperty -LiteralPath $userKey
    $location = [IO.Path]::GetFullPath($entry.InstallLocation).TrimEnd('\')
    $uninstaller = [IO.Path]::GetFullPath($entry.UninstallString.Trim('"'))
    if ($location -ne $dir -or [IO.Path]::GetDirectoryName($uninstaller) -ne $dir -or
        [IO.Path]::GetFileName($uninstaller) -notmatch '^unins[0-9]{3}\.exe$' -or
        -not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "卸载记录不属于本次验收，停止操作：$uninstaller"
    }
    return $uninstaller
}

<#
.SYNOPSIS
    验证开始菜单目标和安装所有权，并确认默认未创建桌面图标。
.OUTPUTS
    返回快捷方式解析出的主程序绝对路径，只用此路径启动验收窗口。
#>
function Get-InstalledEntry {
    $null = Get-OwnedUninstaller
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($shortcut)
        if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf) -or $link.TargetPath -ne $exe -or
            $link.WorkingDirectory -ne $dir -or $link.Arguments -or (Test-Path -LiteralPath $desktop)) {
            throw '快捷方式的目标、启动参数、工作目录或默认桌面图标状态与试装预期不符。'
        }
        return $link.TargetPath
    }
    finally {
        if ($link) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

$quiet = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
$installArgs = "$quiet /LANG=zhcn /DIR=`"$dir`""
$oldSetup = $null
if ($isolated) {
    $oldSetup = New-IsolatedSetup (Get-Item -LiteralPath $PreviousInstaller).FullName 'previous'
    $setup = New-IsolatedSetup $setup 'current'
}
$app = $null
try {
    Invoke-Setup $setup "$installArgs /LOG=`"$run/install.log`""
    & (Join-Path $PSScriptRoot 'Test-PublishLayout.ps1') -Directory $dir -Layout Installed
    $entryPoint = Get-InstalledEntry
    $firstUninstaller = Get-OwnedUninstaller
    Write-Output '首次安装：当前用户卸载记录、开始菜单入口和默认图标选项通过。'

    $app = Start-Process -FilePath $entryPoint -ArgumentList '--standard' -WorkingDirectory $dir -PassThru -WindowStyle Minimized
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 200
        $app.Refresh()
        if ($app.HasExited) { throw "首页未完成启动，程序退出码：$($app.ExitCode)" }
    } until ($app.MainWindowTitle -eq $idleTitle -or [DateTime]::UtcNow -ge $deadline)
    if ($app.MainWindowTitle -ne $idleTitle) { throw '未找到本次安装程序的未连接首页。' }
    if ($app.MainModule.FileName -ne $exe) { throw '当前窗口不属于本轮安装。' }
    Test-AgentHandshake
    Write-Output "首页启动：$($app.MainWindowTitle)；试装代理握手通过。"
    if (-not $app.CloseMainWindow() -or -not $app.WaitForExit(10000) -or $app.ExitCode -ne 0) {
        throw '未连接首页未正常退出。'
    }

    Invoke-Setup $setup "$installArgs /LOG=`"$run/reinstall.log`""
    $null = Get-InstalledEntry
    if ((Get-OwnedUninstaller) -ne $firstUninstaller) { throw '重复安装没有复用原有卸载记录。' }
    Write-Output '重复安装：复用了同一目录、快捷方式和卸载记录。'
    if ($isolated) {
        Invoke-Setup (Get-OwnedUninstaller) "$quiet /LOG=`"$run/before-upgrade.log`""
        if ((Test-Path -LiteralPath $userKey) -or (Test-Path -LiteralPath $exe)) { throw '未回到干净的试装状态。' }
        Invoke-Setup $oldSetup "$installArgs /LOG=`"$run/previous.log`""
        $null = Get-InstalledEntry
        $legacyCount = @(Get-ChildItem -LiteralPath $dir -File -Filter '*.dll').Count
        if (!$legacyCount) { throw '跨版验收的起点不是平铺 DLL 的旧版。' }
        Invoke-Setup $setup "$installArgs /LOG=`"$run/upgrade.log`""
        if (!(Select-String -LiteralPath "$run/upgrade.log" -SimpleMatch 'SideStage mode: update' -Quiet)) { throw '未进入覆盖升级分支。' }
        $null = Get-InstalledEntry
        & (Join-Path $PSScriptRoot 'Test-PublishLayout.ps1') -Directory $dir -Layout Installed
        Write-Output "真实旧负载升级：顶层 DLL 从 $legacyCount 个降至 0。"
    }
}
finally {
    # 只关闭自己启动且仍未连接的首页。如用户已接管窗口，不中断会话，不强制终止进程。
    if ($app) {
        $app.Refresh()
        if (-not $app.HasExited -and $app.MainWindowTitle -eq $idleTitle) {
            $null = $app.CloseMainWindow()
            $null = $app.WaitForExit(10000)
        }
    }
    if ((-not $app -or $app.HasExited) -and (Test-Path -LiteralPath $userKey)) {
        Invoke-Setup (Get-OwnedUninstaller) "$quiet /LOG=`"$run/uninstall.log`""
    }
    if ($app) { $app.Dispose() }
}

# 卸载程序可能交由临时副本清理自身，等待文件和注册项实际消失再报告通过。
$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    $remaining = @(@($dir, $userKey, $shortcut, $desktop) | Where-Object { Test-Path -LiteralPath $_ })
    if ($remaining.Count -eq 0) { break }
    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $deadline)
if ($remaining.Count) { throw "卸载未清理以下项目：$($remaining -join '；')" }
Write-Output "卸载通过，临时安装已移除。验收记录：$run"
