# A-27: give the worker a KEDA scale rule on the depth of the embed-requests queue, so it scales out
# when the queue backs up and back to zero replicas when it drains.
#
# Makes (or reuses), in rg-meepleledger:
#   - meepleledger-scaler-id: a user-assigned managed identity for the SCALER, with "Azure Service Bus
#                             Data Owner" on the embed-requests queue only
# and then a new worker revision, meepleledger-worker--<suffix> (default v3), whose template has:
#   - min replicas 0, max replicas -MaxReplicas (default 5)
#   - one azure-servicebus scale rule: queueName embed-requests, namespace meepleledger-bus,
#     messageCount -MessageCount (default 40), authenticated as meepleledger-scaler-id
# Once it is provisioned, every other revision is deactivated (revisions.ps1 rollback): the A-26
# revisions have min replicas 1, so they would keep running (and billing) and take messages off the
# queue, and the new revision would never get to scale.
#
# How the rule works. KEDA (part of the Container Apps platform, not your container) polls the queue's
# active message count every pollingInterval seconds (30). At zero replicas, one message is enough to
# activate the revision (activationMessageCount defaults to 0). After that it asks for
#   replicas = ceil(active messages / messageCount), capped at maxReplicas.
# messageCount is messages PER REPLICA, not a total threshold: 200 messages with messageCount 1 asks
# for 200 replicas. When the queue has been empty for cooldownPeriod seconds (300) it scales back to 0.
#
# Why these numbers (change them with -MessageCount and -MaxReplicas):
#   - messageCount 40: 200 queued asks for exactly 5 replicas, and 41 for 2. Below 40 messages, one
#     replica clears the backlog in well under a polling interval, so a second one would arrive to an
#     empty queue.
#   - maxReplicas 5: the real limit is not the worker, it is the game-embeddings deployment (20K tokens
#     a minute, about 80 Game embeds). 5 replicas x 2 concurrent calls is already more than that, so a
#     6th replica would only add 429s, and cost. 5 x 0.25 vCPU for a few minutes is pennies.
#   - Worker__MaxConcurrentCalls 2 (-MaxConcurrentCalls): set here on purpose, rather than inherited
#     from whichever revision came last, for the same 429 reason.
#
# Why the scaler has its own identity. KEDA has to read the queue depth when NO replica exists, so it
# cannot use anything inside a running container: not the worker's Service Bus connection string, and
# not a token the container would fetch. The scale rule names a managed identity assigned to the app,
# and the platform signs in as that identity itself. It is a separate identity from
# meepleledger-worker-id (which only has AcrPull) so each one holds one permission. Reading a queue's
# message count goes through the management side of Service Bus (the runtime properties), which needs
# the Manage right: "Data Receiver" is not enough, so the role is Data Owner, scoped to the one queue.
#
# The scale rule is part of the TEMPLATE, so it makes a new revision. The az CLI (2.90) has no flag for
# a scale rule's identity, so the template is read with az containerapp show, changed, and written back
# with a PATCH to the ARM API through az rest (body from a file: no az.cmd quoting). The template holds
# no secret values (since A-29 the worker reads them from Key Vault), and none are sent.
#
# The image must have A-27's worker (it reads the "force" property and logs the replica name):
# build it first with pwsh infra/acr/build-in-acr.ps1, then this script deploys the newest version tag,
# or -Tag.
#
# METERED: a replica is billed per second while it runs, so at zero replicas the worker costs nothing.
# The scaler's polling is free. The Service Bus calls it makes are not billed on Standard.
#
# Run from the repo root:
#   pwsh infra/containerapps/scale-on-queue.ps1 [-Suffix v3] [-MessageCount 40] [-MaxReplicas 5] [-MaxConcurrentCalls 2] [-Tag <version>]

param(
    [string]$Suffix = "v3",
    [int]$MessageCount = 40,
    [int]$MaxReplicas = 5,
    [int]$MaxConcurrentCalls = 2,
    [string]$Tag = ""
)

$resourceGroup = "rg-meepleledger"
$location = "eastus2"
$appName = "meepleledger-worker"          # from provision-containerapps.ps1
$scalerIdentityName = "meepleledger-scaler-id"
$ruleName = "embed-requests-depth"

$namespaceName = "meepleledger-bus"       # from provision-servicebus.ps1
$queueName = "embed-requests"             # the same name as EmbedRequest.QueueName in the worker

$registryName = "meepleledgeracr"
$imageName = "meepleledger-worker"

# KEDA's defaults, written out so they show in the template. Container Apps calls them
# pollingInterval and cooldownPeriod too.
$pollingIntervalSeconds = 30
$cooldownPeriodSeconds = 300

