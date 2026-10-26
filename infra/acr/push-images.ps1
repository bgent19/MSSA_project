# A-24: build both images on this machine with Docker and push them to the registry.
#
# Each image gets two tags:
#   - a version: the short git commit hash, e.g. meepleledger:cd8b339. It never moves, so it says
#     exactly which code is in the image, and deploying an older one is a rollback.
#   - latest: moved to the newest push, for convenience only. "latest" is a name, not a version.
#     Don't deploy it: you could not tell which image is running, or go back to the one before.
#
# The script refuses to run with uncommitted changes, because then the commit hash would not
# describe what is in the image.
#
# Signs in with "az acr login": Docker gets a token for your Azure CLI identity, no admin account.
#
# METERED: the pushed layers count against the registry's storage. Needs Docker running and
# provision-acr.ps1 run first.
#
# Run from the repo root:  pwsh infra/acr/push-images.ps1

$registryName = "meepleledgeracr"   # from provision-acr.ps1

# Image name -> its Dockerfile. Both build from the repo root, so .dockerignore applies.
$images = [ordered]@{
    "meepleledger"        = "MeepleLedger/Dockerfile"
    "meepleledger-worker" = "MeepleLedger.Worker/Dockerfile"
}

# Stop on the first failed command instead of pushing half a release.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

$changes = git status --porcelain
if ($changes) {
    Write-Host "There are uncommitted changes. Commit them first, so the version tag names the code in the image:" -ForegroundColor Red
    Write-Host ($changes -join "`n")
    exit 1
}
$version = git rev-parse --short HEAD
Test-LastCommand "read the commit hash"

$loginServer = az acr show --name $registryName --query loginServer -o tsv
Test-LastCommand "find registry $registryName (run provision-acr.ps1 first)"

Write-Host "Signing Docker in to $loginServer..."
az acr login --name $registryName
Test-LastCommand "az acr login"

foreach ($imageName in $images.Keys) {
    $dockerfile = $images[$imageName]
    $versionTag = "${loginServer}/${imageName}:$version"
    $latestTag = "${loginServer}/${imageName}:latest"

    Write-Host ""
    Write-Host "Building $imageName from $dockerfile..."
    docker build -f $dockerfile -t $versionTag -t $latestTag .
    Test-LastCommand "build $imageName"

    # Both tags point at the same image, so the second push uploads no layers, only the tag.
    docker push $versionTag
    Test-LastCommand "push $versionTag"
    docker push $latestTag
    Test-LastCommand "push $latestTag"
}

Write-Host ""
Write-Host "Done. Pushed version $version and latest of each image. In the registry now:"
foreach ($imageName in $images.Keys) {
    Write-Host "  ${imageName}:"
    az acr repository show-tags --name $registryName --repository $imageName --orderby time_desc -o tsv
}
