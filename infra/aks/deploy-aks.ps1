# A-28: apply the hand-written manifests in infra/aks/manifests to the AKS cluster.
#
# In order: namespace, ConfigMap, Secret, the web Deployment, its Service, the worker Deployment.
# Each file has __NAME__ placeholders (images, endpoints, keys). This script reads the file, replaces
# the placeholders from the user secrets and its parameters, and pipes the text to
# "kubectl apply -f -". The filled-in text only ever exists in memory: no key is written to disk.
# It refuses to apply a file that still has a placeholder left.
#
# kubectl apply is declarative: run it again and anything that drifted from the files (an image set
# by hand, a patched probe, a ConfigMap value) goes back to what the files say. break-aks.ps1 fix
# relies on that.
#
# After the apply it restarts the Deployments that already existed (kubectl rollout restart),
# because a pod reads its environment variables once, when it starts: a changed ConfigMap or Secret
# reaches nothing until the pods are replaced. A Deployment created by this run starts fresh anyway.
#
# Images go by VERSION tag, never :latest (A-24): -Tag / -WorkerTag, or by default the newest tag of
# each repository that is not "latest".
#
# -Provider picks the Search Index: InMemory (the default, needs nothing, ADR-0001) or Cosmos (uses
# the Cosmos and Azure OpenAI settings from the Secret and ConfigMap). Postgres and Redis would need
# their connection strings added to the Secret; not part of this ticket.
#
# METERED: the LoadBalancer Service creates a public IP, which bills while it exists. teardown-aks.ps1
# deletes it with everything else.
#
# Run from the repo root:  pwsh infra/aks/deploy-aks.ps1 [-Tag <version>] [-WorkerTag <version>] [-Provider InMemory|Cosmos]

param(
    [string]$Tag = "",
    [string]$WorkerTag = "",
    [string]$Provider = "InMemory"
)

$clusterName = "meepleledger-aks"          # also the kubectl context name, from az aks get-credentials
$namespace = "meepleledger"
$registryName = "meepleledgeracr"
$webImageName = "meepleledger"
$workerImageName = "meepleledger-worker"

$userSecretsId = "93de1646-b3dc-4c1f-9bc8-3ebc34f364f3"   # the Seeder's, shared by every project

# The manifests, in the order they are applied. The namespace comes first because everything else
# is created inside it; the ConfigMap and Secret come before the Deployments that read them.
$manifestFolder = Join-Path $PSScriptRoot "manifests"
$manifestFiles = @(
    "namespace.yaml",
    "configmap.yaml",
    "secret.yaml",
    "web-deployment.yaml",
    "web-service.yaml",
    "worker-deployment.yaml"
)

# Stop on the first failed command instead of carrying on with half a deployment.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# The newest tag of a repository that is not "latest", or "" if there is none.
function Get-NewestVersionTag([string]$repository) {
    $tags = az acr repository show-tags --name $registryName --repository $repository --orderby time_desc -o tsv
    Test-LastCommand "list the tags of $repository"
    foreach ($candidate in $tags) {
        if ($candidate -ne "latest") {
            return $candidate
        }
    }
    return ""
}

if (@("InMemory", "Cosmos") -notcontains $Provider) {
    Write-Host "FAILED: -Provider must be InMemory or Cosmos, not '$Provider'." -ForegroundColor Red
    exit 1
}

# 0. Make sure kubectl is talking to the AKS cluster, not to Docker Desktop or anything else.
$context = kubectl config current-context
Test-LastCommand "read the current kubectl context (run pwsh infra/aks/provision-aks.ps1 first)"
if ($context -ne $clusterName) {
    Write-Host "FAILED: kubectl's current context is '$context', not '$clusterName'." -ForegroundColor Red
    Write-Host "Switch with: kubectl config use-context $clusterName"
    exit 1
}

