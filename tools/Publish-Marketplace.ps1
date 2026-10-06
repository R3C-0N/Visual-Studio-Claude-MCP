<#
.SYNOPSIS
    Publie le .vsix compile sur le Visual Studio Marketplace.

.DESCRIPTION
    Le jeton (PAT Azure DevOps, scope Marketplace > Manage) est lu dans la variable
    d'environnement VSMARKETPLACE_PAT, a defaut dans le .env a la racine du depot sous la
    forme TOKEN=xxx. Il n'est jamais affiche.

    La fiche est decrite par src\ClaudeCodeVsMcp\publishManifest.json ; nom, description,
    icone et version viennent du .vsixmanifest embarque dans le .vsix.

    Le script ne compile pas : il publie le .vsix Release existant, apres avoir verifie
    qu'il porte bien la version du manifeste source.

.EXAMPLE
    pwsh tools\Publish-Marketplace.ps1 -WhatIf
    Deroule toutes les verifications sans rien publier.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$manifest = Join-Path $root 'src\ClaudeCodeVsMcp\source.extension.vsixmanifest'
$publishManifest = Join-Path $root 'src\ClaudeCodeVsMcp\publishManifest.json'
$vsixPath = Join-Path $root 'src\ClaudeCodeVsMcp\bin\Release\ClaudeCodeVsMcp.vsix'
$envFile = Join-Path $root '.env'

# --- 1. Jeton ---------------------------------------------------------------

$token = $env:VSMARKETPLACE_PAT
if (-not $token -and (Test-Path $envFile)) {
    $line = Get-Content $envFile | Where-Object { $_ -match '^\s*TOKEN\s*=' } | Select-Object -First 1
    if ($line) { $token = ($line -replace '^\s*TOKEN\s*=', '').Trim().Trim('"', "'") }
}
if (-not $token) {
    throw "Jeton introuvable : definir VSMARKETPLACE_PAT ou ajouter TOKEN=xxx dans $envFile"
}

# --- 2. VsixPublisher -------------------------------------------------------

# vswhere ne voit pas toujours les installations hors emplacement par defaut : on se rabat
# sur le dossier d'un devenv en cours d'execution.
$installPaths = @()
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    $installPaths += & $vswhere -all -prerelease -products * -property installationPath
}
$installPaths += Get-Process devenv -ErrorAction SilentlyContinue |
    Where-Object { $_.Path } |
    ForEach-Object { Split-Path (Split-Path (Split-Path $_.Path -Parent) -Parent) -Parent }

$publisher = $installPaths |
    Where-Object { $_ } |
    ForEach-Object { Join-Path $_ 'VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe' } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

if (-not $publisher) {
    throw "VsixPublisher.exe introuvable. Installer la charge de travail 'Developpement d'extensions Visual Studio', ou ouvrir Visual Studio pour qu'il soit detecte."
}
Write-Host "VsixPublisher : $publisher" -ForegroundColor DarkGray

# --- 3. VSIX ----------------------------------------------------------------

$version = ([xml](Get-Content $manifest)).PackageManifest.Metadata.Identity.Version

if (-not (Test-Path $vsixPath)) {
    throw "VSIX introuvable : $vsixPath. Compiler d'abord en Release."
}

# Le .vsix embarque son propre manifeste : on refuse de publier un artefact plus ancien
# que les sources.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($vsixPath)
try {
    $entry = $zip.GetEntry('extension.vsixmanifest')
    if (-not $entry) { throw "Le VSIX ne contient pas extension.vsixmanifest." }
    $reader = New-Object System.IO.StreamReader($entry.Open())
    $packaged = ([xml]$reader.ReadToEnd()).PackageManifest.Metadata.Identity.Version
    $reader.Dispose()
}
finally { $zip.Dispose() }

if ($packaged -ne $version) {
    throw "Incoherence : le manifeste source annonce $version, le VSIX compile $packaged. Recompiler en Release."
}
Write-Host "VSIX verifie en $packaged" -ForegroundColor Green

# --- 4. Publication ---------------------------------------------------------

if (-not $PSCmdlet.ShouldProcess("Marketplace (version $version)", 'publier le VSIX')) {
    Write-Host "Arret avant publication (-WhatIf). Tout est verifie." -ForegroundColor Yellow
    return
}

& $publisher publish -payload $vsixPath -publishManifest $publishManifest -personalAccessToken $token
if ($LASTEXITCODE -ne 0) { throw "VsixPublisher a echoue (code $LASTEXITCODE)." }

$item = (Get-Content $publishManifest -Raw | ConvertFrom-Json)
Write-Host "`nVersion $version publiee : https://marketplace.visualstudio.com/items?itemName=$($item.publisher).$($item.identity.internalName)" -ForegroundColor Green
