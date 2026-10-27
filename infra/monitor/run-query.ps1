# A-32: run one of the saved KQL queries against the workspace and print the result as a table, so
# you can check a query without opening the portal.
#
#   pwsh infra/monitor/run-query.ps1                               lists the queries
#   pwsh infra/monitor/run-query.ps1 retrieval-latency-by-provider runs queries/retrieval-latency-by-provider.kql
#   pwsh infra/monitor/run-query.ps1 alerts/dead-lettered-messages runs the alert's query
#
# This runs the FILE, not the saved copy in the workspace: after editing a file, run save-queries.ps1
# again so the two stay the same.
#
# A "render" line is a portal instruction; here it is ignored and you get the rows. An empty table is
# not an error: no telemetry in the time range, or (the usual mistake) a classic table name such as
# "requests" instead of "AppRequests", which a workspace answers with nothing.
#
# The query goes to the Log Analytics query API through az rest (the same sign-in as az), because a
# KQL "|" does not survive being passed to az.cmd as an argument on Windows.
#
# Free: queries against a workspace on the default (Analytics) plan are not billed.

param(
    [string]$Name = ""
)

$resourceGroup = "rg-meepleledger"
$workspaceName = "meepleledger-logs"   # from provision-functions.ps1 (A-22)

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# No name: list what there is
if ($Name -eq "") {
    Write-Host "Queries (infra/monitor/queries):"
    foreach ($file in Get-ChildItem -Path (Join-Path $PSScriptRoot "queries") -Filter "*.kql" | Sort-Object Name) {
        Write-Host "  $($file.BaseName)"
    }
    Write-Host "Alert queries (infra/monitor/alerts):"
    foreach ($file in Get-ChildItem -Path (Join-Path $PSScriptRoot "alerts") -Filter "*.kql" | Sort-Object Name) {
        Write-Host "  alerts/$($file.BaseName)"
    }
    exit 0
}

# A bare name is in queries/; "alerts/<name>" is in alerts/
$path = Join-Path $PSScriptRoot "queries/$Name.kql"
if ($Name.StartsWith("alerts/")) {
    $path = Join-Path $PSScriptRoot "$Name.kql"
}
if (-not (Test-Path $path)) {
    Write-Host "No query file $path. Run with no argument to list them." -ForegroundColor Red
    exit 1
}

# The query API wants the workspace's customer id (a GUID), not its resource id
$customerId = az monitor log-analytics workspace show --workspace-name $workspaceName --resource-group $resourceGroup --query customerId -o tsv
Test-LastCommand "find workspace $workspaceName (run pwsh infra/functions/provision-functions.ps1 first)"

# The query's own "where TimeGenerated > ago(...)" decides the range; the timespan here is only an
# upper limit, set to the longest any of the queries looks back.
$body = [ordered]@{
    query = Get-Content -Path $path -Raw
    timespan = "P7D"
}
$bodyFile = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-query-$([guid]::NewGuid()).json"
ConvertTo-Json -InputObject $body -Depth 3 | Set-Content -Path $bodyFile -Encoding utf8
try {
    $url = "https://api.loganalytics.io/v1/workspaces/$customerId/query"
    $responseText = az rest --method post --url $url --resource "https://api.loganalytics.io" --body "@$bodyFile"
    Test-LastCommand "run $Name"
}
finally {
    Remove-Item $bodyFile -Force
}

# The answer is { tables: [ { columns: [ {name} ], rows: [ [values] ] } ] }. Turn each row into an
# object with one property per column, so PowerShell can print it as a table.
$response = ($responseText -join "`n") | ConvertFrom-Json
$table = $response.tables[0]
$rows = @()
foreach ($row in $table.rows) {
    $item = [ordered]@{}
    for ($i = 0; $i -lt $table.columns.Count; $i++) {
        $item[$table.columns[$i].name] = $row[$i]
    }
    $rows += [pscustomobject]$item
}

Write-Host "$Name : $($rows.Count) row(s)"
if ($rows.Count -gt 0) {
    $rows | Format-Table -AutoSize | Out-String -Width 4096 | Write-Host
}
