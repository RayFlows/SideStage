<#
.SYNOPSIS
    检查用户实际看到的 SideStage 发布目录，而不是检查项目文件中的配置值。
.DESCRIPTION
    正式桌面程序和离线体验宿主共用此检查。两者均应保留字体、许可证和简繁中文的
    WinForms 资源；新增顶层目录时应先确认用途，不能让 SDK 的全部语言目录重新混入。
.PARAMETER Directory
    全新发布的程序目录；对体验 ZIP 应传入解压后的 app 子目录。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Directory)
$ErrorActionPreference = 'Stop'
$expected = @('Artwork', 'Licenses', 'zh-Hans', 'zh-Hant')
$actual = @(Get-ChildItem -LiteralPath $Directory -Directory -Force | Select-Object -ExpandProperty Name)
$difference = @(Compare-Object $expected $actual)
if ($difference.Count) { throw "发布目录不符合预期。应为：$($expected -join ', ')；实际：$($actual -join ', ')" }
foreach ($file in 'zh-Hans/System.Windows.Forms.resources.dll', 'zh-Hant/System.Windows.Forms.resources.dll',
    'Artwork/Fonts/HarmonyOS_Sans_SC_Regular.ttf', 'Licenses/SideStage-LICENSE.txt', 'coreclr.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $Directory $file) -PathType Leaf)) { throw "发布目录缺少：$file" }
}
Write-Output "发布布局通过：$($actual -join ', ')"
