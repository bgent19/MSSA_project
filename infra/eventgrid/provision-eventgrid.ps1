# A-20: provision Event Grid for PlayLogged.
#
# Creates (or reuses) the resource group, and:
#   - an Event Grid custom topic "meepleledger-plays" the web app publishes PlayLogged to
#   - a storage account "meepleledgerevents" with two queues (one per working subscriber) and a blob
#     container for dead-lettered events
#   - two event subscriptions with DIFFERENT filters, each delivering to its own storage queue:
#       recommendation-cache  event type + subject prefix          -> MeepleLedger.Events drops cached answers
#       not-owned-feed        event type + ADVANCED filter on data -> MeepleLedger.Events prints a line
#   - with -BrokenEndpoint, a third subscription "broken-webhook" pointed at a webhook that fails on
#     purpose, with few attempts, so its events are retried on a schedule and then dead-lettered
# then stores the topic endpoint and key, and the storage connection string, in the user secrets.
#
# Why storage queues for the two working subscribers, not webhooks: a webhook needs a public HTTPS
# URL that answers the validation handshake, and the subscribers here run on a laptop. A queue
# holds the events until MeepleLedger.Events reads them, so it can be stopped and started freely.
# The broken subscription IS a webhook, because watching each retry arrive is the point of it.
#
# The broken webhook, step by step (the endpoint must be running BEFORE this script, because Event
# Grid sends the validation handshake while the subscription is being created):
#   1. dotnet run --project MeepleLedger.Events -- webhook          (listens on http://localhost:5095)
#   2. devtunnel host -p 5095 --allow-anonymous                     (prints an https://... URL)
#      Install it once with: winget install Microsoft.devtunnel, then: devtunnel user login
#   3. pwsh infra/eventgrid/provision-eventgrid.ps1 -BrokenEndpoint https://<tunnel>/api/events
#
# METERED, but barely: Event Grid charges per million operations (the first 100,000 a month are
# free) and the storage account per GB stored. Neither has an hourly charge. A-33 deletes
# rg-meepleledger, which takes both with it.
#
# Run from the repo root:  pwsh infra/eventgrid/provision-eventgrid.ps1 [-BrokenEndpoint <https url>]

param(
    [string]$BrokenEndpoint = ""
)

$resourceGroup = "rg-meepleledger"
$location = "eastus2"                       # the resource group's region, shared with A-04, A-07, A-18
$topicName = "meepleledger-plays"           # endpoint is https://<name>.<region>-1.eventgrid.azure.net
$storageAccountName = "meepleledgerevents"  # must be unique across Azure, 3-24 lowercase letters and digits

# The app publishes exactly this type (PlayLoggedPublisher.EventType). The version is in the type,
# so a v2 with different data reaches no subscription until it is added here on purpose.
$eventType = "MeepleLedger.PlayLogged.v1"

$cacheQueueName = "play-logged-cache"
$feedQueueName = "play-logged-not-owned"
$deadLetterContainerName = "eventgrid-deadletter"

# Subscription 1: "recommendation-cache". Basic filters only: the event type and a subject prefix.
# Every PlayLogged subject starts "games/<name>/plays/<id>", so "games/" means "every Play"; a
# narrower prefix like "games/Wingspan/" would mean "Plays of one Game". Subject filtering is the
# cheapest filter and has no quota, so reach for it first.
$cacheSubscription = "recommendation-cache"
$cacheSubjectPrefix = "games/"

# Subscription 2: "not-owned-feed". An ADVANCED filter on a field inside the event's data: only Plays
# of Games you do not own. A subject prefix cannot express that, which is what earns the advanced
# filter here. Advanced filters are limited (25 per subscription, values of 512 characters at most)
# and each operator only works on its own value type: BoolEquals on a boolean, NumberIn on numbers,
# StringContains on strings. A filter whose type does not match the data silently matches nothing.
$feedSubscription = "not-owned-feed"
$feedAdvancedFilter = @("data.owned", "BoolEquals", "false")