$apiVersion = "2025-01-01"                # Container Apps ARM API: has scale rule identity, polling and cooldown

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
if ($MessageCount -lt 1 -or $MaxReplicas -lt 1 -or $MaxConcurrentCalls -lt 1) {
    Write-Host "FAILED: -MessageCount, -MaxReplicas and -MaxConcurrentCalls must be 1 or more." -ForegroundColor Red
    exit 1
}
if ($Tag -eq "latest") {
    Write-Host "FAILED: deploy a version tag, not latest (see A-24)." -ForegroundColor Red
    exit 1
}

# 1. The app, its mode, and a suffix that has not been used
$appId = az containerapp show --name $appName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find container app $appName (run pwsh infra/containerapps/provision-containerapps.ps1 first)"
$mode = az containerapp show --name $appName --resource-group $resourceGroup --query properties.configuration.activeRevisionsMode -o tsv
if ($mode -ne "Multiple") {
    Write-Host "FAILED: $appName is in $mode revision mode; the A-26 scripts expect Multiple." -ForegroundColor Red
    exit 1
}

$revisionName = "$appName--$Suffix"
$existing = az containerapp revision list --name $appName --resource-group $resourceGroup --all --query "[?name=='$revisionName'].name" -o tsv
Test-LastCommand "list revisions"
if ($existing -eq $revisionName) {
    Write-Host "FAILED: revision $revisionName already exists. Pick another -Suffix." -ForegroundColor Red
    exit 1
}

# 2. The image: -Tag, or the newest version tag in the registry
$loginServer = az acr show --name $registryName --query loginServer -o tsv
Test-LastCommand "read the registry login server"
if (-not $Tag) {
    $tags = az acr repository show-tags --name $registryName --repository $imageName --orderby time_desc -o tsv
    Test-LastCommand "list the tags of $imageName"
    foreach ($candidate in $tags) {
        if ($candidate -ne "latest") {
            $Tag = $candidate
            break
        }
    }
    if (-not $Tag) {
        Write-Host "FAILED: $imageName has no version tag in $registryName. Run pwsh infra/acr/build-in-acr.ps1 first." -ForegroundColor Red
        exit 1
    }
}
$image = "${loginServer}/${imageName}:$Tag"
Write-Host "Image: $image (it must have A-27's worker: build-in-acr.ps1 after this commit)"

# 3. The scaler's identity
$existingIdentity = az identity list --resource-group $resourceGroup --query "[?name=='$scalerIdentityName'].name" -o tsv
if ($existingIdentity -eq $scalerIdentityName) {
    Write-Host "Managed identity $scalerIdentityName already exists."
}
else {
    Write-Host "Creating managed identity $scalerIdentityName..."
    az identity create --name $scalerIdentityName --resource-group $resourceGroup --location $location --output none
    Test-LastCommand "create managed identity"
}
$scalerIdentityId = az identity show --name $scalerIdentityName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "read the scaler identity id"
$scalerPrincipalId = az identity show --name $scalerIdentityName --resource-group $resourceGroup --query principalId -o tsv
Test-LastCommand "read the scaler identity principal id"

# 4. Data Owner on the one queue (not the namespace): the scaler can see embed-requests and nothing else
$namespaceId = az servicebus namespace show --name $namespaceName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find Service Bus namespace $namespaceName (run pwsh infra/servicebus/provision-servicebus.ps1 first)"
$queueId = "$namespaceId/queues/$queueName"

