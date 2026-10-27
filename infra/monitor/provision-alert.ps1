# A-32: one alert rule: "embed requests were dead-lettered in the last 15 minutes".
#
# A log search alert (Microsoft.Insights/scheduledQueryRules) on the meepleledger-logs workspace. Every
# 15 minutes it runs alerts/dead-lettered-messages.kql over the last 15 minutes and fires when the
# query returns at least one row. It resolves itself (autoMitigate) once a later run returns none.
#
# Why a log alert and not the Service Bus DeadletteredMessages metric: the metric is the number of
# messages SITTING in the dead-letter queue. It stays above zero until someone runs "dlq purge", so an
# alert on it says nothing new after the first time. The spans say what was dead-lettered when, and why.
# The catch: with tracing off (no AzureMonitor:ConnectionString) or a trace sampled away, a dead-letter
# is invisible to this rule.
#
# Who hears about it: with -Email, an action group meepleledger-alerts that emails that address
# (Azure sends a "you have been added" email first). Without it, the rule has no action group and a
# fired alert only shows in the portal (Monitor > Alerts).
#
# Why az rest and not "az monitor scheduled-query create": that command wants the KQL on the command
# line, and az.cmd on Windows reads its "|" as a pipe. A JSON body file has no such problem.
#
# METERED: a log search alert rule bills per rule per month by how often it runs (a 15-minute rule is
# the cheapest step); emails from an action group have a free monthly allowance. Deleting
# rg-meepleledger removes both.
#
# Run from the repo root:  pwsh infra/monitor/provision-alert.ps1 [-Email you@example.com]

param(
    [string]$Email = ""
)

$resourceGroup = "rg-meepleledger"
$workspaceName = "meepleledger-logs"   # from provision-functions.ps1 (A-22)
$ruleName = "meepleledger-dead-letters"
$actionGroupName = "meepleledger-alerts"
$queryFile = Join-Path $PSScriptRoot "alerts/dead-lettered-messages.kql"

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# 1. The workspace the rule queries, and its region (the rule goes in the same one).
$workspaceId = az monitor log-analytics workspace show --workspace-name $workspaceName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find workspace $workspaceName (run pwsh infra/functions/provision-functions.ps1 first)"
$location = az monitor log-analytics workspace show --workspace-name $workspaceName --resource-group $resourceGroup --query location -o tsv
Test-LastCommand "read the region of $workspaceName"
$subscriptionId = az account show --query id -o tsv
Test-LastCommand "read the subscription id"

# 2. The action group, only when there is someone to email. "create" overwrites, so it is idempotent.
$actionGroups = @()
if ($Email -ne "") {
    Write-Host "Action group $actionGroupName (email)..."
    $actionGroupId = az monitor action-group create --name $actionGroupName --resource-group $resourceGroup --short-name "meeple" --action email owner $Email --query id -o tsv
    Test-LastCommand "create action group $actionGroupName"
    $actionGroups += $actionGroupId
}
else {
    Write-Host "No -Email: the rule gets no action group; a fired alert shows in Monitor > Alerts only."
}

# 3. The rule. timeAggregation Count with no measure column counts the query's rows; threshold 0 with
#    GreaterThan means "any row at all".
$body = [ordered]@{
    location = $location
    kind = "LogAlert"
    properties = [ordered]@{
        displayName = "MeepleLedger: embed requests dead-lettered"
        description = "An embed request was dead-lettered (BadPayload, UnsupportedSchemaVersion, or abandoned on its 5th delivery) in the last 15 minutes. Query: infra/monitor/alerts/dead-lettered-messages.kql. Then: dotnet run --project MeepleLedger.Worker -- dlq list"
        severity = 2
        enabled = $true
        evaluationFrequency = "PT15M"
        windowSize = "PT15M"
        scopes = @($workspaceId)
        criteria = [ordered]@{
            allOf = @(
                [ordered]@{
                    query = Get-Content -Path $queryFile -Raw
                    timeAggregation = "Count"
                    operator = "GreaterThan"
                    threshold = 0
                    failingPeriods = [ordered]@{
                        numberOfEvaluationPeriods = 1
                        minFailingPeriodsToAlert = 1
                    }
                }
            )
        }
        autoMitigate = $true
        actions = [ordered]@{
            actionGroups = $actionGroups
        }
    }
}

Write-Host "Alert rule $ruleName..."
$bodyFile = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-alert-$([guid]::NewGuid()).json"
ConvertTo-Json -InputObject $body -Depth 8 | Set-Content -Path $bodyFile -Encoding utf8
try {
    $url = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Insights/scheduledQueryRules/${ruleName}?api-version=2023-12-01"
    az rest --method put --url $url --body "@$bodyFile" --output none
    Test-LastCommand "create alert rule $ruleName"
}
finally {
    Remove-Item $bodyFile -Force
}

Write-Host ""
Write-Host "Done. Alert rule $ruleName checks every 15 minutes."
Write-Host "Try it:  dotnet run --project MeepleLedger.Worker -- send-bad json   (dead-lettered at once)"
Write-Host "         then wait for the next evaluation, up to 15 minutes plus ingestion delay, and look in Monitor > Alerts."
