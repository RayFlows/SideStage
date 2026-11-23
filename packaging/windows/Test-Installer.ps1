<#
.SYNOPSIS
    在当前 Windows x64 交互式桌面验收本地 SideStage 安装包。
.DESCRIPTION
    使用一次性目录，验证首次安装、快捷方式、首页启动、运行时来源、重复安装和卸载。
    没有干净的试装位置时直接停止；不覆盖已有安装，不发送点击或按键，不连接子会话。
.PARAMETER Installer
    由同目录 Build-Installer.ps1 生成的可信安装包。验收记录保存在 artifacts/installer-tests。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Installer)
$ErrorActionPreference = 'Stop'
$setup = (Get-Item -LiteralPath $Installer).FullName
$key = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{28C18EA6-CBC7-4C56-AFC2-A5307EAD9573}_is1'
$userKey = "HKCU:\$key"
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'SideStage.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'SideStage.lnk'
$defaultDir = Join-Path $env:LOCALAPPDATA 'Programs/SideStage'
if (-not [Environment]::Is64BitProcess) { throw '请使用 64 位 PowerShell，以核对正确的卸载记录。' }
foreach ($existing in @($userKey, "HKLM:\$key", $shortcut, $desktop, $defaultDir)) {
    if (Test-Path -LiteralPath $existing) { throw "已存在正式安装或快捷方式，跳过验收，不会覆盖：$existing" }
}

$root = (Get-Item -LiteralPath "$PSScriptRoot/../..").FullName
$run = Join-Path $root ("artifacts/installer-tests/" + [guid]::NewGuid().ToString('N'))
$dir = Join-Path $run 'SideStage 试装'
$exe = Join-Path $dir 'SideStage.exe'
$idleTitle = "SideStage $([char]0x2014) 未连接"
$null = New-Item -ItemType Directory -Path $run

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
$app = $null
try {
    Invoke-Setup $setup "$installArgs /LOG=`"$run/install.log`""
    $entryPoint = Get-InstalledEntry
    $firstUninstaller = Get-OwnedUninstaller
    Write-Output '首次安装：当前用户卸载记录、开始菜单入口和默认图标选项通过。'

    $app = Start-Process -FilePath $entryPoint -WorkingDirectory $dir -PassThru -WindowStyle Minimized
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 200
        $app.Refresh()
        if ($app.HasExited) { throw "首页未完成启动，程序退出码：$($app.ExitCode)" }
    } until ($app.MainWindowTitle -eq $idleTitle -or [DateTime]::UtcNow -ge $deadline)
    if ($app.MainWindowTitle -ne $idleTitle) { throw '未找到本次安装程序的未连接首页。' }
    $clr = $app.Modules | Where-Object ModuleName -EQ 'coreclr.dll' | Select-Object -First 1
    if ($clr.FileName -ne (Join-Path $dir 'coreclr.dll')) { throw '主程序没有加载试装目录内的运行时。' }
    Write-Output "首页启动：$($app.MainWindowTitle)；已确认加载包内 coreclr.dll。"
    if (-not $app.CloseMainWindow() -or -not $app.WaitForExit(10000) -or $app.ExitCode -ne 0) {
        throw '未连接首页未正常退出。'
    }

    Invoke-Setup $setup "$installArgs /LOG=`"$run/reinstall.log`""
    $null = Get-InstalledEntry
    if ((Get-OwnedUninstaller) -ne $firstUninstaller) { throw '重复安装没有复用原有卸载记录。' }
    Write-Output '重复安装：复用了同一目录、快捷方式和卸载记录。'
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
