# A-30: turn the Tonight feature flag on or off. NO RESTART.
#
# A feature flag is not a plain key-value: it lives under the reserved key prefix
# .appconfig.featureflag/Tonight, with a JSON value ({"id":"Tonight","enabled":true,...}), and the app
# reads it through the feature management library (IFeatureManager), not with config["..."]. That is
# why it has its own az commands.
#
# The flags have their own refresh (no sentinel needed): the app checks them at most every 120
# seconds, on a request. The nav menu is drawn on every page load, so after that the next page you
# open shows the Tonight link come or go, and /tonight itself says the screen is switched off.
#
# Run from the repo root:  pwsh infra/appconfig/set-tonight.ps1 <on|off> [-Label azure|local]

param(
    [Parameter(Mandatory = $true)]
    [string]$State,
    [string]$Label = "azure"
)

$storeName = "meepleledger-appconfig"     # from provision-appconfig.ps1
$flagName = "Tonight"                     # AppConfigKeys.TonightFeature in the web app

if (@("on", "off") -notcontains $State) {
    Write-Host "FAILED: say on or off, not '$State'." -ForegroundColor Red
    exit 1
}
if (@("azure", "local") -cnotcontains $Label) {
    Write-Host "FAILED: -Label must be azure or local, not '$Label'." -ForegroundColor Red
    exit 1
}

$command = "disable"
$linkWillBe = "gone"
if ($State -eq "on") {
    $command = "enable"
    $linkWillBe = "back"
}

Write-Host "Turning $flagName ($Label) $State..."
az appconfig feature $command --name $storeName --feature $flagName --label $Label --auth-mode login --yes --output none
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: $command $flagName (run pwsh infra/appconfig/provision-appconfig.ps1 first)" -ForegroundColor Red
    exit 1
}

az appconfig feature show --name $storeName --feature $flagName --label $Label --auth-mode login --fields name label state -o table
Write-Host ""
Write-Host "Done. Within about 2 minutes, open any page (or reload) and the Tonight link is $linkWillBe."
