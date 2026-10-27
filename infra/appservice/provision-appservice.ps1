# A-25: run the MeepleLedger web app in Azure App Service, from the image in ACR.
#
# Creates (or reuses), in rg-meepleledger:
#   - meepleledger-plan: a Linux App Service plan, B1 (one small instance, billed per hour)
#   - meepleledger-web:  a Web App for Containers on that plan, running meepleledger:<tag> from
#                        meepleledgeracr. https://meepleledger-web.azurewebsites.net
# then gives the app a system-assigned managed identity, grants it AcrPull on the registry only,
# tells the app to pull with that identity, grants it Key Vault Secrets User on meepleledger-kv (A-29)
# and App Configuration Data Reader on meepleledger-appconfig (A-30), and writes the app settings.
# Safe to run again: anything that exists is left alone, and the settings and image are re-applied.
#
# Since A-30 the app settings say only WHERE the settings are: AppConfig__Endpoint and
# AppConfig__Label=azure. Everything else the app reads (the Provider, the endpoints, and the secrets
# through Key Vault references) is in App Configuration, read as the app's managed identity. The
# Provider is switched there, with pwsh infra/appconfig/set-provider.ps1, and takes effect without a
# restart. The Key Vault references are resolved by THIS app's identity, which is why it still needs
# Key Vault Secrets User. App settings from before A-30 (SearchIndex__Provider, KeyVault__Uri, the
# endpoints) and secret ones from before A-29 are deleted. Run, in this order, first:
#   pwsh infra/keyvault/provision-keyvault.ps1      the vault and its secrets
#   pwsh infra/appconfig/provision-appconfig.ps1    the store, its settings and references
#
# The image is pulled by IDENTITY, not with the registry's admin user name and password (that account
# stays off, A-24). The pull needs two things:
#   - AcrPull for the app's identity on the registry
#   - acrUseManagedIdentityCreds = true in the app's site config. Without it App Service tries an
#     anonymous pull, which a private registry refuses.
#
# App Service hands app settings to the container as environment variables, and .NET reads "__" in a
# variable name as ":". So AppConfig:Endpoint is the app setting AppConfig__Endpoint. A name with a
# colon in it is not an error: it is just never read.
#
# The image is deployed by its VERSION tag, never :latest (A-24): with -Tag, or by default the newest
# tag in the registry that is not "latest". Deploying an older tag is a rollback.
#
# METERED. B1 is a fixed hourly charge for as long as the plan exists, running or not (stopping the
# app does not stop the bill; deleting the plan does). Delete both:
#   az webapp delete --name meepleledger-web --resource-group rg-meepleledger
#   az appservice plan delete --name meepleledger-plan --resource-group rg-meepleledger --yes
# A-33 tears them down with the resource group.
#
# Run from the repo root:  pwsh infra/appservice/provision-appservice.ps1 [-Tag <version>]

param(
    [string]$Tag = ""
)

$resourceGroup = "rg-meepleledger"
$location = "eastus2"                     # same region as the registry, Cosmos and OpenAI
$planName = "meepleledger-plan"
$appName = "meepleledger-web"             # globally unique: <name>.azurewebsites.net
$sku = "B1"                               # the cheapest Linux tier with Always On

$registryName = "meepleledgeracr"         # from provision-acr.ps1
$imageName = "meepleledger"

$vaultName = "meepleledger-kv"            # from provision-keyvault.ps1 (A-29)
$storeName = "meepleledger-appconfig"     # from provision-appconfig.ps1 (A-30)
$appConfigLabel = "azure"                 # the label this app reads; this machine reads "local"

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# 0. The vault the Key Vault references point at, and the store that holds them.
$vaultId = az keyvault show --name $vaultName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find Key Vault $vaultName (run pwsh infra/keyvault/provision-keyvault.ps1 first)"
$storeId = az appconfig show --name $storeName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "find App Configuration store $storeName (run pwsh infra/appconfig/provision-appconfig.ps1 first)"
$storeEndpoint = az appconfig show --name $storeName --resource-group $resourceGroup --query endpoint -o tsv
Test-LastCommand "read the store endpoint"

# 1. The registry, and the image version to run.
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
        Write-Host "FAILED: $imageName has no version tag in $registryName. Run pwsh infra/acr/push-images.ps1 first." -ForegroundColor Red
        exit 1
    }
}
if ($Tag -eq "latest") {
    Write-Host "FAILED: deploy a version tag, not latest (see A-24)." -ForegroundColor Red
    exit 1
}
$image = "${loginServer}/${imageName}:$Tag"
Write-Host "Image: $image"

# 2. The plan. --is-linux matters: a Windows plan cannot run a Linux container.
$existingPlan = az appservice plan list --resource-group $resourceGroup --query "[?name=='$planName'].name" -o tsv
if ($existingPlan -eq $planName) {
    Write-Host "App Service plan $planName already exists."
}
else {
    Write-Host "Creating App Service plan $planName (Linux, $sku)..."
    az appservice plan create --name $planName --resource-group $resourceGroup --location $location --is-linux --sku $sku --output none
    Test-LastCommand "create App Service plan"
}

