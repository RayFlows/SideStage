<#
.SYNOPSIS
    将已编译的 Inno 向导封装为单个带交互授权入口的离线安装包。
.DESCRIPTION
    使用 Windows 自带的 Framework 编译器、项目图标及发行版本资源。签名和摘要在入口生成后计算。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Payload, [Parameter(Mandatory)][string] $Destination,
    [Parameter(Mandatory)][string] $Version, [Parameter(Mandatory)][string] $FileVersion)
$ErrorActionPreference = 'Stop'
$compiler = "$env:SystemRoot/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
$icon = (Get-Item -LiteralPath "$PSScriptRoot/../../assets/branding/SideStage.ico").FullName
$inputFile = (Get-Item -LiteralPath $Payload).FullName
$entrySource = (Get-Item -LiteralPath (Join-Path $PSScriptRoot 'SetupEntry.cs')).FullName
$Destination = [IO.Path]::GetFullPath($Destination)
if ($inputFile -eq [IO.Path]::GetFullPath($Destination)) { throw '安装入口与内嵌向导不能是同一文件。' }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?$' -or $FileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw '安装入口版本格式不正确。'
}
$metadata = Join-Path ([IO.Path]::GetDirectoryName($inputFile)) ('Version-' + [guid]::NewGuid().ToString('N') + '.cs')
try {
    [IO.File]::WriteAllText($metadata, "[assembly:System.Reflection.AssemblyFileVersion(`"$FileVersion`")]`n[assembly:System.Reflection.AssemblyInformationalVersion(`"$Version`")]")
    & $compiler /nologo /optimize+ /target:winexe /platform:x64 /codepage:65001 "/win32icon:$icon" `
        "/resource:$inputFile,SideStage.InnoSetup" "/out:$Destination" $entrySource $metadata
    if ($LASTEXITCODE) { throw '无法生成安装入口。' }
} finally {
    if (Test-Path -LiteralPath $metadata) { Remove-Item -LiteralPath $metadata }
}
