# A-18: provision Service Bus for the embedding pipeline.
#
# Creates (or reuses) the resource group, a Standard tier Service Bus namespace, and:
#   - a queue "embed-requests": work I want done (A-19's worker, A-21's function, A-27's KEDA rule)
#   - a topic "meeple-events" with two filtered subscriptions: something happened, and only the
#     subscribers that care about that kind of event get a copy
# then stores a Send + Listen connection string in the Seeder's user secrets. Safe to run again:
# anything that already exists is left alone, apart from the queue and subscription settings, which
# are re-applied so they always match this file.
#
# Check it in the portal afterwards: namespace > Queues > embed-requests > Service Bus Explorer.
# Send a message, then "Peek from start". Peek does not lock or remove the message.
#
# METERED. Standard tier has a small base charge for every hour the namespace exists (plus a charge
# per million operations, which this project will barely touch). It cannot be stopped, only deleted:
#   az servicebus namespace delete --name meepleledger-bus --resource-group rg-meepleledger
# A-33 tears it down: it lives in the same resource group as everything else, rg-meepleledger.
#
# Run from the repo root:  pwsh infra/servicebus/provision-servicebus.ps1

$resourceGroup = "rg-meepleledger"
$location = "eastus2"                 # the resource group's region, shared with A-04 and A-07
$namespaceName = "meepleledger-bus"   # must be unique across Azure: <name>.servicebus.windows.net

# Standard, not Basic. Basic has queues but no topics or subscriptions, and topics are half of the
# exam bullet. Premium is dedicated capacity at a much higher fixed price; nothing here needs it.
$sku = "Standard"

$queueName = "embed-requests"
$topicName = "meeple-events"
$authRuleName = "meepleledger-app"

# Queue settings, chosen on purpose. These are re-applied on every run.
#
# Max delivery count 5 (default 10). Every receive that ends without a Complete (a throw, a crash,
# a lock that runs out) counts as a delivery. After the 5th the message goes to the dead-letter
# queue with reason MaxDeliveryCountExceeded. A throttled embeddings call is already retried inside
# the HttpClient call, so a message that fails 5 whole attempts is a poison message, not bad luck,
# and 10 would only make it take twice as long to show up in the DLQ.
$maxDeliveryCount = 5
#
# Lock duration 1 minute (ISO 8601). The lock starts at receive, and while it is held no other
# receiver gets the message. One embed request is one embeddings call plus one write: a few seconds.
# One minute leaves room for a slow call, and is still short enough that a worker that crashes
# mid-message hands it to another worker within a minute. The cap is 5 minutes, so long work (an
# embedding batch) has to RENEW the lock (A-19's processor does this), not ask for a longer one.
$lockDuration = "PT1M"
#
# Message time to live 1 day (ISO 8601). An embed request nobody has handled in a day means the
# worker is down or broken. With dead-lettering on expiration the request is not lost: it moves to
# the dead-letter queue (reason TTLExpiredException), where A-19's DLQ reader can see it and
# resubmit it. Without that flag an expired message is silently dropped and the vector stays stale.
$messageTimeToLive = "P1D"

# The two subscriptions on meeple-events. Every event is sent with its name in the message Subject
# (shown as "Label" in the az CLI and in SQL filters as sys.Label), e.g. BlurbChanged, GameAdded,
# GameRemoved, PlayLogged.
#
# "embedding" only cares about Blurb changes, because a new Blurb means a new vector. It uses a
# CORRELATION filter: an exact match on one property, the cheapest filter Service Bus has.
$embeddingSubscription = "embedding"
$embeddingRuleName = "blurb-changed"
$embeddingLabel = "BlurbChanged"
#
# "recommendation-cache" cares about anything that changes which Games a Recommendation can contain
# (A-16's cache). It uses a SQL filter, which can match several values in one rule. PlayLogged is
# deliberately left out: a Play changes no Recommendation, and A-20 handles it over Event Grid.
$cacheSubscription = "recommendation-cache"
$cacheRuleName = "catalog-changed"
$cacheSqlFilter = "sys.Label IN ('BlurbChanged', 'GameAdded', 'GameRemoved')"

# Stop on the first failed az command instead of carrying on with half a setup.
function Test-LastCommand([string]$step) {
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $step" -ForegroundColor Red
        exit 1
    }
}

