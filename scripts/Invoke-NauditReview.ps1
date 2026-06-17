<#
.SYNOPSIS
    Feuert ein simuliertes GitLab-Merge-Request-Webhook-Event an den lokal laufenden
    Naudit-Bot ab — zum lokalen Testen ohne echten GitLab-Webhook (Weg A).

.DESCRIPTION
    Der Bot holt anhand von ProjectId/MrIid das echte MR-Diff aus GitLab, lässt es
    vom LLM reviewen und postet einen echten Kommentar in den MR. Das Event selbst
    ist nur Auslöser — Inhalt kommt aus GitLab.

.PARAMETER ProjectId
    Numerische GitLab-Projekt-ID (Settings -> General -> "Project ID").

.PARAMETER MrIid
    Die MR-Nummer (die !nummer, projekt-scoped IID).

.PARAMETER Action
    open | reopen | update (Default: open). Nur diese werden reviewt.

.PARAMETER Title
    Optionaler MR-Titel (fließt in den Prompt ein, kosmetisch).

.PARAMETER BaseUrl
    Adresse des laufenden Bots (Default: http://localhost:5080).

.PARAMETER Secret
    Muss exakt Naudit:GitLab:WebhookSecret entsprechen (Default: test-secret).

.EXAMPLE
    .\scripts\Invoke-NauditReview.ps1 -ProjectId 1234 -MrIid 5
#>
param(
    [Parameter(Mandatory)][int]$ProjectId,
    [Parameter(Mandatory)][int]$MrIid,
    [ValidateSet('open','reopen','update')][string]$Action = 'open',
    [string]$Title = 'Local test review',
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$Secret = 'test-secret'
)

$ErrorActionPreference = 'Stop'

$payload = @{
    object_kind = 'merge_request'
    project     = @{ id = $ProjectId }
    object_attributes = @{
        iid    = $MrIid
        title  = $Title
        action = $Action
    }
} | ConvertTo-Json -Depth 5

$url = "$BaseUrl/webhook/gitlab"
Write-Host "POST $url  (project=$ProjectId, mr=!$MrIid, action=$Action)" -ForegroundColor Cyan

try {
    $resp = Invoke-WebRequest -Uri $url -Method Post -Body $payload `
        -ContentType 'application/json' `
        -Headers @{ 'X-Gitlab-Token' = $Secret }
    Write-Host "HTTP $($resp.StatusCode) — Event angenommen. Review läuft asynchron." -ForegroundColor Green
    Write-Host "Ergebnis erscheint als Kommentar im MR !$MrIid. Bei Ausbleiben: Bot-Logs prüfen." -ForegroundColor Yellow
}
catch {
    $code = $_.Exception.Response.StatusCode.value__
    Write-Host "Fehler: HTTP $code — $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "401 = Secret/Token falsch, 404 = Endpoint nicht erreichbar (läuft der Bot?)." -ForegroundColor Red
    exit 1
}
