# A-30: one Azure App Configuration store for the settings every app reads, the Provider switch among them.
#
# Creates (or reuses), in rg-meepleledger:
#   - meepleledger-appconfig: an App Configuration store, Free tier by default, with LOCAL AUTH OFF:
#     there are no access keys or connection strings to leak. Every reader is an Entra identity with
#     an Azure role, the same as Key Vault (A-29).
# then gives YOU "App Configuration Data Owner" on it (an owner of the subscription still cannot read
# or write key-values without a data role), and writes:
#
#   Key (label)                          Value
#   SearchIndex:Provider (local)         InMemory, only if missing: a re-run keeps your choice
#   SearchIndex:Provider (azure)         InMemory, the same
#   MeepleStore:Provider (local, azure)  InMemory, only if missing (S-04). Read once at startup, so
#                                        unlike SearchIndex:Provider a change needs a restart
#   Cosmos:Endpoint, AzureOpenAI:Endpoint, AzureOpenAI:EmbeddingDeployment, EventGrid:TopicEndpoint
#                        (no label)      from the user secrets: not secret, shared by local and azure
#   AzureOpenAI:Key, Cosmos:Key, ServiceBus:ConnectionString, Postgres:ConnectionString,
#   Redis:ConnectionString, EventGrid:TopicKey
#                        (no label)      KEY VAULT REFERENCES to meepleledger-kv, one for each secret
#                                        the vault holds. Never the secret itself.
#   MeepleLedger:Sentinel (no label)     the key the apps watch; set-provider.ps1 bumps it
#   feature flag Tonight (local, azure)  on, only if missing
#
# LABELS keep this machine apart from the deployed app. The web app reads every key with no label and
# then the keys with its own label (AppConfig:Label: "local" in appsettings.json, "azure" as an App
# Service app setting), and the label wins. So the deployed site can run Cosmos while a local run stays
# on InMemory, from one store.
#
# KEY VAULT REFERENCES are only links. An app that reads one gets the URI and reads the SECRET itself,
# as its own identity: so each app needs Key Vault Secrets User on the vault in its own right. The
# store's identity has nothing to do with it (it does not even have one here). This script grants the
# role to the web app and the worker identity again, in case the A-29 scripts have not.
# The references have no version, so they follow the newest version: a rotated key (A-29) reaches the
# apps on their next secret refresh (KeyVault:ReloadIntervalMinutes).
#
# Readers, granted "App Configuration Data Reader" on the store (only if they exist yet):
#   - meepleledger-web (its system-assigned identity)         provision-appservice.ps1 points it here
#   - meepleledger-worker-id (the worker's user identity)      infra/containerapps/use-appconfig.ps1
#
# FREE TIER: 1,000 requests a day per store, then HTTP 429 until midnight UTC. An app that hits it
# keeps the settings it already has, so it looks like stale configuration, not an error. Every
# refresh check is a request (the sentinel, and the feature flags), so the apps check at most every
# 120 seconds (AppConfig:RefreshIntervalSeconds), and only when a request or an Ask comes in. One
# store per subscription on Free. -Sku Standard lifts the quota (and costs about $1.20 a day).
#
# METERED: Free costs nothing; Standard bills per day. A-33 deletes it with the resource group. A
# deleted Standard store is soft-deleted and keeps its name for 7 days; Free has no soft delete.
#
# Run from the repo root:  pwsh infra/appconfig/provision-appconfig.ps1 [-Sku Free|Standard]

param(
    [string]$Sku = "Free"
)

$resourceGroup = "rg-meepleledger"
$location = "eastus2"                     # same region as everything else in the group
$storeName = "meepleledger-appconfig"     # globally unique: https://<name>.azconfig.io
$vaultName = "meepleledger-kv"            # from provision-keyvault.ps1 (A-29)
$webAppName = "meepleledger-web"          # from provision-appservice.ps1 (A-25)
$workerIdentityName = "meepleledger-worker-id"   # from provision-containerapps.ps1 (A-26)

