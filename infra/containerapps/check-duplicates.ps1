# A-27: was any message processed twice while several replicas were reading embed-requests?
#
# Reads the worker's log lines from Log Analytics (ContainerAppConsoleLogs_CL, like who-handled.ps1)
# and groups them by Service Bus sequence number: every message has its own, and the worker prints
# it on every line about that message, with the replica that handled it:
#   [14:02:11] [meepleledger-worker--v3-6c9f...-x7k2p] #4211 (delivery 1) 'Catan': re-embedded (forced), ... Completed.
#
# What it reports:
#   - Completed more than once: should be 0. Only the replica holding a message's lock can complete
#     it; a second complete fails with MessageLockLost.
#   - Handled more than once: a message whose lines come from more than one delivery. That is
#     at-least-once delivery being visible: the first attempt was abandoned (a 429 storm, a 412 from
#     Cosmos) or lost its lock, and the message came back, maybe to another replica. Each one is
#     printed in full so you can see why. It is harmless because the worker is idempotent (A-19): a
#     second embed writes the same vector and hash, guarded by the ETag.
#   - Completions per replica: how the work was shared out.
#
# Logs reach the workspace a few minutes after they are written, so run this a few minutes after
# watch-scale.ps1 finishes.
#
# Run from the repo root:  pwsh infra/containerapps/check-duplicates.ps1 [-Revision <name>] [-Hours 1]

param(
    [string]$Revision = "",
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

# Revision names only have letters, digits and "-", so they are safe in a KQL string as they are
$revisionFilter = ""
if ($Revision) {
    if ($Revision -cnotmatch '^[a-z0-9-]+$') {
        Write-Host "FAILED: '$Revision' is not a revision name." -ForegroundColor Red
        exit 1
    }
    $revisionFilter = "| where RevisionName_s == '$Revision'"
}

# Only the lines about one message: they all have "#<sequence number> (delivery <n>)" in them
$query = @"
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(${Hours}h)
| where ContainerAppName_s == '$appName'
$revisionFilter
| where Log_s contains '(delivery '
| project TimeGenerated, RevisionName_s, Log_s
| order by TimeGenerated asc
| take 10000
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
    Write-Host "No worker lines about messages in the last $Hours hour(s). Logs take a few minutes to arrive; try again shortly."
    exit 0
}

# [time] [replica] #sequence (delivery n) ...
$pattern = '^\[[^\]]+\] \[(?<who>[^\]]+)\] #(?<sequence>\d+) \(delivery (?<delivery>\d+)\)'

$linesBySequence = [ordered]@{}       # sequence number -> every line about it
$deliveriesBySequence = @{}           # sequence number -> the delivery counts seen (one per attempt)
$completionsBySequence = @{}          # sequence number -> how many "Completed." lines
$completionsByReplica = [ordered]@{}  # replica -> how many messages it completed
$abandonedCount = 0
$unreadable = 0

foreach ($row in $rows) {
    $line = $row[2].TrimEnd()
    $match = [regex]::Match($line, $pattern)
    if (-not $match.Success) {
        # e.g. a line from before A-27, without a replica name
        $unreadable++
        continue
    }

    $who = $match.Groups["who"].Value
    $sequence = $match.Groups["sequence"].Value
    $delivery = $match.Groups["delivery"].Value

    if (-not $linesBySequence.Contains($sequence)) {
        $linesBySequence[$sequence] = @()
        $deliveriesBySequence[$sequence] = @()
        $completionsBySequence[$sequence] = 0
    }
    $linesBySequence[$sequence] += $line
    if ($deliveriesBySequence[$sequence] -notcontains $delivery) {
        $deliveriesBySequence[$sequence] += $delivery
    }

    if ($line.EndsWith("Completed.")) {
        $completionsBySequence[$sequence] = $completionsBySequence[$sequence] + 1
        if (-not $completionsByReplica.Contains($who)) {
            $completionsByReplica[$who] = 0
        }
        $completionsByReplica[$who] = $completionsByReplica[$who] + 1
    }
    if ($line.EndsWith("abandoned, back on the queue")) {
        $abandonedCount++
    }
}

# Sort the messages into the three groups
$completedTwice = @()
$handledTwice = @()
$completedOnce = 0
$neverCompleted = @()
foreach ($sequence in $linesBySequence.Keys) {
    $completions = $completionsBySequence[$sequence]
    if ($completions -gt 1) {
        $completedTwice += $sequence
    }
    if ($completions -eq 1) {
        $completedOnce++
    }
    if ($completions -eq 0) {
        $neverCompleted += $sequence
    }
    if ($deliveriesBySequence[$sequence].Count -gt 1) {
        $handledTwice += $sequence
    }
}

Write-Host "Messages seen:            $($linesBySequence.Count)   (last $Hours hour(s))"
Write-Host "Completed once:           $completedOnce"
Write-Host "Completed more than once: $($completedTwice.Count)"
Write-Host "Handled more than once:   $($handledTwice.Count)   (redelivered after an abandon or a lost lock)"
Write-Host "Not completed (yet):      $($neverCompleted.Count)   (still on the queue, dead-lettered, or logs not in yet)"
Write-Host "Abandon lines:            $abandonedCount"
if ($unreadable -gt 0) {
    Write-Host "Lines without a replica:  $unreadable   (older image; not counted)"
}

Write-Host ""
Write-Host "Completions per replica:"
foreach ($who in $completionsByReplica.Keys) {
    Write-Host "  ${who}: $($completionsByReplica[$who])"
}

if ($completedTwice.Count -gt 0) {
    Write-Host ""
    Write-Host "Completed more than once:" -ForegroundColor Red
    foreach ($sequence in $completedTwice) {
        foreach ($line in $linesBySequence[$sequence]) {
            Write-Host "  $line"
        }
    }
}

if ($handledTwice.Count -gt 0) {
    Write-Host ""
    Write-Host "Handled more than once (every line about each):"
    foreach ($sequence in $handledTwice) {
        Write-Host "  #${sequence}:"
        foreach ($line in $linesBySequence[$sequence]) {
            Write-Host "    $line"
        }
    }
}

Write-Host ""
if ($completedTwice.Count -eq 0) {
    Write-Host "No message was completed twice."
}
if ($neverCompleted.Count -gt 0) {
    Write-Host "Check the dead-letter queue for the ones never completed: dotnet run --project MeepleLedger.Worker -- dlq"
}
