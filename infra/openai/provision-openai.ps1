# A-04: provision the embeddings resource.
#
# Creates (or reuses) the resource group, an Azure OpenAI account and a text-embedding-3-small
# deployment, then stores the endpoint, key and deployment name in the Seeder's user secrets.
# Safe to run again: anything that already exists is left alone.
#
# METERED. The account itself costs nothing idle; embedding calls bill per token (the whole
# Catalog is well under one cent). A-33 tears it down.
#
# Run from the repo root:  pwsh infra/openai/provision-openai.ps1

$resourceGroup = "rg-meepleledger"
$location = "eastus2"            # offers text-embedding-3-small; everything else goes here too
$accountName = "meepleledger-openai"
$deploymentName = "game-embeddings"
$modelName = "text-embedding-3-small"
$modelVersion = "1"
$capacity = 20                   # thousands of tokens per minute (20K TPM)

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# 1. A fresh subscription has to register the Cognitive Services provider once.
$providerState = az provider show --namespace Microsoft.CognitiveServices --query registrationState -o tsv
if ($providerState -ne "Registered") {
    Write-Host "Registering Microsoft.CognitiveServices provider (takes a minute or two)..."
    az provider register --namespace Microsoft.CognitiveServices --wait
    Test-LastCommand "register Microsoft.CognitiveServices"
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

# 3. The account. A deleted account stays soft-deleted for a while and keeps its name reserved;
#    if this step fails with a name conflict, purge it with
#    az cognitiveservices account purge --name <name> --resource-group <group> --location <region>
$existingAccount = az cognitiveservices account list --resource-group $resourceGroup --query "[?name=='$accountName'].name" -o tsv
if ($existingAccount -eq $accountName) {
    Write-Host "Azure OpenAI account $accountName already exists."
}
else {
    Write-Host "Creating Azure OpenAI account $accountName..."
    # The custom domain gives the endpoint https://<accountName>.openai.azure.com/
    az cognitiveservices account create `
        --name $accountName `
        --resource-group $resourceGroup `
        --location $location `
        --kind OpenAI `
        --sku S0 `
        --custom-domain $accountName `
        --yes `
        --output none
    Test-LastCommand "create Azure OpenAI account"
}

# 4. The deployment. The app refers to the deployment name, never the model id.
$existingDeployment = az cognitiveservices account deployment list --name $accountName --resource-group $resourceGroup --query "[?name=='$deploymentName'].name" -o tsv
if ($existingDeployment -eq $deploymentName) {
    Write-Host "Deployment $deploymentName already exists."
}
else {
    Write-Host "Creating deployment $deploymentName ($modelName v$modelVersion)..."
    az cognitiveservices account deployment create `
        --name $accountName `
        --resource-group $resourceGroup `
        --deployment-name $deploymentName `
        --model-name $modelName `
        --model-version $modelVersion `
        --model-format OpenAI `
        --sku-name Standard `
        --sku-capacity $capacity `
        --output none
    Test-LastCommand "create deployment"
}

# 5. Connection details go into the Seeder's user secrets, never into appsettings.json.
$endpoint = az cognitiveservices account show --name $accountName --resource-group $resourceGroup --query properties.endpoint -o tsv
Test-LastCommand "read account endpoint"
$key = az cognitiveservices account keys list --name $accountName --resource-group $resourceGroup --query key1 -o tsv
Test-LastCommand "read account key"

dotnet user-secrets set "AzureOpenAI:Endpoint" $endpoint --project MeepleLedger.Seeder | Out-Null
dotnet user-secrets set "AzureOpenAI:Key" $key --project MeepleLedger.Seeder | Out-Null
dotnet user-secrets set "AzureOpenAI:EmbeddingDeployment" $deploymentName --project MeepleLedger.Seeder | Out-Null

Write-Host ""
Write-Host "Done."
Write-Host "  Resource group: $resourceGroup"
Write-Host "  Account:        $accountName"
Write-Host "  Endpoint:       $endpoint"
Write-Host "  Deployment:     $deploymentName ($modelName v$modelVersion, Standard, ${capacity}K TPM)"
Write-Host "  User secrets:   AzureOpenAI:Endpoint, AzureOpenAI:Key, AzureOpenAI:EmbeddingDeployment on MeepleLedger.Seeder"
Write-Host ""
Write-Host "A new deployment can return DeploymentNotFound for a few minutes. Then check it with:"
Write-Host "  dotnet run --project MeepleLedger.Seeder -- smoke-embed"
