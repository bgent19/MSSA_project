# A-26: run the embed worker (A-19) in Azure Container Apps, from the image in ACR.
#
# Creates (or reuses), in rg-meepleledger:
#   - meepleledger-logs:      the Log Analytics workspace (already made by provision-functions.ps1 for
#                             Application Insights; reused, so A-32 queries one workspace). The
#                             environment sends every container's console output to it.
#   - meepleledger-env:       a Container Apps environment. The apps in one environment share a
#                             network and a log destination.
#   - meepleledger-worker-id: a user-assigned managed identity with AcrPull on the registry only
#   - meepleledger-worker:    the container app, running meepleledger-worker:<tag> as its first
#                             revision, meepleledger-worker--<suffix> (default suffix v1)
#
# Why a USER-assigned identity, when App Service (A-25) used a system-assigned one: a system identity
# only exists once the app does, so the very first revision would try to pull before the identity has
# AcrPull, and fail. A user-assigned identity is made, and given its role, before the app.
#
# Secrets are Container Apps secrets (configuration.secrets), and the environment variables refer to
# them by name (secretRef), so the values never show in the revision's template or in the portal's
# environment variable list. The Service Bus connection string, the Azure OpenAI key and the Cosmos
# key are secrets; endpoints and names are plain environment variables. A-29 moves the secrets to
# Key Vault. Changing a secret does NOT make a new revision: running replicas keep the old value
# until they are restarted (az containerapp revision restart).
#
# Settings chosen on purpose:
#   - Revision mode MULTIPLE. Single is the default, and in single mode a new revision replaces the
#     old one, so two revisions can never run side by side. This ticket is impossible without it.
#   - No ingress. The worker takes messages off a queue; nothing calls it. Without ingress there is
#     no traffic splitting by weight: "splitting traffic" here means two active revisions competing
#     for the same queue (revisions.ps1 split).
#   - Min replicas 1, max 1. The default minimum is 0, and a queue worker at 0 replicas processes
#     nothing until A-27 gives it a KEDA scale rule. 1 keeps it running until then (and billing).
#
# The image is deployed by its VERSION tag, never :latest (A-24): with -Tag, or by default the newest
# worker tag in the registry that is not "latest". A new revision comes from a change to the template
# (image, environment variables, scale, suffix), never from pushing a new image to the same tag:
# that changes nothing and makes no revision. new-revision.ps1 makes the second one.
#
# If the app already exists this script leaves it alone (a second run would otherwise make another
# revision). Use new-revision.ps1 to change it.
#
# METERED. The environment itself costs nothing on the Consumption plan, but the worker replica is
# billed per second of vCPU and memory while it runs, and min replicas 1 means always. The workspace
# bills per GB of logs ingested. The environment is the billable boundary and stays after the app is
# deleted, so tear down both:
#   az containerapp delete --name meepleledger-worker --resource-group rg-meepleledger --yes
#   az containerapp env delete --name meepleledger-env --resource-group rg-meepleledger --yes
# A-33 tears them down with the resource group.
#
# Run from the repo root:  pwsh infra/containerapps/provision-containerapps.ps1 [-Tag <version>] [-Suffix v1]

param(
    [string]$Tag = "",
    [string]$Suffix = "v1"
)

$resourceGroup = "rg-meepleledger"
$location = "eastus2"                     # same region as the registry, Service Bus, Cosmos and OpenAI
$workspaceName = "meepleledger-logs"      # shared with provision-functions.ps1
$environmentName = "meepleledger-env"
$identityName = "meepleledger-worker-id"
$appName = "meepleledger-worker"
$containerName = "worker"

$registryName = "meepleledgeracr"         # from provision-acr.ps1
$imageName = "meepleledger-worker"

