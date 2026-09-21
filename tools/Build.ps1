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

# VSIXInstaller met a jour en place des que la version du manifeste est superieure a celle
# installee. La desinstallation n'est donc qu'un repli, pour le cas ou la version n'a pas
# ete incrementee depuis la derniere installation.
$identity = 'ClaudeCodeVsMcp.CD41CDB5-D6BB-4351-972A-BE5912703F9C'

Write-Host "Installation dans l'instance $instanceId..." -ForegroundColor Cyan
& $installer /quiet /instanceIds:$instanceId $vsix

if ($LASTEXITCODE -ne 0) {
    Write-Host "Mise a jour refusee (version inchangee ?). Desinstallation puis nouvelle tentative..." -ForegroundColor Yellow
    & $installer /quiet /uninstall:$identity /instanceIds:$instanceId 2>&1 | Out-Null
    & $installer /quiet /instanceIds:$instanceId $vsix
    if ($LASTEXITCODE -ne 0) { throw "VSIXInstaller a echoue (code $LASTEXITCODE)." }
}
# VSIXInstaller peut sortir en code 0 SANS avoir rien installe, notamment faute de droits
# suffisants. On verifie donc ce qui est reellement deploye plutot que de croire le code retour.
$expected = ([xml](Get-Content (Join-Path $root 'src\ClaudeCodeVsMcp\source.extension.vsixmanifest'))).PackageManifest.Metadata.Identity.Version
$extensionsRoot = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio8.0_$instanceId\Extensions"

$deployed = Get-ChildItem $extensionsRoot -Recurse -Filter 'extension.vsixmanifest' -ErrorAction SilentlyContinue |
    ForEach-Object { try { ([xml](Get-Content $_.FullName)).PackageManifest.Metadata.Identity } catch { } } |
    Where-Object { $_.Id -eq $identity } |
    Select-Object -ExpandProperty Version -First 1

if ($deployed -ne $expected) {
    throw ("Installation non effective : version attendue $expected, version deployee " +
           "$(if ($deployed) { $deployed } else { 'aucune' }). " +
           "VSIXInstaller sort parfois en succes sans rien faire, typiquement faute de droits : " +
           "relancer ce script depuis un terminal eleve.")
}

Write-Host "Installe en $deployed. Relancer Visual Studio, puis : pwsh tools\Smoke-Test.ps1" -ForegroundColor Green
