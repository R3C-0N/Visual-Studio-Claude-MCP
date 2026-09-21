<#
.SYNOPSIS
    Enregistre le serveur MCP aupres de Claude Code.

.DESCRIPTION
    Une seule fois suffit, quelle que soit l'instance de Visual Studio qui joue le role de hub :
    le port du hub est fixe et le jeton est partage par toutes les instances de l'utilisateur.
#>
[CmdletBinding()]
param(
    [int] $Port = 5230,
    [string] $Name = 'visual-studio',
    [ValidateSet('local', 'user', 'project')]
    [string] $Scope = 'user',
    [switch] $PrintOnly
)

$ErrorActionPreference = 'Stop'

$tokenFile = Join-Path $env:APPDATA 'claude-vs-mcp\token'
if (-not (Test-Path $tokenFile)) {
    throw "Jeton introuvable dans $tokenFile. Lancer Visual Studio avec l'extension installee d'abord."
}

$token = (Get-Content $tokenFile -Raw).Trim()
$url = "http://127.0.0.1:$Port/mcp"

$command = 'claude mcp add --transport http {0} {1} --scope {2} --header "Authorization: Bearer {3}"' -f `
    $Name, $url, $Scope, $token

if ($PrintOnly) {
    Write-Host $command
    return
}

Write-Host "Enregistrement de '$Name' sur $url (scope $Scope)..." -ForegroundColor Cyan
& claude mcp add --transport http $Name $url --scope $Scope --header "Authorization: Bearer $token"

if ($LASTEXITCODE -ne 0) {
    Write-Warning "La commande a echoue. Commande equivalente a lancer a la main :"
    Write-Host $command
    return
}

Write-Host "`nVerification :" -ForegroundColor Cyan
& claude mcp list
