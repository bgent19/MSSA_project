# A-07: provision Cosmos DB for NoSQL with a vector policy.
#
# Creates (or reuses) the resource group, a serverless Cosmos DB account with NoSQL vector search
# turned on, a "meepleledger" database and a "games" container, then stores the endpoint and key
# in the Seeder's user secrets. Also creates the "leases" container the change feed processor
# needs (A-10). Safe to run again: anything that already exists is left alone.
#
# METERED. Serverless bills per request unit and per GB stored, so an idle account with 200 games
# costs close to nothing, but it is still a billable resource. A-33 tears it down.
#
# Run from the repo root:  pwsh infra/cosmos/provision-cosmos.ps1

$resourceGroup = "rg-meepleledger"
$location = "eastus2"            # same region as the embeddings resource from A-04
$accountName = "meepleledger-cosmos"
$databaseName = "meepleledger"
$containerName = "games"
$leaseContainerName = "leases"   # bookkeeping for the change feed processor (A-10)

# One sentence on the partition key: the only access patterns are point upserts by Game and a
# vector search over the whole Catalog for every Ask, so no key could scope the search and /id
# gives each Game its own logical partition with no hot spot. (With real per-user data, /id would
# be a bad habit: a query by owner would fan out to every partition.)
$partitionKeyPath = "/id"