# Subscription 3: "broken-webhook" (only with -BrokenEndpoint). The defaults are 30 attempts over up
# to 24 hours (1440 minutes), which is far too slow to watch. 5 attempts and a 30 minute time to live
# cut it down, but not to 30 minutes: Event Grid only checks the TTL when a retry falls due. On the
# first live run the attempts came at +0 s, +60 s and +11 min, the next one was due about an hour
# later, and that is when the events were dead-lettered (TimeToLiveExceeded, +76 min).
$brokenSubscription = "broken-webhook"
$brokenMaxAttempts = 5
$brokenEventTtlMinutes = 30

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# 1. A fresh subscription has to register the providers once (same gotcha as A-04).
foreach ($provider in @("Microsoft.EventGrid", "Microsoft.Storage")) {
    $providerState = az provider show --namespace $provider --query registrationState -o tsv
    if ($providerState -ne "Registered") {
        Write-Host "Registering $provider provider (takes a minute or two)..."
        az provider register --namespace $provider --wait
        Test-LastCommand "register $provider"
    }
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

# 3. Storage account. The name is global, so check it is free before trying, for a clearer error.
$existingAccount = az storage account list --resource-group $resourceGroup --query "[?name=='$storageAccountName'].name" -o tsv
if ($existingAccount -eq $storageAccountName) {
    Write-Host "Storage account $storageAccountName already exists."
}
else {
    $nameAvailable = az storage account check-name --name $storageAccountName --query nameAvailable -o tsv
    if ($nameAvailable -ne "true") {
        Write-Host "FAILED: the storage account name $storageAccountName is taken. Change `$storageAccountName in this script." -ForegroundColor Red
        exit 1
    }
    Write-Host "Creating storage account $storageAccountName in $location (Standard_LRS)..."
    az storage account create `
        --name $storageAccountName `
        --resource-group $resourceGroup `
        --location $location `
        --sku Standard_LRS `
        --kind StorageV2 `
        --min-tls-version TLS1_2 `
        --allow-blob-public-access false `
        --output none
    Test-LastCommand "create storage account"
}
$storageId = az storage account show --name $storageAccountName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "read storage account id"
$storageConnectionString = az storage account show-connection-string --name $storageAccountName --resource-group $resourceGroup --query connectionString -o tsv
Test-LastCommand "read storage connection string"

# 4. The two queues and the dead-letter container. Both commands do nothing if it already exists.
#    Without a dead-letter destination, an event that runs out of attempts is DROPPED, with no error
#    anywhere. The container is what makes a failed delivery something you can go and look at.
foreach ($queueName in @($cacheQueueName, $feedQueueName)) {
    Write-Host "Making sure queue $queueName exists..."
    az storage queue create --name $queueName --connection-string $storageConnectionString --output none
    Test-LastCommand "create queue $queueName"
}
Write-Host "Making sure blob container $deadLetterContainerName exists..."
az storage container create --name $deadLetterContainerName --connection-string $storageConnectionString --output none
Test-LastCommand "create container $deadLetterContainerName"

$deadLetterEndpoint = "$storageId/blobServices/default/containers/$deadLetterContainerName"

# 5. The custom topic, in the Event Grid schema (the default). That schema's webhook handshake is a
#    SubscriptionValidationEvent in the body, which MeepleLedger.Events answers.
$existingTopic = az eventgrid topic list --resource-group $resourceGroup --query "[?name=='$topicName'].name" -o tsv
if ($existingTopic -eq $topicName) {
    Write-Host "Topic $topicName already exists."
}
else {
    Write-Host "Creating Event Grid topic $topicName in $location..."
    az eventgrid topic create `
        --name $topicName `
        --resource-group $resourceGroup `
        --location $location `
        --input-schema eventgridschema `
        --output none
    Test-LastCommand "create topic"
}
$topicId = az eventgrid topic show --name $topicName --resource-group $resourceGroup --query id -o tsv
Test-LastCommand "read topic id"
$topicEndpoint = az eventgrid topic show --name $topicName --resource-group $resourceGroup --query endpoint -o tsv
$topicKey = az eventgrid topic key list --name $topicName --resource-group $resourceGroup --query key1 -o tsv
Test-LastCommand "read topic key"

# 6. The subscriptions. "event-subscription create" is a PUT (create or replace), so re-running this
#    script puts every filter and retry setting back to what this file says.
Write-Host "Creating or updating subscription $cacheSubscription (subject begins with '$cacheSubjectPrefix')..."
az eventgrid event-subscription create `
    --name $cacheSubscription `
    --source-resource-id $topicId `
    --endpoint-type storagequeue `
    --endpoint "$storageId/queueServices/default/queues/$cacheQueueName" `
    --included-event-types $eventType `
    --subject-begins-with $cacheSubjectPrefix `
    --deadletter-endpoint $deadLetterEndpoint `
    --output none
Test-LastCommand "create subscription $cacheSubscription"

Write-Host "Creating or updating subscription $feedSubscription (advanced filter $($feedAdvancedFilter -join ' '))..."
az eventgrid event-subscription create `
    --name $feedSubscription `
    --source-resource-id $topicId `
    --endpoint-type storagequeue `
    --endpoint "$storageId/queueServices/default/queues/$feedQueueName" `
    --included-event-types $eventType `
    --advanced-filter @feedAdvancedFilter `
    --deadletter-endpoint $deadLetterEndpoint `
    --output none
Test-LastCommand "create subscription $feedSubscription"

# The az CLI turns the BoolEquals value into a boolean the Python way: any non-empty string, "false"
# included, becomes true. So the create above stores "data.owned BoolEquals TRUE", the opposite of
# what was asked, and the subscription silently receives only Plays of Games you DO own. The generic
# --set parses its value as JSON, where false really is false, so the value is put right here.
az eventgrid event-subscription update `
    --name $feedSubscription `
    --source-resource-id $topicId `
    --set "filter.advancedFilters[0].value=false" `
    --output none
Test-LastCommand "fix the BoolEquals value on $feedSubscription"

if ($BrokenEndpoint -ne "") {
    # Event Grid sends the validation handshake now, while creating it. If the webhook is not
    # running, or the tunnel is down, this step fails: a webhook that never validates gets nothing.
    Write-Host "Creating or updating subscription $brokenSubscription -> $BrokenEndpoint ..."
    Write-Host "  (MeepleLedger.Events -- webhook and the tunnel must be running for the handshake)"
    az eventgrid event-subscription create `
        --name $brokenSubscription `
        --source-resource-id $topicId `
        --endpoint-type webhook `
        --endpoint $BrokenEndpoint `
        --included-event-types $eventType `
        --max-delivery-attempts $brokenMaxAttempts `
        --event-ttl $brokenEventTtlMinutes `
        --deadletter-endpoint $deadLetterEndpoint `
        --output none
    Test-LastCommand "create subscription $brokenSubscription"
}
else {
    $existingBroken = az eventgrid event-subscription list --source-resource-id $topicId --query "[?name=='$brokenSubscription'].name" -o tsv
    if ($existingBroken -eq $brokenSubscription) {
        Write-Host "Subscription $brokenSubscription exists and was left alone. Delete it when you are done with it:"
        Write-Host "  az eventgrid event-subscription delete --name $brokenSubscription --source-resource-id $topicId"
    }
    else {
        Write-Host "Skipping ${brokenSubscription}: no -BrokenEndpoint given."
    }
}

# 7. Read back what Azure actually has, rather than trusting what was asked for.
$subscriptions = az eventgrid event-subscription list --source-resource-id $topicId --output json | ConvertFrom-Json
Test-LastCommand "list subscriptions"
foreach ($subscription in $subscriptions) {
    if ($null -eq $subscription.deadLetterDestination) {
        Write-Host "WARNING: subscription $($subscription.name) has no dead-letter destination: failed events are dropped silently" -ForegroundColor Yellow
    }
    if ($subscription.name -eq $feedSubscription) {
        $ownedValue = $subscription.filter.advancedFilters[0].value
        if ($ownedValue -ne $false) {
            Write-Host "WARNING: $feedSubscription filters on data.owned BoolEquals $ownedValue, not false: it gets Plays of Games you own" -ForegroundColor Yellow
        }
    }
}

# 8. Secrets into the shared user secrets (same ID for the Seeder, the web app and MeepleLedger.Events),
#    never into appsettings.json. The topic key can publish anything to the topic; A-29 moves it to Key Vault.
dotnet user-secrets set "EventGrid:TopicEndpoint" $topicEndpoint --project MeepleLedger.Seeder | Out-Null
Test-LastCommand "store topic endpoint"
dotnet user-secrets set "EventGrid:TopicKey" $topicKey --project MeepleLedger.Seeder | Out-Null
Test-LastCommand "store topic key"
dotnet user-secrets set "EventStorage:ConnectionString" $storageConnectionString --project MeepleLedger.Seeder | Out-Null
Test-LastCommand "store storage connection string"

Write-Host ""
Write-Host "Done."
Write-Host "  Resource group:  $resourceGroup (note it for A-33)"
Write-Host "  Topic:           $topicName  $topicEndpoint"
Write-Host "  Storage:         $storageAccountName"
Write-Host "    queues $cacheQueueName, $feedQueueName; dead letters in container $deadLetterContainerName"
Write-Host "  Subscriptions:"
foreach ($subscription in $subscriptions) {
    $attempts = $subscription.retryPolicy.maxDeliveryAttempts
    $ttl = $subscription.retryPolicy.eventTimeToLiveInMinutes
    Write-Host "    $($subscription.name)  ($($subscription.destination.endpointType), $attempts attempts, TTL $ttl min)"
}
Write-Host "  User secrets:    EventGrid:TopicEndpoint, EventGrid:TopicKey, EventStorage:ConnectionString"
Write-Host ""
Write-Host "Next:"
Write-Host "  dotnet run --project MeepleLedger.Events                                  # the two subscribers"
Write-Host "  dotnet run --project MeepleLedger -- --EventGrid:Enabled=true --RecommendationCache:Enabled=true"
Write-Host "  then log a Play of a Game you do not own, and watch both subscribers react."
