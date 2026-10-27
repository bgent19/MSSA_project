# A-22: provision the function apps that run MeepleLedger.Functions in Azure.
#
# Creates (or reuses), in rg-meepleledger:
#   - a storage account for the Functions hosts (AzureWebJobsStorage, and Flex's deployment container)
#   - a Log Analytics workspace and a workspace-based Application Insights, shared by both apps
#   - meepleledger-func:      Flex Consumption, Linux, .NET 10 isolated. The real one: its
#                             EmbedRequest trigger processes embed-requests.
#   - meepleledger-func-win:  Windows Consumption, .NET 10 isolated, with a "staging" slot. Only for
#                             the slot exercise: its EmbedRequest trigger is switched off, so it never
#                             competes with the Flex app for messages.
# then turns on a system-assigned managed identity for each app (and the slot), grants those
# identities roles on the embed-requests queue, and writes the app settings from the user secrets.
# Safe to run again: anything that exists is left alone, and the app settings are re-applied.
#
# Why two apps: no single cheap plan has everything A-22 asks for. Flex Consumption runs .NET 10 on
# Linux but has no deployment slots. Linux Consumption has slots but gets no new language versions
# (no .NET 10) and retires in September 2028. Windows Consumption has both .NET 10 and slots.
#
# Service Bus goes through the identity, not a connection string. The trigger's Connection =
# "ServiceBusConnection" stops being the name of ONE setting and becomes a PREFIX:
#   ServiceBusConnection__fullyQualifiedNamespace = meepleledger-bus.servicebus.windows.net
# With nothing else under that prefix, the host uses the app's system-assigned identity. A plain
# "ServiceBusConnection" setting would win over it, so this script deletes one if it finds it.
#
# Cosmos and Azure OpenAI still use keys, but no key is an app setting (A-29). The two settings that
# hold one, CosmosConnection and AzureOpenAI__Key, are Key Vault REFERENCES:
#   @Microsoft.KeyVault(VaultName=meepleledger-kv;SecretName=AzureOpenAI--Key)
# The platform reads the secret as the app's system identity (Key Vault Secrets User on the vault,
# granted below) and hands the code the value as an ordinary setting, so the code does not change.
# A reference with no version follows the latest version, but App Service only re-reads it every 24
# hours, or at once on any configuration change: rotate-openai-key.ps1 makes one. Both keys could go
# altogether with an identity (Cosmos data plane RBAC, "Cognitive Services OpenAI User"), but that
# needs credential code in GameEmbedder. Run pwsh infra/keyvault/provision-keyvault.ps1 first.
#
# METERED, but close to free while idle: both apps bill per execution, the storage account per GB
# and operation, Application Insights per GB of logs. A-33 tears them down with the resource group.
#
# Run from the repo root:  pwsh infra/functions/provision-functions.ps1
# Then deploy the code:    pwsh infra/functions/deploy-functions.ps1

$resourceGroup = "rg-meepleledger"
$location = "eastus2"                       # same region as Service Bus, Cosmos and OpenAI
$storageAccount = "meepleledgerfuncs"       # globally unique, 3-24 lowercase letters and digits
$workspaceName = "meepleledger-logs"
$appInsightsName = "meepleledger-func-insights"
$flexApp = "meepleledger-func"              # globally unique: <name>.azurewebsites.net
$slotApp = "meepleledger-func-win"
$slotName = "staging"

$serviceBusNamespace = "meepleledger-bus"   # from provision-servicebus.ps1
$queueName = "embed-requests"

$vaultName = "meepleledger-kv"              # from provision-keyvault.ps1 (A-29)

$userSecretsId = "93de1646-b3dc-4c1f-9bc8-3ebc34f364f3"   # the Seeder's, shared by every project

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# Grants a role to an identity on one scope, unless it already has it.
function Grant-Role([string]$principalId, [string]$role, [string]$scope, [string]$who) {
    # Filtered by principal id in the query rather than --assignee, which looks the id up in
    # Microsoft Graph first and warns when it cannot
    $existing = az role assignment list --role $role --scope $scope --query "[?principalId=='$principalId'].id" -o tsv
    if ($existing) {
        Write-Host "  $who already has $role."
        return
    }
    Write-Host "  Granting $role to $who..."
    # --assignee-object-id with the principal type skips a Microsoft Graph lookup, which can fail
    # for an identity created seconds ago
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role $role `
        --scope $scope `
        --output none
    Test-LastCommand "grant $role to $who"
}

