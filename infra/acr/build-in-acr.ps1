# A-24: build both images in Azure, by ACR, with no local Docker involved.
#
#   pwsh infra/acr/build-in-acr.ps1        az acr build, once per image
#   pwsh infra/acr/build-in-acr.ps1 -Run   az acr run of infra/acr/acr-task.yaml, both images at once
#
# Both upload this repo as the build context: the CLI packs the folder into a tarball and sends it
# to Azure. .dockerignore decides what is left out, so whatever it misses is transmitted. (The az
# CLI reads that file with a simpler matcher than Docker: see the note at the top of .dockerignore.)
#
# The difference between the two:
#   - az acr build is "docker build" in the cloud: one Dockerfile, one image (any number of tags),
#     pushed when it succeeds. Tagged here like push-images.ps1: the short commit hash, and latest.
#   - az acr run runs a task file (or a single --cmd) once: several steps, builds, pushes and plain
#     container runs, with dependencies between them. It is what a task does on a trigger, without
#     saving a task. acr-task.yaml builds both images side by side and pushes them together, tagged
#     with the run id ({{.Run.ID}}) and latest.
#
# Like push-images.ps1 this refuses to run with uncommitted changes: the upload is the working
# folder, so a version tag would not describe it otherwise.
#
# METERED: ACR Tasks bill per second of build time, and the images use registry storage.
#
# Run from the repo root.

param(
    [switch]$Run
)

$registryName = "meepleledgeracr"           # from provision-acr.ps1
$taskFile = "infra/acr/acr-task.yaml"

# Image name -> its Dockerfile, as in push-images.ps1
$images = [ordered]@{
    "meepleledger"        = "MeepleLedger/Dockerfile"
    "meepleledger-worker" = "MeepleLedger.Worker/Dockerfile"
}

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

$changes = git status --porcelain
if ($changes) {
    Write-Host "There are uncommitted changes. Commit them first, so the image is a commit:" -ForegroundColor Red
    Write-Host ($changes -join "`n")
    exit 1
}
$version = git rev-parse --short HEAD
Test-LastCommand "read the commit hash"

if ($Run) {
    # The task file is piped in on stdin ("--file -"). Given as a path, it would have to be inside
    # the uploaded context, and .dockerignore leaves infra/ out.
    Write-Host "az acr run: $taskFile, both images, uploading the repo at commit $version..."
    Get-Content $taskFile -Raw | az acr run --registry $registryName --file - .
    Test-LastCommand "az acr run"
    Write-Host ""
    Write-Host "Done. The version tag is the run id printed above (e.g. ca1). Look a run up later with:"
    Write-Host "  az acr task list-runs --registry $registryName -o table"
    exit 0
}

foreach ($imageName in $images.Keys) {
    $dockerfile = $images[$imageName]
    Write-Host ""
    Write-Host "az acr build: ${imageName}:$version and latest from $dockerfile..."
    # --image takes the name without the login server; ACR puts its own in front
    az acr build `
        --registry $registryName `
        --file $dockerfile `
        --image "${imageName}:$version" `
        --image "${imageName}:latest" `
        .
    Test-LastCommand "az acr build $imageName"
}

Write-Host ""
Write-Host "Done. Built and pushed version $version and latest of each image, with no local Docker."
foreach ($imageName in $images.Keys) {
    Write-Host "  ${imageName}:"
    az acr repository show-tags --name $registryName --repository $imageName --orderby time_desc -o tsv
}
