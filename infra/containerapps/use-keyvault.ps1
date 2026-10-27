# A-29: move the worker in Container Apps from Container Apps secrets to Key Vault.
#
# provision-containerapps.ps1 now makes the worker with no secrets at all, but an app made before A-29
# still has three Container Apps secrets (servicebus-connection-string, azure-openai-key, cosmos-key)
# and environment variables that point at them by secretRef. This script:
#   1. grants meepleledger-worker-id "Key Vault Secrets User" on meepleledger-kv
#   2. makes a new revision from the app's CURRENT template (so the A-27 scale rule, if there is one,
#      carries over) with the image changed to one that reads Key Vault, the secretRef variables taken
#      out, and KeyVault__Uri and AZURE_CLIENT_ID put in
#   3. keeps only that revision active: older ones still read the Container Apps secrets
#   4. deletes the Container Apps secrets, so no key is left in the app's configuration
#
# AZURE_CLIENT_ID: the app has two user-assigned identities once A-27 has run (the worker's, and the
# scaler's). DefaultAzureCredential cannot pick one by itself, so the variable names the worker's.
#
# The template is written back with a PATCH through az rest, as in scale-on-queue.ps1 (the az CLI has
# no flag for a scale rule's identity, and "az containerapp update" would drop it).
#
# The image must have A-29's worker (it reads KeyVault:Uri): build it first with
# pwsh infra/acr/build-in-acr.ps1, then this script deploys the newest version tag, or -Tag.
#
# METERED: the same as any revision (billed per second while a replica runs).
#
# Run from the repo root:  pwsh infra/containerapps/use-keyvault.ps1 [-Suffix kv] [-Tag <version>]

param(
    [string]$Suffix = "kv",
    [string]$Tag = ""
)

$resourceGroup = "rg-meepleledger"
$appName = "meepleledger-worker"          # from provision-containerapps.ps1
$identityName = "meepleledger-worker-id"
$vaultName = "meepleledger-kv"            # from provision-keyvault.ps1

$registryName = "meepleledgeracr"
$imageName = "meepleledger-worker"

$apiVersion = "2025-01-01"                # the same Container Apps ARM API as scale-on-queue.ps1

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
Write-Host "Image: $image (it must have A-29's worker: build-in-acr.ps1 after this commit)"

# 3. The vault, the identity, and Key Vault Secrets User for it on the one vault
$vaultId = az keyvault show --name $vaultName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find Key Vault $vaultName (run pwsh infra/keyvault/provision-keyvault.ps1 first)"
$vaultUri = az keyvault show --name $vaultName --resource-group $resourceGroup --query properties.vaultUri -o tsv
Test-LastCommand "read the vault URI"

$principalId = az identity show --name $identityName --resource-group $resourceGroup --query principalId -o tsv
Test-LastCommand "read the principal id of $identityName"
$clientId = az identity show --name $identityName --resource-group $resourceGroup --query clientId -o tsv
Test-LastCommand "read the client id of $identityName"

$keyVaultRole = "Key Vault Secrets User"
$existingRole = az role assignment list --role $keyVaultRole --scope $vaultId --query "[?principalId=='$principalId'].id" -o tsv
if ($existingRole) {
    Write-Host "$identityName already has $keyVaultRole on $vaultName."
}
else {
    Write-Host "Granting $keyVaultRole on $vaultName to $identityName..."
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role $keyVaultRole `
        --scope $vaultId `
        --output none
    Test-LastCommand "grant $keyVaultRole to $identityName"
    Write-Host "  (a new role can take minutes to work; until then the new revision restarts on a 403)"
}

# 4. The new template: the current one, with the suffix and image changed, every secretRef variable
#    taken out, and the two Key Vault variables set.
$templateJson = az containerapp show --name $appName --resource-group $resourceGroup --query properties.template -o json
Test-LastCommand "read the app's template"
$template = $templateJson | ConvertFrom-Json -AsHashtable

$template["revisionSuffix"] = $Suffix
$container = $template["containers"][0]
$container["image"] = $image

$newEnvironment = @()
foreach ($variable in $container["env"]) {
    $name = $variable["name"]
    if ($variable["secretRef"]) {
        Write-Host "  Taking out $name (secretRef $($variable["secretRef"])): it comes from Key Vault now"
        continue
    }
    if ($name -eq "KeyVault__Uri" -or $name -eq "AZURE_CLIENT_ID") {
        continue
    }
    $newEnvironment += @($variable)
}
$newEnvironment += @([ordered]@{ name = "KeyVault__Uri"; value = $vaultUri })
$newEnvironment += @([ordered]@{ name = "AZURE_CLIENT_ID"; value = $clientId })
$container["env"] = $newEnvironment

$body = [ordered]@{ properties = [ordered]@{ template = $template } }
$file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-worker-keyvault-$([guid]::NewGuid()).json"
ConvertTo-Json -InputObject $body -Depth 20 | Set-Content -Path $file -Encoding utf8
try {
    Write-Host "Creating revision $revisionName (secrets from $vaultUri)..."
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
    Write-Host "FAILED: $revisionName is '$state'. Other revisions and the secrets left as they are. Read the system log:" -ForegroundColor Red
    Write-Host "  az containerapp logs show --name $appName --resource-group $resourceGroup --revision $revisionName --type system"
    exit 1
}

# 6. Only the new revision stays active. The older ones read keys from Container Apps secrets that
#    are about to go, and would break at the next rotation anyway.
& "$PSScriptRoot/revisions.ps1" rollback $revisionName
if ($LASTEXITCODE -ne 0) {
    exit 1
}

# 7. Delete the Container Apps secrets. Configuration, not template: this makes no revision.
$secretNames = az containerapp secret list --name $appName --resource-group $resourceGroup --query "[].name" -o tsv
Test-LastCommand "list the Container Apps secrets"
$secretList = @()
foreach ($secretName in $secretNames) {
    if ($secretName) {
        $secretList += $secretName
    }
}
if ($secretList.Count -gt 0) {
    Write-Host "Deleting Container Apps secrets: $($secretList -join ', ')..."
    az containerapp secret remove --name $appName --resource-group $resourceGroup --secret-names @secretList --output none
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Could not delete them (a deactivated revision may still refer to them). The new revision does not use them." -ForegroundColor Yellow
    }
}
else {
    Write-Host "No Container Apps secrets to delete."
}

Write-Host ""
Write-Host "Done. $revisionName reads its secrets from $vaultUri as $identityName."
Write-Host "Container Apps secrets left:"
az containerapp secret list --name $appName --resource-group $resourceGroup --query "[].name" -o tsv
Write-Host "Environment variables of ${revisionName} (names only):"
az containerapp revision show --name $appName --resource-group $resourceGroup --revision $revisionName --query "properties.template.containers[0].env[].name" -o tsv
Write-Host ""
Write-Host "Look for 'Secrets come from Key Vault' when a replica starts:"
Write-Host "  az containerapp logs show --name $appName --resource-group $resourceGroup --revision $revisionName --type console"
