# A-29: one Key Vault for every secret the solution holds.
#
# Creates (or reuses), in rg-meepleledger:
#   - meepleledger-kv: a Key Vault, Standard tier, with
#       * RBAC authorization, not access policies. Who may read a secret is an Azure role assignment
#         (Key Vault Secrets User), granted the same way as AcrPull or the Service Bus roles.
#       * soft delete (always on for new vaults) kept 7 days, the shortest allowed
#       * purge protection: a deleted secret, or the deleted vault, cannot be purged before those
#         7 days are up, not even by an owner. THIS CANNOT BE TURNED OFF AGAIN.
# then gives YOU "Key Vault Secrets Officer" on it (an owner of the subscription still cannot read or
# write secrets in an RBAC vault without a data role), and copies the secrets in from the user secrets.
# Safe to run again: a secret whose value has not changed is left alone (no new version).
#
# The secrets, named for the setting they become. Key Vault names allow letters, digits and "-" only,
# so the configuration provider reads "--" as ":" (AzureOpenAI--Key is the setting AzureOpenAI:Key):
#   AzureOpenAI--Key               the embeddings key (rotated by rotate-openai-key.ps1)
#   Cosmos--Key                    the Cosmos DB account key
#   Cosmos--ConnectionString       the same key as a connection string, for the function apps' Cosmos binding
#   ServiceBus--ConnectionString   the worker's Service Bus connection string
#   Postgres--ConnectionString     with the password in it (only if the user secret exists)
#   Redis--ConnectionString        with the access key in it (only if the user secret exists)
#   EventGrid--TopicKey            (only if the user secret exists)
#
# Who reads them: each app's own provisioning script grants its identity "Key Vault Secrets User" on
# this vault (provision-appservice.ps1, provision-containerapps.ps1 / use-keyvault.ps1,
# provision-functions.ps1), so run this one first.
#
# The user secrets on this machine stay: the Seeder and the provisioning scripts still read them, and
# they are outside the repo. What goes away is every copy in app settings and container secrets.
#
# METERED, but only per operation (a few cents per 10,000 reads); the vault itself costs nothing.
# Deleting the resource group soft-deletes the vault: with purge protection the NAME stays taken for
# 7 days. Running this script inside those 7 days recovers the deleted vault instead of making one.
#
# Run from the repo root:  pwsh infra/keyvault/provision-keyvault.ps1 [-CheckSoftDelete]

param(
    [switch]$CheckSoftDelete
)

$resourceGroup = "rg-meepleledger"
$location = "eastus2"                     # same region as everything else in the group
$vaultName = "meepleledger-kv"            # globally unique: https://<name>.vault.azure.net
$retentionDays = 7                        # soft delete retention, 7 to 90 days

$userSecretsId = "93de1646-b3dc-4c1f-9bc8-3ebc34f364f3"   # the Seeder's, shared by every project

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# Writes one secret, unless it already has this value. The value goes through a file, not the command
# line: connection strings have ";" and "=" in them, which do not survive being passed to az.cmd on
# Windows. WriteAllText adds no newline at the end, so the secret is exactly the value.
function Set-Secret([string]$secretName, [string]$value) {
    $current = az keyvault secret show --vault-name $vaultName --name $secretName --query value -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and $current -eq $value) {
        Write-Host "  $secretName is up to date."
        return
    }

    $file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-secret-$([guid]::NewGuid()).txt"
    [System.IO.File]::WriteAllText($file, $value)
    try {
        Write-Host "  Setting $secretName..."
        az keyvault secret set --vault-name $vaultName --name $secretName --file $file --encoding utf-8 --output none
        Test-LastCommand "set secret $secretName"
    }
    finally {
        Remove-Item $file -Force
    }
}

# 0. Read the user secrets. These four must be there; the rest are copied when they are.
$secretsPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$userSecretsId/secrets.json"
if (-not (Test-Path $secretsPath)) {
    Write-Host "FAILED: no user secrets at $secretsPath. Run the provisioning scripts first." -ForegroundColor Red
    exit 1
}
$secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
$needed = @("AzureOpenAI:Key", "Cosmos:Endpoint", "Cosmos:Key", "ServiceBus:ConnectionString")
$missing = @()
foreach ($name in $needed) {
    if (-not $secrets.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($secrets[$name])) {
        $missing += $name
    }
}
if ($missing.Count -gt 0) {
    Write-Host "FAILED: missing user secrets: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "(provision-openai.ps1, provision-cosmos.ps1 and provision-servicebus.ps1 set them)"
    exit 1
}

