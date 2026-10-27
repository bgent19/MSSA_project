# A-28: break the web app in AKS on purpose, one way at a time, then diagnose it with kubectl.
#
#   image  the Deployment asks for an image tag that does not exist  -> ImagePullBackOff
#   crash  the ConfigMap names a Provider that does not exist        -> CrashLoopBackOff
#   ready  the readiness probe asks for a page that does not exist   -> Running, but never READY
#   live   the liveness probe checks a port nothing listens on       -> killed and restarted, forever
#   fix    re-applies the manifests (deploy-aks.ps1), which puts every field back to what the files say
#
# Each one changes the Deployment's pod template (or the ConfigMap it reads, plus a restart), so
# Kubernetes starts a ROLLOUT: a new pod is created beside the old one, and the old one is only
# removed once the new one is ready.
#   image, crash, ready: the new pod never gets ready, so the OLD POD KEEPS SERVING the site. That is
#          the rolling update protecting you: the site stays up, the rollout is stuck, and
#          "kubectl get pods" shows two pods.
#   live:  the new pod DOES get ready (readiness is a different probe and still passes), so the
#          rollout finishes and the old pod is gone. Only then does liveness start killing the new
#          one, every minute and a half, and the site drops out while it restarts. A rollout cannot
#          catch a bad liveness probe; that is why liveness has to be the forgiving one.
# (kubectl rollout undo deployment/meepleledger-web also fixes the Deployment ones, by going back to
# the previous template. It does not undo a ConfigMap change.)
#
# Where the answer is, for each:
#   image: NOT in the logs. The container never started, so there are none. It is in the events:
#          kubectl describe pod, at the bottom ("Failed to pull image ... not found").
#   crash: in the logs of the container that died: kubectl logs <pod> --previous shows the
#          "Unknown SearchIndex:Provider" exception. describe shows Last State: Terminated, Exit Code.
#   ready: the pod is Running with 0 restarts, and the logs look healthy. describe shows
#          "Readiness probe failed: HTTP probe failed with statuscode: 404".
#   live:  the logs look perfectly healthy (the app started fine!), and the restart count climbs.
#          describe shows "Liveness probe failed: dial tcp ...:8081: connect: connection refused"
#          and "Container web failed liveness probe, will be restarted".
#
# Run from the repo root:  pwsh infra/aks/break-aks.ps1 image|crash|ready|live|fix [-Provider InMemory|Cosmos]
# (-Provider only matters for fix: pass the one you deployed with.)

param(
    [string]$Scenario = "",
    [string]$Provider = "InMemory"
)

$clusterName = "meepleledger-aks"
$namespace = "meepleledger"
$deployment = "meepleledger-web"
$configMap = "meepleledger-config"

# Stop on the first failed command.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# kubectl patch with the patch text in a temporary file. Passing JSON with quotes in it on the
# command line is fragile on Windows; a file is not.
function Invoke-KubectlPatch([string]$kind, [string]$name, [string]$patchType, [string]$patch) {
    $file = Join-Path ([System.IO.Path]::GetTempPath()) "meepleledger-patch-$([guid]::NewGuid()).json"
    Set-Content -Path $file -Value $patch -Encoding utf8NoBOM
    try {
        kubectl patch $kind $name --namespace $namespace --type $patchType --patch-file $file
        Test-LastCommand "patch $kind $name"
    }
    finally {
        Remove-Item $file -Force
    }
}

$scenarios = @("image", "crash", "ready", "live", "fix")
if ($scenarios -notcontains $Scenario) {
    Write-Host "Usage: pwsh infra/aks/break-aks.ps1 image|crash|ready|live|fix [-Provider InMemory|Cosmos]"
    exit 1
}

# Only ever touch the AKS cluster.
$context = kubectl config current-context
Test-LastCommand "read the current kubectl context"
if ($context -ne $clusterName) {
    Write-Host "FAILED: kubectl's current context is '$context', not '$clusterName'." -ForegroundColor Red
    exit 1
}

if ($Scenario -eq "fix") {
    Write-Host "Re-applying the manifests..."
    & (Join-Path $PSScriptRoot "deploy-aks.ps1") -Provider $Provider
    exit $LASTEXITCODE
}

if ($Scenario -eq "image") {
    # The registry is right and the identity can pull from it; the tag just is not there.
    $loginServer = "meepleledgeracr.azurecr.io"
    Write-Host "Setting the web image to a tag that does not exist..."
    kubectl set image "deployment/$deployment" "web=$loginServer/meepleledger:does-not-exist" --namespace $namespace
    Test-LastCommand "set image"
}

if ($Scenario -eq "crash") {
    # Program.cs throws at startup for an unknown Provider, so the process exits, Kubernetes starts it
    # again, it exits again, and the wait between tries doubles: CrashLoopBackOff.
    # Changing the ConfigMap alone does nothing to a running pod (environment variables are read at
    # start), so the restart is what makes a new pod read the bad value.
    Write-Host "Setting SearchIndex__Provider to Bogus in the ConfigMap, then restarting..."
    Invoke-KubectlPatch "configmap" $configMap "merge" '{"data":{"SearchIndex__Provider":"Bogus"}}'
    kubectl rollout restart "deployment/$deployment" --namespace $namespace
    Test-LastCommand "rollout restart"
}

if ($Scenario -eq "ready") {
    # The app is fine; the probe asks the wrong question. /not-here is a 404.
    Write-Host "Pointing the readiness probe at /not-here..."
    Invoke-KubectlPatch "deployment" $deployment "json" '[{"op":"replace","path":"/spec/template/spec/containers/0/readinessProbe/httpGet/path","value":"/not-here"}]'
}

if ($Scenario -eq "live") {
    # The app is fine; the liveness probe checks a port it does not listen on, so after
    # initialDelaySeconds plus three failed checks the kubelet kills it, and it starts all over.
    Write-Host "Pointing the liveness probe at port 8081..."
    Invoke-KubectlPatch "deployment" $deployment "json" '[{"op":"replace","path":"/spec/template/spec/containers/0/livenessProbe/tcpSocket/port","value":8081}]'
}

Write-Host ""
Write-Host "Broken: $Scenario. Give it a minute (live: about two), then diagnose. describe FIRST:" -ForegroundColor Yellow
Write-Host "  kubectl get pods -n $namespace                                     STATUS, READY and RESTARTS"
Write-Host "  kubectl describe pod -l app=$deployment -n $namespace               the Events at the bottom say why"
Write-Host "  kubectl get events -n $namespace --sort-by=.lastTimestamp           the same events, for the whole namespace"
Write-Host "  kubectl logs <pod> -n $namespace [--previous]                        what the container wrote (if it ever ran)"
Write-Host "  kubectl rollout status deployment/$deployment -n $namespace          stuck: the old pod is still serving"
Write-Host "  kubectl get endpoints $deployment -n $namespace                      which pod IPs the Service sends to"
Write-Host ""
Write-Host "Then put it back:  pwsh infra/aks/break-aks.ps1 fix"
