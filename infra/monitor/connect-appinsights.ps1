# A-31: point every component at ONE Application Insights resource, so their spans join up.
#
# The resource already exists: provision-functions.ps1 (A-22) made meepleledger-func-insights, on the
# meepleledger-logs workspace, and the function apps already send to it. Despite its name it becomes
# the one resource for the whole solution: separate resources would mean separate application maps,
# and a trace that crosses from one to the other would show as two halves with no link between them.
#
# This script copies its connection string to where each component reads it:
#   - the user secret AzureMonitor:ConnectionString     dotnet run of the web app, the worker, and the
#                                                       Seeder's watch-cosmos (where the trace starts);
#                                                       write-local-settings.ps1 copies it for func start
#   - the App Configuration key AzureMonitor:ConnectionString (no label)
#                                                       the web app in App Service and the worker in
#                                                       Container Apps (A-30), so no new app setting
#   - the function apps' APPLICATIONINSIGHTS_CONNECTION_STRING app setting: only CHECKED, they have it
# then restarts the web app and the worker's active revisions: the connection string is read once at
# startup (unlike the Provider, it is not worth a sentinel).
#
# The connection string is not a secret in the way a key is: it lets you SEND telemetry to the
# resource, not read anything from it. It is still kept out of the repo.
#
# Sampling: the web app keeps every trace (AzureMonitor:SamplingRatio 1.0 in appsettings.json) while
# you look for one. The worker, the Seeder and the Function follow the decision of the trace's root,
# so lowering that one setting samples whole traces, never half of one.
#
# METERED: Application Insights bills per GB ingested (the first 5 GB a month are free per billing
# account). One Ask is a handful of spans; a scale test (send-many) is a few hundred.
#
# Run from the repo root:  pwsh infra/monitor/connect-appinsights.ps1

$resourceGroup = "rg-meepleledger"
$appInsightsName = "meepleledger-func-insights"   # from provision-functions.ps1 (A-22)
$storeName = "meepleledger-appconfig"             # from provision-appconfig.ps1 (A-30)
$webAppName = "meepleledger-web"                  # from provision-appservice.ps1 (A-25)
$workerAppName = "meepleledger-worker"            # from provision-containerapps.ps1 (A-26)
$functionApps = @("meepleledger-func", "meepleledger-func-win")
$settingName = "AzureMonitor:ConnectionString"

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# Reads the InstrumentationKey part of a connection string, to compare two without printing either.
function Get-InstrumentationKey([string]$connectionString) {
    foreach ($part in $connectionString.Split(";")) {
        if ($part.StartsWith("InstrumentationKey=")) {
            return $part.Substring("InstrumentationKey=".Length)
        }
    }
    return ""
}

# 1. The resource and its connection string.
az extension add --name application-insights --upgrade --yes --only-show-errors
Test-LastCommand "install the application-insights CLI extension"
$connectionString = az monitor app-insights component show --app $appInsightsName --resource-group $resourceGroup --query connectionString -o tsv
Test-LastCommand "read the connection string of $appInsightsName (run pwsh infra/functions/provision-functions.ps1 first)"
$instrumentationKey = Get-InstrumentationKey $connectionString
Write-Host "Application Insights $appInsightsName (instrumentation key ending ...$($instrumentationKey.Substring($instrumentationKey.Length - 4)))"

# 2. The user secret. dotnet is an .exe, not a .cmd, so the ";" in the value survives the command line.
Write-Host "User secret $settingName..."
dotnet user-secrets set $settingName $connectionString --project MeepleLedger.Seeder | Out-Null
Test-LastCommand "set the user secret $settingName"

# 3. The App Configuration key. The value has ";" and "=" in it, which do not survive being passed to
#    az.cmd on Windows, so it goes in through an import file instead of --value. The file holds the
#    connection string, so it is deleted straight away. Import only adds or changes the keys in the
#    file; every other key in the store is left alone.
$storeExists = az appconfig list --resource-group $resourceGroup --query "[?name=='$storeName'].name" -o tsv
if ($storeExists -eq $storeName) {
    Write-Host "App Configuration key $settingName (no label)..."
    $file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-appinsights-$([guid]::NewGuid()).json"
    $content = [ordered]@{ AzureMonitor = [ordered]@{ ConnectionString = $connectionString } }
    ConvertTo-Json -InputObject $content -Depth 3 | Set-Content -Path $file -Encoding utf8
    try {
        az appconfig kv import --name $storeName --source file --path $file --format json --separator ":" --auth-mode login --yes --output none
        Test-LastCommand "import $settingName into $storeName"
    }
    finally {
        Remove-Item $file -Force
    }
}
else {
    Write-Host "  (no App Configuration store ${storeName}: the deployed apps get no traces until provision-appconfig.ps1 has run)"
}

# 4. The function apps: they must send to the SAME resource, or the trace splits in two.
Write-Host "Function apps:"
foreach ($functionApp in $functionApps) {
    $appExists = az functionapp list --resource-group $resourceGroup --query "[?name=='$functionApp'].name" -o tsv
    if ($appExists -ne $functionApp) {
        Write-Host "  (no $functionApp)"
        continue
    }
    $theirs = az functionapp config appsettings list --name $functionApp --resource-group $resourceGroup --query "[?name=='APPLICATIONINSIGHTS_CONNECTION_STRING'].value" -o tsv
    if ((Get-InstrumentationKey $theirs) -eq $instrumentationKey) {
        Write-Host "  $functionApp sends to $appInsightsName."
    }
    else {
        Write-Host "  $functionApp sends somewhere else (or nowhere). Run pwsh infra/functions/provision-functions.ps1." -ForegroundColor Yellow
    }
}

# 5. Restarts, so the deployed apps read the new setting.
$webExists = az webapp list --resource-group $resourceGroup --query "[?name=='$webAppName'].name" -o tsv
if ($webExists -eq $webAppName) {
    Write-Host "Restarting $webAppName..."
    az webapp restart --name $webAppName --resource-group $resourceGroup --output none
    Test-LastCommand "restart $webAppName"
}
$workerExists = az containerapp list --resource-group $resourceGroup --query "[?name=='$workerAppName'].name" -o tsv
if ($workerExists -eq $workerAppName) {
    $activeRevisions = az containerapp revision list --name $workerAppName --resource-group $resourceGroup --query "[?properties.active].name" -o tsv
    foreach ($revision in $activeRevisions) {
        if ($revision) {
            Write-Host "Restarting worker revision $revision..."
            az containerapp revision restart --name $workerAppName --resource-group $resourceGroup --revision $revision --output none
            Test-LastCommand "restart $revision"
        }
    }
}

Write-Host ""
Write-Host "Done. Every component sends to $appInsightsName."
Write-Host "Locally:  pwsh MeepleLedger.Functions/write-local-settings.ps1   (so func start sends too)"
Write-Host "Try it:   dotnet run --project MeepleLedger.Seeder -- watch-cosmos, edit a Blurb, and search"
Write-Host "          Application Insights for the trace id it prints (Transaction search, or the end-to-end view)."
