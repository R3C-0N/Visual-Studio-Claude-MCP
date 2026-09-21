<#
.SYNOPSIS
    Compile l'extension et produit le .vsix.

.DESCRIPTION
    MSBuild est resolu par vswhere : cette installation de Visual Studio n'est pas dans
    l'emplacement par defaut, aucun chemin ne doit etre ecrit en dur.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Install
)

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere introuvable : $vswhere" }

$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild introuvable via vswhere." }

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'ClaudeCodeVsMcp.sln'

Write-Host "MSBuild : $msbuild" -ForegroundColor DarkGray
Write-Host "Compilation de $solution en $Configuration..." -ForegroundColor Cyan

& $msbuild $solution -restore -t:Rebuild -p:Configuration=$Configuration -p:DeployExtension=false -v:minimal
if ($LASTEXITCODE -ne 0) { throw "La compilation a echoue (code $LASTEXITCODE)." }

$vsix = Join-Path $root "src\ClaudeCodeVsMcp\bin\$Configuration\ClaudeCodeVsMcp.vsix"
if (-not (Test-Path $vsix)) { throw "VSIX introuvable apres compilation : $vsix" }

Write-Host "`nVSIX produit : $vsix" -ForegroundColor Green

if (-not $Install) {
    Write-Host "Installer avec : pwsh tools\Build.ps1 -Install   (Visual Studio doit etre ferme)"
    return
}

if (Get-Process devenv -ErrorAction SilentlyContinue) {
    throw "Fermer Visual Studio avant d'installer l'extension."
}

$installPath = & $vswhere -latest -prerelease -property installationPath
$instanceId = & $vswhere -latest -prerelease -property instanceId
$installer = Join-Path $installPath 'Common7\IDE\VSIXInstaller.exe'

Write-Host "Installation dans l'instance $instanceId..." -ForegroundColor Cyan
& $installer /quiet /instanceIds:$instanceId $vsix
Write-Host "Installe. Relancer Visual Studio, puis : pwsh tools\Smoke-Test.ps1" -ForegroundColor Green
