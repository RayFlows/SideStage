<#
.SYNOPSIS
    用普通账户创建独立的登录任务，在真实侧栏里验证关闭、恢复和缓存回读。
.PARAMETER IncludeElevated
    同一条普通账户登记再交由管理员窗口操作，覆盖默认通过 UAC 启动的使用路径。
#>
[CmdletBinding()]
param([switch] $IncludeElevated)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请从普通权限终端运行，以便测试任务归桌面用户所有。'
}
$runner = (Get-Item -LiteralPath "$PSScriptRoot/bin/Debug/net10.0-windows/SideStage.App.FrameCheck.exe").FullName
$id = [guid]::NewGuid().ToString('N')
$name = "SideStage-Frame-$id"
$trialRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'SideStage.FrameCheck'))
$trial = Join-Path $trialRoot $id
$null = New-Item -ItemType Directory -Path $trial
$registered = $false
try {
    $target = Join-Path $trial 'fixture.exe'
    Copy-Item -LiteralPath ([IO.Path]::Combine([Environment]::SystemDirectory, 'sort.exe')) -Destination $target
    $action = New-ScheduledTaskAction -Execute $target -Argument '/?'
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
    $principal = New-ScheduledTaskPrincipal -UserId $identity.User.Value -LogonType Interactive -RunLevel Limited
    $null = Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal
    $registered = $true
    foreach ($elevated in @($false, $true)) {
        if ($elevated -and !$IncludeElevated) { break }
        $options = @{ FilePath = $runner; ArgumentList = @('--startup-toggle', $id); Wait = $true; PassThru = $true; WindowStyle = 'Hidden' }
        if ($elevated) { $options.Verb = 'RunAs' }
        $process = Start-Process @options
        if ($process.ExitCode -ne 0) {
            Get-ChildItem -LiteralPath $trial -Filter 'preview-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1 |
                Get-Content -Encoding utf8 | Select-String '无法修改|恢复原启动项|下次登录子会话'
            throw "启动项点击验收失败，管理员=$elevated，退出码=$($process.ExitCode)"
        }
        Write-Output "启动项关闭与恢复通过，管理员=$elevated。"
    }
}
finally {
    if ($registered) { Unregister-ScheduledTask -TaskName $name -Confirm:$false }
    $resolved = (Resolve-Path -LiteralPath $trial).Path
    if ([IO.Path]::GetDirectoryName($resolved) -eq $trialRoot -and [IO.Path]::GetFileName($resolved) -eq $id) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    $identity.Dispose()
}