# Turns on the system-assigned identity for an app (or one of its slots) and returns its principal id.
# A slot has its OWN identity, separate from the production slot's, and keeps it through a swap.
function Enable-Identity([string]$appName, [string]$slot) {
    $slotArguments = @()
    if ($slot) {
        $slotArguments = @("--slot", $slot)
    }
    $principalId = az functionapp identity assign --name $appName --resource-group $resourceGroup @slotArguments --query principalId -o tsv
    Test-LastCommand "assign identity to $appName $slot"
    return $principalId
}

# Writes the shared app settings onto an app (or slot). The values go through a temporary JSON file,
# not the command line: the Cosmos connection string has ";" and "=" in it, which do not survive
# being passed to az.cmd on Windows. The file holds keys, so it is deleted straight away.
function Set-AppSettings([string]$appName, [string]$slot, [hashtable]$settings) {
    $slotArguments = @()
    if ($slot) {
        $slotArguments = @("--slot", $slot)
    }

    $list = @()
    foreach ($name in $settings.Keys) {
        $list += [ordered]@{ name = $name; value = $settings[$name]; slotSetting = $false }
    }
    $file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-appsettings-$([guid]::NewGuid()).json"
    ConvertTo-Json -InputObject $list -Depth 3 | Set-Content -Path $file -Encoding utf8
    try {
        az functionapp config appsettings set --name $appName --resource-group $resourceGroup @slotArguments --settings "@$file" --output none
        Test-LastCommand "set app settings on $appName $slot"
    }
    finally {
        Remove-Item $file -Force
    }

    # A leftover connection-string setting would be used instead of the identity
    $names = az functionapp config appsettings list --name $appName --resource-group $resourceGroup @slotArguments --query "[].name" -o tsv
    if ($names -contains "ServiceBusConnection") {
        Write-Host "  Removing the old ServiceBusConnection connection string setting..."
        az functionapp config appsettings delete --name $appName --resource-group $resourceGroup @slotArguments --setting-names ServiceBusConnection --output none
        Test-LastCommand "delete ServiceBusConnection on $appName $slot"
    }
}

# 0. Read the user secrets, for the settings that are NOT secret. The keys are in Key Vault.
$secretsPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$userSecretsId/secrets.json"
if (-not (Test-Path $secretsPath)) {
    Write-Host "FAILED: no user secrets at $secretsPath. Run the provisioning scripts first." -ForegroundColor Red
    exit 1
}
$secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
$needed = @(
    "Cosmos:Database", "Cosmos:Container",
    "AzureOpenAI:Endpoint", "AzureOpenAI:EmbeddingDeployment"
)
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

# The vault the Key Vault references point at.
$vaultId = az keyvault show --name $vaultName --query id -o tsv
Test-LastCommand "find Key Vault $vaultName (run pwsh infra/keyvault/provision-keyvault.ps1 first)"

# 1. Providers and the Application Insights CLI extension.
foreach ($provider in @("Microsoft.Web", "Microsoft.Storage", "Microsoft.Insights", "Microsoft.OperationalInsights")) {
    $providerState = az provider show --namespace $provider --query registrationState -o tsv
    if ($providerState -ne "Registered") {
        Write-Host "Registering $provider provider (takes a minute or two)..."
        az provider register --namespace $provider --wait
        Test-LastCommand "register $provider"
    }
}
az extension add --name application-insights --upgrade --yes --only-show-errors
Test-LastCommand "install the application-insights CLI extension"

# 2. Resource group, and the Service Bus queue the identities get roles on.
$groupExists = az group exists --name $resourceGroup
if ($groupExists -ne "true") {
    Write-Host "Creating resource group $resourceGroup in $location..."
    az group create --name $resourceGroup --location $location --output none
    Test-LastCommand "create resource group"
}
$queueId = az servicebus queue show --namespace-name $serviceBusNamespace --resource-group $resourceGroup --name $queueName --query id -o tsv
if (-not $queueId) {
    Write-Host "FAILED: no queue $queueName in $serviceBusNamespace. Run pwsh infra/servicebus/provision-servicebus.ps1 first." -ForegroundColor Red
    exit 1
}

