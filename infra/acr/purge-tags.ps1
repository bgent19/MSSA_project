# A-24: run the retention purge now, instead of waiting for Sunday's purge-old-tags task.
#
#   pwsh infra/acr/purge-tags.ps1           dry run: lists what would be deleted, deletes nothing
#   pwsh infra/acr/purge-tags.ps1 -Delete   deletes it
#
# "acr purge" is a command that runs inside ACR, not on this machine, so this is an az acr run of a
# single --cmd with no source context (/dev/null). The retention rule itself is explained in
# provision-acr.ps1; the command here must stay in step with $purgeCommand there.
#
# METERED: a few seconds of ACR Tasks time.
#
# Run from the repo root.

param(
    [switch]$Delete
)

$registryName = "meepleledgeracr"   # from provision-acr.ps1
$purgeCommand = "acr purge --filter 'meepleledger:.*' --filter 'meepleledger-worker:.*' --ago 7d --keep 5 --untagged"

if (-not $Delete) {
    $purgeCommand = "$purgeCommand --dry-run"
    Write-Host "Dry run. Nothing is deleted; pass -Delete to delete for real."
}

Write-Host "az acr run: $purgeCommand"
az acr run --registry $registryName --cmd $purgeCommand /dev/null
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: az acr run" -ForegroundColor Red
    exit 1
}