# 1. Read the user secrets. The worker needs all of these; the web app needs the Cosmos and Azure
#    OpenAI ones when -Provider is Cosmos.
$secretsPath = Join-Path $env:APPDATA "Microsoft/UserSecrets/$userSecretsId/secrets.json"
if (-not (Test-Path $secretsPath)) {
    Write-Host "FAILED: no user secrets at $secretsPath. Run the provisioning scripts first." -ForegroundColor Red
    exit 1
}
$secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
$needed = @(
    "ServiceBus:ConnectionString",
    "AzureOpenAI:Endpoint", "AzureOpenAI:Key", "AzureOpenAI:EmbeddingDeployment",
    "Cosmos:Endpoint", "Cosmos:Key", "Cosmos:Database", "Cosmos:Container"
)
$missing = @()
foreach ($name in $needed) {
    if (-not $secrets.ContainsKey($name) -or [string]::IsNullOrWhiteSpace($secrets[$name])) {
        $missing += $name
    }
}
if ($missing.Count -gt 0) {
    Write-Host "FAILED: missing user secrets: $($missing -join ', ')" -ForegroundColor Red
    Write-Host "(provision-servicebus.ps1, provision-openai.ps1 and provision-cosmos.ps1 set them)"
    exit 1
}

# 2. The images.
$loginServer = az acr show --name $registryName --query loginServer -o tsv
Test-LastCommand "read the registry login server (run pwsh infra/acr/provision-acr.ps1 first)"
if (-not $Tag) {
    $Tag = Get-NewestVersionTag $webImageName
}
if (-not $WorkerTag) {
    $WorkerTag = Get-NewestVersionTag $workerImageName
}
if (-not $Tag -or -not $WorkerTag) {
    Write-Host "FAILED: $webImageName or $workerImageName has no version tag in $registryName. Run pwsh infra/acr/build-in-acr.ps1 first." -ForegroundColor Red
    exit 1
}
if ($Tag -eq "latest" -or $WorkerTag -eq "latest") {
    Write-Host "FAILED: deploy a version tag, not latest (see A-24)." -ForegroundColor Red
    exit 1
}
$webImage = "${loginServer}/${webImageName}:$Tag"
$workerImage = "${loginServer}/${workerImageName}:$WorkerTag"
Write-Host "Web image:    $webImage"
Write-Host "Worker image: $workerImage"
Write-Host "Provider:     $Provider"

# 3. What each placeholder becomes.
$values = [ordered]@{
    "__WEB_IMAGE__" = $webImage
    "__WORKER_IMAGE__" = $workerImage
    "__SEARCH_PROVIDER__" = $Provider
    "__COSMOS_ENDPOINT__" = $secrets["Cosmos:Endpoint"]
    "__COSMOS_DATABASE__" = $secrets["Cosmos:Database"]
    "__COSMOS_CONTAINER__" = $secrets["Cosmos:Container"]
    "__AZURE_OPENAI_ENDPOINT__" = $secrets["AzureOpenAI:Endpoint"]
    "__AZURE_OPENAI_DEPLOYMENT__" = $secrets["AzureOpenAI:EmbeddingDeployment"]
    "__COSMOS_KEY__" = $secrets["Cosmos:Key"]
    "__AZURE_OPENAI_KEY__" = $secrets["AzureOpenAI:Key"]
    "__SERVICEBUS_CONNECTION_STRING__" = $secrets["ServiceBus:ConnectionString"]
}

# 4. Which Deployments exist before the apply (see the restart in step 6).
$webExisted = $false
$workerExisted = $false
kubectl get deployment meepleledger-web --namespace $namespace --output name 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    $webExisted = $true
}
kubectl get deployment meepleledger-worker --namespace $namespace --output name 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    $workerExisted = $true
}

# 5. Apply each manifest.
foreach ($fileName in $manifestFiles) {
    $text = Get-Content (Join-Path $manifestFolder $fileName) -Raw
    foreach ($placeholder in $values.Keys) {
        $text = $text.Replace($placeholder, $values[$placeholder])
    }
    if ($text -match "__[A-Z_]+__") {
        Write-Host "FAILED: $fileName still has the placeholder $($Matches[0]) after filling in." -ForegroundColor Red
        exit 1
    }
    Write-Host "Applying $fileName..."
    $text | kubectl apply -f -
    Test-LastCommand "kubectl apply $fileName"
}