# 3. The web app. Created with its system-assigned identity, AcrPull on the registry for it, and
#    "pull with the identity" switched on, all in one command, so even the first pull needs no password.
$existingApp = az webapp list --resource-group $resourceGroup --query "[?name=='$appName'].name" -o tsv
if ($existingApp -eq $appName) {
    Write-Host "Web app $appName already exists."
}
else {
    Write-Host "Creating web app $appName..."
    az webapp create `
        --name $appName `
        --resource-group $resourceGroup `
        --plan $planName `
        --container-image-name $image `
        --assign-identity "[system]" `
        --role AcrPull `
        --scope $registryId `
        --acr-use-identity `
        --acr-identity "[system]" `
        --https-only true `
        --output none
    Test-LastCommand "create web app $appName"
}

# 4. The identity and its role, again, so a re-run repairs an app made some other way. Scoped to the
#    one registry: the app can pull images from it and do nothing else.
$principalId = az webapp identity assign --name $appName --resource-group $resourceGroup --query principalId -o tsv
Test-LastCommand "assign identity to $appName"
$existingRole = az role assignment list --role AcrPull --scope $registryId --query "[?principalId=='$principalId'].id" -o tsv
if ($existingRole) {
    Write-Host "$appName already has AcrPull on $registryName."
}
else {
    Write-Host "Granting AcrPull on $registryName to $appName..."
    # --assignee-object-id with the principal type skips a Microsoft Graph lookup, which can fail for
    # an identity created seconds ago
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role AcrPull `
        --scope $registryId `
        --output none
    Test-LastCommand "grant AcrPull to $appName"
}

# Key Vault Secrets User on the one vault: read secret values, nothing else (no writing, no listing
# keys or certificates). Like AcrPull, a new assignment can take minutes to work; until then the app
# fails at startup with a 403 from Key Vault, and App Service restarts it until it gets through.
$keyVaultRole = "Key Vault Secrets User"
$existingVaultRole = az role assignment list --role $keyVaultRole --scope $vaultId --query "[?principalId=='$principalId'].id" -o tsv
if ($existingVaultRole) {
    Write-Host "$appName already has $keyVaultRole on $vaultName."
}
else {
    Write-Host "Granting $keyVaultRole on $vaultName to $appName..."
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role $keyVaultRole `
        --scope $vaultId `
        --output none
    Test-LastCommand "grant $keyVaultRole to $appName"
}

# App Configuration Data Reader on the one store: read key-values and feature flags, nothing else. Like
# the others, a new assignment can take minutes; until then the app stops at startup with a 403.
$appConfigRole = "App Configuration Data Reader"
$existingStoreRole = az role assignment list --role $appConfigRole --scope $storeId --query "[?principalId=='$principalId'].id" -o tsv
if ($existingStoreRole) {
    Write-Host "$appName already has $appConfigRole on $storeName."
}
else {
    Write-Host "Granting $appConfigRole on $storeName to $appName..."
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role $appConfigRole `
        --scope $storeId `
        --output none
    Test-LastCommand "grant $appConfigRole to $appName"
}

# 5. Site config:
#    - acrUseManagedIdentityCreds: pull with the identity above
#    - Always On: off by default on the lower tiers, and then the app is unloaded after 20 idle
#      minutes and the first click of a demo waits for a cold start. B1 allows it at no extra cost.
#    - Web sockets: a Blazor Server circuit is a SignalR connection, which wants a web socket. Without
#      one SignalR falls back to long polling, which works but is slow.
#    - ARR affinity (client affinity): a cookie that sends a browser back to the same instance. A
#      Blazor Server circuit lives in ONE instance's memory, so with more than one instance and no
#      affinity, a request that lands on another instance has no circuit and the page reloads. It is
#      on by default; set here so it stays on.
Write-Host "Site config on $appName..."
$appId = az webapp show --name $appName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "read the id of $appName"
az resource update --ids "$appId/config/web" --set properties.acrUseManagedIdentityCreds=true --output none
Test-LastCommand "switch on pulling with the managed identity"
az webapp config set --name $appName --resource-group $resourceGroup --always-on true --web-sockets-enabled true --output none
Test-LastCommand "set Always On and web sockets"
az webapp update --name $appName --resource-group $resourceGroup --client-affinity-enabled true --https-only true --output none
Test-LastCommand "set ARR affinity and HTTPS only"

# 6. App settings. They are re-applied every run. The values go through a temporary JSON file, not
#    the command line: connection strings have ";" and "=" in them, which do not survive being passed
#    to az.cmd on Windows. The file holds keys, so it is deleted straight away.
#
#    WEBSITES_PORT: App Service sends traffic to port 80 unless told otherwise, and the .NET images
#    listen on 8080. Without it the app "deploys" fine and every request times out.
#
#    AppConfig__Endpoint and AppConfig__Label: where every other setting is (A-30). The Provider, the
#    endpoints, and the keys (through Key Vault references) are all in the store.
$settings = [ordered]@{
    "WEBSITES_PORT" = "8080"
    "AppConfig__Endpoint" = $storeEndpoint
    "AppConfig__Label" = $appConfigLabel
    "RecommendationCache__Enabled" = "false"
    "EventGrid__Enabled" = "false"
}
# Cosmos:Database and Cosmos:Container, the Postgres pool settings, the cache TTL, the refresh and
# reload intervals are not secrets and do not change per deployment: they are in appsettings.json,
# which is inside the image. An app setting would override them.

# App settings from before. The secret ones (A-29) would still be keys sitting in the portal. The rest
# moved to App Configuration (A-30): an app setting is an environment variable, which App
# Configuration would win over anyway, but leaving SearchIndex__Provider here would make the portal
# say one Provider while the app runs another.
$oldSecretSettings = @(
    "Cosmos__Key", "AzureOpenAI__Key", "Postgres__ConnectionString", "Redis__ConnectionString", "EventGrid__TopicKey",
    "SearchIndex__Provider", "KeyVault__Uri", "Cosmos__Endpoint", "AzureOpenAI__Endpoint",
    "AzureOpenAI__EmbeddingDeployment", "EventGrid__TopicEndpoint"
)

Write-Host "App settings on $appName..."
$list = @()
foreach ($name in $settings.Keys) {
    $list += [ordered]@{ name = $name; value = $settings[$name]; slotSetting = $false }
}
$file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-appsettings-$([guid]::NewGuid()).json"
ConvertTo-Json -InputObject $list -Depth 3 | Set-Content -Path $file -Encoding utf8
try {
    az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings "@$file" --output none
    Test-LastCommand "set app settings on $appName"
}
finally {
    Remove-Item $file -Force
}

$currentNames = az webapp config appsettings list --name $appName --resource-group $resourceGroup --query "[].name" -o tsv
Test-LastCommand "list the app settings of $appName"
foreach ($oldName in $oldSecretSettings) {
    if ($currentNames -contains $oldName) {
        Write-Host "  Deleting the old app setting $oldName (it is in App Configuration or Key Vault now)..."
        az webapp config appsettings delete --name $appName --resource-group $resourceGroup --setting-names $oldName --output none
        Test-LastCommand "delete app setting $oldName"
    }
}

# 7. The image. Set after the settings, so the container that starts already has them.
#    The CLI warns "Retrieving credentials failed ... enable admin first": with no user name and
#    password given it looks for the admin account, finds it off, and carries on. That is expected;
#    the pull uses the identity. (Not "az webapp config set --linux-fx-version DOCKER|<image>":
#    az.cmd on Windows reads the "|" as a pipe.)
Write-Host "Pointing $appName at $image..."
az webapp config container set `
    --name $appName `
    --resource-group $resourceGroup `
    --container-image-name $image `
    --container-registry-url "https://$loginServer" `
    --output none
Test-LastCommand "set the container image"

# 8. Container logs: what the container writes to stdout and stderr, kept on the app's file system so
#    "az webapp log tail" can stream it. Kept 3 days, at most 35 MB.
az webapp log config --name $appName --resource-group $resourceGroup --docker-container-logging filesystem --output none
Test-LastCommand "switch on container logging"

az webapp restart --name $appName --resource-group $resourceGroup --output none
Test-LastCommand "restart $appName"

# 9. Wait for the site to answer. The first start pulls the image (a minute or two), and a new AcrPull
#    or Key Vault Secrets User assignment can take several more minutes to take effect; until then the
#    pull is refused, or the app stops at startup with a 403 from Key Vault, and App Service keeps
#    retrying. Waiting is the fix, not debugging.
$hostName = az webapp show --name $appName --resource-group $resourceGroup --query defaultHostName -o tsv
$url = "https://$hostName"
Write-Host ""
Write-Host "Waiting for $url to answer (up to 10 minutes)..."
$status = 0
for ($attempt = 1; $attempt -le 40; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri $url -TimeoutSec 30 -SkipHttpErrorCheck
        $status = $response.StatusCode
    }
    catch {
        $status = 0
    }
    if ($status -eq 200) {
        break
    }
    Write-Host "  attempt ${attempt}: $status"
    Start-Sleep -Seconds 15
}

Write-Host ""
if ($status -eq 200) {
    Write-Host "Done. $url answers 200."
}
else {
    Write-Host "Done, but $url has not answered 200 yet. Read the container log:" -ForegroundColor Yellow
    Write-Host "  az webapp log tail --name $appName --resource-group $resourceGroup"
}
Write-Host "  Plan:      $planName (Linux, $sku) in $location"
Write-Host "  App:       $appName, image $image"
Write-Host "  Identity:  $principalId, AcrPull on $registryName, $keyVaultRole on $vaultName, $appConfigRole on $storeName"
Write-Host "  Settings:  from $storeEndpoint, label $appConfigLabel (secrets through its Key Vault references)"
Write-Host ""
Write-Host "Next:"
Write-Host "  pwsh infra/appconfig/set-provider.ps1 Cosmos       flip the Search Index Provider (no restart)"
Write-Host "  pwsh infra/appconfig/set-tonight.ps1 off           hide the Tonight screen (no restart)"
Write-Host "  az webapp log tail --name $appName --resource-group $resourceGroup      stream the container log"
