# A-25: switch the Search Index Provider of the app running in App Service.
#
# Changes one app setting, SearchIndex__Provider. Nothing is rebuilt or redeployed: the same image
# runs, and App Service restarts the container so it reads the new value. This is ADR-0001 in public:
# the in-memory Provider needs no settings, and a cloud Provider is a configuration choice.
#
# Cosmos needs the Cosmos__* and AzureOpenAI__* app settings, which provision-appservice.ps1 writes.
# Postgres and Redis need their servers to exist, and Postgres also needs a firewall rule for the
# app's outbound IP addresses (az webapp show --query outboundIpAddresses).
# If a setting is missing the app fails at startup; the container log says which one.
#
# Run from the repo root:  pwsh infra/appservice/set-provider.ps1 <InMemory|Cosmos|Postgres|Redis>

param(
    [Parameter(Mandatory = $true)]
    [string]$Provider
)

$resourceGroup = "rg-meepleledger"
$appName = "meepleledger-web"   # from provision-appservice.ps1

if (@("InMemory", "Cosmos", "Postgres", "Redis") -notcontains $Provider) {
    Write-Host "FAILED: the Provider must be InMemory, Cosmos, Postgres or Redis, not '$Provider'." -ForegroundColor Red
    exit 1
}

# Double underscore: App Service turns app settings into environment variables, and a colon is not
# allowed in an environment variable name on Linux. "SearchIndex:Provider" here would do nothing.
Write-Host "Setting SearchIndex__Provider=$Provider on $appName (this restarts the app)..."
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings "SearchIndex__Provider=$Provider" --output none
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: set the app setting" -ForegroundColor Red
    exit 1
}

$hostName = az webapp show --name $appName --resource-group $resourceGroup --query defaultHostName -o tsv
Write-Host "Done. Open https://$hostName/tonight once it has restarted, and watch it start in the log:"
Write-Host "  az webapp log tail --name $appName --resource-group $resourceGroup"
