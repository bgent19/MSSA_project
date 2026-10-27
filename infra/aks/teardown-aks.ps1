# A-28: delete the AKS cluster and everything it made, then check it is really gone.
#
# Deleting the RESOURCE GROUP is the only teardown to trust. Deleting the Service, the Deployment or
# even the cluster one by one can leave a public IP, a managed disk or a load balancer behind, still
# billing. rg-meepleledger-aks holds only the cluster (provision-aks.ps1 made it for that), and AKS
# deletes its node resource group, rg-meepleledger-aks-nodes (the VM, disk, load balancer, public IP),
# with the cluster.
#
# One thing lives OUTSIDE the group: the kubelet identity's AcrPull role assignment, on the registry
# in rg-meepleledger. Deleting the identity does not delete its role assignments; they stay on the
# registry as "Identity not found". So that assignment is removed first, while the identity's id can
# still be read from the cluster.
#
# Then it waits for the delete (5 to 10 minutes), checks both groups no longer exist, and removes
# the cluster from ~/.kube/config so kubectl stops pointing at an address that is gone.
#
# Confirm in the portal afterwards too (Resource groups: neither group listed). That is the
# ticket's last acceptance criterion.
#
# Run from the repo root:  pwsh infra/aks/teardown-aks.ps1

$resourceGroup = "rg-meepleledger-aks"
$nodeResourceGroup = "rg-meepleledger-aks-nodes"
$clusterName = "meepleledger-aks"

$registryResourceGroup = "rg-meepleledger"
$registryName = "meepleledgeracr"

# Stop on the first failed az command.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

$groupExists = az group exists --name $resourceGroup
Test-LastCommand "check resource group $resourceGroup"

if ($groupExists -eq "true") {
    # 1. What is about to go, so the delete is not a surprise.
    Write-Host "In ${resourceGroup}:"
    az resource list --resource-group $resourceGroup --query "[].{name:name, type:type}" -o table

    # 2. The AcrPull assignment on the registry, if the cluster and the registry are both still there.
    $kubeletObjectId = az aks show --resource-group $resourceGroup --name $clusterName --query identityProfile.kubeletidentity.objectId -o tsv 2>$null
    $registryId = az acr show --name $registryName --resource-group $registryResourceGroup --query id -o tsv 2>$null
    if ($kubeletObjectId -and $registryId) {
        $assignmentIds = az role assignment list --role AcrPull --scope $registryId --query "[?principalId=='$kubeletObjectId'].id" -o tsv
        foreach ($assignmentId in $assignmentIds) {
            Write-Host "Removing the kubelet identity's AcrPull on $registryName..."
            az role assignment delete --ids $assignmentId --output none
            Test-LastCommand "remove AcrPull from the kubelet identity"
        }
    }

    # 3. The group. Waits until Azure says it is done.
    Write-Host ""
    Write-Host "Deleting resource group $resourceGroup (cluster, and with it $nodeResourceGroup). 5 to 10 minutes..."
    az group delete --name $resourceGroup --yes
    Test-LastCommand "delete resource group $resourceGroup"
}
else {
    Write-Host "Resource group $resourceGroup does not exist (already deleted)."
}

# 4. Check. The node group is deleted by AKS as part of the cluster delete, so it is normally gone
#    by now; give it a few more minutes if not.
$nodeGroupExists = "true"
for ($attempt = 1; $attempt -le 20; $attempt++) {
    $nodeGroupExists = az group exists --name $nodeResourceGroup
    if ($nodeGroupExists -ne "true") {
        break
    }
    Write-Host "  $nodeResourceGroup still exists, waiting (attempt $attempt)..."
    Start-Sleep -Seconds 30
}
$groupExists = az group exists --name $resourceGroup

# 5. kubectl. az aks get-credentials added a context, a cluster and a user, all for this cluster.
#    Errors are ignored: they only mean an entry was already removed.
kubectl config delete-context $clusterName 2>$null | Out-Null
kubectl config delete-cluster $clusterName 2>$null | Out-Null
kubectl config delete-user "clusterUser_${resourceGroup}_${clusterName}" 2>$null | Out-Null

Write-Host ""
if ($groupExists -ne "true" -and $nodeGroupExists -ne "true") {
    Write-Host "Done. $resourceGroup and $nodeResourceGroup are gone; nothing from A-28 is billing." -ForegroundColor Green
}
else {
    Write-Host "NOT DONE: still there:" -ForegroundColor Red
    if ($groupExists -eq "true") {
        Write-Host "  $resourceGroup"
    }
    if ($nodeGroupExists -eq "true") {
        Write-Host "  $nodeResourceGroup   (delete it: az group delete --name $nodeResourceGroup --yes)"
    }
    exit 1
}
Write-Host "Confirm in the portal: Resource groups should list neither group."
Write-Host "  https://portal.azure.com/#view/HubsExtension/BrowseResourceGroups"