# 1. The vault. A vault deleted in the last 7 days still holds its name, so recover it rather than
#    fail to create a new one.
$existingVault = az keyvault list --resource-group $resourceGroup --query "[?name=='$vaultName'].name" -o tsv
if ($existingVault -eq $vaultName) {
    Write-Host "Key Vault $vaultName already exists."
}
else {
    $deletedVault = az keyvault list-deleted --resource-type vault --query "[?name=='$vaultName'].name" -o tsv
    if ($deletedVault -eq $vaultName) {
        Write-Host "Key Vault $vaultName was deleted and is still soft-deleted; recovering it..."
        az keyvault recover --name $vaultName --output none
        Test-LastCommand "recover Key Vault $vaultName"
    }
    else {
        Write-Host "Creating Key Vault $vaultName (RBAC, purge protection, $retentionDays days soft delete)..."
        az keyvault create `
            --name $vaultName `
            --resource-group $resourceGroup `
            --location $location `
            --sku standard `
            --enable-rbac-authorization true `
            --enable-purge-protection true `
            --retention-days $retentionDays `
            --output none
        Test-LastCommand "create Key Vault $vaultName"
    }
}

# Again on an existing vault, so a vault made some other way ends up the same. (Soft delete cannot be
# switched off any more, and retention days cannot be changed after creation, so neither is here.)
az keyvault update --name $vaultName --resource-group $resourceGroup --enable-rbac-authorization true --enable-purge-protection true --output none
Test-LastCommand "switch on RBAC authorization and purge protection"

$vaultId = az keyvault show --name $vaultName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "read the vault id"
$vaultUri = az keyvault show --name $vaultName --resource-group $resourceGroup --query properties.vaultUri -o tsv
Test-LastCommand "read the vault URI"

