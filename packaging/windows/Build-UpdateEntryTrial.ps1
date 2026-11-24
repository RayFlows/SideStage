<#
.SYNOPSIS
    制作仅用于测试手动、自动更新入口的 Windows x64 离线体验 ZIP。
.DESCRIPTION
    从当前源码发布体验宿主，不读取旧 ZIP，不打入真实会话代理。ZIP 内只展示两个
    测试入口、使用说明和 app 目录；验收布局通过后才替换上一次的体验包。
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath "$PSScriptRoot/../..").FullName
$build = Join-Path $root 'artifacts/build/update-trial'
$id = [guid]::NewGuid().ToString('N')
$stage = Join-Path $build $id
$package = Join-Path $stage 'package'
$app = Join-Path $package 'app'
$null = New-Item -ItemType Directory -Path $app

# 体验宿主使用模拟更新列表，不连接真实 RDP，也不需要把多种发布模式的代理复制到同一位置。
& dotnet publish (Join-Path $root 'tests/SideStage.App.FrameCheck') -c Release -r win-x64 --self-contained true `
    '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:PublishDocumentationFiles=false' `
    '-p:SideStagePublishAgentSeparately=true' '-p:DebugType=None' '-p:DebugSymbols=false' -o $app
if ($LASTEXITCODE) { throw "体验宿主发布失败，保留现场：$stage" }
& (Join-Path $PSScriptRoot 'Test-PublishLayout.ps1') -Directory $app -Layout UpdateTrial
foreach ($entry in @(@('01-Manual.cmd','manual'), @('02-Auto.cmd','auto'))) {
    $command = '@start "" "%~dp0app\SideStage.App.FrameCheck.exe" --update-entries ' + $entry[1]
    [IO.File]::WriteAllText((Join-Path $package $entry[0]), $command + "`r`n", [Text.Encoding]::ASCII)
}
$guide = @'
SideStage 更新入口体验版（Windows x64）

请先解压整个文件夹；两个入口都会打开 SideStage 首页。
01-Manual.cmd：打开「高级设置 → 应用更新 → 检查更新」。
02-Auto.cmd：保持首页在前台，模拟发现新版后会自动弹出更新窗口。
选择「稍后」后可通过首页「设置 · 有更新」再次查看。每次启动使用独立设置，不影响正式版。
本包不联网、不安装更新、不连接真实 RDP 会话。
'@
[IO.File]::WriteAllText((Join-Path $package '说明.txt'), $guide, [Text.UTF8Encoding]::new($true))
$zip = Join-Path $stage 'SideStage-UpdateEntryTrial-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip)
$output = [IO.Directory]::CreateDirectory((Join-Path $root 'artifacts/trials')).FullName
$destination = Join-Path $output (Split-Path $zip -Leaf)
[IO.File]::Move($zip, $destination, $true)

# 只清理本轮拥有的随机目录，历史 ZIP 和其他构建现场均不受影响。
$completed = (Resolve-Path -LiteralPath $stage).ProviderPath
if ((Split-Path $completed -Parent) -ne (Resolve-Path -LiteralPath $build).ProviderPath -or
    (Split-Path $completed -Leaf) -cne $id -or ((Get-Item -LiteralPath $completed).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "已生成体验 ZIP，但未能确认本轮暂存目录，未清理：$completed"
}
Remove-Item -LiteralPath $completed -Recurse -Force
Write-Output "离线体验 ZIP：$destination"