$userSecretsId = "93de1646-b3dc-4c1f-9bc8-3ebc34f364f3"   # the Seeder's, shared by every project

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# A revision name is <app>--<suffix>, and the suffix may only have lower-case letters, digits and "-".
if ($Suffix -cnotmatch '^[a-z0-9][a-z0-9-]*$') {
    Write-Host "FAILED: -Suffix must be lower-case letters, digits and '-', not '$Suffix'." -ForegroundColor Red
    exit 1
}

# 0. Read the user secrets. The worker needs all of these (see RunWorker in MeepleLedger.Worker).
$secretsPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$userSecretsId/secrets.json"
if (-not (Test-Path $secretsPath)) {
    Write-Host "FAILED: no user secrets at $secretsPath. Run the provisioning scripts first." -ForegroundColor Red
    exit 1
}
$secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
$needed = @(
    "ServiceBus:ConnectionString",
    "AzureOpenAI:Endpoint", "AzureOpenAI:Key", "AzureOpenAI:EmbeddingDeployment",
    "Cosmos:Endpoint", "Cosmos:Key", "Cosmos:Database", "Cosmos:Container"
)
$missing = @()
foreach ($name in $needed) {
    if (-not $secrets.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($secrets[$name])) {
        $missing += $name
    }
}
if ($missing.Count -gt 0) {
    Write-Host "FAILED: missing user secrets: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "(provision-servicebus.ps1, provision-openai.ps1 and provision-cosmos.ps1 set them)"
    exit 1
}

# 1. Resource providers. A subscription that has never used Container Apps has to register
#    Microsoft.App first; --wait returns once it is done (instantly if it already is).
Write-Host "Registering the Microsoft.App and Microsoft.OperationalInsights providers..."
az provider register --namespace Microsoft.App --wait --output none
Test-LastCommand "register Microsoft.App"
az provider register --namespace Microsoft.OperationalInsights --wait --output none
Test-LastCommand "register Microsoft.OperationalInsights"

# 2. The registry, and the image version to run.
$registryId = az acr show --name $registryName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find registry $registryName (run pwsh infra/acr/provision-acr.ps1 first)"
$loginServer = az acr show --name $registryName --query loginServer -o tsv
Test-LastCommand "read the registry login server"

if (-not $Tag) {
    # Newest first. The first one that is not "latest" is the newest version.
    $tags = az acr repository show-tags --name $registryName --repository $imageName --orderby time_desc -o tsv
    Test-LastCommand "list the tags of $imageName"
    foreach ($candidate in $tags) {
        if ($candidate -ne "latest") {
            $Tag = $candidate
            break
        }
    }
    if (-not $Tag) {
        Write-Host "FAILED: $imageName has no version tag in $registryName. Run pwsh infra/acr/build-in-acr.ps1 -Run first." -ForegroundColor Red
        exit 1
    }
}
if ($Tag -eq "latest") {
    Write-Host "FAILED: deploy a version tag, not latest (see A-24)." -ForegroundColor Red
    exit 1
}
$image = "${loginServer}/${imageName}:$Tag"
Write-Host "Image: $image"

# 3. The Log Analytics workspace. 30 days retention is the cheapest setting (the first 31 are free).
$existingWorkspace = az monitor log-analytics workspace list --resource-group $resourceGroup --query "[?name=='$workspaceName'].name" -o tsv
if ($existingWorkspace -eq $workspaceName) {
    Write-Host "Log Analytics workspace $workspaceName already exists."
}
else {
    Write-Host "Creating Log Analytics workspace $workspaceName..."
    az monitor log-analytics workspace create --workspace-name $workspaceName --resource-group $resourceGroup --location $location --retention-time 30 --output none
    Test-LastCommand "create Log Analytics workspace"
}
# The environment needs the workspace's customer id (a GUID, not the resource id) and a key.
$workspaceCustomerId = az monitor log-analytics workspace show --workspace-name $workspaceName --resource-group $resourceGroup --query customerId -o tsv
Test-LastCommand "read the workspace customer id"

