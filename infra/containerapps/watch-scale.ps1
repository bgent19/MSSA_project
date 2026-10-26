# A-27: watch the worker scale on queue depth, and time it.
#
# Every few seconds prints the time, the active messages on embed-requests and the replicas of one
# revision (default: the newest active one). Start it, then back the queue up in another terminal:
#   dotnet run --project MeepleLedger.Worker -- send-many 200
# It stops by itself once the queue has drained and the revision is back at zero replicas, and prints:
#   - scale-out delay: first message seen on the queue -> first replica
#   - scale-in delay:  queue empty -> zero replicas
# They are not the same. Scale-out waits for KEDA's next poll (up to 30 s) plus the replica starting.
# Scale-in waits for the cooldown (300 s of an empty queue) on top of that, on purpose: a queue that
# empties and refills a minute later should not pay for a cold start each time.
#
# The times are only as fine as one round of az calls (a few seconds), which is fine against
# 30-second polls and a 5-minute cooldown.
#
# Anything else reading embed-requests takes messages too and spoils the numbers: a local worker,
# watch-cosmos, or the A-22 function app meepleledger-func (checked below).
#
# Run from the repo root:  pwsh infra/containerapps/watch-scale.ps1 [-Revision <name>] [-IntervalSeconds 5] [-TimeoutMinutes 30]

param(
    [string]$Revision = "",
    [int]$IntervalSeconds = 5,
    [int]$TimeoutMinutes = 30
)

$resourceGroup = "rg-meepleledger"
$appName = "meepleledger-worker"          # from provision-containerapps.ps1
$namespaceName = "meepleledger-bus"       # from provision-servicebus.ps1
$queueName = "embed-requests"
$functionAppName = "meepleledger-func"    # from provision-functions.ps1 (A-22)

function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# Seconds between two times, as "95 s (1 min 35 s)"
function Format-Delay([datetime]$from, [datetime]$to) {
    $seconds = [int][math]::Round(($to - $from).TotalSeconds)
    $minutes = [math]::Floor($seconds / 60)
    $rest = $seconds % 60
    return "$seconds s ($minutes min $rest s)"
}

# 1. Which revision
if (-not $Revision) {
    $activeNames = az containerapp revision list --name $appName --resource-group $resourceGroup `
        --query "sort_by([?properties.active], &properties.createdTime)[].name" -o tsv
    Test-LastCommand "list revisions of $appName"
    $activeNames = @($activeNames)
    if ($activeNames.Count -eq 0) {
        Write-Host "FAILED: $appName has no active revision." -ForegroundColor Red
        exit 1
    }
    $Revision = $activeNames[$activeNames.Count - 1]
    if ($activeNames.Count -gt 1) {
        Write-Host "Warning: $($activeNames.Count) revisions are active ($($activeNames -join ', ')). They all take messages;" -ForegroundColor Yellow
        Write-Host "  keep one with: pwsh infra/containerapps/revisions.ps1 rollback $Revision" -ForegroundColor Yellow
    }
}

$minReplicas = az containerapp revision show --name $appName --resource-group $resourceGroup --revision $Revision --query properties.template.scale.minReplicas -o tsv
Test-LastCommand "read revision $Revision"
if ($minReplicas -ne "0") {
    Write-Host "Warning: $Revision has min replicas $minReplicas, so it can never scale to zero. Run scale-on-queue.ps1 first." -ForegroundColor Yellow
}

# 2. The function app also has a trigger on embed-requests unless it is disabled
$functionApp = az functionapp list --resource-group $resourceGroup --query "[?name=='$functionAppName'].name" -o tsv
if ($functionApp -eq $functionAppName) {
    $disabled = az functionapp config appsettings list --name $functionAppName --resource-group $resourceGroup `
        --query "[?name=='AzureWebJobs.EmbedRequest.Disabled'].value" -o tsv
    # A stop, not a warning: Flex Consumption scales out on the same queue and drains it in seconds
    # (A-27's first run lost 161 of 200 messages to it), so the numbers would mean nothing
    if ($disabled -ne "true" -and $disabled -ne "1") {
        Write-Host "FAILED: $functionAppName's EmbedRequest function is on and takes messages off $queueName too. Disable it for the test:" -ForegroundColor Red
        Write-Host "  az functionapp config appsettings set --name $functionAppName --resource-group $resourceGroup --settings AzureWebJobs.EmbedRequest.Disabled=true"
        Write-Host "and afterwards set it back to false (or delete the setting) to turn the function on again."
        exit 1
    }
}