# 6. Replace the pods of a Deployment that already existed, so they read the ConfigMap and Secret
#    again. A rollout replaces them one at a time: the old pod keeps serving until the new one is ready.
if ($webExisted) {
    kubectl rollout restart deployment/meepleledger-web --namespace $namespace
    Test-LastCommand "restart meepleledger-web"
}
if ($workerExisted) {
    kubectl rollout restart deployment/meepleledger-worker --namespace $namespace
    Test-LastCommand "restart meepleledger-worker"
}

# 7. Wait for the rollouts. A rollout is done when the new pods are READY (readiness probe passing).
#    A first pull of each image from ACR takes a minute or two.
Write-Host ""
Write-Host "Waiting for the rollouts (up to 5 minutes each)..."
kubectl rollout status deployment/meepleledger-web --namespace $namespace --timeout=5m
$webRolledOut = ($LASTEXITCODE -eq 0)
kubectl rollout status deployment/meepleledger-worker --namespace $namespace --timeout=5m
$workerRolledOut = ($LASTEXITCODE -eq 0)

Write-Host ""
kubectl get pods --namespace $namespace -o wide

if (-not $webRolledOut -or -not $workerRolledOut) {
    Write-Host ""
    Write-Host "A rollout did not finish. Look at the events first, then the logs:" -ForegroundColor Yellow
    Write-Host "  kubectl describe pod -l app=meepleledger-web -n $namespace"
    Write-Host "  kubectl get events -n $namespace --sort-by=.lastTimestamp"
    Write-Host "  kubectl logs deployment/meepleledger-web -n $namespace"
    exit 1
}

# 8. Wait for the public IP. Azure creates the load balancer rule and the IP after the Service
#    exists; until then EXTERNAL-IP shows <pending>.
Write-Host ""
Write-Host "Waiting for the Service's public IP (up to 5 minutes)..."
$ip = ""
for ($attempt = 1; $attempt -le 30; $attempt++) {
    $ip = kubectl get service meepleledger-web --namespace $namespace -o jsonpath="{.status.loadBalancer.ingress[0].ip}"
    if ($ip) {
        break
    }
    Write-Host "  attempt ${attempt}: <pending>"
    Start-Sleep -Seconds 10
}
if (-not $ip) {
    Write-Host "FAILED: no public IP yet. See: kubectl describe service meepleledger-web -n $namespace" -ForegroundColor Red
    exit 1
}

# 9. Ask the site for its home page, through the Service. A new load balancer rule can take a
#    minute to start passing traffic.
$url = "http://$ip"
Write-Host "Waiting for $url to answer..."
$status = 0
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        $response = Invoke-WebRequest -Uri $url -TimeoutSec 15 -SkipHttpErrorCheck
        $status = $response.StatusCode
    }
    catch {
        $status = 0
    }
    if ($status -eq 200) {
        break
    }
    Write-Host "  attempt ${attempt}: $status"
    Start-Sleep -Seconds 10
}

Write-Host ""
if ($status -eq 200) {
    Write-Host "Done. $url answers 200."
}
else {
    Write-Host "Done, but $url has not answered 200 yet. Check the Service has endpoints:" -ForegroundColor Yellow
    Write-Host "  kubectl get endpoints meepleledger-web -n $namespace"
}
Write-Host ""
Write-Host "Next:"
Write-Host "  kubectl get all -n $namespace                                      what is running"
Write-Host "  kubectl logs deployment/meepleledger-worker -n $namespace -f       the worker's log"
Write-Host "  kubectl scale deployment/meepleledger-web -n $namespace --replicas=3   then: kubectl get pods -n $namespace -w"
Write-Host "  pwsh infra/aks/break-aks.ps1 image|crash|ready|live                break it on purpose; fix puts it back"
Write-Host "  pwsh infra/aks/teardown-aks.ps1                                    delete the cluster when you are done"
