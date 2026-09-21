<#
.SYNOPSIS
    Publie une release GitHub a partir de la version du manifeste.

.DESCRIPTION
    La version n'est jamais saisie ici : elle est lue dans source.extension.vsixmanifest,
    seule source de verite du projet. Le script verifie, compile, etiquette et publie.

    Enchainement :
      1. arbre de travail propre et branche a jour
      2. compilation Release, puis controle que le .vsix porte bien la version attendue
      3. tag annote vX.Y.Z, refuse d'ecraser un tag existant
      4. push de la branche et du tag
      5. release GitHub avec le .vsix attache

.EXAMPLE
    pwsh tools\New-Release.ps1 -WhatIf
    Deroule toutes les verifications et la compilation sans rien publier.

.EXAMPLE
    pwsh tools\New-Release.ps1 -Notes "Corrige la negociation de protocole."
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # Notes de release. Par defaut, GitHub les genere depuis les commits.
    [string] $Notes,

    # Publie en brouillon, pour relire avant de rendre visible.
    [switch] $Draft,

    # Autorise la publication depuis une branche autre que la branche par defaut.
    [switch] $AllowAnyBranch
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$manifest = Join-Path $root 'src\ClaudeCodeVsMcp\source.extension.vsixmanifest'
$vsixPath = Join-Path $root 'src\ClaudeCodeVsMcp\bin\Release\ClaudeCodeVsMcp.vsix'

function Assert-Command {
    param([string] $Name, [string] $Hint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name est introuvable. $Hint"
    }
}

Assert-Command 'git' 'Installer git et le rendre accessible dans le PATH.'
Assert-Command 'gh'  'Installer GitHub CLI (https://cli.github.com) puis lancer : gh auth login'

# --- 1. Etat du depot -------------------------------------------------------

Push-Location $root
try {
    if (git status --porcelain) {
        throw "L'arbre de travail n'est pas propre. Commiter ou remiser avant de publier : une release doit correspondre a un etat versionne."
    }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    $defaultBranch = (gh repo view --json defaultBranchRef --jq '.defaultBranchRef.name').Trim()

    if (-not $AllowAnyBranch -and $branch -ne $defaultBranch) {
        throw "Branche courante '$branch', branche par defaut '$defaultBranch'. Utiliser -AllowAnyBranch pour publier quand meme."
    }

    # --- 2. Version -------------------------------------------------------------

    $version = ([xml](Get-Content $manifest)).PackageManifest.Metadata.Identity.Version
    if (-not $version) { throw "Version introuvable dans $manifest." }

    $tag = "v$version"
    Write-Host "Version : $version   tag : $tag   branche : $branch" -ForegroundColor Cyan

    if ((git tag --list $tag)) {
        throw "Le tag $tag existe deja en local. Incrementer la version dans le manifeste (voir la section Versionnage du README)."
    }
    if ((gh release view $tag --json tagName 2>$null)) {
        throw "La release $tag existe deja sur GitHub. Incrementer la version dans le manifeste."
    }

    # --- 3. Compilation ---------------------------------------------------------

    Write-Host "Compilation..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release
    if (-not (Test-Path $vsixPath)) { throw "VSIX introuvable apres compilation : $vsixPath" }

    # Le .vsix embarque son propre manifeste : on verifie qu'il porte la version attendue,
    # sinon on publierait un artefact qui ne correspond pas au tag.
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
        throw "Incoherence : le manifeste source annonce $version, le VSIX compile $packaged."
    }
    Write-Host "  VSIX verifie en $packaged" -ForegroundColor Green

    # --- 4. Tag et push ---------------------------------------------------------

    if (-not $PSCmdlet.ShouldProcess("$tag sur origin", 'creer le tag, pousser et publier la release')) {
        Write-Host "`nArret avant publication (-WhatIf). Tout est verifie et compile." -ForegroundColor Yellow
        return
    }

    git tag -a $tag -m "Version $version"
    git push origin $branch
    git push origin $tag

    # --- 5. Release GitHub ------------------------------------------------------

    $arguments = @('release', 'create', $tag, $vsixPath,
                   '--title', "v$version",
                   '--target', $branch)

    if ($Notes) { $arguments += @('--notes', $Notes) } else { $arguments += '--generate-notes' }
    if ($Draft) { $arguments += '--draft' }

    & gh @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh release create a echoue. Le tag $tag est pousse : corriger puis relancer uniquement la creation de la release."
    }

    Write-Host "`nRelease $tag publiee." -ForegroundColor Green
    gh release view $tag --json url --jq '.url'
}
finally {
    Pop-Location
}