# The vector and indexing policies live next to this script. The vector policy cannot be changed
# once the container exists, so check the path (/embedding) and dimensions (1536) before running.
# The full-text policy (S-02) names /facts, the Game's categories and mechanics as one string, for
# hybrid search. Not /categories/[] and /mechanics/[]: Azure accepts array paths, but FullTextContains
# and FullTextScore on c.mechanics then match nothing. Unlike the vector policy it can be added to a
# container that already exists, and step 5 does that.
$vectorPolicyFile = Join-Path $PSScriptRoot "games-vector-policy.json"
$indexingPolicyFile = Join-Path $PSScriptRoot "games-indexing-policy.json"
$fullTextPolicyFile = Join-Path $PSScriptRoot "games-full-text-policy.json"

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# 1. A fresh subscription has to register the Cosmos DB provider once (same gotcha as A-04).
$providerState = az provider show --namespace Microsoft.DocumentDB --query registrationState -o tsv
if ($providerState -ne "Registered") {
    Write-Host "Registering Microsoft.DocumentDB provider (takes a minute or two)..."
    az provider register --namespace Microsoft.DocumentDB --wait
    Test-LastCommand "register Microsoft.DocumentDB"
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

# 3. The account. Serverless for cost control, and vector search enabled at creation time so
#    the container below can be given a vector policy.
#    Session consistency is a measured choice (A-09): Strong and Bounded Staleness doubled every
#    read, and Consistent Prefix and Eventual cost no less than Session. Session is the weakest
#    level that lets a writer (upload-cosmos, A-10) read its own writes.
$existingAccount = az cosmosdb list --resource-group $resourceGroup --query "[?name=='$accountName'].name" -o tsv
if ($existingAccount -eq $accountName) {
    Write-Host "Cosmos DB account $accountName already exists."
}
else {
    $nameTaken = az cosmosdb check-name-exists --name $accountName
    if ($nameTaken -eq "true") {
        Write-Host "The account name $accountName is taken by someone else. Change `$accountName and run again." -ForegroundColor Red
        exit 1
    }

    Write-Host "Creating Cosmos DB account $accountName (this usually takes 5-10 minutes)..."
    az cosmosdb create `
        --name $accountName `
        --resource-group $resourceGroup `
        --kind GlobalDocumentDB `
        --locations regionName=$location failoverPriority=0 isZoneRedundant=False `
        --capabilities EnableServerless EnableNoSQLVectorSearch `
        --default-consistency-level Session `
        --output none
    Test-LastCommand "create Cosmos DB account"
}

# Make sure the vector search capability really is on, even if the account was made by hand.
$capabilities = az cosmosdb show --name $accountName --resource-group $resourceGroup --query "capabilities[].name" -o tsv
if ($capabilities -notcontains "EnableNoSQLVectorSearch") {
    Write-Host "Vector search is not enabled on $accountName. Enabling it now..."
    Write-Host "Azure says this can take up to 15 minutes to take effect; run this script again after that."
    az cosmosdb update --name $accountName --resource-group $resourceGroup --capabilities EnableServerless EnableNoSQLVectorSearch --output none
    Test-LastCommand "enable NoSQL vector search"
    exit 0
}

# 4. The database. Serverless accounts have no throughput setting, so none is passed.
$existingDatabase = az cosmosdb sql database list --account-name $accountName --resource-group $resourceGroup --query "[?name=='$databaseName'].name" -o tsv
if ($existingDatabase -eq $databaseName) {
    Write-Host "Database $databaseName already exists."
}
else {
    Write-Host "Creating database $databaseName..."
    az cosmosdb sql database create --account-name $accountName --resource-group $resourceGroup --name $databaseName --output none
    Test-LastCommand "create database"
}

# 5. The container, with the vector, full-text and indexing policies declared up front.
#    In PowerShell a bare @name means splatting, so the "@file" arguments must be quoted.
$existingContainer = az cosmosdb sql container list --account-name $accountName --resource-group $resourceGroup --database-name $databaseName --query "[?name=='$containerName'].name" -o tsv
if ($existingContainer -eq $containerName) {
    Write-Host "Container $containerName already exists. Its vector policy cannot be changed in place;"
    Write-Host "delete the container and run this script again if the policy is wrong."

    # The full-text policy and the indexing policy can be changed in place (S-02), so a container
    # made before hybrid search gets them here. Sending the same policies again changes nothing.
    $fullTextPaths = az cosmosdb sql container show --account-name $accountName --resource-group $resourceGroup --database-name $databaseName --name $containerName --query "resource.fullTextPolicy.fullTextPaths[].path" -o tsv
    if ($fullTextPaths -notcontains "/facts") {
        Write-Host "Adding the full-text policy and full-text indexes to $containerName..."
        az cosmosdb sql container update `
            --account-name $accountName `
            --resource-group $resourceGroup `
            --database-name $databaseName `
            --name $containerName `
            --full-text-policy "@$fullTextPolicyFile" `
            --idx "@$indexingPolicyFile" `
            --output none
        Test-LastCommand "add full-text policy"
    }
    else {
        Write-Host "Container $containerName already has its full-text policy."
    }
}
else {
    Write-Host "Creating container $containerName with the vector and full-text policies..."
    az cosmosdb sql container create `
        --account-name $accountName `
        --resource-group $resourceGroup `
        --database-name $databaseName `
        --name $containerName `
        --partition-key-path $partitionKeyPath `
        --vector-embeddings "@$vectorPolicyFile" `
        --full-text-policy "@$fullTextPolicyFile" `
        --idx "@$indexingPolicyFile" `
        --output none
    Test-LastCommand "create container"
}

# 6. The leases container for the change feed processor (A-10). The processor writes one lease
#    document per range of the games container, recording how far it has read, so a restarted
#    host carries on from there instead of starting over. The SDK needs its partition key to be /id.
#    It has no vector or indexing policy to set. On serverless its reads and writes are billed as
#    request units like any other container; on provisioned throughput it would need its own RU/s,
#    or the processor gets throttled by its own bookkeeping.
$existingLeases = az cosmosdb sql container list --account-name $accountName --resource-group $resourceGroup --database-name $databaseName --query "[?name=='$leaseContainerName'].name" -o tsv
if ($existingLeases -eq $leaseContainerName) {
    Write-Host "Container $leaseContainerName already exists."
}
else {
    Write-Host "Creating container $leaseContainerName for the change feed leases..."
    az cosmosdb sql container create `
        --account-name $accountName `
        --resource-group $resourceGroup `
        --database-name $databaseName `
        --name $leaseContainerName `
        --partition-key-path "/id" `
        --output none
    Test-LastCommand "create leases container"
}

# 7. Connection details go into the Seeder's user secrets, never into appsettings.json.
$endpoint = az cosmosdb show --name $accountName --resource-group $resourceGroup --query documentEndpoint -o tsv
Test-LastCommand "read account endpoint"
$key = az cosmosdb keys list --name $accountName --resource-group $resourceGroup --query primaryMasterKey -o tsv
Test-LastCommand "read account key"

dotnet user-secrets set "Cosmos:Endpoint" $endpoint --project MeepleLedger.Seeder | Out-Null
dotnet user-secrets set "Cosmos:Key" $key --project MeepleLedger.Seeder | Out-Null
dotnet user-secrets set "Cosmos:Database" $databaseName --project MeepleLedger.Seeder | Out-Null
dotnet user-secrets set "Cosmos:Container" $containerName --project MeepleLedger.Seeder | Out-Null
dotnet user-secrets set "Cosmos:LeaseContainer" $leaseContainerName --project MeepleLedger.Seeder | Out-Null

Write-Host ""
Write-Host "Done."
Write-Host "  Resource group: $resourceGroup"
Write-Host "  Account:        $accountName"
Write-Host "  Endpoint:       $endpoint"
Write-Host "  Container:      $databaseName/$containerName (partition key $partitionKeyPath)"
Write-Host "  Leases:         $databaseName/$leaseContainerName (partition key /id)"
Write-Host "  User secrets:   Cosmos:Endpoint, Cosmos:Key, Cosmos:Database, Cosmos:Container, Cosmos:LeaseContainer on MeepleLedger.Seeder"