# 3. Storage account for the hosts. Standard LRS is the cheapest that Functions accepts.
$existingStorage = az storage account list --resource-group $resourceGroup --query "[?name=='$storageAccount'].name" -o tsv
if ($existingStorage -eq $storageAccount) {
    Write-Host "Storage account $storageAccount already exists."
}
else {
    $nameAvailable = az storage account check-name --name $storageAccount --query nameAvailable -o tsv
    if ($nameAvailable -ne "true") {
        Write-Host "FAILED: the storage account name $storageAccount is taken. Change `$storageAccount in this script." -ForegroundColor Red
        exit 1
    }
    Write-Host "Creating storage account $storageAccount..."
    az storage account create --name $storageAccount --resource-group $resourceGroup --location $location --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --output none
    Test-LastCommand "create storage account"
}

# 4. Logs. Created here, in this resource group, on purpose: left to itself, az functionapp create
#    makes a "DefaultWorkspace-..." in a DefaultResourceGroup-... group, which deleting
#    rg-meepleledger would leave behind.
$workspaceId = az monitor log-analytics workspace list --resource-group $resourceGroup --query "[?name=='$workspaceName'].id" -o tsv
if (-not $workspaceId) {
    Write-Host "Creating Log Analytics workspace $workspaceName..."
    $workspaceId = az monitor log-analytics workspace create --workspace-name $workspaceName --resource-group $resourceGroup --location $location --query id -o tsv
    Test-LastCommand "create Log Analytics workspace"
}
$existingInsights = az monitor app-insights component show --app $appInsightsName --resource-group $resourceGroup --query name -o tsv 2>$null
if ($existingInsights -ne $appInsightsName) {
    Write-Host "Creating Application Insights $appInsightsName..."
    az monitor app-insights component create --app $appInsightsName --resource-group $resourceGroup --location $location --workspace $workspaceId --application-type web --output none
    Test-LastCommand "create Application Insights"
}

# 5. The Flex Consumption app (Linux). Flex keeps the runtime in its own config, not in a
#    FUNCTIONS_WORKER_RUNTIME app setting, and refuses that setting if you add it.
$existingFlex = az functionapp list --resource-group $resourceGroup --query "[?name=='$flexApp'].name" -o tsv
if ($existingFlex -eq $flexApp) {
    Write-Host "Function app $flexApp already exists."
}
else {
    Write-Host "Creating function app $flexApp (Flex Consumption, Linux, dotnet-isolated 10)..."
    az functionapp create `
        --name $flexApp `
        --resource-group $resourceGroup `
        --storage-account $storageAccount `
        --flexconsumption-location $location `
        --runtime dotnet-isolated `
        --runtime-version 10 `
        --app-insights $appInsightsName `
        --output none
    Test-LastCommand "create $flexApp"
}

# 6. The Windows Consumption app and its staging slot.
$existingWindows = az functionapp list --resource-group $resourceGroup --query "[?name=='$slotApp'].name" -o tsv
if ($existingWindows -eq $slotApp) {
    Write-Host "Function app $slotApp already exists."
}
else {
    Write-Host "Creating function app $slotApp (Consumption, Windows, dotnet-isolated 10)..."
    az functionapp create `
        --name $slotApp `
        --resource-group $resourceGroup `
        --storage-account $storageAccount `
        --consumption-plan-location $location `
        --os-type Windows `
        --functions-version 4 `
        --runtime dotnet-isolated `
        --runtime-version 10 `
        --app-insights $appInsightsName `
        --output none
    Test-LastCommand "create $slotApp"
}
$existingSlot = az functionapp deployment slot list --name $slotApp --resource-group $resourceGroup --query "[?name=='$slotName'].name" -o tsv
if ($existingSlot -eq $slotName) {
    Write-Host "Slot $slotApp/$slotName already exists."
}
else {
    # Copies the production slot's configuration (runtime, app settings) as a starting point
    Write-Host "Creating slot $slotName on $slotApp..."
    az functionapp deployment slot create --name $slotApp --resource-group $resourceGroup --slot $slotName --configuration-source $slotApp --output none
    Test-LastCommand "create slot $slotName"
}

