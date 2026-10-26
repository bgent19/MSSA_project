# A-21: writes MeepleLedger.Functions/local.settings.json from the user secrets the Seeder, the web app
# and the Worker share.
#
# Why a file at all: the Service Bus trigger and the Cosmos input binding are run by the Functions
# HOST (Core Tools), not by this project's code, and the host does not read user secrets. Locally it
# reads local.settings.json; in Azure the same names become app settings (A-22). The file holds keys,
# so git ignores it (MeepleLedger.Functions/.gitignore) and it is never published. Run this again after
# a key changes; the user secrets stay the one place a key is set.
#
# Run from the repo root:  pwsh MeepleLedger.Functions/write-local-settings.ps1

$userSecretsId = "93de1646-b3dc-4c1f-9bc8-3ebc34f364f3"   # the Seeder's, shared by every project
$secretsPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$userSecretsId/secrets.json"
$outputPath = Join-Path $PSScriptRoot "local.settings.json"

if (-not (Test-Path $secretsPath)) {
    Write-Host "FAILED: no user secrets at $secretsPath. Run the provisioning scripts first." -ForegroundColor Red
    exit 1
}

$secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable

$needed = @(
    "ServiceBus:ConnectionString",
    "Cosmos:Endpoint", "Cosmos:Key", "Cosmos:Database", "Cosmos:Container",
    "AzureOpenAI:Endpoint", "AzureOpenAI:Key", "AzureOpenAI:EmbeddingDeployment"
)
$missing = @()
foreach ($name in $needed) {
    if (-not $secrets.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($secrets[$name])) {
        $missing += $name
    }
}
if ($missing.Count -gt 0) {
    Write-Host "FAILED: missing user secrets: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "(provision-servicebus.ps1, provision-cosmos.ps1 and provision-openai.ps1 set them)"
    exit 1
}

# The host's own storage (leases, timer state, keys). Required even though no function here uses
# storage: without it the host does not start. A-20's storage account is reused if it exists;
# otherwise the local Azurite emulator, which must then be running (npm install -g azurite; azurite).
$webJobsStorage = "UseDevelopmentStorage=true"
if ($secrets.ContainsKey("EventStorage:ConnectionString")) {
    $webJobsStorage = $secrets["EventStorage:ConnectionString"]
}

# The binding settings are flat names, because a binding's Connection and a %name% expression are
# app setting names. The code's own settings use "__", which .NET configuration reads as ":", and
# which also works as an environment variable name on Linux (":" does not).
$cosmosConnection = "AccountEndpoint=$($secrets['Cosmos:Endpoint']);AccountKey=$($secrets['Cosmos:Key']);"

$settings = [ordered]@{
    IsEncrypted = $false
    Values = [ordered]@{
        FUNCTIONS_WORKER_RUNTIME = "dotnet-isolated"
        AzureWebJobsStorage = $webJobsStorage
        ServiceBusConnection = $secrets["ServiceBus:ConnectionString"]
        CosmosConnection = $cosmosConnection
        CosmosDatabase = $secrets["Cosmos:Database"]
        CosmosContainer = $secrets["Cosmos:Container"]
        AzureOpenAI__Endpoint = $secrets["AzureOpenAI:Endpoint"]
        AzureOpenAI__Key = $secrets["AzureOpenAI:Key"]
        AzureOpenAI__EmbeddingDeployment = $secrets["AzureOpenAI:EmbeddingDeployment"]
    }
}

$settings | ConvertTo-Json -Depth 3 | Set-Content -Path $outputPath -Encoding utf8

Write-Host "Wrote $outputPath"
foreach ($key in $settings.Values.Keys) {
    Write-Host "  $key"
}
if ($webJobsStorage -eq "UseDevelopmentStorage=true") {
    Write-Host "AzureWebJobsStorage is the local emulator: start Azurite before func start." -ForegroundColor Yellow
}
Write-Host "Next: cd MeepleLedger.Functions; func start"
