# A-26: which revision handled a message? Asks the Log Analytics workspace.
#
# The environment sends every replica's console output to meepleledger-logs, into the table
# ContainerAppConsoleLogs_CL: one row per line, with the app (ContainerAppName_s), the revision
# (RevisionName_s) and the line itself (Log_s). The worker also prints the revision on every line,
# so the answer is in the log text too.
#
# Logs reach the workspace a few minutes after they are written. For the last few seconds, stream
# one revision instead:
#   az containerapp logs show --name meepleledger-worker --resource-group rg-meepleledger --revision <revision> --type console --follow
#
# The query goes to the Log Analytics query API through az rest (body from a file, so the KQL's
# "|" and quotes never meet az.cmd), so no az extension is needed. A-32 writes the real KQL.
#
# Run from the repo root:  pwsh infra/containerapps/who-handled.ps1 [text to look for, e.g. a game id] [-Hours 1]

param(
    [string]$Text = "",
    [int]$Hours = 1
)

$resourceGroup = "rg-meepleledger"
$workspaceName = "meepleledger-logs"      # from provision-containerapps.ps1
$appName = "meepleledger-worker"

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

$workspaceCustomerId = az monitor log-analytics workspace show --workspace-name $workspaceName --resource-group $resourceGroup --query customerId -o tsv
Test-LastCommand "read the workspace customer id"

# A KQL string in single quotes: a backslash and a quote inside it need a backslash before them
$textFilter = ""
if ($Text) {
    $escaped = $Text.Replace("\", "\\").Replace("'", "\'")
    $textFilter = "| where Log_s contains '$escaped'"
}

$query = @"
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(${Hours}h)
| where ContainerAppName_s == '$appName'
$textFilter
| project TimeGenerated, RevisionName_s, Log_s
| order by TimeGenerated asc
| take 200
"@

$file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-query-$([guid]::NewGuid()).json"
ConvertTo-Json -InputObject @{ query = $query } | Set-Content -Path $file -Encoding utf8
try {
    $json = az rest --method post `
        --url "https://api.loganalytics.io/v1/workspaces/$workspaceCustomerId/query" `
        --resource "https://api.loganalytics.io" `
        --headers "Content-Type=application/json" `
        --body "@$file"
    Test-LastCommand "query $workspaceName"
}
finally {
    Remove-Item $file -Force
}

$result = $json | ConvertFrom-Json
$rows = $result.tables[0].rows
if ($rows.Count -eq 0) {
    Write-Host "No matching lines in the last $Hours hour(s). Logs take a few minutes to arrive; try again shortly."
    exit 0
}

# Count the lines per revision too, to see how the queue was split between them
$perRevision = [ordered]@{}
foreach ($row in $rows) {
    $time = ([datetime]$row[0]).ToLocalTime().ToString("HH:mm:ss")
    $revision = $row[1]
    $line = $row[2].TrimEnd()
    Write-Host "$time  $revision  $line"

    if (-not $perRevision.Contains($revision)) {
        $perRevision[$revision] = 0
    }
    $perRevision[$revision] = $perRevision[$revision] + 1
}

Write-Host ""
Write-Host "Lines per revision:"
foreach ($revision in $perRevision.Keys) {
    Write-Host "  ${revision}: $($perRevision[$revision])"
}