# Each slot needs its OWN file share. On Consumption the code lives in the Azure Files share named
# by WEBSITE_CONTENTSHARE, and --configuration-source copies production's value into the slot. Left
# like that, both slots run from one share: a deploy to staging lands in production too, and a swap
# swaps nothing. So the slot gets its own share. (It cannot be marked as a slot setting, Azure refuses:
# the platform never swaps WEBSITE_CONTENTSHARE anyway, it only has to be different per slot.)
$stagingShare = "$slotApp-$slotName"
$shareExists = az storage share-rm exists --storage-account $storageAccount --resource-group $resourceGroup --name $stagingShare --query exists -o tsv
if ($shareExists -ne "true") {
    Write-Host "Creating file share $stagingShare for the $slotName slot..."
    az storage share-rm create --storage-account $storageAccount --resource-group $resourceGroup --name $stagingShare --output none
    Test-LastCommand "create file share $stagingShare"
}
az functionapp config appsettings set --name $slotApp --resource-group $resourceGroup --slot $slotName --settings "WEBSITE_CONTENTSHARE=$stagingShare" --output none
Test-LastCommand "give the $slotName slot its own content share"

# 7. Identities and roles. Roles are scoped to the one queue, not the namespace or resource group.
#    The Flex app receives (the trigger, and settling messages: complete, abandon, dead-letter) and
#    sends (QueueEmbed's output binding). The Windows app's trigger is off, so it only sends.
Write-Host "Managed identities and roles on $queueName..."
$flexPrincipal = Enable-Identity $flexApp ""
$windowsPrincipal = Enable-Identity $slotApp ""
$stagingPrincipal = Enable-Identity $slotApp $slotName
Grant-Role $flexPrincipal "Azure Service Bus Data Receiver" $queueId $flexApp
Grant-Role $flexPrincipal "Azure Service Bus Data Sender" $queueId $flexApp
Grant-Role $windowsPrincipal "Azure Service Bus Data Sender" $queueId $slotApp
Grant-Role $stagingPrincipal "Azure Service Bus Data Sender" $queueId "$slotApp/$slotName"

# Key Vault Secrets User on the one vault, for the Key Vault references. The slot has its own identity,
# so it needs its own role. A new role takes minutes to work; until then a reference shows as
# AccessToKeyVaultDenied and the app gets the reference text itself instead of the key.
Write-Host "Key Vault Secrets User on $vaultName..."
Grant-Role $flexPrincipal "Key Vault Secrets User" $vaultId $flexApp
Grant-Role $windowsPrincipal "Key Vault Secrets User" $vaultId $slotApp
Grant-Role $stagingPrincipal "Key Vault Secrets User" $vaultId "$slotApp/$slotName"

# 8. App settings: every value local.settings.json has, under the name the app reads.
#    A code deploy does NOT carry these: publishing replaces the app's files, not its configuration.
$commonSettings = [ordered]@{
    "ServiceBusConnection__fullyQualifiedNamespace" = "$serviceBusNamespace.servicebus.windows.net"
    "CosmosConnection" = "@Microsoft.KeyVault(VaultName=$vaultName;SecretName=Cosmos--ConnectionString)"
    "CosmosDatabase" = $secrets["Cosmos:Database"]
    "CosmosContainer" = $secrets["Cosmos:Container"]
    "AzureOpenAI__Endpoint" = $secrets["AzureOpenAI:Endpoint"]
    "AzureOpenAI__Key" = "@Microsoft.KeyVault(VaultName=$vaultName;SecretName=AzureOpenAI--Key)"
    "AzureOpenAI__EmbeddingDeployment" = $secrets["AzureOpenAI:EmbeddingDeployment"]
}

Write-Host "App settings on $flexApp..."
$flexSettings = [ordered]@{}
foreach ($name in $commonSettings.Keys) {
    $flexSettings[$name] = $commonSettings[$name]
}
$flexSettings["SlotName"] = "production (Flex has no slots)"
Set-AppSettings $flexApp "" $flexSettings

# The Windows app runs the same package, so it needs the same settings (the host resolves
# %CosmosDatabase% for every function when it starts), plus:
#   - AzureWebJobs.EmbedRequest.Disabled: the trigger does not listen, so only the Flex app takes
#     messages. Not a slot setting: it should be true in both slots, whichever code is where.
#   - SlotName: a deployment SLOT setting ("sticky"), so it stays with its slot through a swap. This
#     is the one-checkbox fix in the ticket: a staging slot pointed at test resources has to keep
#     those settings when its code swaps into production. Anything not marked sticky travels.
#   - 64-bit worker: Consumption on Windows defaults to 32-bit.
foreach ($slot in @("", $slotName)) {
    $label = $slotApp
    $slotArguments = @()
    $slotValue = "production"
    if ($slot) {
        $label = "$slotApp/$slot"
        $slotArguments = @("--slot", $slot)
        $slotValue = $slot
    }
    Write-Host "App settings on $label..."
    $windowsSettings = [ordered]@{}
    foreach ($name in $commonSettings.Keys) {
        $windowsSettings[$name] = $commonSettings[$name]
    }
    $windowsSettings["AzureWebJobs.EmbedRequest.Disabled"] = "true"
    Set-AppSettings $slotApp $slot $windowsSettings

    az functionapp config appsettings set --name $slotApp --resource-group $resourceGroup @slotArguments --slot-settings "SlotName=$slotValue" --output none
    Test-LastCommand "set the SlotName slot setting on $label"
    az functionapp config set --name $slotApp --resource-group $resourceGroup @slotArguments --use-32bit-worker-process false --output none
    Test-LastCommand "set 64-bit on $label"
}

