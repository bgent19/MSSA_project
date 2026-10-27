# A-28: an AKS cluster to run the two MeepleLedger images from ACR, by hand-written manifests.
#
# Creates (or reuses):
#   - rg-meepleledger-aks:       a resource group of its OWN, not rg-meepleledger. The ticket ends
#                                with "az group delete", and that must take the cluster and nothing
#                                else (the registry, Cosmos, Service Bus... stay in rg-meepleledger).
#   - meepleledger-aks:          the cluster. Free tier control plane, ONE small node (Standard_B2ls_v2,
#                                2 vCPU / 4 GiB, the smallest size AKS accepts for a system pool).
#                                The older Standard_B2s is not allowed in this subscription in eastus2;
#                                the "create" error lists the sizes that are. Avoid the "p" sizes
#                                (B2pls_v2): they are ARM, and the images are built for amd64.
#   - rg-meepleledger-aks-nodes: the node resource group. AKS always makes one for the VMs, disks,
#                                load balancer and public IPs; it is named here so it is easy to find in
#                                the portal (the default is MC_<group>_<cluster>_<region>). AKS deletes
#                                it with the cluster.
# then grants AcrPull on meepleledgeracr to the cluster's KUBELET identity and points kubectl at the
# cluster.
#
# Why the kubelet identity: the kubelet on each node is what pulls images, and it pulls as the
# cluster's kubelet managed identity (not the cluster's own identity). With AcrPull on the registry,
# a pod can name meepleledgeracr.azurecr.io/meepleledger:<tag> and it just pulls: no imagePullSecrets,
# no registry password, and the admin account stays off (A-24).
# ("az aks create --attach-acr" does the same role assignment; it is done by hand here so it is visible.)
#
# No Container Insights (--enable-addons monitoring): it bills per GB of logs, and this ticket reads
# logs with kubectl. A-31/A-32 do monitoring properly.
#
# METERED, TIME-BOXED. The Free tier control plane costs nothing; the node does (a B2ls_v2 VM plus its OS
# disk), billed per minute while it exists, and the LoadBalancer Service's public IP bills too. Do the
# whole ticket in one sitting and finish with:
#   pwsh infra/aks/teardown-aks.ps1
#
# Needs kubectl on the PATH (Docker Desktop ships one, or: az aks install-cli).
#
# Run from the repo root:  pwsh infra/aks/provision-aks.ps1 [-NodeSize Standard_B2ls_v2] [-Location eastus2]

param(
    [string]$NodeSize = "Standard_B2ls_v2",
    [string]$Location = "eastus2"          # same region as the registry
)

$resourceGroup = "rg-meepleledger-aks"
$nodeResourceGroup = "rg-meepleledger-aks-nodes"
$clusterName = "meepleledger-aks"

$registryResourceGroup = "rg-meepleledger"
$registryName = "meepleledgeracr"          # from provision-acr.ps1

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

$startedAt = Get-Date

# 0. kubectl, before anything is billed.
if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    Write-Host "FAILED: kubectl is not on the PATH. Install it with: az aks install-cli" -ForegroundColor Red
    exit 1
}

# 1. The registry has to exist already: it lives in rg-meepleledger and outlives the cluster.
$registryId = az acr show --name $registryName --resource-group $registryResourceGroup --query id -o tsv
Test-LastCommand "find registry $registryName (run pwsh infra/acr/provision-acr.ps1 first)"

# 2. A subscription that has never used AKS has to register Microsoft.ContainerService first; --wait
#    returns once it is done (instantly if it already is).
Write-Host "Registering the Microsoft.ContainerService provider..."
az provider register --namespace Microsoft.ContainerService --wait --output none
Test-LastCommand "register Microsoft.ContainerService"

