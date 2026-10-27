# A-29: rotate the Azure OpenAI (embeddings) key with the two-key pattern, and nothing redeployed.
#
# Azure OpenAI, like Cosmos, Storage and Redis, has TWO keys, key1 and key2, both valid at once. That
# is what makes a rotation with no outage possible:
#
#   Step 1 (no switch):  point the Key Vault secret AzureOpenAI--Key at the OTHER key. Both keys still
#                        work, so nothing breaks while the apps catch up. The web app and the worker
#                        reload Key Vault every KeyVault:ReloadIntervalMinutes (5) and read the key on
#                        every call, so they move over by themselves: no restart, no redeploy.
#   Step 2 (-Regenerate): once everything has had time to move, regenerate the key that is NO LONGER
#                        in the vault. Its old value stops working for good. Anything still using it
#                        fails now, which is the proof the apps really did move.
#
# Regenerating the key that is in use (one-key rotation) is an outage with extra steps: every app
# fails from the regenerate until it gets the new value.
#
# Neither step touches the web app or the worker. The script prints the web app's last-modified time
# and the worker's latest revision in both steps, so you can see they are the same.
#
# Also kept in step:
#   - your user secret AzureOpenAI:Key, so the Seeder keeps working after step 2
#   - the function apps: their AzureOpenAI__Key app setting is a Key Vault reference, and App Service
#     only re-reads references every 24 hours, or at once on a configuration change. Step 1 makes
#     that change (the KeyVaultRefreshedAt app setting). That is a setting, not a deploy.
#   - MeepleLedger.Functions/local.settings.json is NOT changed: rerun write-local-settings.ps1.
#
# METERED: one embeddings call per step to prove the key in the vault works (a fraction of a cent).
#
# Run from the repo root:
#   pwsh infra/keyvault/rotate-openai-key.ps1                 step 1: the vault moves to the other key
#   pwsh infra/keyvault/rotate-openai-key.ps1 -Regenerate     step 2: regenerate the key no longer used
#   pwsh infra/keyvault/rotate-openai-key.ps1 -Regenerate -Force   skip the "has it been long enough" check

param(
    [switch]$Regenerate,
    [switch]$Force
)

$resourceGroup = "rg-meepleledger"
$vaultName = "meepleledger-kv"            # from provision-keyvault.ps1
$secretName = "AzureOpenAI--Key"
$accountName = "meepleledger-openai"      # from provision-openai.ps1
$webAppName = "meepleledger-web"          # from provision-appservice.ps1
$workerAppName = "meepleledger-worker"    # from provision-containerapps.ps1
$functionApps = @("meepleledger-func", "meepleledger-func-win")   # from provision-functions.ps1
$slotName = "staging"                     # meepleledger-func-win's slot

