# A-26: look at and move between the worker's revisions in Container Apps.
#
#   pwsh infra/containerapps/revisions.ps1                     list every revision, active or not
#   pwsh infra/containerapps/revisions.ps1 split               activate every revision: all of them take messages
#   pwsh infra/containerapps/revisions.ps1 rollback [revision] keep only one revision active (default: the first)
#
# "Traffic" for this worker is not ingress traffic. Traffic weights (az containerapp ingress traffic
# set) only split HTTP requests, and the worker has no ingress: nothing calls it, it pulls messages
# off embed-requests. So the split is every active revision competing for the same queue. Service
# Bus hands each message to one receiver, and a revision's share is roughly its replicas times its
# MaxConcurrentCalls (v1: 4, v2: 2), not a percentage you set.
#
# Rolling back is deactivating the newer revision. A deactivated revision keeps its template and can
# be activated again; it runs no replicas, so it costs nothing. Its in-flight messages are abandoned
# when the replica stops and go back on the queue for the revision still running (at-least-once).
#
# Run from the repo root.

param(
    [string]$Action = "list",
    [string]$Revision = ""
)

$resourceGroup = "rg-meepleledger"
$appName = "meepleledger-worker"          # from provision-containerapps.ps1

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

function Show-Revisions {
    # --all includes deactivated revisions, which the plain list leaves out
    az containerapp revision list --name $appName --resource-group $resourceGroup --all `
        --query "sort_by([], &properties.createdTime)[].{revision:name, created:properties.createdTime, active:properties.active, replicas:properties.replicas, state:properties.runningState, image:properties.template.containers[0].image}" -o table
    Test-LastCommand "list revisions"
}

# Revision names, oldest first
function Get-RevisionNames {
    $names = az containerapp revision list --name $appName --resource-group $resourceGroup --all `
        --query "sort_by([], &properties.createdTime)[].name" -o tsv
    Test-LastCommand "list revisions"
    return @($names)
}

# Active state of one revision: "true" or "false"
function Get-RevisionActive([string]$name) {
    return az containerapp revision show --name $appName --resource-group $resourceGroup --revision $name --query properties.active -o tsv
}

if ($Action -eq "list") {
    Show-Revisions
    exit 0
}

if ($Action -eq "split") {
    $names = Get-RevisionNames
    foreach ($name in $names) {
        if ((Get-RevisionActive $name) -eq "true") {
            Write-Host "$name is already active."
        }
        else {
            Write-Host "Activating $name..."
            az containerapp revision activate --name $appName --resource-group $resourceGroup --revision $name --output none
            Test-LastCommand "activate $name"
        }
    }
    Write-Host ""
    Write-Host "Every revision is active and competing for embed-requests:"
    Show-Revisions
    exit 0
}

if ($Action -eq "rollback") {
    $names = Get-RevisionNames
    if ($names.Count -eq 0) {
        Write-Host "FAILED: $appName has no revisions." -ForegroundColor Red
        exit 1
    }

    # The revision to keep: the one named, or the first one ever made
    $keep = $Revision
    if (-not $keep) {
        $keep = $names[0]
    }
    if ($names -notcontains $keep) {
        Write-Host "FAILED: $appName has no revision '$keep'. It has: $($names -join ', ')" -ForegroundColor Red
        exit 1
    }

    # Activate the one to keep first, so there is never a moment with nothing reading the queue
    if ((Get-RevisionActive $keep) -ne "true") {
        Write-Host "Activating $keep..."
        az containerapp revision activate --name $appName --resource-group $resourceGroup --revision $keep --output none
        Test-LastCommand "activate $keep"
    }

    foreach ($name in $names) {
        if ($name -eq $keep) {
            continue
        }
        if ((Get-RevisionActive $name) -eq "true") {
            Write-Host "Deactivating $name..."
            az containerapp revision deactivate --name $appName --resource-group $resourceGroup --revision $name --output none
            Test-LastCommand "deactivate $name"
        }
    }

    Write-Host ""
    Write-Host "Only $keep is active:"
    Show-Revisions
    exit 0
}

Write-Host "Usage: pwsh infra/containerapps/revisions.ps1 [list | split | rollback [revision]]"
exit 1