# 9. Check: every setting local.settings.json has must exist on the Flex app under its Azure name.
#    The names, never the values, are printed.
$localToAzure = [ordered]@{
    "AzureWebJobsStorage" = "AzureWebJobsStorage"
    "ServiceBusConnection" = "ServiceBusConnection__fullyQualifiedNamespace"
    "CosmosConnection" = "CosmosConnection"
    "CosmosDatabase" = "CosmosDatabase"
    "CosmosContainer" = "CosmosContainer"
    "AzureOpenAI__Endpoint" = "AzureOpenAI__Endpoint"
    "AzureOpenAI__Key" = "AzureOpenAI__Key"
    "AzureOpenAI__EmbeddingDeployment" = "AzureOpenAI__EmbeddingDeployment"
}
$flexNames = az functionapp config appsettings list --name $flexApp --resource-group $resourceGroup --query "[].name" -o tsv
$runtime = az functionapp show --name $flexApp --resource-group $resourceGroup --query "properties.functionAppConfig.runtime" -o json | ConvertFrom-Json
Write-Host ""
Write-Host "local.settings.json -> $flexApp app settings"
Write-Host "  FUNCTIONS_WORKER_RUNTIME -> Flex runtime config: $($runtime.name) $($runtime.version)"
$problems = 0
foreach ($localName in $localToAzure.Keys) {
    $azureName = $localToAzure[$localName]
    if ($flexNames -contains $azureName) {
        Write-Host "  $localName -> $azureName"
    }
    else {
        Write-Host "  $localName -> MISSING $azureName" -ForegroundColor Red
        $problems++
    }
}
if ($flexNames -contains "ServiceBusConnection") {
    Write-Host "  WARNING: ServiceBusConnection is set; the trigger would use it instead of the identity" -ForegroundColor Yellow
    $problems++
}

# The Key Vault references: Resolved, or why not. Not a failure here, because a role granted a minute
# ago is often not working yet; run the script again in a few minutes if they are not Resolved.
$flexId = az functionapp show --name $flexApp --resource-group $resourceGroup --query id -o tsv
Write-Host ""
Write-Host "Key Vault references on ${flexApp}:"
az rest --method get --url "https://management.azure.com$flexId/config/configreferences/appsettings?api-version=2022-03-01" `
    --query "value[].{setting:name, status:properties.status}" -o table

# Flex keeps the host name under "properties"; the Windows app has it at the top level too
$flexHost = az functionapp show --name $flexApp --resource-group $resourceGroup --query properties.defaultHostName -o tsv
$windowsHost = az functionapp show --name $slotApp --resource-group $resourceGroup --query defaultHostName -o tsv
$stagingHost = az functionapp show --name $slotApp --resource-group $resourceGroup --slot $slotName --query defaultHostName -o tsv

Write-Host ""
if ($problems -eq 0) {
    Write-Host "Done."
}
else {
    Write-Host "Done, with $problems problem(s) above." -ForegroundColor Yellow
}
Write-Host "  $flexApp      https://$flexHost  (Flex Consumption, Linux; identity $flexPrincipal)"
Write-Host "  $slotApp  https://$windowsHost  (Consumption, Windows; identity $windowsPrincipal)"
Write-Host "    slot $slotName      https://$stagingHost  (identity $stagingPrincipal)"
Write-Host "  Logs:  Application Insights $appInsightsName, workspace $workspaceName"
Write-Host ""
Write-Host "Role assignments take a few minutes to reach Service Bus. A trigger tested straight away can"
Write-Host "fail with 'Unauthorized' and then start working by itself: wait 5 minutes before debugging it."
Write-Host "Next: pwsh infra/functions/deploy-functions.ps1"