$userSecretsId = "93de1646-b3dc-4c1f-9bc8-3ebc34f364f3"   # the Seeder's, shared by every project

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# Calls the embeddings deployment once with a key. Returns the HTTP status code (200 = the key works).
function Test-Key([string]$key) {
    $url = $endpoint.TrimEnd("/") + "/openai/deployments/$deployment/embeddings?api-version=2024-10-21"
    try {
        $response = Invoke-WebRequest -Uri $url -Method Post -Headers @{ "api-key" = $key } `
            -ContentType "application/json" -Body '{"input":"key check"}' -SkipHttpErrorCheck -TimeoutSec 30
        return $response.StatusCode
    }
    catch {
        return 0
    }
}

# What the apps look like right now: the same in both steps means nothing was redeployed or restarted.
function Show-AppState {
    $webModified = az webapp show --name $webAppName --resource-group $resourceGroup --query lastModifiedTimeUtc -o tsv 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  $webAppName last modified: $webModified"
    }
    $workerRevision = az containerapp show --name $workerAppName --resource-group $resourceGroup --query properties.latestRevisionName -o tsv 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  $workerAppName latest revision: $workerRevision"
    }
}

# The endpoint and deployment are not secret; they are in the user secrets with everything else.
$secretsPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$userSecretsId/secrets.json"
if (-not (Test-Path $secretsPath)) {
    Write-Host "FAILED: no user secrets at $secretsPath." -ForegroundColor Red
    exit 1
}
$secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
$endpoint = $secrets["AzureOpenAI:Endpoint"]
$deployment = $secrets["AzureOpenAI:EmbeddingDeployment"]
if ([string]::IsNullOrWhiteSpace($endpoint) -or [string]::IsNullOrWhiteSpace($deployment)) {
    Write-Host "FAILED: missing user secrets AzureOpenAI:Endpoint or AzureOpenAI:EmbeddingDeployment." -ForegroundColor Red
    exit 1
}

# How long the apps take to see a new secret: KeyVault:ReloadIntervalMinutes in the web app's
# appsettings.json (the worker uses the same default).
$appSettings = Get-Content "MeepleLedger/appsettings.json" -Raw | ConvertFrom-Json
$reloadMinutes = [int]$appSettings.KeyVault.ReloadIntervalMinutes

# The two keys of the account, and which one the vault holds now. The values are never printed.
$keys = az cognitiveservices account keys list --name $accountName --resource-group $resourceGroup -o json | ConvertFrom-Json
Test-LastCommand "list the keys of $accountName"
$inVault = az keyvault secret show --vault-name $vaultName --name $secretName --query value -o tsv
Test-LastCommand "read $secretName from $vaultName (run pwsh infra/keyvault/provision-keyvault.ps1 first)"

$inUse = ""
if ($inVault -eq $keys.key1) {
    $inUse = "key1"
}
elseif ($inVault -eq $keys.key2) {
    $inUse = "key2"
}

if (-not $Regenerate) {
    # ---- Step 1: move the vault to the other key ----
    $newKeyName = "key1"
    $newKey = $keys.key1
    if ($inUse -eq "key1") {
        $newKeyName = "key2"
        $newKey = $keys.key2
    }
    if ($inUse -eq "") {
        Write-Host "The vault holds neither key1 nor key2 (an old, regenerated key?). Every app using it is already failing." -ForegroundColor Yellow
    }
    else {
        Write-Host "The vault holds $inUse."
    }

    Write-Host "Apps before:"
    Show-AppState

    $status = Test-Key $newKey
    if ($status -ne 200) {
        Write-Host "FAILED: $newKeyName does not work against the deployment ($status); not moving to it." -ForegroundColor Red
        exit 1
    }
    Write-Host "$newKeyName works against $deployment (200)."

    # A new VERSION of the same secret. The apps read the latest version, so nothing points at a version.
    $file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-secret-$([guid]::NewGuid()).txt"
    [System.IO.File]::WriteAllText($file, $newKey)
    try {
        Write-Host "Setting $secretName to $newKeyName (a new version)..."
        az keyvault secret set --vault-name $vaultName --name $secretName --file $file --encoding utf-8 --output none
        Test-LastCommand "set $secretName"
    }
    finally {
        Remove-Item $file -Force
    }

    # The user secret too, so the Seeder and local runs keep working after step 2
    dotnet user-secrets set "AzureOpenAI:Key" $newKey --id $userSecretsId | Out-Null
    Test-LastCommand "update the AzureOpenAI:Key user secret"
    Write-Host "User secret AzureOpenAI:Key updated."

    # Key Vault references in the function apps are re-read on a configuration change. Apps that do
    # not exist (torn down) are skipped.
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    foreach ($functionApp in $functionApps) {
        $exists = az functionapp list --resource-group $resourceGroup --query "[?name=='$functionApp'].name" -o tsv
        if ($exists -ne $functionApp) {
            continue
        }
        az functionapp config appsettings set --name $functionApp --resource-group $resourceGroup --settings "KeyVaultRefreshedAt=$stamp" --output none
        Test-LastCommand "refresh the Key Vault references on $functionApp"
        Write-Host "$functionApp re-reads its Key Vault references (KeyVaultRefreshedAt=$stamp)."
        if ($functionApp -eq "meepleledger-func-win") {
            az functionapp config appsettings set --name $functionApp --resource-group $resourceGroup --slot $slotName --settings "KeyVaultRefreshedAt=$stamp" --output none
            Test-LastCommand "refresh the Key Vault references on $functionApp/$slotName"
        }
    }

    Write-Host ""
    Write-Host "Step 1 done: the vault now holds $newKeyName. $inUse still works, so nothing is broken."
    Write-Host "Within $reloadMinutes minutes the web app and the worker reload Key Vault. Then:"
    Write-Host "  - Ask something on /tonight, and look for this line in the log:"
    Write-Host "      The Azure OpenAI key changed since the last Ask (a Key Vault reload); using the new one."
    Write-Host "    az webapp log tail --name $webAppName --resource-group $resourceGroup"
    Write-Host "  - pwsh infra/keyvault/rotate-openai-key.ps1 -Regenerate"
    exit 0
}

# ---- Step 2: regenerate the key that is no longer in the vault ----
if ($inUse -eq "") {
    Write-Host "FAILED: the vault holds neither key1 nor key2, so there is no 'other' key to regenerate safely. Run step 1 first." -ForegroundColor Red
    exit 1
}
$oldKeyName = "key1"
if ($inUse -eq "key1") {
    $oldKeyName = "key2"
}

# Has every app had a reload since the secret changed? Regenerating sooner would break whoever has not.
$updated = az keyvault secret show --vault-name $vaultName --name $secretName --query attributes.updated -o tsv
Test-LastCommand "read when $secretName was last set"
$minutesSince = ((Get-Date).ToUniversalTime() - ([datetime]::Parse($updated)).ToUniversalTime()).TotalMinutes
$minutesNeeded = $reloadMinutes + 1
if ($minutesSince -lt $minutesNeeded -and -not $Force) {
    $wait = [math]::Ceiling($minutesNeeded - $minutesSince)
    Write-Host "Not yet: $secretName changed $([math]::Floor($minutesSince)) minute(s) ago and the apps reload every $reloadMinutes." -ForegroundColor Yellow
    Write-Host "Try again in $wait minute(s), or add -Force."
    exit 1
}

Write-Host "Apps before:"
Show-AppState

Write-Host "Regenerating $oldKeyName (the vault holds $inUse)..."
az cognitiveservices account keys regenerate --name $accountName --resource-group $resourceGroup --key-name $oldKeyName --output none
Test-LastCommand "regenerate $oldKeyName"

$status = Test-Key $inVault
Write-Host "The key in the vault ($inUse) still works against ${deployment}: $status"

Write-Host "Apps after:"
Show-AppState

Write-Host ""
Write-Host "Step 2 done: the old value of $oldKeyName is dead. Nothing was redeployed (see the lines above)."
Write-Host "Ask something on /tonight: an answer means the web app is on $inUse. A 401 in the log means it was not."
Write-Host "Locally: pwsh MeepleLedger.Functions/write-local-settings.ps1 refreshes local.settings.json."
