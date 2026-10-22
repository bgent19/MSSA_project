# A-11: provision Azure Database for PostgreSQL Flexible Server with pgvector allow-listed.
#
# Creates (or reuses) the resource group, a Burstable Flexible Server with a "meepleledger"
# database, a firewall rule for this machine's public IP, and adds VECTOR to the azure.extensions
# server parameter. It then stores a connection string in the Seeder's user secrets.
# Safe to run again: anything that already exists is left alone.
#
# Allow-listing is only half the job. CREATE EXTENSION is run from the Seeder afterwards:
#   dotnet run --project MeepleLedger.Seeder -- setup-postgres
#
# METERED. Unlike Cosmos serverless, this bills for compute every hour the server is RUNNING,
# whether or not anything queries it, plus storage all the time. Stop it when you are done:
#   az postgres flexible-server stop --name meepleledger-pg --resource-group rg-meepleledger
# A-33 tears it down.
#
# Run from the repo root:  pwsh infra/postgres/provision-postgres.ps1

$resourceGroup = "rg-meepleledger"
$location = "eastus2"            # the resource group's region, shared with A-04 and A-07
# The server cannot go in eastus2: this subscription is restricted from creating Flexible Servers
# there (and in eastus, westus2 and southcentralus). Check a region with
#   az postgres flexible-server list-skus --location <region> --query "[0].reason"
# A resource can live in a different region from its resource group.
$serverLocation = "centralus"
$serverName = "meepleledger-pg"  # must be unique across Azure: <name>.postgres.database.azure.com
$databaseName = "meepleledger"
$adminUser = "meepleadmin"
$postgresVersion = "17"

# Sizing, chosen on purpose rather than accepted at the default (the default is General Purpose
# D2ds, about ten times the price). 200 x 1536 float4 vectors plus an HNSW index is a few MB, so the
# smallest Burstable SKU (1 vCore, 2 GiB) holds it with room to spare. The risk on Burstable is CPU
# credits, not memory: see the A-11 ticket.
$tier = "Burstable"
$skuName = "Standard_B1ms"
$storageSizeGiB = 32             # the minimum; storage can be grown later but never shrunk

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# A random admin password. Azure wants at least three of: upper case, lower case, digits, symbols.
# The letters-and-digits part is random; the fixed "Aa1" suffix guarantees all three are present.
function New-AdminPassword {
    $choices = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"
    $randomPart = [System.Security.Cryptography.RandomNumberGenerator]::GetString($choices, 24)
    return $randomPart + "Aa1"
}

# 1. A fresh subscription has to register the PostgreSQL provider once (same gotcha as A-04).
$providerState = az provider show --namespace Microsoft.DBforPostgreSQL --query registrationState -o tsv
if ($providerState -ne "Registered") {
    Write-Host "Registering Microsoft.DBforPostgreSQL provider (takes a minute or two)..."
    az provider register --namespace Microsoft.DBforPostgreSQL --wait
    Test-LastCommand "register Microsoft.DBforPostgreSQL"
}

# 2. Resource group.
$groupExists = az group exists --name $resourceGroup
if ($groupExists -eq "true") {
    Write-Host "Resource group $resourceGroup already exists."
}
else {
    Write-Host "Creating resource group $resourceGroup in $location..."
    az group create --name $resourceGroup --location $location --output none
    Test-LastCommand "create resource group"
}

# This machine's public IP, for the firewall rule.
$clientIp = (Invoke-RestMethod -Uri "https://api.ipify.org").Trim()
Write-Host "Your public IP is $clientIp."

# 3. The server. Public access with a firewall rule for this machine only (not Entra auth, to keep
#    the connection string a plain username and password like the other Providers' keys).
#    High availability off and auto-grow off: both would cost money this project does not need.
$adminPassword = $null
$existingServer = az postgres flexible-server list --resource-group $resourceGroup --query "[?name=='$serverName'].name" -o tsv
if ($existingServer -eq $serverName) {
    Write-Host "Server $serverName already exists."

    # A stopped server rejects parameter changes and connections, so start it first.
    $serverState = az postgres flexible-server show --name $serverName --resource-group $resourceGroup --query state -o tsv
    if ($serverState -eq "Stopped") {
        Write-Host "Server is stopped. Starting it (a few minutes)..."
        az postgres flexible-server start --name $serverName --resource-group $resourceGroup --output none
        Test-LastCommand "start server"
    }
}
else {
    $adminPassword = New-AdminPassword
    Write-Host "Creating server $serverName in $serverLocation ($tier $skuName, $storageSizeGiB GiB, PostgreSQL $postgresVersion)."
    Write-Host "This usually takes 5-10 minutes..."
    az postgres flexible-server create `
        --name $serverName `
        --resource-group $resourceGroup `
        --location $serverLocation `
        --tier $tier `
        --sku-name $skuName `
        --storage-size $storageSizeGiB `
        --storage-auto-grow Disabled `
        --version $postgresVersion `
        --zonal-resiliency Disabled `
        --admin-user $adminUser `
        --admin-password $adminPassword `
        --public-access $clientIp `
        --yes `
        --output none
    Test-LastCommand "create server"
}

