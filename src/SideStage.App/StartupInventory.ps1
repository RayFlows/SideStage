<#
.SYNOPSIS
    读取登录计划任务及应用包启动声明。个人启动登记由宿主直接读取并核对实时状态。
.DESCRIPTION
    每次解析当前账户可见的所有应用包清单，查询失败时由宿主保留最近成功快照。
    只传回名称、来源、图标位置以及只读原因，任务参数不进入缓存。
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

<# 在包自身的 Assets 中寻找图标，优先清单标记的文件，再找系统缩放资源。 #>
function Get-PackageIcon($package, $manifest) {
    $node = $manifest.SelectSingleNode("//*[local-name()='VisualElements' and @Square44x44Logo]")
    if (!$node) { return $null }
    $relative = $node.GetAttribute('Square44x44Logo')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) { return $null }
    $path = Join-Path $package.InstallLocation $relative
    if ([IO.File]::Exists($path)) { return $path }
    $dir = [IO.Path]::GetDirectoryName($path)
    if ([IO.Directory]::Exists($dir)) {
        return [IO.Directory]::GetFiles($dir, ([IO.Path]::GetFileNameWithoutExtension($path) + '.*.png')) |
            Sort-Object | Select-Object -First 1
    }
}

<#
.SYNOPSIS
    查询当前用户为单个应用包启动入口保存的 Windows 状态。
.DESCRIPTION
    同一个包可以声明多个任务，按包族名和任务标识分别定位。仅解释可读取的 DWORD
    状态值；记录缺失、无权读取或系统返回新值时保留未知，清单中的默认值不能代表用户选择。
#>
function Get-PackageStartupState([string]$family, [string]$taskId) {
    if (!$taskId -or $taskId.Contains('\') -or $taskId.Contains('/')) { return $null }
    $path = "Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$family\$taskId"
    $key = $null
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
        if (!$key) { return $null }
        $state = $key.GetValue('State')
        if ($state -isnot [int]) { return $null }
        if ($state -in 0, 1, 3) { return $false }
        if ($state -in 2, 4) { return $true }
    }
    catch [System.Security.SecurityException] { return $null }
    catch [System.UnauthorizedAccessException] { return $null }
    catch [System.IO.IOException] { return $null }
    finally { if ($key) { $key.Dispose() } }
    return $null
}

<# 枚举可见登录任务，保留完整任务路径供宿主读取定义。 #>
function Get-LogonTasks {
    foreach ($task in (Get-ScheduledTask)) {
        if ($task.Triggers.CimClass.CimClassName -notcontains 'MSFT_TaskLogonTrigger') { continue }
        $first = @($task.Actions)[0]
        $icon = if ($first.Execute) { [Environment]::ExpandEnvironmentVariables($first.Execute.Trim('"')) }
        [pscustomobject]@{Source='Task'; Name=$task.TaskName; Detail=$task.TaskPath; IconPath=[string]$icon}
    }
}
$packages = @{}
foreach ($package in (Get-AppxPackage -PackageTypeFilter Main)) {
    $id = $package.PackageFullName
    $manifest = Get-AppxPackageManifest -Package $id
    $packages[$id] = @(
        foreach ($node in $manifest.SelectNodes("//*[local-name()='Extension' and @Category='windows.startupTask']/*[local-name()='StartupTask']")) {
            $taskId = $node.GetAttribute('TaskId')
            [pscustomobject]@{Source='Package'; Name=$package.Name; Detail=$taskId;
                WindowsEnabled=(Get-PackageStartupState $package.PackageFamilyName $taskId);
                IconPath=[string](Get-PackageIcon $package $manifest); ReadOnlyReason='由应用包管理'}
        }
    )
}
ConvertTo-Json -InputObject @{Entries=@(Get-LogonTasks); Packages=$packages} -Depth 6 -Compress
