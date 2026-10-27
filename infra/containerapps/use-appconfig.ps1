# A-30: point the worker in Container Apps at App Configuration instead of straight at Key Vault.
#
# Before: KeyVault__Uri, and the worker read its three secrets from the vault (A-29).
# After:  AppConfig__Endpoint and AppConfig__Label=azure. The worker reads its settings from
#         meepleledger-appconfig, and the secrets through the store's Key Vault references.
#
# Both read as meepleledger-worker-id, so this script:
#   1. grants it "App Configuration Data Reader" on the store, and checks it still has "Key Vault
#      Secrets User" on the vault: a Key Vault reference is resolved by the WORKER's identity (the
#      store only holds the link), so taking that role away would break it
#   2. makes a new revision from the app's CURRENT template (so the A-27 scale rule, if there is one,
#      carries over) with the image changed to one that reads App Configuration, KeyVault__Uri taken
#      out, and AppConfig__Endpoint, AppConfig__Label and AZURE_CLIENT_ID put in
#   3. keeps only that revision active
#
# AZURE_CLIENT_ID: the app has two user-assigned identities once A-27 has run (the worker's, and the
# scaler's). DefaultAzureCredential cannot pick one by itself, so the variable names the worker's.
#
# The worker checks the sentinel only when it is about to embed a Game, at most every 300 seconds, so
# an idle worker (or one scaled to zero by KEDA) makes no requests against the Free tier's daily quota.
#
# The image must have A-30's worker (it reads AppConfig:Endpoint): build it first with
# pwsh infra/acr/build-in-acr.ps1, then this script deploys the newest version tag, or -Tag.
#
# METERED: the same as any revision (billed per second while a replica runs).
#
# Run from the repo root:  pwsh infra/containerapps/use-appconfig.ps1 [-Suffix appconfig] [-Tag <version>]

param(
    [string]$Suffix = "appconfig",
    [string]$Tag = ""
)

$resourceGroup = "rg-meepleledger"
$appName = "meepleledger-worker"          # from provision-containerapps.ps1
$identityName = "meepleledger-worker-id"
$vaultName = "meepleledger-kv"            # from provision-keyvault.ps1 (A-29)
$storeName = "meepleledger-appconfig"     # from provision-appconfig.ps1 (A-30)
$appConfigLabel = "azure"

$registryName = "meepleledgeracr"
$imageName = "meepleledger-worker"

$apiVersion = "2025-01-01"                # the same Container Apps ARM API as scale-on-queue.ps1

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# Grants a role at a scope, unless the identity has it already.
function Grant-Role([string]$principalId, [string]$role, [string]$scope, [string]$who) {
    $existing = az role assignment list --role $role --scope $scope --query "[?principalId=='$principalId'].id" -o tsv
    if ($existing) {
        Write-Host "$who already has $role."
        return
    }
    Write-Host "Granting $role to $who..."
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role $role `
        --scope $scope `
        --output none
    Test-LastCommand "grant $role to $who"
    Write-Host "  (a new role can take minutes to work; until then the new revision restarts on a 403)"
}

if ($Suffix -cnotmatch '^[a-z0-9][a-z0-9-]*$') {
    Write-Host "FAILED: -Suffix must be lower-case letters, digits and '-', not '$Suffix'." -ForegroundColor Red
    exit 1
}
if ($Tag -eq "latest") {
    Write-Host "FAILED: deploy a version tag, not latest (see A-24)." -ForegroundColor Red
    exit 1
}

# 1. The app, and a suffix that has not been used
$appId = az containerapp show --name $appName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find container app $appName (run pwsh infra/containerapps/provision-containerapps.ps1 first)"

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
Write-Host "Image: $image (it must have A-30's worker: build-in-acr.ps1 after this commit)"

# 3. The store, the vault, the identity, and its two roles
$storeId = az appconfig show --name $storeName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find App Configuration store $storeName (run pwsh infra/appconfig/provision-appconfig.ps1 first)"
$storeEndpoint = az appconfig show --name $storeName --resource-group $resourceGroup --query endpoint -o tsv
Test-LastCommand "read the store endpoint"
$vaultId = az keyvault show --name $vaultName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find Key Vault $vaultName (run pwsh infra/keyvault/provision-keyvault.ps1 first)"

$principalId = az identity show --name $identityName --resource-group $resourceGroup --query principalId -o tsv
Test-LastCommand "read the principal id of $identityName"
$clientId = az identity show --name $identityName --resource-group $resourceGroup --query clientId -o tsv
Test-LastCommand "read the client id of $identityName"

Grant-Role $principalId "App Configuration Data Reader" $storeId "$identityName (on $storeName)"
Grant-Role $principalId "Key Vault Secrets User" $vaultId "$identityName (on $vaultName)"

# 4. The new template: the current one, with the suffix and image changed, KeyVault__Uri taken out,
#    and the App Configuration variables set. Endpoint variables such as Cosmos__Endpoint stay: the
#    store has the same values and is read after them, so it wins.
$templateJson = az containerapp show --name $appName --resource-group $resourceGroup --query properties.template -o json
Test-LastCommand "read the app's template"
$template = $templateJson | ConvertFrom-Json -AsHashtable

$template["revisionSuffix"] = $Suffix
$container = $template["containers"][0]
$container["image"] = $image

$replacedNames = @("KeyVault__Uri", "AppConfig__Endpoint", "AppConfig__Label", "AZURE_CLIENT_ID")
$newEnvironment = @()
foreach ($variable in $container["env"]) {
    $name = $variable["name"]
    if ($replacedNames -contains $name) {
        if ($name -eq "KeyVault__Uri") {
            Write-Host "  Taking out KeyVault__Uri: the secrets come through App Configuration now"
        }
        continue
    }
    $newEnvironment += @($variable)
}
$newEnvironment += @([ordered]@{ name = "AppConfig__Endpoint"; value = $storeEndpoint })
$newEnvironment += @([ordered]@{ name = "AppConfig__Label"; value = $appConfigLabel })
$newEnvironment += @([ordered]@{ name = "AZURE_CLIENT_ID"; value = $clientId })
$container["env"] = $newEnvironment

$body = [ordered]@{ properties = [ordered]@{ template = $template } }
$file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-worker-appconfig-$([guid]::NewGuid()).json"
ConvertTo-Json -InputObject $body -Depth 20 | Set-Content -Path $file -Encoding utf8
try {
    Write-Host "Creating revision $revisionName (settings from $storeEndpoint, label $appConfigLabel)..."
    az rest --method patch `
        --url "https://management.azure.com${appId}?api-version=$apiVersion" `
        --headers "Content-Type=application/json" `
        --body "@$file" `
        --output none
    Test-LastCommand "patch $appName"
}
finally {
    Remove-Item $file -Force
}

# 5. Wait for the revision to be provisioned. Not "Running": with a KEDA rule and an empty queue it
#    goes straight to zero replicas.
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

# 6. Only the new revision stays active.
& "$PSScriptRoot/revisions.ps1" rollback $revisionName
if ($LASTEXITCODE -ne 0) {
    exit 1
}

Write-Host ""
Write-Host "Done. $revisionName reads its settings from $storeEndpoint (label $appConfigLabel) as $identityName."
Write-Host "Environment variables of ${revisionName} (names only):"
az containerapp revision show --name $appName --resource-group $resourceGroup --revision $revisionName --query "properties.template.containers[0].env[].name" -o tsv
Write-Host ""
Write-Host "Look for 'Settings come from App Configuration' when a replica starts:"
Write-Host "  az containerapp logs show --name $appName --resource-group $resourceGroup --revision $revisionName --type console"
