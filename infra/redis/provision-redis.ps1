# A-15: provision Azure Managed Redis with the RediSearch module.
#
# Creates (or reuses) the resource group and an Azure Managed Redis instance with the RediSearch
# module, TLS only, then stores a StackExchange.Redis connection string in the Seeder's user
# secrets. Safe to run again: anything that already exists is left alone.
#
# This is Azure MANAGED Redis (az redisenterprise, Microsoft.Cache/redisEnterprise), not the older
# Azure Cache for Redis (az redis, Basic/Standard/Premium). They are different products with
# different tiers, and the old Basic/Standard/Premium tiers have no search module at all.
#
# Check it from your machine afterwards (PING and MODULE LIST):
#   dotnet run --project MeepleLedger.Seeder -- ping-redis
#
# METERED, and the most expensive idle resource in the project. There is no serverless option and
# no stop: it bills by the hour for as long as it EXISTS, whether or not anything connects. If you
# are pausing for more than a day, delete it and run this script again later:
#   az redisenterprise delete --cluster-name meepleledger-redis --resource-group rg-meepleledger --yes
# A-33 tears it down.
#
# Run from the repo root:  pwsh infra/redis/provision-redis.ps1

$resourceGroup = "rg-meepleledger"
$location = "eastus2"             # the resource group's region, shared with A-04 and A-07
# The instance cannot go in eastus2: creating Balanced_B0 there fails for this subscription with
# "insufficient capacity" (the same kind of regional restriction A-11 hit for PostgreSQL). A resource
# can live in a different region from its resource group.
$redisLocation = "centralus"
$clusterName = "meepleledger-redis"  # must be unique across Azure: <name>.<region>.redis.azure.net

# Sizing, chosen on purpose. Balanced B0 (0.5 GB) is the smallest Azure Managed Redis SKU, and the
# Balanced tier includes RediSearch (Flash Optimized does not). 200 x 1536 float32 vectors are about
# 1.2 MB, so 0.5 GB is plenty. Scaling down is not supported, so start at the bottom.
$sku = "Balanced_B0"

# RediSearch only works with the Enterprise clustering policy and the NoEviction eviction policy,
# and modules can only be chosen when the database is created. Getting any of these three wrong
# means deleting and recreating the instance.
#   EnterpriseCluster: one endpoint, a proxy routes each key to its shard. The client sees what
#   looks like a single non-clustered Redis, but it IS clustered inside, so multi-key commands
#   other than DEL, MSET, MGET, EXISTS, UNLINK and TOUCH can still fail with CROSSSLOT (A-16, A-17).
#   NoEviction: Redis must never silently drop an indexed key to make room.
$clusteringPolicy = "EnterpriseCluster"
$evictionPolicy = "NoEviction"

# High availability off: no replica, no SLA, about half the price. Fine for study, never for
# production. It can be turned on later but never off again.
$highAvailability = "Disabled"

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# 0. The redisenterprise commands live in an az CLI extension. Without it, az asks "install it now?"
#    on stdin, which fails when nobody is there to answer.
Write-Host "Making sure the redisenterprise az extension is installed..."
az extension add --name redisenterprise --upgrade --only-show-errors
Test-LastCommand "install redisenterprise extension"