# 4. The environment. It takes a few minutes the first time.
$existingEnvironment = az containerapp env list --resource-group $resourceGroup --query "[?name=='$environmentName'].name" -o tsv
if ($existingEnvironment -eq $environmentName) {
    Write-Host "Container Apps environment $environmentName already exists."
}
else {
    $workspaceKey = az monitor log-analytics workspace get-shared-keys --workspace-name $workspaceName --resource-group $resourceGroup --query primarySharedKey -o tsv
    Test-LastCommand "read the workspace key"

    Write-Host "Creating Container Apps environment $environmentName (a few minutes)..."
    az containerapp env create `
        --name $environmentName `
        --resource-group $resourceGroup `
        --location $location `
        --logs-destination log-analytics `
        --logs-workspace-id $workspaceCustomerId `
        --logs-workspace-key $workspaceKey `
        --output none
    Test-LastCommand "create Container Apps environment"
}
$environmentId = az containerapp env show --name $environmentName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "read the environment id"

# 5. The identity, and AcrPull on the one registry for it: it can pull images from it, nothing else.
$existingIdentity = az identity list --resource-group $resourceGroup --query "[?name=='$identityName'].name" -o tsv
if ($existingIdentity -eq $identityName) {
    Write-Host "Managed identity $identityName already exists."
}
else {
    Write-Host "Creating managed identity $identityName..."
    az identity create --name $identityName --resource-group $resourceGroup --location $location --output none
    Test-LastCommand "create managed identity"
}
$identityId = az identity show --name $identityName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "read the identity id"
$principalId = az identity show --name $identityName --resource-group $resourceGroup --query principalId -o tsv
Test-LastCommand "read the identity principal id"

$existingRole = az role assignment list --role AcrPull --scope $registryId --query "[?principalId=='$principalId'].id" -o tsv
if ($existingRole) {
    Write-Host "$identityName already has AcrPull on $registryName."
}
else {
    Write-Host "Granting AcrPull on $registryName to $identityName..."
    # --assignee-object-id with the principal type skips a Microsoft Graph lookup, which can fail for
    # an identity created seconds ago
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role AcrPull `
        --scope $registryId `
        --output none
    Test-LastCommand "grant AcrPull to $identityName"
}

# 6. The container app. Left alone if it exists: re-applying the template would make a new revision.
$existingApp = az containerapp list --resource-group $resourceGroup --query "[?name=='$appName'].name" -o tsv
if ($existingApp -eq $appName) {
    Write-Host ""
    Write-Host "Container app $appName already exists; not changed. Make a new revision with:"
    Write-Host "  pwsh infra/containerapps/new-revision.ps1"
    Write-Host "Revisions now:"
    az containerapp revision list --name $appName --resource-group $resourceGroup `
        --query "[].{revision:name, active:properties.active, replicas:properties.replicas, state:properties.runningState, image:properties.template.containers[0].image}" -o table
    exit 0
}

# The whole app as one YAML file. JSON is valid YAML, so it is written with ConvertTo-Json. The file
# goes to az, not the command line, because the secret values have ";" and "=" in them, which do not
# survive being passed to az.cmd on Windows. It holds keys, so it is deleted straight away.
#
# Environment variable names use "__" for ":" (the worker reads env vars after user secrets).
$containerSecrets = @(
    [ordered]@{ name = "servicebus-connection-string"; value = $secrets["ServiceBus:ConnectionString"] },
    [ordered]@{ name = "azure-openai-key"; value = $secrets["AzureOpenAI:Key"] },
    [ordered]@{ name = "cosmos-key"; value = $secrets["Cosmos:Key"] }
)
$environmentVariables = @(
    [ordered]@{ name = "ServiceBus__ConnectionString"; secretRef = "servicebus-connection-string" },
    [ordered]@{ name = "AzureOpenAI__Endpoint"; value = $secrets["AzureOpenAI:Endpoint"] },
    [ordered]@{ name = "AzureOpenAI__Key"; secretRef = "azure-openai-key" },
    [ordered]@{ name = "AzureOpenAI__EmbeddingDeployment"; value = $secrets["AzureOpenAI:EmbeddingDeployment"] },
    [ordered]@{ name = "Cosmos__Endpoint"; value = $secrets["Cosmos:Endpoint"] },
    [ordered]@{ name = "Cosmos__Key"; secretRef = "cosmos-key" },
    [ordered]@{ name = "Cosmos__Database"; value = $secrets["Cosmos:Database"] },
    [ordered]@{ name = "Cosmos__Container"; value = $secrets["Cosmos:Container"] },
    [ordered]@{ name = "Worker__MaxConcurrentCalls"; value = "4" }
)

