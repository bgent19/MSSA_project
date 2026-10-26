# A-24: provision the Azure Container Registry that holds the MeepleLedger images.
#
# Creates (or reuses), in rg-meepleledger:
#   - meepleledgeracr: a Basic tier registry with the admin account switched OFF
#   - "purge-old-tags": an ACR task on a weekly timer that deletes old version tags and the untagged
#     manifests repeated pushes leave behind (see the retention notes below)
#   - "build-images": an ACR task that runs infra/acr/acr-task.yaml whenever a commit lands on main
#     in the GitHub repo. Only created when a GitHub token is passed in, see below.
# Safe to run again: the registry is left alone (apart from switching the admin account back off),
# the purge task is recreated so it always matches this file, and the build task is updated in place.
#
# The admin account is one shared user name and password for the whole registry, with push and
# pull. It stays off. Who needs access gets an Azure role instead:
#   - you, pushing from this machine: "az acr login" signs Docker in with your Azure CLI identity,
#     which already has Owner on the resource group
#   - App Service (A-25), Container Apps (A-26), AKS (A-28): a managed identity with AcrPull only
#
# The commit trigger needs a GitHub personal access token: ACR uses it to add a webhook to the repo
# and to clone it. Make a classic token (GitHub > Settings > Developer settings > Personal access
# tokens) with the "repo" scope, which includes webhooks. ACR stores it with the task. Pass it in:
#   pwsh infra/acr/provision-acr.ps1 -GitHubToken <token>
# The task reads acr-task.yaml from GitHub, not from this machine, so push the commit that adds it
# first.
#
# METERED. Basic tier is a fixed daily charge for as long as the registry exists, with 10 GB of
# storage included; more storage bills per GB. ACR Tasks bill per second of build time. Delete it:
#   az acr delete --name meepleledgeracr --resource-group rg-meepleledger --yes
# A-33 tears it down with the resource group, rg-meepleledger.
#
# Run from the repo root:  pwsh infra/acr/provision-acr.ps1 [-GitHubToken <token>]

param(
    [string]$GitHubToken = ""
)

$resourceGroup = "rg-meepleledger"
$location = "eastus2"                   # same region as everything else in rg-meepleledger
$registryName = "meepleledgeracr"       # globally unique, 5-50 letters and digits: <name>.azurecr.io

# Basic, not Standard or Premium. All three tiers have the same API, ACR Tasks and Entra sign-in.
# What the higher tiers buy:
#   - Standard: more included storage (100 GB) and more throughput, for more pulls at once
#   - Premium:  500 GB, geo-replication (one registry, copies in several regions, pulled from the
#               nearest), private endpoints, content trust, and repository-scoped tokens
# One developer pulling a couple of images a day needs none of that.
$sku = "Basic"

$repoUrl = "https://github.com/bgent19/MSSA_project.git"
$branch = "main"
$buildTaskName = "build-images"
$buildTaskFile = "infra/acr/acr-task.yaml"   # path inside the repo; ACR reads it from GitHub

# Retention, decided: keep the 5 newest version tags of each image, delete version tags older than
# 7 days beyond those, and delete untagged manifests. "latest" is moved by every push, so it is
# always among the newest and is never deleted. Every image that is actually deployed (A-25 to
# A-28) is deployed by its version tag, so 5 is enough to roll back a few releases.
#
# --filter is "<repository>:<tag regex>". --untagged also deletes manifests with no tag at all:
# when "latest" moves to a new image, the old manifest keeps only its version tag; when that tag is
# purged too, nothing points at the layers any more. Basic has no automatic retention policy for
# untagged manifests (that is a Premium feature), so this task is the cleanup.
#
# Keep in step with purge-tags.ps1, which runs the same command once, by hand.
$purgeTaskName = "purge-old-tags"
$purgeCommand = "acr purge --filter 'meepleledger:.*' --filter 'meepleledger-worker:.*' --ago 7d --keep 5 --untagged"
$purgeSchedule = "0 4 * * 0"   # 04:00 UTC every Sunday

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# 1. Resource group, shared with every other A- ticket
$groupExists = az group exists --name $resourceGroup
if ($groupExists -ne "true") {
    Write-Host "Creating resource group $resourceGroup in $location..."
    az group create --name $resourceGroup --location $location --output none
    Test-LastCommand "create resource group"
}