# 3. Watch
Write-Host ""
Write-Host "Watching $Revision and $queueName every ${IntervalSeconds}s. Queue the work now in another terminal:"
Write-Host "  dotnet run --project MeepleLedger.Worker -- send-many 200"
Write-Host "Ctrl+C to stop early."
Write-Host ""
Write-Host "time       queued  replicas"

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$firstMessagesAt = $null      # first time the queue had messages
$firstReplicaAt = $null       # first replica after that
$peakReplicas = 0
$peakAt = $null
$queueEmptyAt = $null         # when the queue last became empty
$zeroReplicasAt = $null       # when the revision went back to zero after that
$lastLine = ""

while ((Get-Date) -lt $deadline) {
    $queued = az servicebus queue show --namespace-name $namespaceName --resource-group $resourceGroup --name $queueName `
        --query countDetails.activeMessageCount -o tsv
    Test-LastCommand "read the depth of $queueName"
    $queued = [int]$queued

    # The names, counted here. Not --query "length(@)": PowerShell drops the quotes from an argument
    # with no space in it, and az.cmd (a cmd.exe script) then reads the bare ")" as its own syntax.
    $replicaNames = az containerapp replica list --name $appName --resource-group $resourceGroup --revision $Revision --query "[].name" -o tsv
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("{0}  could not read the replicas, trying again" -f (Get-Date).ToString("HH:mm:ss")) -ForegroundColor Yellow
        Start-Sleep -Seconds $IntervalSeconds
        continue
    }
    $replicas = @($replicaNames | Where-Object { $_ }).Count

    $now = Get-Date

    if ($queued -gt 0 -and $null -eq $firstMessagesAt) {
        $firstMessagesAt = $now
    }
    if ($null -ne $firstMessagesAt -and $replicas -gt 0 -and $null -eq $firstReplicaAt) {
        $firstReplicaAt = $now
    }
    if ($replicas -gt $peakReplicas) {
        $peakReplicas = $replicas
        $peakAt = $now
    }

    # The queue counts as drained the first time it reads 0 after work was seen. If it fills again,
    # the clock starts over.
    if ($null -ne $firstMessagesAt) {
        if ($queued -eq 0 -and $null -eq $queueEmptyAt) {
            $queueEmptyAt = $now
        }
        if ($queued -gt 0) {
            $queueEmptyAt = $null
        }
    }
    if ($null -ne $queueEmptyAt -and $null -ne $firstReplicaAt -and $replicas -eq 0) {
        $zeroReplicasAt = $now
    }

    # Print a line when something changed, and at least every minute
    $line = "{0,6}  {1,8}" -f $queued, $replicas
    $note = ""
    if ($now -eq $firstMessagesAt) { $note = "  <- messages on the queue" }
    if ($now -eq $firstReplicaAt) { $note = "  <- first replica" }
    if ($now -eq $queueEmptyAt) { $note = "  <- queue empty, cooldown starts" }
    if ($now -eq $zeroReplicasAt) { $note = "  <- back to zero" }
    if ($line -ne $lastLine -or $note -ne "" -or $now.Second -lt $IntervalSeconds) {
        Write-Host ("{0}  {1}{2}" -f $now.ToString("HH:mm:ss"), $line, $note)
        $lastLine = $line
    }

    if ($null -ne $zeroReplicasAt) {
        break
    }
    Start-Sleep -Seconds $IntervalSeconds
}

# 4. Summary
Write-Host ""
if ($null -eq $firstMessagesAt) {
    Write-Host "No messages seen on $queueName in $TimeoutMinutes minutes."
    exit 0
}
Write-Host "Revision:        $Revision"
Write-Host "Messages seen:   $($firstMessagesAt.ToString('HH:mm:ss'))"
if ($null -ne $firstReplicaAt) {
    Write-Host "First replica:   $($firstReplicaAt.ToString('HH:mm:ss'))   scale-out delay $(Format-Delay $firstMessagesAt $firstReplicaAt)"
    Write-Host "Peak replicas:   $peakReplicas at $($peakAt.ToString('HH:mm:ss'))"
}
else {
    Write-Host "No replica started. Check the scale rule and the scaler's role:" -ForegroundColor Yellow
    Write-Host "  az containerapp logs show --name $appName --resource-group $resourceGroup --revision $Revision --type system"
}
if ($null -ne $queueEmptyAt) {
    Write-Host "Queue empty:     $($queueEmptyAt.ToString('HH:mm:ss'))   drained $(Format-Delay $firstMessagesAt $queueEmptyAt) after the first message"
}
if ($null -ne $zeroReplicasAt) {
    Write-Host "Zero replicas:   $($zeroReplicasAt.ToString('HH:mm:ss'))   scale-in delay $(Format-Delay $queueEmptyAt $zeroReplicasAt)"
}
else {
    Write-Host "Not back to zero within $TimeoutMinutes minutes."
}
Write-Host ""
Write-Host "In a few minutes, when the logs have reached the workspace:"
Write-Host "  pwsh infra/containerapps/check-duplicates.ps1"