# 2. You, as Key Vault Secrets Officer (read and write secrets, nothing else). With RBAC even the
#    person who created the vault cannot touch its secrets without a data role.
$myObjectId = az ad signed-in-user show --query id -o tsv
Test-LastCommand "read your own object id (az login first)"
$myRole = "Key Vault Secrets Officer"
$existingRole = az role assignment list --role $myRole --scope $vaultId --query "[?principalId=='$myObjectId'].id" -o tsv
if ($existingRole) {
    Write-Host "You already have $myRole on $vaultName."
}
else {
    Write-Host "Granting $myRole on $vaultName to you..."
    az role assignment create `
        --assignee-object-id $myObjectId `
        --assignee-principal-type User `
        --role $myRole `
        --scope $vaultId `
        --output none
    Test-LastCommand "grant $myRole to you"
}

# 3. Wait until the role works. A new role assignment can take several minutes to reach the vault, and
#    until then every secret call is a 403 (ForbiddenByRbac). That is RBAC being slow, not broken:
#    going back to access policies is not the fix.
Write-Host "Checking that you can list secrets (a new role can take minutes)..."
$canList = $false
for ($attempt = 1; $attempt -le 40; $attempt++) {
    # (No "length(@)" query: az.cmd mangles a JMESPath with parentheses, see A-27.)
    az keyvault secret list --vault-name $vaultName --query "[].id" -o tsv 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $canList = $true
        break
    }
    Write-Host "  attempt ${attempt}: not yet (403 until the role reaches the vault)"
    Start-Sleep -Seconds 15
}
if (-not $canList) {
    Write-Host "FAILED: still no access to $vaultName secrets after 10 minutes. Run the script again later." -ForegroundColor Red
    exit 1
}

# 4. The secrets.
Write-Host "Secrets in ${vaultName}:"
Set-Secret "AzureOpenAI--Key" $secrets["AzureOpenAI:Key"]
Set-Secret "Cosmos--Key" $secrets["Cosmos:Key"]
Set-Secret "Cosmos--ConnectionString" "AccountEndpoint=$($secrets['Cosmos:Endpoint']);AccountKey=$($secrets['Cosmos:Key']);"
Set-Secret "ServiceBus--ConnectionString" $secrets["ServiceBus:ConnectionString"]

$optionalSecrets = [ordered]@{
    "Postgres:ConnectionString" = "Postgres--ConnectionString"
    "Redis:ConnectionString" = "Redis--ConnectionString"
    "EventGrid:TopicKey" = "EventGrid--TopicKey"
}
foreach ($settingName in $optionalSecrets.Keys) {
    if ($secrets.ContainsKey($settingName) -and -not [string]::IsNullOrWhiteSpace($secrets[$settingName])) {
        Set-Secret $optionalSecrets[$settingName] $secrets[$settingName]
    }
    else {
        Write-Host "  (no $settingName user secret, so no $($optionalSecrets[$settingName]) secret)"
    }
}

# 5. Optional: show soft delete and purge protection doing their job on a throwaway secret.
#    Delete it, see it in the deleted list, fail to purge it, recover it, delete it again. It then
#    sits soft-deleted for 7 days (costing nothing); the next check recovers it first.
if ($CheckSoftDelete) {
    $checkName = "soft-delete-check"
    Write-Host ""
    Write-Host "Soft delete check with secret $checkName..."

    $alreadyDeleted = az keyvault secret list-deleted --vault-name $vaultName --query "[?name=='$checkName'].name" -o tsv
    if ($alreadyDeleted -eq $checkName) {
        Write-Host "  It is still soft-deleted from a past check; recovering it first..."
        az keyvault secret recover --vault-name $vaultName --name $checkName --output none
        Test-LastCommand "recover $checkName"
        Start-Sleep -Seconds 10
    }

    az keyvault secret set --vault-name $vaultName --name $checkName --value "check" --output none
    Test-LastCommand "set $checkName"

    az keyvault secret delete --vault-name $vaultName --name $checkName --output none
    Test-LastCommand "delete $checkName"
    # Deleting finishes in the background; wait until the deleted secret can be read
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        az keyvault secret show-deleted --vault-name $vaultName --name $checkName --output none 2>$null
        if ($LASTEXITCODE -eq 0) {
            break
        }
        Start-Sleep -Seconds 5
    }
    $deletedNames = az keyvault secret list-deleted --vault-name $vaultName --query "[].name" -o tsv
    if ($deletedNames -contains $checkName) {
        Write-Host "  Deleted: it is in the soft-deleted list, not gone."
    }
    else {
        Write-Host "  Deleted, but it is NOT in the soft-deleted list." -ForegroundColor Red
    }

    # Expected to FAIL: purge protection refuses a purge before the retention days are up
    az keyvault secret purge --vault-name $vaultName --name $checkName 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Purge refused: purge protection is on."
    }
    else {
        Write-Host "  Purge WORKED, so purge protection is off." -ForegroundColor Red
    }

    if ($deletedNames -contains $checkName) {
        az keyvault secret recover --vault-name $vaultName --name $checkName --output none
        Test-LastCommand "recover $checkName"
        $recovered = ""
        for ($attempt = 1; $attempt -le 12; $attempt++) {
            $recovered = az keyvault secret show --vault-name $vaultName --name $checkName --query value -o tsv 2>$null
            if ($LASTEXITCODE -eq 0) {
                break
            }
            Start-Sleep -Seconds 5
        }
        Write-Host "  Recovered: the value reads back as '$recovered'."

        az keyvault secret delete --vault-name $vaultName --name $checkName --output none
        Test-LastCommand "delete $checkName again"
        Write-Host "  Deleted again; it stays soft-deleted for $retentionDays days."
    }
}

# 6. The settings that matter, read back from Azure rather than assumed.
Write-Host ""
Write-Host "Done. $vaultName ($vaultUri):"
az keyvault show --name $vaultName --resource-group $resourceGroup `
    --query "{rbacAuthorization:properties.enableRbacAuthorization, softDelete:properties.enableSoftDelete, purgeProtection:properties.enablePurgeProtection, retentionDays:properties.softDeleteRetentionInDays}" -o table
Write-Host ""
Write-Host "Secrets (names only):"
az keyvault secret list --vault-name $vaultName --query "[].name" -o tsv
Write-Host ""
Write-Host "Next, so each app reads them as its managed identity:"
Write-Host "  pwsh infra/appservice/provision-appservice.ps1     the web app (KeyVault__Uri, no secret app settings)"
Write-Host "  pwsh infra/containerapps/use-keyvault.ps1         the worker (a new revision with no Container Apps secrets)"
Write-Host "  pwsh infra/functions/provision-functions.ps1      the function apps (Key Vault references)"
Write-Host "Locally:"
Write-Host "  dotnet run --project MeepleLedger -- --KeyVault:Uri=$vaultUri --SearchIndex:Provider=Cosmos"