$userAssignedIdentities = @{}
$userAssignedIdentities[$identityId] = @{}

$app = [ordered]@{
    location = $location
    identity = [ordered]@{
        type = "UserAssigned"
        userAssignedIdentities = $userAssignedIdentities
    }
    properties = [ordered]@{
        managedEnvironmentId = $environmentId
        configuration = [ordered]@{
            activeRevisionsMode = "Multiple"
            secrets = $containerSecrets
            # Pull from the registry as the identity: no user name, no password
            registries = @(
                [ordered]@{ server = $loginServer; identity = $identityId }
            )
        }
        template = [ordered]@{
            revisionSuffix = $Suffix
            containers = @(
                [ordered]@{
                    name = $containerName
                    image = $image
                    # The smallest size: a quarter of a vCPU and half a GB. The worker waits on HTTP calls.
                    resources = [ordered]@{ cpu = 0.25; memory = "0.5Gi" }
                    env = $environmentVariables
                }
            )
            scale = [ordered]@{ minReplicas = 1; maxReplicas = 1 }
        }
    }
}

$file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-worker-$([guid]::NewGuid()).yaml"
ConvertTo-Json -InputObject $app -Depth 10 | Set-Content -Path $file -Encoding utf8
try {
    Write-Host "Creating container app $appName (revision $appName--$Suffix)..."
    az containerapp create --name $appName --resource-group $resourceGroup --yaml $file --output none
    Test-LastCommand "create container app $appName"
}
finally {
    Remove-Item $file -Force
}

# 7. Wait for the revision to run. A new AcrPull assignment can take several minutes to reach the
#    registry; until then the pull is refused and Container Apps keeps retrying. Waiting is the fix.
$revisionName = "$appName--$Suffix"
Write-Host ""
Write-Host "Waiting for $revisionName to be running (up to 10 minutes)..."
$state = ""
for ($attempt = 1; $attempt -le 40; $attempt++) {
    $state = az containerapp revision show --name $appName --resource-group $resourceGroup --revision $revisionName --query properties.runningState -o tsv
    if ($state -eq "Running" -or $state -eq "RunningAtMaxScale") {
        break
    }
    Write-Host "  attempt ${attempt}: $state"
    Start-Sleep -Seconds 15
}

Write-Host ""
if ($state -eq "Running" -or $state -eq "RunningAtMaxScale") {
    Write-Host "Done. $revisionName is $state."
}
else {
    Write-Host "Done, but $revisionName is '$state', not running yet. Read the system log (image pulls, crashes):" -ForegroundColor Yellow
    Write-Host "  az containerapp logs show --name $appName --resource-group $resourceGroup --revision $revisionName --type system"
}
Write-Host "  Environment: $environmentName in $location, logs to $workspaceName"
Write-Host "  App:         $appName, revision mode Multiple, no ingress, 1 replica"
Write-Host "  Revision:    $revisionName, image $image"
Write-Host "  Identity:    $identityName, AcrPull on $registryName"
Write-Host ""
Write-Host "Next:"
Write-Host "  az containerapp logs show --name $appName --resource-group $resourceGroup --revision $revisionName --type console --follow"
Write-Host "  pwsh infra/containerapps/new-revision.ps1      a second revision, both active"
