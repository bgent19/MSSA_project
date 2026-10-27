# A-32: save every query in infra/monitor/queries/ to the Log Analytics workspace, so they are not
# left in a Logs scratch tab.
#
# They go to meepleledger-logs, the workspace behind meepleledger-func-insights (A-22, A-31), as saved
# searches in the category "MeepleLedger" (Logs > Queries, or Legacy queries in newer portals). Each
# file is one query; its first line ("// <name>") is the display name, and the file name is the saved
# search's id, so running this again overwrites the same ones instead of adding copies. A query whose
# file you delete stays in the workspace until you delete it there.
#
# The queries use the WORKSPACE table names (AppRequests, AppDependencies, Properties). Pasted into the
# Application Insights resource's Logs blade instead, they need the classic names (requests,
# dependencies, customDimensions).
#
# Why az rest and not "az monitor log-analytics workspace saved-search create --saved-query": KQL is
# full of "|", and az.cmd on Windows reads a "|" in an argument as a pipe. A JSON body file has no
# such problem.
#
# Free: saved searches cost nothing, and running a query against a workspace on the default
# (Analytics) plan is not billed. Ingestion is what bills (see connect-appinsights.ps1).
#
# Run from the repo root:  pwsh infra/monitor/save-queries.ps1

$resourceGroup = "rg-meepleledger"
$workspaceName = "meepleledger-logs"   # from provision-functions.ps1 (A-22)
$category = "MeepleLedger"
$queryFolder = Join-Path $PSScriptRoot "queries"

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# 1. The workspace.
$workspaceId = az monitor log-analytics workspace show --workspace-name $workspaceName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find workspace $workspaceName (run pwsh infra/functions/provision-functions.ps1 first)"
Write-Host "Workspace $workspaceName"

# 2. One saved search per .kql file.
$files = Get-ChildItem -Path $queryFolder -Filter "*.kql" | Sort-Object Name
if ($files.Count -eq 0) {
    Write-Host "No .kql files in $queryFolder" -ForegroundColor Red
    exit 1
}

foreach ($file in $files) {
    $query = Get-Content -Path $file.FullName -Raw

    # The first line is "// <display name>"
    $firstLine = (Get-Content -Path $file.FullName -TotalCount 1).Trim()
    if (-not $firstLine.StartsWith("//")) {
        Write-Host "FAILED: $($file.Name) must start with a '// <display name>' line" -ForegroundColor Red
        exit 1
    }
    $displayName = $firstLine.Substring(2).Trim()

    # The id must be unique in the workspace; the file name already is
    $savedSearchId = "meepleledger-" + $file.BaseName

    # etag "*" means: create it, or overwrite whatever is there
    $body = [ordered]@{
        etag = "*"
        properties = [ordered]@{
            category = $category
            displayName = $displayName
            query = $query
            version = 2
        }
    }

    $bodyFile = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-savedsearch-$([guid]::NewGuid()).json"
    ConvertTo-Json -InputObject $body -Depth 4 | Set-Content -Path $bodyFile -Encoding utf8
    try {
        $url = "https://management.azure.com$workspaceId/savedSearches/${savedSearchId}?api-version=2020-08-01"
        az rest --method put --url $url --body "@$bodyFile" --output none
        Test-LastCommand "save $($file.Name)"
    }
    finally {
        Remove-Item $bodyFile -Force
    }

    Write-Host "  saved  $displayName"
}

Write-Host ""
Write-Host "Done. $($files.Count) queries in workspace $workspaceName, category $category."
Write-Host "Run one here:  pwsh infra/monitor/run-query.ps1 <file name without .kql>"
