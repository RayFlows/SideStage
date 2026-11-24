<#
.SYNOPSIS
    检查用户实际看到的 SideStage 发布目录，而不是检查项目文件中的配置值。
.DESCRIPTION
    按相对路径核对完整目录树，拒绝散落的依赖、调试文件和空语言目录。
    WinForms 翻译随单文件入口分发；只把按路径读取的字体和授权声明留在磁盘。
.PARAMETER Directory
    全新发布或隔离安装的程序目录；体验 ZIP 传入解压后的 app 子目录，不对用户目录执行。
.PARAMETER Layout
    App 检查正式负载；Installed 另外允许根目录的 Inno 卸载文件；UpdateTrial 只应有体验宿主。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Directory,
    [ValidateSet('App', 'Installed', 'UpdateTrial')][string] $Layout = 'App')
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $Directory -ErrorAction Stop).FullName
$expected = @('Artwork/', 'Artwork/Fonts/', 'Licenses/',
    'Artwork/Fonts/HarmonyOS_Sans_SC_Regular.ttf', 'Artwork/Fonts/HarmonyOS_Sans_SC_Bold.ttf',
    'Artwork/Fonts/LICENSE_Fonts.txt', 'Licenses/SideStage-LICENSE.txt', 'Licenses/UpdateSources.txt')
$expected += if ($Layout -eq 'UpdateTrial') { 'SideStage.App.FrameCheck.exe' }
    else { 'SideStage.exe', 'SideStage.SessionAgent.exe', 'SideStage.Updater.exe' }
$actual = @(foreach ($entry in Get-ChildItem -LiteralPath $root -Recurse -Force) {
    $path = [IO.Path]::GetRelativePath($root, $entry.FullName).Replace('\', '/')
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "发布目录含链接：$path" }
    if ($entry.PSIsContainer) { "$path/"; continue }
    if ($Layout -eq 'Installed' -and $path -match '^unins\d{3}\.(dat|exe|msg)$') { continue }
    if ($entry.Length -eq 0) { throw "发布文件为空：$path" }
    $path
})
$difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)
if ($difference.Count) { throw "发布布局不符（<= 缺少，=> 多出）：$($difference.ForEach({ "$($_.SideIndicator) $($_.InputObject)" }) -join '；')" }
Write-Output "发布布局通过（$Layout）：无散落 DLL，字体与许可证齐全。"
