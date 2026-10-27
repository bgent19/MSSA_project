# A-30: switch the Search Index Provider in App Configuration. NO RESTART, no redeploy.
#
# Two writes:
#   1. SearchIndex:Provider, under the label (azure: the deployed app; local: dotnet run on this machine)
#   2. the sentinel, MeepleLedger:Sentinel
# The apps only watch the sentinel. Once their refresh interval has passed (120 seconds,
# AppConfig:RefreshIntervalSeconds), the next request or Ask sees the new sentinel and reloads every
# setting, and the next Ask after that goes to the new Provider. SwitchingGameSearchIndex logs
# "Search Index Provider switched from ... to ...". An Ask that is already running finishes on the
# old Provider.
#
# Changing only the Provider key does nothing until the sentinel changes too: that is the point of a
# sentinel. Several settings can be changed and then picked up together, never half of them.
#
# The new Provider must be able to run: Cosmos needs the Cosmos and Azure OpenAI settings (in the store);
# Postgres and Redis need their servers to exist and their connection strings in Key Vault (and the
# Key Vault references made by provision-appconfig.ps1), and Postgres needs a firewall rule for the web
# app's outbound IP addresses (az webapp show --name meepleledger-web --resource-group rg-meepleledger
# --query outboundIpAddresses). If it cannot run, each Ask shows "The search is unavailable" and the
# log says why; switch back and the next Ask works again.
#
# Run from the repo root:  pwsh infra/appconfig/set-provider.ps1 <InMemory|Cosmos|Postgres|Redis> [-Label azure|local]

param(
    [Parameter(Mandatory = $true)]
    [string]$Provider,
    [string]$Label = "azure"
)

$storeName = "meepleledger-appconfig"     # from provision-appconfig.ps1
$providerKey = "SearchIndex:Provider"
$sentinelKey = "MeepleLedger:Sentinel"

if (@("InMemory", "Cosmos", "Postgres", "Redis") -cnotcontains $Provider) {
    Write-Host "FAILED: the Provider must be InMemory, Cosmos, Postgres or Redis (exactly), not '$Provider'." -ForegroundColor Red
    exit 1
}
if (@("azure", "local") -cnotcontains $Label) {
    Write-Host "FAILED: -Label must be azure or local, not '$Label'." -ForegroundColor Red
    exit 1
}

$before = az appconfig kv show --name $storeName --key $providerKey --label $Label --auth-mode login --query value -o tsv 2>$null
if ($LASTEXITCODE -ne 0) {
    $before = "(not set)"
}

Write-Host "$providerKey ($Label): $before -> $Provider"
az appconfig kv set --name $storeName --key $providerKey --value $Provider --label $Label --auth-mode login --yes --output none
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: set $providerKey (run pwsh infra/appconfig/provision-appconfig.ps1 first)" -ForegroundColor Red
    exit 1
}

# The sentinel has no label, so every app (local and azure) reloads. That costs the other one a reload,
# nothing more: its own labelled value has not changed.
$stamp = (Get-Date).ToUniversalTime().ToString("o")
Write-Host "$sentinelKey -> $stamp"
az appconfig kv set --name $storeName --key $sentinelKey --value $stamp --auth-mode login --yes --output none
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: bump the sentinel. The Provider key changed, but no app will reload it until the sentinel does." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Done. Within about 2 minutes the next Ask goes to $Provider, with no restart."
if ($Label -eq "azure") {
    Write-Host "Watch it switch in the log:"
    Write-Host "  az webapp log tail --name meepleledger-web --resource-group rg-meepleledger"
}
if ($Provider -eq "Postgres") {
    Write-Host "Postgres also needs a firewall rule for the app's outbound IP addresses (see the top of this script)." -ForegroundColor Yellow
}