# Creates a subscription if it is missing, re-applies its settings, then makes sure it has exactly
# one rule: ours. A new subscription comes with a rule called $Default whose filter is 1=1 (match
# everything). Adding our rule does NOT replace it, and rules are OR'ed together, so leaving $Default
# behind turns a "filtered" subscription into a firehose that still looks like it works.
function Set-Subscription([string]$subscriptionName, [string]$ruleName, [string[]]$filterArguments) {
    $existing = az servicebus topic subscription list --namespace-name $namespaceName --resource-group $resourceGroup --topic-name $topicName --query "[?name=='$subscriptionName'].name" -o tsv
    if ($existing -eq $subscriptionName) {
        Write-Host "Updating subscription $subscriptionName..."
        az servicebus topic subscription update `
            --namespace-name $namespaceName `
            --resource-group $resourceGroup `
            --topic-name $topicName `
            --name $subscriptionName `
            --max-delivery-count $maxDeliveryCount `
            --lock-duration $lockDuration `
            --default-message-time-to-live $messageTimeToLive `
            --enable-dead-lettering-on-message-expiration true `
            --output none
        Test-LastCommand "update subscription $subscriptionName"
    }
    else {
        Write-Host "Creating subscription $subscriptionName..."
        az servicebus topic subscription create `
            --namespace-name $namespaceName `
            --resource-group $resourceGroup `
            --topic-name $topicName `
            --name $subscriptionName `
            --max-delivery-count $maxDeliveryCount `
            --lock-duration $lockDuration `
            --default-message-time-to-live $messageTimeToLive `
            --enable-dead-lettering-on-message-expiration true `
            --output none
        Test-LastCommand "create subscription $subscriptionName"
    }

    $ruleNames = az servicebus topic subscription rule list --namespace-name $namespaceName --resource-group $resourceGroup --topic-name $topicName --subscription-name $subscriptionName --query "[].name" -o tsv

    # Add ours first, then remove $Default, so the subscription is never left with no rule at all
    # (a subscription with no rules receives nothing).
    if ($ruleNames -contains $ruleName) {
        Write-Host "  Rule $ruleName already exists."
    }
    else {
        Write-Host "  Adding rule $ruleName..."
        az servicebus topic subscription rule create `
            --namespace-name $namespaceName `
            --resource-group $resourceGroup `
            --topic-name $topicName `
            --subscription-name $subscriptionName `
            --name $ruleName `
            @filterArguments `
            --output none
        Test-LastCommand "create rule $ruleName"
    }

    if ($ruleNames -contains '$Default') {
        Write-Host "  Removing the `$Default (1=1) rule..."
        az servicebus topic subscription rule delete `
            --namespace-name $namespaceName `
            --resource-group $resourceGroup `
            --topic-name $topicName `
            --subscription-name $subscriptionName `
            --name '$Default' `
            --output none
        Test-LastCommand "delete `$Default rule on $subscriptionName"
    }
}