# 1. A fresh subscription has to register the Cache provider once (same gotcha as A-04).
$providerState = az provider show --namespace Microsoft.Cache --query registrationState -o tsv
if ($providerState -ne "Registered") {
    Write-Host "Registering Microsoft.Cache provider (takes a minute or two)..."
    az provider register --namespace Microsoft.Cache --wait
    Test-LastCommand "register Microsoft.Cache"
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

# 3. The instance and its "default" database, created together.
#    --client-protocol Encrypted: TLS only, there is no plaintext port at all.
#    --access-keys-authentication Enabled: Managed Redis defaults to Entra ID only. A key keeps the
#    connection string a plain secret like the other Providers' keys.
#    --modules: RediSearch only. A-17 stores Games as hashes, so RedisJSON is not needed. (Careful:
#    'name=RediSearch name=RedisJSON' silently installs only the first one.)
#    --port 10000: the standard Managed Redis port, pinned so the connection string never changes.
$existingCluster = az redisenterprise list --resource-group $resourceGroup --query "[?name=='$clusterName'].name" -o tsv
if ($existingCluster -eq $clusterName) {
    Write-Host "Redis instance $clusterName already exists."
}
else {
    Write-Host "Creating Redis instance $clusterName in $redisLocation ($sku, $clusteringPolicy, RediSearch)."
    Write-Host "This usually takes 5-15 minutes..."
    az redisenterprise create `
        --cluster-name $clusterName `
        --resource-group $resourceGroup `
        --location $redisLocation `
        --sku $sku `
        --high-availability $highAvailability `
        --minimum-tls-version "1.2" `
        --public-network-access Enabled `
        --client-protocol Encrypted `
        --clustering-policy $clusteringPolicy `
        --eviction-policy $evictionPolicy `
        --access-keys-authentication Enabled `
        --port 10000 `
        --modules name="RediSearch" `
        --output none
    Test-LastCommand "create Redis instance"
}

# 4. Read back what Azure actually built, rather than trusting what was asked for. An instance
#    made by hand in the portal could differ, and none of these can be changed in place.
$hostName = az redisenterprise show --cluster-name $clusterName --resource-group $resourceGroup --query hostName -o tsv
Test-LastCommand "read host name"
$actualSku = az redisenterprise show --cluster-name $clusterName --resource-group $resourceGroup --query sku.name -o tsv
$database = az redisenterprise database show --cluster-name $clusterName --resource-group $resourceGroup --output json | ConvertFrom-Json
Test-LastCommand "read database"

$moduleNames = @()
foreach ($module in $database.modules) {
    $moduleNames += $module.name
}

$problems = @()
if (-not ($moduleNames -contains "RediSearch")) {
    $problems += "RediSearch module is missing (modules can only be added at creation: delete and recreate)"
}
if ($database.clientProtocol -ne "Encrypted") {
    # Unlike the other three, this one can be fixed in place.
    $problems += "client protocol is $($database.clientProtocol), not Encrypted. Fix with: az redisenterprise database update --cluster-name $clusterName --resource-group $resourceGroup --client-protocol Encrypted"
}
if ($database.clusteringPolicy -ne "EnterpriseCluster") {
    $problems += "clustering policy is $($database.clusteringPolicy); RediSearch needs EnterpriseCluster"
}
if ($database.evictionPolicy -ne "NoEviction") {
    $problems += "eviction policy is $($database.evictionPolicy); RediSearch needs NoEviction"
}
foreach ($problem in $problems) {
    Write-Host "WARNING: $problem" -ForegroundColor Yellow
}

# 5. Connection string into the Seeder's user secrets, never into appsettings.json.
#    StackExchange.Redis format. ssl=True because the port only speaks TLS; abortConnect=False so a
#    client keeps retrying instead of failing hard if the first connect is slow.
$accessKey = az redisenterprise database list-keys --cluster-name $clusterName --resource-group $resourceGroup --query primaryKey -o tsv
Test-LastCommand "read access key"
$connectionString = "${hostName}:$($database.port),password=$accessKey,ssl=True,abortConnect=False"
dotnet user-secrets set "Redis:ConnectionString" $connectionString --project MeepleLedger.Seeder | Out-Null
Test-LastCommand "store connection string"

Write-Host ""
Write-Host "Done."
Write-Host "  Resource group:    $resourceGroup"
Write-Host "  Instance:          $clusterName in $redisLocation"
Write-Host "  SKU:               $actualSku (Balanced B0 = 0.5 GB, high availability $highAvailability)"
Write-Host "  Host:              ${hostName}:$($database.port)"
Write-Host "  Modules:           $($moduleNames -join ', ')"
Write-Host "  Client protocol:   $($database.clientProtocol) (TLS only)"
Write-Host "  Clustering policy: $($database.clusteringPolicy) (clustered inside, one endpoint)"
Write-Host "  Eviction policy:   $($database.evictionPolicy)"
Write-Host "  User secrets:      Redis:ConnectionString on MeepleLedger.Seeder"
Write-Host ""
Write-Host "Next, check it from this machine (PING and MODULE LIST):"
Write-Host "  dotnet run --project MeepleLedger.Seeder -- ping-redis"
Write-Host ""
Write-Host "It bills every hour it exists. There is no stop. Delete it when you pause:"
Write-Host "  az redisenterprise delete --cluster-name $clusterName --resource-group $resourceGroup --yes"