$roleName = "Azure Service Bus Data Owner"
$existingRole = az role assignment list --role $roleName --scope $queueId --query "[?principalId=='$scalerPrincipalId'].id" -o tsv
if ($existingRole) {
    Write-Host "$scalerIdentityName already has $roleName on $queueName."
}
else {
    Write-Host "Granting $roleName on $queueName to $scalerIdentityName..."
    az role assignment create `
        --assignee-object-id $scalerPrincipalId `
        --assignee-principal-type ServicePrincipal `
        --role $roleName `
        --scope $queueId `
        --output none
    Test-LastCommand "grant $roleName to $scalerIdentityName"
}

# 5. Assign the identity to the app, beside meepleledger-worker-id. Identities are configuration, not
#    template, so this makes no revision. A scale rule can only name an identity the app has.
$assignedJson = az containerapp identity show --name $appName --resource-group $resourceGroup --query userAssignedIdentities -o json
Test-LastCommand "read the app's identities"
$alreadyAssigned = $false
if ($assignedJson) {
    $assigned = $assignedJson | ConvertFrom-Json -AsHashtable
    if ($assigned) {
        foreach ($key in $assigned.Keys) {
            # Resource ids come back with different capitalisation from different commands
            if ($key -ieq $scalerIdentityId) {
                $alreadyAssigned = $true
            }
        }
    }
}
if ($alreadyAssigned) {
    Write-Host "$scalerIdentityName is already assigned to $appName."
}
else {
    Write-Host "Assigning $scalerIdentityName to $appName..."
    az containerapp identity assign --name $appName --resource-group $resourceGroup --user-assigned $scalerIdentityId --output none
    Test-LastCommand "assign $scalerIdentityName to $appName"
}

# 6. The new template: the app's current one, with the suffix, image, env var and scale changed
$templateJson = az containerapp show --name $appName --resource-group $resourceGroup --query properties.template -o json
Test-LastCommand "read the app's template"
$template = $templateJson | ConvertFrom-Json -AsHashtable

$template["revisionSuffix"] = $Suffix

$container = $template["containers"][0]
$container["image"] = $image

# Set Worker__MaxConcurrentCalls, or add it if this template does not have it
$foundSetting = $false
foreach ($variable in $container["env"]) {
    if ($variable["name"] -eq "Worker__MaxConcurrentCalls") {
        $variable["value"] = "$MaxConcurrentCalls"
        $foundSetting = $true
    }
}
if (-not $foundSetting) {
    $container["env"] += @([ordered]@{ name = "Worker__MaxConcurrentCalls"; value = "$MaxConcurrentCalls" })
}

# KEDA's azure-servicebus scaler. Metadata values are strings. "namespace" is the short namespace
# name: with an identity there is no connection string to take it from.
$rule = [ordered]@{
    name = $ruleName
    custom = [ordered]@{
        type = "azure-servicebus"
        metadata = [ordered]@{
            queueName = $queueName
            namespace = $namespaceName
            messageCount = "$MessageCount"
        }
        identity = $scalerIdentityId
    }
}
$template["scale"] = [ordered]@{
    minReplicas = 0
    maxReplicas = $MaxReplicas
    pollingInterval = $pollingIntervalSeconds
    cooldownPeriod = $cooldownPeriodSeconds
    rules = @($rule)
}

$body = [ordered]@{ properties = [ordered]@{ template = $template } }
$file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-worker-scale-$([guid]::NewGuid()).json"
ConvertTo-Json -InputObject $body -Depth 20 | Set-Content -Path $file -Encoding utf8
try {
    Write-Host "Creating revision $revisionName (min 0, max $MaxReplicas, messageCount $MessageCount, $MaxConcurrentCalls at a time)..."
    az rest --method patch `
        --url "https://management.azure.com${appId}?api-version=$apiVersion" `
        --headers "Content-Type=application/json" `
        --body "@$file" `
        --output none
    Test-LastCommand "patch $appName with the scale rule"
}
finally {
    Remove-Item $file -Force
}

# 7. Wait for the revision to be provisioned. Not "Running": with an empty queue it goes straight to
#    zero replicas, which is the point.
Write-Host "Waiting for $revisionName to be provisioned (up to 5 minutes)..."
$state = ""
for ($attempt = 1; $attempt -le 20; $attempt++) {
    $state = az containerapp revision show --name $appName --resource-group $resourceGroup --revision $revisionName --query properties.provisioningState -o tsv
    if ($state -eq "Provisioned" -or $state -eq "Failed") {
        break
    }
    Write-Host "  attempt ${attempt}: $state"
    Start-Sleep -Seconds 15
}
if ($state -ne "Provisioned") {
    Write-Host "FAILED: $revisionName is '$state'. Other revisions left as they are. Read the system log:" -ForegroundColor Red
    Write-Host "  az containerapp logs show --name $appName --resource-group $resourceGroup --revision $revisionName --type system"
    exit 1
}

# 8. Only the new revision stays active (see the top of this file)
& "$PSScriptRoot/revisions.ps1" rollback $revisionName
if ($LASTEXITCODE -ne 0) {
    exit 1
}

Write-Host ""
Write-Host "Scale settings of ${revisionName}:"
az containerapp revision show --name $appName --resource-group $resourceGroup --revision $revisionName --query properties.template.scale -o json
Write-Host ""
Write-Host "Next (two terminals):"
Write-Host "  pwsh infra/containerapps/watch-scale.ps1                              replicas and queue depth every few seconds"
Write-Host "  dotnet run --project MeepleLedger.Worker -- send-many 200             back the queue up"
Write-Host "Then, a few minutes after it is back to zero:"
Write-Host "  pwsh infra/containerapps/check-duplicates.ps1                         was any message completed twice?"
