# A-22: publish MeepleLedger.Functions and deploy it, or swap the Windows app's slots.
#
#   pwsh infra/functions/deploy-functions.ps1            # deploy to meepleledger-func (Flex, Linux)
#   pwsh infra/functions/deploy-functions.ps1 -Staging   # deploy to meepleledger-func-win, slot staging
#   pwsh infra/functions/deploy-functions.ps1 -Swap      # swap meepleledger-func-win staging <-> production
#
# A deploy replaces the app's code only. App settings are not in the package and are not touched:
# provision-functions.ps1 owns them. Each package gets a build label (git commit + time) that
# GET /api/about reports, so you can see which code is in which slot before and after a swap.
#
# METERED: deploying is free, the requests the checks at the end make bill per execution (pennies).
#
# Run from the repo root, after provision-functions.ps1.

param(
    [switch]$Staging,
    [switch]$Swap
)

$resourceGroup = "rg-meepleledger"
$flexApp = "meepleledger-func"           # the same names as provision-functions.ps1
$slotApp = "meepleledger-func-win"
$slotName = "staging"

$projectFolder = Join-Path $PSScriptRoot "../../MeepleLedger.Functions"
$publishFolder = Join-Path $projectFolder "bin/deploy/publish"
$zipPath = Join-Path $projectFolder "bin/deploy/functions.zip"

# Stop on the first failed command instead of carrying on with half a deploy.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# GET /api/about, retried, because the first request after a deploy or swap waits for a cold start.
function Show-About([string]$hostName, [string]$label) {
    $url = "https://$hostName/api/about"
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            $about = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 60
            Write-Host "  $label  slot=$($about.slot)  build=$($about.build)"
            return
        }
        catch {
            Start-Sleep -Seconds 10
        }
    }
    Write-Host "  $label  no answer from $url after 10 tries" -ForegroundColor Yellow
}

$windowsHost = az functionapp show --name $slotApp --resource-group $resourceGroup --query defaultHostName -o tsv
Test-LastCommand "read $slotApp (run provision-functions.ps1 first)"
$stagingHost = az functionapp show --name $slotApp --resource-group $resourceGroup --slot $slotName --query defaultHostName -o tsv
Test-LastCommand "read $slotApp/$slotName"

# The swap: staging's code goes to production and production's to staging. Settings marked as slot
# settings (SlotName) stay where they are; every other setting travels with the code. Azure warms up
# the staging slot with production's settings before switching, so production does not go cold.
if ($Swap) {
    Write-Host "Before the swap:"
    Show-About $windowsHost "production"
    Show-About $stagingHost "staging   "

    Write-Host "Swapping $slotApp $slotName -> production..."
    az functionapp deployment slot swap --name $slotApp --resource-group $resourceGroup --slot $slotName --target-slot production --output none
    Test-LastCommand "swap slots"

    Write-Host "After the swap (the builds trade places, the slot names do not):"
    Show-About $windowsHost "production"
    Show-About $stagingHost "staging   "
    exit 0
}

# 1. Publish. The label is baked into the assembly's informational version.
$commit = git rev-parse --short HEAD
$build = "$commit-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Write-Host "Publishing MeepleLedger.Functions (build $build)..."
if (Test-Path $publishFolder) {
    Remove-Item $publishFolder -Recurse -Force
}
dotnet publish $projectFolder --configuration Release --output $publishFolder "-p:InformationalVersion=$build" -p:IncludeSourceRevisionInInformationalVersion=false --nologo --verbosity quiet
Test-LastCommand "dotnet publish"

# 2. Zip the publish folder. ZipFile writes "/" separators, which the Linux host needs;
#    host.json and the .azurefunctions folder must be at the root of the zip, not in a subfolder.
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $publishFolder).Path, $zipPath)

# 3. Deploy. config-zip uploads the package and then syncs the triggers, so the host sees the
#    functions (and the Service Bus trigger starts listening) without a manual restart.
if ($Staging) {
    Write-Host "Deploying to $slotApp, slot $slotName..."
    az functionapp deployment source config-zip --name $slotApp --resource-group $resourceGroup --slot $slotName --src $zipPath --output none
    Test-LastCommand "deploy to $slotApp/$slotName"

    Write-Host "Deployed. Both slots now:"
    Show-About $windowsHost "production"
    Show-About $stagingHost "staging   "
    Write-Host "Swap it into production with: pwsh infra/functions/deploy-functions.ps1 -Swap"
    exit 0
}

Write-Host "Deploying to $flexApp..."
az functionapp deployment source config-zip --name $flexApp --resource-group $resourceGroup --src $zipPath --output none
Test-LastCommand "deploy to $flexApp"

# Flex keeps the host name under "properties"; the Windows app has it at the top level too
$flexHost = az functionapp show --name $flexApp --resource-group $resourceGroup --query properties.defaultHostName -o tsv
Write-Host "Deployed. The functions Azure sees:"
az functionapp function list --name $flexApp --resource-group $resourceGroup --query "[].name" -o tsv
Show-About $flexHost "flex"

Write-Host ""
Write-Host "Queue a re-embed through the real URL (QueueEmbed needs a function key):"
Write-Host "  `$key = az functionapp keys list --name $flexApp --resource-group $resourceGroup --query functionKeys.default -o tsv"
Write-Host "  Invoke-WebRequest -Method Post -Uri `"https://$flexHost/api/games/Wingspan/embed?code=`$key`""