# 3. The resource group of its own.
$groupExists = az group exists --name $resourceGroup
if ($groupExists -eq "true") {
    Write-Host "Resource group $resourceGroup already exists."
}
else {
    Write-Host "Creating resource group $resourceGroup in $Location..."
    az group create --name $resourceGroup --location $Location --tags "ticket=A-28" "delete=same-sitting" --output none
    Test-LastCommand "create resource group $resourceGroup"
}

# 4. The cluster. Takes 5 to 10 minutes.
#    --tier free:          no SLA on the control plane, and no charge for it
#    --node-count 1:       one node; there is no autoscaler, so it stays one
#    --node-osdisk-size 30: the default OS disk is 128 GB, billed whether used or not
#    --no-ssh-key:         nobody logs in to the node; kubectl is the way in
$existingCluster = az aks list --resource-group $resourceGroup --query "[?name=='$clusterName'].name" -o tsv
if ($existingCluster -eq $clusterName) {
    Write-Host "Cluster $clusterName already exists."
}
else {
    Write-Host "Creating cluster $clusterName (Free tier, 1 x $NodeSize). This takes 5 to 10 minutes..."
    az aks create `
        --resource-group $resourceGroup `
        --name $clusterName `
        --location $Location `
        --node-resource-group $nodeResourceGroup `
        --tier free `
        --node-count 1 `
        --node-vm-size $NodeSize `
        --node-osdisk-size 30 `
        --enable-managed-identity `
        --no-ssh-key `
        --tags "ticket=A-28" `
        --output none
    Test-LastCommand "create cluster $clusterName (if the size is not offered here, the error lists the sizes that are; try -NodeSize Standard_B2s_v2 or another -Location)"
}

# 5. AcrPull for the kubelet identity, scoped to the one registry. --assignee-object-id with the
#    principal type skips a Microsoft Graph lookup, which can fail for an identity made minutes ago.
$kubeletObjectId = az aks show --resource-group $resourceGroup --name $clusterName --query identityProfile.kubeletidentity.objectId -o tsv
Test-LastCommand "read the kubelet identity of $clusterName"
$existingRole = az role assignment list --role AcrPull --scope $registryId --query "[?principalId=='$kubeletObjectId'].id" -o tsv
if ($existingRole) {
    Write-Host "The kubelet identity already has AcrPull on $registryName."
}
else {
    Write-Host "Granting AcrPull on $registryName to the kubelet identity ($kubeletObjectId)..."
    az role assignment create `
        --assignee-object-id $kubeletObjectId `
        --assignee-principal-type ServicePrincipal `
        --role AcrPull `
        --scope $registryId `
        --output none
    Test-LastCommand "grant AcrPull to the kubelet identity"
}

# 6. kubectl. Writes the cluster's address and a client certificate to ~/.kube/config and makes it
#    the current context, so every kubectl command from here on talks to this cluster.
Write-Host "Pointing kubectl at $clusterName..."
az aks get-credentials --resource-group $resourceGroup --name $clusterName --overwrite-existing --output none
Test-LastCommand "get credentials for $clusterName"

Write-Host ""
kubectl get nodes -o wide
Test-LastCommand "kubectl get nodes"

$deleteBy = $startedAt.AddMinutes(90)
Write-Host ""
Write-Host "Done."
Write-Host "  Cluster:        $clusterName in $resourceGroup ($Location), Free tier, 1 x $NodeSize"
Write-Host "  Node group:     $nodeResourceGroup"
Write-Host "  Kubelet:        $kubeletObjectId, AcrPull on $registryName"
Write-Host "  kubectl:        context $clusterName"
Write-Host ""
Write-Host "The node is billing from now. Time box: tear down by $($deleteBy.ToString('HH:mm'))." -ForegroundColor Yellow
Write-Host ""
Write-Host "Next:"
Write-Host "  pwsh infra/aks/deploy-aks.ps1          the manifests: namespace, ConfigMap, Secret, Deployments, Service"
Write-Host "  pwsh infra/aks/teardown-aks.ps1        delete $resourceGroup when you are done"
