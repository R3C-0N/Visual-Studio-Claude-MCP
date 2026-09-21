<#
.SYNOPSIS
    Teste le serveur MCP en HTTP brut, sans passer par Claude Code.

.DESCRIPTION
    A lancer apres avoir installe l'extension et ouvert Visual Studio. Isole les problemes
    de transport (port, jeton, protocole) des problemes de configuration cote client.
#>
[CmdletBinding()]
param(
    [int] $Port = 5230,
    [string] $Token
)

$ErrorActionPreference = 'Stop'

if (-not $Token) {
    $tokenFile = Join-Path $env:APPDATA 'claude-vs-mcp\token'
    if (-not (Test-Path $tokenFile)) {
        throw "Jeton introuvable dans $tokenFile. Visual Studio a-t-il demarre avec l'extension installee ?"
    }
    $Token = (Get-Content $tokenFile -Raw).Trim()
}

$uri = "http://127.0.0.1:$Port/mcp"
$headers = @{ Authorization = "Bearer $Token" }

function Invoke-Mcp {
    param([string] $Method, [hashtable] $Params = @{}, [int] $Id = 1)

    $body = @{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Params } | ConvertTo-Json -Depth 10
    return Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -ContentType 'application/json' -Body $body
}

Write-Host "=== 1. initialize ===" -ForegroundColor Cyan
$init = Invoke-Mcp -Method 'initialize' -Params @{
    protocolVersion = '2025-06-18'
    capabilities    = @{}
    clientInfo      = @{ name = 'smoke-test'; version = '1' }
}
$init.result | ConvertTo-Json -Depth 5

Write-Host "`n=== 2. tools/list ===" -ForegroundColor Cyan
$tools = Invoke-Mcp -Method 'tools/list' -Id 2
Write-Host ("{0} outils exposes :" -f $tools.result.tools.Count)
$tools.result.tools | ForEach-Object { "  - $($_.name)" }

Write-Host "`n=== 3. list_instances ===" -ForegroundColor Cyan
$instances = Invoke-Mcp -Method 'tools/call' -Id 3 -Params @{
    name = 'list_instances'; arguments = @{}
}
$instances.result.content[0].text

Write-Host "`n=== 4. solution_info ===" -ForegroundColor Cyan
$solution = Invoke-Mcp -Method 'tools/call' -Id 4 -Params @{
    name = 'solution_info'; arguments = @{}
}
$solution.result.content[0].text

Write-Host "`n=== 5. Controles negatifs ===" -ForegroundColor Cyan
try {
    Invoke-RestMethod -Uri $uri -Method Post -Headers @{ Authorization = 'Bearer mauvais' } `
        -ContentType 'application/json' -Body '{"jsonrpc":"2.0","id":9,"method":"ping"}' | Out-Null
    Write-Warning "Un jeton invalide a ete accepte : verifier l'authentification."
}
catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 401) {
        Write-Host "  Jeton invalide correctement rejete (401)." -ForegroundColor Green
    }
    else {
        Write-Warning "  Code inattendu : $($_.Exception.Response.StatusCode.value__)"
    }
}

try {
    Invoke-RestMethod -Uri $uri -Method Get -Headers $headers | Out-Null
    Write-Warning "GET accepte alors qu'il devrait renvoyer 405."
}
catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 405) {
        Write-Host "  GET correctement refuse (405)." -ForegroundColor Green
    }
}

Write-Host "`nLe serveur repond correctement." -ForegroundColor Green
Write-Host "Enregistrement dans Claude Code :" -ForegroundColor Yellow
Write-Host "  claude mcp add --transport http visual-studio $uri --header `"Authorization: Bearer $Token`""