# 4. Firewall rule for this machine. Re-running from a new network updates the same rule.
$ruleName = "client-" + $env:COMPUTERNAME.ToLower()
Write-Host "Setting firewall rule $ruleName to $clientIp..."
az postgres flexible-server firewall-rule create `
    --server-name $serverName `
    --resource-group $resourceGroup `
    --name $ruleName `
    --start-ip-address $clientIp `
    --end-ip-address $clientIp `
    --output none
Test-LastCommand "create firewall rule"

# 5. The database.
$existingDatabase = az postgres flexible-server db list --server-name $serverName --resource-group $resourceGroup --query "[?name=='$databaseName'].name" -o tsv
if ($existingDatabase -eq $databaseName) {
    Write-Host "Database $databaseName already exists."
}
else {
    Write-Host "Creating database $databaseName..."
    az postgres flexible-server db create --server-name $serverName --resource-group $resourceGroup --name $databaseName --output none
    Test-LastCommand "create database"
}

# 6. Allow-list the extension. CREATE EXTENSION vector fails until VECTOR is in azure.extensions,
#    and the error does not say so. Only restart if Azure says the parameter is not dynamic.
$extensions = az postgres flexible-server parameter show --server-name $serverName --resource-group $resourceGroup --name azure.extensions --query value -o tsv
Test-LastCommand "read azure.extensions"
$alreadyAllowed = $false
foreach ($name in $extensions.Split(",")) {
    if ($name.Trim().ToUpper() -eq "VECTOR") {
        $alreadyAllowed = $true
    }
}

if ($alreadyAllowed) {
    Write-Host "VECTOR is already in azure.extensions."
}
else {
    # Keep anything already allow-listed and add VECTOR to the end of the list.
    $newValue = "VECTOR"
    if (-not [string]::IsNullOrWhiteSpace($extensions)) {
        $newValue = $extensions + ",VECTOR"
    }
    Write-Host "Setting azure.extensions to $newValue..."
    az postgres flexible-server parameter set --server-name $serverName --resource-group $resourceGroup --name azure.extensions --value $newValue --output none
    Test-LastCommand "set azure.extensions"

    $isDynamic = az postgres flexible-server parameter show --server-name $serverName --resource-group $resourceGroup --name azure.extensions --query isDynamicConfig -o tsv
    if ($isDynamic -ne "true") {
        Write-Host "azure.extensions only takes effect after a restart. Restarting (a few minutes)..."
        az postgres flexible-server restart --name $serverName --resource-group $resourceGroup --output none
        Test-LastCommand "restart server"
    }
}

# 7. Connection details go into the Seeder's user secrets, never into appsettings.json.
#    The password only exists at creation time. On a re-run, keep the stored connection string;
#    if it is missing, reset the admin password so a new one can be stored.
$secrets = dotnet user-secrets list --project MeepleLedger.Seeder
$hasConnectionString = $false
foreach ($line in $secrets) {
    if ($line.StartsWith("Postgres:ConnectionString")) {
        $hasConnectionString = $true
    }
}

if ($adminPassword -eq $null -and -not $hasConnectionString) {
    Write-Host "No stored connection string for the existing server. Resetting the admin password..."
    $adminPassword = New-AdminPassword
    az postgres flexible-server update --name $serverName --resource-group $resourceGroup --admin-password $adminPassword --output none
    Test-LastCommand "reset admin password"
}

$hostName = az postgres flexible-server show --name $serverName --resource-group $resourceGroup --query fullyQualifiedDomainName -o tsv
Test-LastCommand "read server host name"

if ($adminPassword -ne $null) {
    # Azure only accepts encrypted connections, so SSL Mode=Require is not optional.
    $connectionString = "Host=$hostName;Database=$databaseName;Username=$adminUser;Password=$adminPassword;SSL Mode=Require"
    dotnet user-secrets set "Postgres:ConnectionString" $connectionString --project MeepleLedger.Seeder | Out-Null
}

Write-Host ""
Write-Host "Done."
Write-Host "  Resource group: $resourceGroup"
Write-Host "  Server:         $serverName in $serverLocation ($tier $skuName, $storageSizeGiB GiB, PostgreSQL $postgresVersion)"
Write-Host "  Host:           $hostName"
Write-Host "  Database:       $databaseName (admin user $adminUser)"
Write-Host "  Firewall:       $ruleName -> $clientIp"
Write-Host "  User secrets:   Postgres:ConnectionString on MeepleLedger.Seeder"
Write-Host ""
Write-Host "Next, create the extension and check it:"
Write-Host "  dotnet run --project MeepleLedger.Seeder -- setup-postgres"
Write-Host ""
Write-Host "The server bills while it runs. Stop it when you are done:"
Write-Host "  az postgres flexible-server stop --name $serverName --resource-group $resourceGroup"