# 2. The registry
$existingRegistry = az acr list --resource-group $resourceGroup --query "[?name=='$registryName'].name" -o tsv
if ($existingRegistry -ne $registryName) {
    Write-Host "Creating registry $registryName ($sku)..."
    az acr create `
        --name $registryName `
        --resource-group $resourceGroup `
        --location $location `
        --sku $sku `
        --admin-enabled false `
        --output none
    Test-LastCommand "create registry"
} else {
    Write-Host "Registry $registryName already exists."
}

# The admin account is off by default, but someone may have switched it on in the portal because
# it was quicker. Switch it back off.
$adminEnabled = az acr show --name $registryName --query adminUserEnabled -o tsv
if ($adminEnabled -eq "true") {
    Write-Host "The admin account is on. Switching it off..."
    az acr update --name $registryName --admin-enabled false --output none
    Test-LastCommand "disable admin account"
}

$loginServer = az acr show --name $registryName --query loginServer -o tsv
Test-LastCommand "read login server"
$actualSku = az acr show --name $registryName --query sku.name -o tsv

# 3. The purge task: a timer, no source code. "--context /dev/null" is how ACR spells "no context",
# on Windows too. Deleted and created again, so the command and schedule always match this file.
$existingPurgeTask = az acr task list --registry $registryName --query "[?name=='$purgeTaskName'].name" -o tsv
if ($existingPurgeTask -eq $purgeTaskName) {
    az acr task delete --registry $registryName --name $purgeTaskName --yes --output none
    Test-LastCommand "delete old purge task"
}
Write-Host "Creating task $purgeTaskName ($purgeSchedule UTC)..."
az acr task create `
    --registry $registryName `
    --name $purgeTaskName `
    --cmd $purgeCommand `
    --schedule $purgeSchedule `
    --context /dev/null `
    --output none
Test-LastCommand "create purge task"

# 4. The build task, triggered by a commit to main. Updated in place rather than recreated, so the
# webhook it added to the GitHub repo stays the same one.
$buildTaskStatus = "skipped: no -GitHubToken given"
$existingBuildTask = az acr task list --registry $registryName --query "[?name=='$buildTaskName'].name" -o tsv
if ($GitHubToken -eq "") {
    if ($existingBuildTask -eq $buildTaskName) {
        $buildTaskStatus = "already exists, left alone (no -GitHubToken given)"
    }
    Write-Host "No -GitHubToken given, so the commit-triggered task is not created or changed."
} elseif ($existingBuildTask -eq $buildTaskName) {
    Write-Host "Updating task $buildTaskName..."
    az acr task update `
        --registry $registryName `
        --name $buildTaskName `
        --context "$repoUrl#$branch" `
        --file $buildTaskFile `
        --git-access-token $GitHubToken `
        --commit-trigger-enabled true `
        --pull-request-trigger-enabled false `
        --output none
    Test-LastCommand "update build task"
    $buildTaskStatus = "updated"
} else {
    # Pull request trigger off: a PR from a fork would otherwise build (and push) unreviewed code.
    # The base image trigger stays at its default, on: when Microsoft updates the dotnet base
    # images, ACR rebuilds ours on top of them.
    Write-Host "Creating task $buildTaskName (commit to $branch triggers it)..."
    az acr task create `
        --registry $registryName `
        --name $buildTaskName `
        --context "$repoUrl#$branch" `
        --file $buildTaskFile `
        --git-access-token $GitHubToken `
        --commit-trigger-enabled true `
        --pull-request-trigger-enabled false `
        --output none
    Test-LastCommand "create build task"
    $buildTaskStatus = "created"
}

Write-Host ""
Write-Host "Done."
Write-Host "  Resource group:  $resourceGroup (note it for A-33)"
Write-Host "  Registry:        $loginServer in $location ($actualSku, admin account off)"
Write-Host "  Purge task:      $purgeTaskName, $purgeSchedule UTC"
Write-Host "                   $purgeCommand"
Write-Host "  Build task:      $buildTaskName on $repoUrl#$branch, $buildTaskStatus"
Write-Host ""
Write-Host "Next:"
Write-Host "  pwsh infra/acr/push-images.ps1      build here with Docker, push with a version tag and latest"
Write-Host "  pwsh infra/acr/build-in-acr.ps1     the same, built by ACR (az acr build), no local Docker"
Write-Host "  pwsh infra/acr/build-in-acr.ps1 -Run    both images in one az acr run of acr-task.yaml"
Write-Host "  az acr task list-runs --registry $registryName -o table"
