# A-26: make a new revision of the worker in Container Apps, next to the ones already running.
#
# A revision is an immutable snapshot of the app's TEMPLATE: the image, the environment variables,
# the scale rules, the revision suffix. Changing any of those makes a new revision; nothing else does.
# Pushing a new image to the tag the app already runs makes no revision, no deployment, and no error,
# which is why the worker is deployed by version tag (A-24). Changing a secret or the registry
# settings (the "configuration") makes no revision either.
#
# The visible change here is Worker__MaxConcurrentCalls: 4 in the first revision, 2 (by default) in
# this one. The worker prints it when it starts ("(2 at a time)"), and every log line carries the
# revision name, so the logs show which revision handled which message. -Tag also changes the image.
#
# The app is in MULTIPLE revision mode, so the revisions already running stay active: both now take
# messages off embed-requests. revisions.ps1 lists them, activates and deactivates them.
#
# METERED: each active revision runs its own replica (min replicas 1), so two active revisions are
# billed twice. Deactivate the one you are done with (revisions.ps1 rollback).
#
# Run from the repo root:  pwsh infra/containerapps/new-revision.ps1 [-Suffix v2] [-MaxConcurrentCalls 2] [-Tag <version>]

param(
    [string]$Suffix = "v2",
    [int]$MaxConcurrentCalls = 2,
    [string]$Tag = ""
)

$resourceGroup = "rg-meepleledger"
$appName = "meepleledger-worker"          # from provision-containerapps.ps1
$containerName = "worker"
$registryName = "meepleledgeracr"
$imageName = "meepleledger-worker"

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

if ($Suffix -cnotmatch '^[a-z0-9][a-z0-9-]*$') {
    Write-Host "FAILED: -Suffix must be lower-case letters, digits and '-', not '$Suffix'." -ForegroundColor Red
    exit 1
}
if ($MaxConcurrentCalls -lt 1) {
    Write-Host "FAILED: -MaxConcurrentCalls must be 1 or more." -ForegroundColor Red
    exit 1
}
if ($Tag -eq "latest") {
    Write-Host "FAILED: deploy a version tag, not latest (see A-24)." -ForegroundColor Red
    exit 1
}

# Without multiple mode the new revision would replace the old one instead of running beside it.
$mode = az containerapp show --name $appName --resource-group $resourceGroup --query properties.configuration.activeRevisionsMode -o tsv
Test-LastCommand "find container app $appName (run pwsh infra/containerapps/provision-containerapps.ps1 first)"
if ($mode -ne "Multiple") {
    Write-Host "FAILED: $appName is in $mode revision mode. Switch it first:" -ForegroundColor Red
    Write-Host "  az containerapp revision set-mode --name $appName --resource-group $resourceGroup --mode multiple"
    exit 1
}

# A suffix can only be used once per app, even by a deactivated revision.
$revisionName = "$appName--$Suffix"
$existing = az containerapp revision list --name $appName --resource-group $resourceGroup --all --query "[?name=='$revisionName'].name" -o tsv
Test-LastCommand "list revisions"
if ($existing -eq $revisionName) {
    Write-Host "FAILED: revision $revisionName already exists. Pick another -Suffix." -ForegroundColor Red
    exit 1
}

# One update: the new suffix plus the template changes. Plain values are safe on the command line.
# Every other environment variable is kept (since A-29 the worker has no secrets in them, only
# KeyVault__Uri and AZURE_CLIENT_ID).
$arguments = @(
    "containerapp", "update",
    "--name", $appName,
    "--resource-group", $resourceGroup,
    "--container-name", $containerName,
    "--revision-suffix", $Suffix,
    "--set-env-vars", "Worker__MaxConcurrentCalls=$MaxConcurrentCalls",
    "--output", "none"
)
if ($Tag) {
    $loginServer = az acr show --name $registryName --query loginServer -o tsv
    Test-LastCommand "read the registry login server"
    $arguments += @("--image", "${loginServer}/${imageName}:$Tag")
}

Write-Host "Creating revision $revisionName (Worker__MaxConcurrentCalls=$MaxConcurrentCalls)..."
az @arguments
Test-LastCommand "update $appName"

Write-Host "Waiting for $revisionName to be running (up to 5 minutes)..."
$state = ""
for ($attempt = 1; $attempt -le 20; $attempt++) {
    $state = az containerapp revision show --name $appName --resource-group $resourceGroup --revision $revisionName --query properties.runningState -o tsv
    if ($state -eq "Running" -or $state -eq "RunningAtMaxScale") {
        break
    }
    Write-Host "  attempt ${attempt}: $state"
    Start-Sleep -Seconds 15
}
Write-Host "$revisionName is '$state'."

Write-Host ""
Write-Host "Revisions now (both should be active):"
az containerapp revision list --name $appName --resource-group $resourceGroup `
    --query "[].{revision:name, active:properties.active, replicas:properties.replicas, state:properties.runningState, image:properties.template.containers[0].image}" -o table
Write-Host ""
Write-Host "Next:"
Write-Host "  dotnet run --project MeepleLedger.Worker -- send <game id>     queue work; either revision may take it"
Write-Host "  pwsh infra/containerapps/who-handled.ps1 <game id>             which revision handled it (logs take a few minutes)"
Write-Host "  pwsh infra/containerapps/revisions.ps1 rollback                 back to the first revision only"