# These names are in the code too (AppConfigKeys.cs, SwitchingGameSearchIndex.cs, the Worker); keep them in step.
$labels = @("local", "azure")
$providerKey = "SearchIndex:Provider"
$storeProviderKey = "MeepleStore:Provider"   # S-04, read in Program.cs
$sentinelKey = "MeepleLedger:Sentinel"
$tonightFlag = "Tonight"

$userSecretsId = "93de1646-b3dc-4c1f-9bc8-3ebc34f364f3"   # the Seeder's, shared by every project

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# Grants a role at a scope, unless the identity has it already.
function Grant-Role([string]$principalId, [string]$principalType, [string]$role, [string]$scope, [string]$who) {
    $existing = az role assignment list --role $role --scope $scope --query "[?principalId=='$principalId'].id" -o tsv
    if ($existing) {
        Write-Host "  $who already has $role."
        return
    }
    Write-Host "  Granting $role to $who..."
    # --assignee-object-id with the principal type skips a Microsoft Graph lookup, which can fail for
    # an identity created seconds ago
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type $principalType `
        --role $role `
        --scope $scope `
        --output none
    Test-LastCommand "grant $role to $who"
}

# Reads a key-value's value, or $null when it is not there. No label means the null label.
function Get-Setting([string]$key, [string]$label) {
    $labelArguments = @()
    if ($label) {
        $labelArguments = @("--label", $label)
    }
    $value = az appconfig kv show --name $storeName --key $key @labelArguments --auth-mode login --query value -o tsv 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    return $value
}

# Writes a plain key-value, unless it already has this value. Values here are endpoints and names:
# no ";" in them, so they survive being passed to az.cmd (see connect-appinsights.ps1 for one that
# does not).
function Set-Setting([string]$key, [string]$value, [string]$label) {
    $labelArguments = @()
    $where = "$key (no label)"
    if ($label) {
        $labelArguments = @("--label", $label)
        $where = "$key ($label)"
    }
    if ((Get-Setting $key $label) -eq $value) {
        Write-Host "  $where is up to date."
        return
    }
    Write-Host "  Setting $where..."
    az appconfig kv set --name $storeName --key $key --value $value @labelArguments --auth-mode login --yes --output none
    Test-LastCommand "set $where"
}

# 0. Read the user secrets, for the settings that are NOT secret. The keys are in Key Vault.
$secretsPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$userSecretsId/secrets.json"
if (-not (Test-Path $secretsPath)) {
    Write-Host "FAILED: no user secrets at $secretsPath. Run the provisioning scripts first." -ForegroundColor Red
    exit 1
}
$secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
$needed = @("Cosmos:Endpoint", "AzureOpenAI:Endpoint", "AzureOpenAI:EmbeddingDeployment")
$missing = @()
foreach ($name in $needed) {
    if (-not $secrets.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($secrets[$name])) {
        $missing += $name
    }
}
if ($missing.Count -gt 0) {
    Write-Host "FAILED: missing user secrets: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "(provision-cosmos.ps1 and provision-openai.ps1 set them)"
    exit 1
}

$vaultId = az keyvault show --name $vaultName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find Key Vault $vaultName (run pwsh infra/keyvault/provision-keyvault.ps1 first)"
$vaultUri = az keyvault show --name $vaultName --resource-group $resourceGroup --query properties.vaultUri -o tsv
Test-LastCommand "read the vault URI"

# 1. The store.
$existingStore = az appconfig list --resource-group $resourceGroup --query "[?name=='$storeName'].name" -o tsv
if ($existingStore -eq $storeName) {
    Write-Host "App Configuration store $storeName already exists."
}
else {
    Write-Host "Creating App Configuration store $storeName ($Sku, local auth off)..."
    az appconfig create `
        --name $storeName `
        --resource-group $resourceGroup `
        --location $location `
        --sku $Sku `
        --disable-local-auth true `
        --output none
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: create the store. The Free tier allows ONE store per subscription; if another exists," -ForegroundColor Red
        Write-Host "delete it or run again with -Sku Standard." -ForegroundColor Red
        exit 1
    }
}

# Again on an existing store, so one made some other way ends up the same.
az appconfig update --name $storeName --resource-group $resourceGroup --disable-local-auth true --output none
Test-LastCommand "switch off local auth"

$storeId = az appconfig show --name $storeName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "read the store id"
$endpoint = az appconfig show --name $storeName --resource-group $resourceGroup --query endpoint -o tsv
Test-LastCommand "read the store endpoint"

# 2. You, as App Configuration Data Owner (read and write key-values and flags, nothing else).
Write-Host "Roles on ${storeName}:"
$myObjectId = az ad signed-in-user show --query id -o tsv
Test-LastCommand "read your own object id (az login first)"
Grant-Role $myObjectId "User" "App Configuration Data Owner" $storeId "you"

# A new role assignment can take minutes to reach the store, and until then every data call is a 403.
Write-Host "Checking that you can read key-values (a new role can take minutes)..."
$canRead = $false
for ($attempt = 1; $attempt -le 40; $attempt++) {
    az appconfig kv list --name $storeName --auth-mode login --top 1 --output none 2>$null
    if ($LASTEXITCODE -eq 0) {
        $canRead = $true
        break
    }
    Write-Host "  attempt ${attempt}: not yet (403 until the role reaches the store)"
    Start-Sleep -Seconds 15
}
if (-not $canRead) {
    Write-Host "FAILED: still no access to $storeName after 10 minutes. Run the script again later." -ForegroundColor Red
    exit 1
}

# 3. The readers. Each needs Data Reader on the store, and Key Vault Secrets User on the vault for the
#    Key Vault references (the APP reads the secret, not the store).
$webPrincipal = az webapp identity show --name $webAppName --resource-group $resourceGroup --query principalId -o tsv 2>$null
if ($LASTEXITCODE -eq 0 -and $webPrincipal) {
    Grant-Role $webPrincipal "ServicePrincipal" "App Configuration Data Reader" $storeId $webAppName
    Grant-Role $webPrincipal "ServicePrincipal" "Key Vault Secrets User" $vaultId "$webAppName (on $vaultName)"
}
else {
    Write-Host "  (no $webAppName yet: provision-appservice.ps1 grants its roles when it makes it)"
}
$workerPrincipal = az identity show --name $workerIdentityName --resource-group $resourceGroup --query principalId -o tsv 2>$null
if ($LASTEXITCODE -eq 0 -and $workerPrincipal) {
    Grant-Role $workerPrincipal "ServicePrincipal" "App Configuration Data Reader" $storeId $workerIdentityName
    Grant-Role $workerPrincipal "ServicePrincipal" "Key Vault Secrets User" $vaultId "$workerIdentityName (on $vaultName)"
}
else {
    Write-Host "  (no $workerIdentityName yet: run this again after provision-containerapps.ps1)"
}

# 4. The Provider switch, one per label. Only written when missing, so a re-run never flips a running
#    site back to InMemory. Change it with set-provider.ps1.
Write-Host "Settings in ${storeName}:"
foreach ($label in $labels) {
    $current = Get-Setting $providerKey $label
    if ($current) {
        Write-Host "  $providerKey ($label) is $current (left as it is)."
    }
    else {
        Set-Setting $providerKey "InMemory" $label
    }

    # The store that keeps the Collection and the Play Log (S-04), the same rule: never overwritten,
    # so a re-run can't put a site that keeps real Plays back on the in-memory Seed.
    $currentStore = Get-Setting $storeProviderKey $label
    if ($currentStore) {
        Write-Host "  $storeProviderKey ($label) is $currentStore (left as it is)."
    }
    else {
        Set-Setting $storeProviderKey "InMemory" $label
    }
}

# 5. The settings that are not secret, shared by both labels.
Set-Setting "Cosmos:Endpoint" $secrets["Cosmos:Endpoint"] ""
Set-Setting "AzureOpenAI:Endpoint" $secrets["AzureOpenAI:Endpoint"] ""
Set-Setting "AzureOpenAI:EmbeddingDeployment" $secrets["AzureOpenAI:EmbeddingDeployment"] ""
if ($secrets.ContainsKey("EventGrid:TopicEndpoint") -and -not [string]::IsNullOrWhiteSpace($secrets["EventGrid:TopicEndpoint"])) {
    Set-Setting "EventGrid:TopicEndpoint" $secrets["EventGrid:TopicEndpoint"] ""
}

# 6. Key Vault references, for every secret the vault holds. A reference to a secret that is NOT in the
#    vault would stop every app at startup (the provider cannot resolve it), so a missing secret gets
#    no reference, and an old reference to one is deleted.
$secretReferences = [ordered]@{
    "AzureOpenAI:Key" = "AzureOpenAI--Key"
    "Cosmos:Key" = "Cosmos--Key"
    "ServiceBus:ConnectionString" = "ServiceBus--ConnectionString"
    "Postgres:ConnectionString" = "Postgres--ConnectionString"
    "Redis:ConnectionString" = "Redis--ConnectionString"
    "EventGrid:TopicKey" = "EventGrid--TopicKey"
}
$vaultSecretNames = az keyvault secret list --vault-name $vaultName --query "[].name" -o tsv
Test-LastCommand "list the secrets in $vaultName (you need Key Vault Secrets Officer: provision-keyvault.ps1)"
Write-Host "Key Vault references:"
foreach ($key in $secretReferences.Keys) {
    $secretName = $secretReferences[$key]
    # No version at the end: the reference follows the newest version of the secret
    $secretUri = $vaultUri.TrimEnd("/") + "/secrets/" + $secretName
    $current = Get-Setting $key ""

    if ($vaultSecretNames -notcontains $secretName) {
        if ($current) {
            Write-Host "  Deleting ${key}: its secret $secretName is no longer in $vaultName..."
            az appconfig kv delete --name $storeName --key $key --auth-mode login --yes --output none
            Test-LastCommand "delete $key"
        }
        else {
            Write-Host "  (no $secretName in $vaultName, so no $key reference)"
        }
        continue
    }

    # A reference's value is a little JSON document: {"uri":"https://.../secrets/AzureOpenAI--Key"}
    if ($current -and $current.Contains($secretUri)) {
        Write-Host "  $key -> $secretName is up to date."
        continue
    }
    Write-Host "  $key -> $secretUri"
    az appconfig kv set-keyvault --name $storeName --key $key --secret-identifier $secretUri --auth-mode login --yes --output none
    Test-LastCommand "set the Key Vault reference $key"
}

# 7. The Tonight feature flag, one per label, on. Only made when missing, so a re-run never turns a
#    switched-off flag back on. Flip it with set-tonight.ps1.
Write-Host "Feature flags:"
foreach ($label in $labels) {
    az appconfig feature show --name $storeName --feature $tonightFlag --label $label --auth-mode login --output none 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  $tonightFlag ($label) exists (left as it is)."
        continue
    }
    Write-Host "  Creating $tonightFlag ($label), on..."
    az appconfig feature set --name $storeName --feature $tonightFlag --label $label --description "The Tonight screen and its nav link" --auth-mode login --yes --output none
    Test-LastCommand "create feature flag $tonightFlag ($label)"
    az appconfig feature enable --name $storeName --feature $tonightFlag --label $label --auth-mode login --yes --output none
    Test-LastCommand "turn on feature flag $tonightFlag ($label)"
}

# 8. The sentinel. Only its first value is written here; set-provider.ps1 changes it.
if (Get-Setting $sentinelKey "") {
    Write-Host "Sentinel $sentinelKey exists."
}
else {
    Set-Setting $sentinelKey (Get-Date).ToUniversalTime().ToString("o") ""
}

# 9. What is in the store, read back rather than assumed.
Write-Host ""
Write-Host "Done. $storeName ($endpoint), $Sku tier, local auth off:"
az appconfig kv list --name $storeName --auth-mode login --all --fields key label content_type -o table
Write-Host ""
Write-Host "Next:"
Write-Host "  pwsh infra/appservice/provision-appservice.ps1        the web app reads the store (AppConfig__Endpoint, label azure)"
Write-Host "  pwsh infra/containerapps/use-appconfig.ps1           the worker reads it (a new revision)"
Write-Host "  pwsh infra/appconfig/set-provider.ps1 Cosmos         flip the deployed Provider, no restart"
Write-Host "  pwsh infra/appconfig/set-tonight.ps1 off             hide the Tonight screen"
Write-Host "Locally:"
Write-Host "  dotnet run --project MeepleLedger -- --AppConfig:Endpoint=$endpoint"