# 1. A fresh subscription has to register the Service Bus provider once (same gotcha as A-04).
$providerState = az provider show --namespace Microsoft.ServiceBus --query registrationState -o tsv
if ($providerState -ne "Registered") {
    Write-Host "Registering Microsoft.ServiceBus provider (takes a minute or two)..."
    az provider register --namespace Microsoft.ServiceBus --wait
    Test-LastCommand "register Microsoft.ServiceBus"
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

# 3. Namespace. The name is global, so check it is free before trying, for a clearer error.
$existingNamespace = az servicebus namespace list --resource-group $resourceGroup --query "[?name=='$namespaceName'].name" -o tsv
if ($existingNamespace -eq $namespaceName) {
    Write-Host "Namespace $namespaceName already exists."
}
else {
    $nameAvailable = az servicebus namespace exists --name $namespaceName --query nameAvailable -o tsv
    if ($nameAvailable -ne "true") {
        Write-Host "FAILED: the namespace name $namespaceName is taken. Change `$namespaceName in this script." -ForegroundColor Red
        exit 1
    }
    Write-Host "Creating namespace $namespaceName in $location ($sku)..."
    az servicebus namespace create `
        --name $namespaceName `
        --resource-group $resourceGroup `
        --location $location `
        --sku $sku `
        --output none
    Test-LastCommand "create namespace"
}

# 4. The queue. Create it if missing, otherwise update it, so the settings above always win.
$queueArguments = @(
    "--namespace-name", $namespaceName,
    "--resource-group", $resourceGroup,
    "--name", $queueName,
    "--max-delivery-count", $maxDeliveryCount,
    "--lock-duration", $lockDuration,
    "--default-message-time-to-live", $messageTimeToLive,
    "--enable-dead-lettering-on-message-expiration", "true",
    "--output", "none"
)
$existingQueue = az servicebus queue list --namespace-name $namespaceName --resource-group $resourceGroup --query "[?name=='$queueName'].name" -o tsv
if ($existingQueue -eq $queueName) {
    Write-Host "Updating queue $queueName..."
    az servicebus queue update @queueArguments
    Test-LastCommand "update queue"
}
else {
    Write-Host "Creating queue $queueName..."
    az servicebus queue create @queueArguments
    Test-LastCommand "create queue"
}

# 5. The topic. Its time to live is the default for messages sent to it; each subscription keeps
#    its own copy and can only make that shorter.
$existingTopic = az servicebus topic list --namespace-name $namespaceName --resource-group $resourceGroup --query "[?name=='$topicName'].name" -o tsv
if ($existingTopic -eq $topicName) {
    Write-Host "Topic $topicName already exists."
}
else {
    Write-Host "Creating topic $topicName..."
    az servicebus topic create `
        --namespace-name $namespaceName `
        --resource-group $resourceGroup `
        --name $topicName `
        --default-message-time-to-live $messageTimeToLive `
        --output none
    Test-LastCommand "create topic"
}

# 6. The two subscriptions, each with its own filter and no $Default rule.
Set-Subscription $embeddingSubscription $embeddingRuleName @("--filter-type", "CorrelationFilter", "--label", $embeddingLabel)
Set-Subscription $cacheSubscription $cacheRuleName @("--filter-type", "SqlFilter", "--filter-sql-expression", $cacheSqlFilter)

# 7. Read back what Azure actually has, rather than trusting what was asked for. A queue or
#    subscription touched in the portal could have drifted.
$queue = az servicebus queue show --namespace-name $namespaceName --resource-group $resourceGroup --name $queueName --output json | ConvertFrom-Json
Test-LastCommand "read queue"
$actualSku = az servicebus namespace show --name $namespaceName --resource-group $resourceGroup --query sku.name -o tsv

$problems = @()
if ($actualSku -ne "Standard") {
    $problems += "namespace tier is $actualSku, not Standard"
}
if ($queue.maxDeliveryCount -ne $maxDeliveryCount) {
    $problems += "queue max delivery count is $($queue.maxDeliveryCount), not $maxDeliveryCount"
}
if ($queue.deadLetteringOnMessageExpiration -ne $true) {
    $problems += "queue does not dead-letter expired messages"
}
foreach ($subscriptionName in @($embeddingSubscription, $cacheSubscription)) {
    $ruleNames = az servicebus topic subscription rule list --namespace-name $namespaceName --resource-group $resourceGroup --topic-name $topicName --subscription-name $subscriptionName --query "[].name" -o tsv
    if ($ruleNames -contains '$Default') {
        $problems += "subscription $subscriptionName still has the `$Default (1=1) rule: it receives every event"
    }
}
foreach ($problem in $problems) {
    Write-Host "WARNING: $problem" -ForegroundColor Yellow
}

# 8. Connection string into the Seeder's user secrets, never into appsettings.json.
#    A namespace-level rule with Send and Listen only. The built-in RootManageSharedAccessKey also
#    has Manage (create and delete queues), which the app and worker never need.
$existingRule = az servicebus namespace authorization-rule list --namespace-name $namespaceName --resource-group $resourceGroup --query "[?name=='$authRuleName'].name" -o tsv
if ($existingRule -ne $authRuleName) {
    Write-Host "Creating authorization rule $authRuleName (Send, Listen)..."
    az servicebus namespace authorization-rule create `
        --namespace-name $namespaceName `
        --resource-group $resourceGroup `
        --name $authRuleName `
        --rights Send Listen `
        --output none
    Test-LastCommand "create authorization rule"
}
$connectionString = az servicebus namespace authorization-rule keys list --namespace-name $namespaceName --resource-group $resourceGroup --name $authRuleName --query primaryConnectionString -o tsv
Test-LastCommand "read connection string"
dotnet user-secrets set "ServiceBus:ConnectionString" $connectionString --project MeepleLedger.Seeder | Out-Null
Test-LastCommand "store connection string"

Write-Host ""
Write-Host "Done."
Write-Host "  Resource group:  $resourceGroup (note it for A-33)"
Write-Host "  Namespace:       $namespaceName.servicebus.windows.net in $location ($actualSku)"
Write-Host "  Queue:           $queueName"
Write-Host "    max delivery count $($queue.maxDeliveryCount), lock $($queue.lockDuration), TTL $($queue.defaultMessageTimeToLive)"
Write-Host "    dead-letter on expiration: $($queue.deadLetteringOnMessageExpiration)"
Write-Host "  Topic:           $topicName"
Write-Host "    $embeddingSubscription  <- correlation filter: Label = $embeddingLabel"
Write-Host "    $cacheSubscription  <- SQL filter: $cacheSqlFilter"
Write-Host "  User secrets:    ServiceBus:ConnectionString (${authRuleName}: Send, Listen) on MeepleLedger.Seeder"
Write-Host ""
Write-Host "Next, in the portal: $namespaceName > Queues > $queueName > Service Bus Explorer."
Write-Host "Send a message, then Peek from start. Try the topic too: send with Label BlurbChanged and"
Write-Host "with Label PlayLogged, and check which subscription each one lands in."
Write-Host ""
Write-Host "It bills every hour it exists. Delete it when you are done with the A-18 to A-22 work:"
Write-Host "  az servicebus namespace delete --name $namespaceName --resource-group $resourceGroup"
