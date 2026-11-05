#Requires -Version 5.1
<#
.SYNOPSIS
  编译 AirPlay 并输出到 publish/<配置>，无参数时以菜单选择 Debug/Release。

.EXAMPLE
  .\build-release.ps1
.EXAMPLE
  .\build-release.ps1 -Configuration Release
.EXAMPLE
  .\build-release.ps1 -SelfContained -Runtime win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [switch] $SelfContained,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = '',

    [string] $OutputDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'AirPlay\AirPlay.csproj'

function Select-Configuration {
    Clear-Host
    Write-Host 'AirPlay 编译配置' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '1. Debug'
    Write-Host '2. Release'
    Write-Host '0. 退出'
    Write-Host ''
    $choice = Read-Host '请选择'
    switch ($choice) {
        '1' { 'Debug' }
        '2' { 'Release' }
        '0' { exit 0 }
        default {
            Write-Host '无效输入' -ForegroundColor Red
            Start-Sleep -Seconds 1
            Select-Configuration
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $Configuration = Select-Configuration
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "publish\$Configuration"
}

if (-not (Test-Path -LiteralPath $project)) {
    throw "找不到项目: $project"
}

Write-Host "==> Restore" -ForegroundColor Cyan
dotnet restore $project

$publishArgs = @(
    'publish', $project
    '-c', $Configuration
    '-o', $OutputDir
    '--no-restore'
)

if ($SelfContained) {
    $publishArgs += @('--self-contained', 'true', '-r', $Runtime)
}
else {
    $publishArgs += @('--self-contained', 'false')
}

Write-Host "==> Publish ($Configuration)" -ForegroundColor Cyan
Write-Host ("dotnet " + ($publishArgs -join ' '))
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码 $LASTEXITCODE"
}

$exe = Join-Path $OutputDir 'AirPlay.exe'
Write-Host ""
Write-Host "完成: $exe" -ForegroundColor Green
if (Test-Path -LiteralPath $exe) {
    Get-Item -LiteralPath $exe | Format-List FullName, Length, LastWriteTime
}

Write-Host ''
Read-Host '按回车键退出' | Out-Null
