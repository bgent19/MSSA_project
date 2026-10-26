using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

// MeepleLedger.Worker (A-19): takes embed requests off the Service Bus queue "embed-requests",
// embeds the Game's current text with Azure OpenAI, and writes the vector (and the hash of the text
// it was made from) back to the Game's document in Cosmos DB. The Seeder's watch-cosmos puts the
// requests there when a Blurb is edited.
//
//   dotnet run --project MeepleLedger.Worker                              run the worker until Enter
//   dotnet run --project MeepleLedger.Worker -- send <game id>            queue one embed request by hand
//   dotnet run --project MeepleLedger.Worker -- send-bad [missing|json|version]   queue a poison message
//   dotnet run --project MeepleLedger.Worker -- dlq                       list the dead-letter queue
//   dotnet run --project MeepleLedger.Worker -- dlq resubmit              put dead-lettered requests back on the queue
//   dotnet run --project MeepleLedger.Worker -- dlq purge                 delete everything in the dead-letter queue
//
// Every secret (ServiceBus:ConnectionString, AzureOpenAI:*, Cosmos:*) comes from the user secrets
// the Seeder and the web app share, never from a settings file. A-29 moves them to Key Vault.
// In a container (A-23) there are no user secrets, so environment variables are read too, with a
// double underscore for the colon: ServiceBus__ConnectionString. They win over user secrets.

IConfiguration config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .Build();

string? busConnectionString = config["ServiceBus:ConnectionString"];
if (string.IsNullOrWhiteSpace(busConnectionString))
{
    Console.WriteLine("Missing setting ServiceBus:ConnectionString (pwsh infra/servicebus/provision-servicebus.ps1 sets it as a user secret;");
    Console.WriteLine("in a container set the environment variable ServiceBus__ConnectionString).");
    return 1;
}

string command = "";
if (args.Length > 0)
{
    command = args[0];
}

// One client for the whole run. Senders, receivers and the processor all share its connection.
await using ServiceBusClient busClient = new ServiceBusClient(busConnectionString);

if (command == "send")
{
    if (args.Length < 2)
    {
        PrintUsage();
        return 1;
    }
    return await SendRequest(busClient, args[1]);
}

if (command == "send-bad")
{
    string kind = "missing";
    if (args.Length > 1)
    {
        kind = args[1];
    }
    return await SendBadRequest(busClient, kind);
}

if (command == "dlq")
{
    string action = "list";
    if (args.Length > 1)
    {
        action = args[1];
    }

    if (action == "list")
    {
        return await ListDeadLetters(busClient);
    }
    if (action == "resubmit")
    {
        return await ResubmitDeadLetters(busClient);
    }
    if (action == "purge")
    {
        return await PurgeDeadLetters(busClient);
    }

    PrintUsage();
    return 1;
}

if (command != "")
{
    PrintUsage();
    return 1;
}

return await RunWorker(config, busClient);


static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project MeepleLedger.Worker                               run the worker until Enter");
    Console.WriteLine("  dotnet run --project MeepleLedger.Worker -- send <game id>             queue one embed request");
    Console.WriteLine("  dotnet run --project MeepleLedger.Worker -- send-bad [missing|json|version]");
    Console.WriteLine("  dotnet run --project MeepleLedger.Worker -- dlq [list|resubmit|purge]");
}

// Runs a Service Bus processor on the queue until Enter is pressed. Each message ends in exactly
// one of three ways, and the handler says which itself:
//   - Complete:    the vector is written (or was already up to date). The message is gone.
//   - Abandon:     something failed that might work next time. The message goes back on the queue
//                  and its delivery count goes up. After the 5th delivery (the queue's max delivery
//                  count) Service Bus moves it to the dead-letter queue: MaxDeliveryCountExceeded.
//   - Dead-letter: the message itself is wrong (not JSON, an unknown schema version). Retrying
//                  cannot fix that, so it goes to the dead-letter queue now, with a reason.
static async Task<int> RunWorker(IConfiguration config, ServiceBusClient busClient)
{
    string? openAiEndpoint = config["AzureOpenAI:Endpoint"];
    string? openAiKey = config["AzureOpenAI:Key"];
    string? deployment = config["AzureOpenAI:EmbeddingDeployment"];

    if (string.IsNullOrWhiteSpace(openAiEndpoint) || string.IsNullOrWhiteSpace(openAiKey) || string.IsNullOrWhiteSpace(deployment))
    {
        Console.WriteLine("Missing settings. Set AzureOpenAI:Endpoint, AzureOpenAI:Key and AzureOpenAI:EmbeddingDeployment");
        Console.WriteLine("(pwsh infra/openai/provision-openai.ps1 sets all three as user secrets; in a container use AzureOpenAI__Endpoint and so on).");
        return 1;
    }

    string? cosmosEndpoint = config["Cosmos:Endpoint"];
    string? cosmosKey = config["Cosmos:Key"];
    string? databaseName = config["Cosmos:Database"];
    string? containerName = config["Cosmos:Container"];

    if (string.IsNullOrWhiteSpace(cosmosEndpoint) || string.IsNullOrWhiteSpace(cosmosKey)
        || string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(containerName))
    {
        Console.WriteLine("Missing settings. Set Cosmos:Endpoint, Cosmos:Key, Cosmos:Database and Cosmos:Container");
        Console.WriteLine("(pwsh infra/cosmos/provision-cosmos.ps1 sets all four as user secrets; in a container use Cosmos__Endpoint and so on).");
        return 1;
    }

    // The deployment name goes in the URL, so the worker never refers to the model id directly
    string embeddingsUrl = openAiEndpoint.TrimEnd('/') + "/openai/deployments/" + deployment + "/embeddings?api-version=2024-10-21";

    HttpClient http = new HttpClient();
    http.DefaultRequestHeaders.Add("api-key", openAiKey);

    // camelCase, the same as upload-cosmos, so the documents read back into CosmosGameDocument
    CosmosClientOptions cosmosOptions = new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        },
    };

    using CosmosClient cosmos = new CosmosClient(cosmosEndpoint, cosmosKey, cosmosOptions);
    Container games = cosmos.GetContainer(databaseName, containerName);

    // Dead-lettered messages never expire and count against the queue's size quota, so say so
    // every time the worker starts rather than letting them pile up unseen
    int deadLetterCount = await CountDeadLetters(busClient);
    if (deadLetterCount > 0)
    {
        Console.WriteLine($"{deadLetterCount} message(s) are waiting in the dead-letter queue. They never expire.");
        Console.WriteLine("  See them with: dotnet run --project MeepleLedger.Worker -- dlq");
    }

    // Up to 4 messages at once by default. Each is one small embeddings call and one Cosmos write.
    // The deployment allows 20K tokens a minute, so many more at once would only meet 429s.
    // Worker:MaxConcurrentCalls overrides it (A-26 changes it to make a second Container Apps revision).
    int maxConcurrentCalls = 4;
    string? maxConcurrentCallsSetting = config["Worker:MaxConcurrentCalls"];
    if (!string.IsNullOrWhiteSpace(maxConcurrentCallsSetting))
    {
        if (!int.TryParse(maxConcurrentCallsSetting, out maxConcurrentCalls) || maxConcurrentCalls < 1)
        {
            Console.WriteLine($"Worker:MaxConcurrentCalls must be a whole number of 1 or more, not '{maxConcurrentCallsSetting}'.");
            return 1;
        }
    }

    // Container Apps sets CONTAINER_APP_REVISION in every replica (A-26). With two revisions reading
    // the same queue, it goes on every log line so the logs show which revision handled a message.
    // Empty when run locally or in docker compose.
    string revision = config["CONTAINER_APP_REVISION"] ?? "";
    string revisionLabel = "";
    if (revision != "")
    {
        revisionLabel = $" [{revision}]";
    }

    ServiceBusProcessorOptions processorOptions = new ServiceBusProcessorOptions
    {
        // PeekLock (the default, spelled out): a received message is locked, not removed, until
        // the handler settles it. ReceiveAndDelete would lose the request if the worker crashed.
        ReceiveMode = ServiceBusReceiveMode.PeekLock,

        MaxConcurrentCalls = maxConcurrentCalls,

        // The handler settles every message itself (complete, abandon or dead-letter), so each
        // outcome is a line of code you can point at, not something that happens after it returns.
        AutoCompleteMessages = false,

        // The queue's lock is 1 minute and starts at receive. The processor renews it in the
        // background for up to 5 minutes, so a slow embeddings call (with its 429 back-off) does not
        // lose the lock and get the message handed to a second worker while this one is still on it.
        MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
    };

    async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        ServiceBusReceivedMessage message = args.Message;
        string label = $"[{DateTime.Now:HH:mm:ss}]{revisionLabel} #{message.SequenceNumber} (delivery {message.DeliveryCount})";

        // 1. Read the body. A body that cannot be read now will not be readable on the 5th try
        //    either, so dead-letter it straight away with a reason. Throwing would only abandon
        //    it, and it would come back 4 more times first.
        EmbedRequest? request = null;
        string? badPayload = null;
        try
        {
            request = JsonSerializer.Deserialize<EmbedRequest>(message.Body.ToString(), EmbedRequest.JsonOptions);
        }
        catch (JsonException ex)
        {
            badPayload = "The body is not an EmbedRequest in JSON: " + ex.Message;
        }

        if (badPayload == null && request == null)
        {
            badPayload = "The body is empty or null.";
        }

        if (badPayload != null)
        {
            await args.DeadLetterMessageAsync(message, "BadPayload", badPayload, args.CancellationToken);
            Console.WriteLine($"{label} dead-lettered: BadPayload. {badPayload}");
            return;
        }

        // 2. The schema version. This worker reads version 1. A missing field reads as 0.
        if (request!.SchemaVersion != EmbedRequest.CurrentSchemaVersion)
        {
            string description = $"This worker reads schema version {EmbedRequest.CurrentSchemaVersion}; the message is version {request.SchemaVersion} (0 means the field is missing).";
            await args.DeadLetterMessageAsync(message, "UnsupportedSchemaVersion", description, args.CancellationToken);
            Console.WriteLine($"{label} dead-lettered: UnsupportedSchemaVersion. {description}");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.GameId))
        {
            string description = "The request has no gameId.";
            await args.DeadLetterMessageAsync(message, "BadPayload", description, args.CancellationToken);
            Console.WriteLine($"{label} dead-lettered: BadPayload. {description}");
            return;
        }

        // 3. Do the work. Anything that fails from here on might work next time: a throttled or
        //    failed embeddings call, Cosmos being busy, the Game edited between our read and our
        //    write. So it is abandoned, and Service Bus dead-letters it after the 5th delivery.
        //    A Game that is not in Cosmos at all fails the same way (see EmbedGameAsync).
        try
        {
            string outcome = await EmbedGameAsync(request.GameId, args.CancellationToken);
            await args.CompleteMessageAsync(message, args.CancellationToken);
            Console.WriteLine($"{label} '{request.GameId}': {outcome}. Completed.");
        }
        catch (Exception ex)
        {
            string error = ShortError(ex);
            Console.WriteLine($"{label} '{request.GameId}': failed: {error}");

            // Abandon puts the message straight back, so without a pause a short outage would use
            // up all 5 deliveries in a second. The lock is still held (and renewed) while we wait.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), args.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The worker is stopping: abandon now rather than after the pause
            }

            // The last error is saved on the message itself, so the dead-letter reader can show
            // why it kept failing (Service Bus's own reason just says MaxDeliveryCountExceeded)
            Dictionary<string, object> properties = new Dictionary<string, object>();
            properties["lastError"] = error;
            await args.AbandonMessageAsync(message, properties);
            Console.WriteLine($"{label} abandoned, back on the queue");
        }
    }

    // Embeds one Game and writes the vector back. Returns what happened, for the log line.
    // Safe to run twice for the same Game: Service Bus delivers at least once, so duplicates are
    // normal, and the second run finds the vector up to date and pays for nothing.
    async Task<string> EmbedGameAsync(string gameId, CancellationToken cancellationToken)
    {
        // Read the Game as it is now, rather than trusting the request: the Blurb may have been
        // edited again since this was queued, and the newest text is the one worth embedding.
        // A Game that is not there throws a 404, which is abandoned like any other failure. The
        // change feed only queues Games it has just read, so a 404 is unexpected, and retrying it
        // is cheap (no embeddings call happens).
        ItemResponse<CosmosGameDocument> read =
            await games.ReadItemAsync<CosmosGameDocument>(gameId, new PartitionKey(gameId), cancellationToken: cancellationToken);
        CosmosGameDocument game = read.Resource;

        if (string.IsNullOrWhiteSpace(game.Blurb))
        {
            return "no Blurb, nothing to embed";
        }

        string text = BuildEmbeddingText(game.Name, game.Categories, game.Mechanics, game.Blurb);
        string textHash = HashText(text);

        if (textHash == game.EmbeddingTextHash)
        {
            return "vector already up to date, nothing to do";
        }

        float[] vector = await EmbedTextAsync(http, embeddingsUrl, text, cancellationToken);

        // Write only the two fields that change, and the hash WITH the vector: the change feed sees
        // this write too, and only the matching hash stops it queueing another embed (A-10).
        List<PatchOperation> operations = new List<PatchOperation>();
        operations.Add(PatchOperation.Set("/embedding", vector));
        operations.Add(PatchOperation.Set("/embeddingTextHash", textHash));

        // Only if the document is still the version we read. If someone edited the Game in between,
        // Cosmos refuses with 412, the message is abandoned, and the next delivery reads the new
        // text. A whole-document upsert here would put the old Blurb back over their edit.
        PatchItemRequestOptions patchOptions = new PatchItemRequestOptions
        {
            IfMatchEtag = read.ETag,
            EnableContentResponseOnWrite = false,
        };

        ItemResponse<CosmosGameDocument> written = await games.PatchItemAsync<CosmosGameDocument>(
            gameId, new PartitionKey(gameId), operations, patchOptions, cancellationToken);

        double charge = read.RequestCharge + written.RequestCharge;
        double? similarity = CosineSimilarity(game.Embedding, vector);
        if (similarity == null)
        {
            return $"embedded (no old vector), {charge:0.00} RU";
        }
        return $"re-embedded, similarity to the old vector {similarity:0.0000}, {charge:0.00} RU";
    }

    // Errors outside the message handler: a dropped connection, a lock that could not be renewed.
    // The processor keeps running and reconnects by itself, so this only logs.
    Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]{revisionLabel} processor error ({args.ErrorSource}): {ShortError(args.Exception)}");
        return Task.CompletedTask;
    }

    await using ServiceBusProcessor processor = busClient.CreateProcessor(EmbedRequest.QueueName, processorOptions);
    processor.ProcessMessageAsync += HandleMessageAsync;
    processor.ProcessErrorAsync += HandleErrorAsync;

    await processor.StartProcessingAsync();

    Console.WriteLine($"Worker listening on queue '{EmbedRequest.QueueName}' ({processorOptions.MaxConcurrentCalls} at a time).");
    if (revision != "")
    {
        Console.WriteLine($"Container Apps revision: {revision}");
    }
    Console.WriteLine($"Vectors are written back to {databaseName}/{containerName}.");

    // A container has no keyboard: stdin is empty, so ReadLine would return at once and the worker
    // would stop as soon as it started. There it waits for docker stop (SIGTERM) or Ctrl+C instead.
    if (Console.IsInputRedirected)
    {
        Console.WriteLine("No console input (running in a container?). Stop with docker stop or Ctrl+C.");
        await WaitForStopSignal();
    }
    else
    {
        Console.WriteLine("Press Enter to stop.");
        Console.ReadLine();
    }

    // Waits for messages already being handled to finish and settle before returning
    await processor.StopProcessingAsync();
    Console.WriteLine("Stopped.");
    return 0;
}

// Finishes when the process is asked to stop: SIGTERM (docker stop) or SIGINT (Ctrl+C).
// Cancelling the signal stops .NET from ending the process straight away, so the caller still gets
// to stop the processor and let messages in flight settle.
static async Task WaitForStopSignal()
{
    TaskCompletionSource stopRequested = new TaskCompletionSource();

    void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {context.Signal} received, stopping.");
        stopRequested.TrySetResult();
    }

    // The registrations only work while they are alive, so they are disposed after the wait
    using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
    using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

    await stopRequested.Task;
}

// Queues one embed request for a Game by hand, the same message watch-cosmos sends
static async Task<int> SendRequest(ServiceBusClient busClient, string gameId)
{
    EmbedRequest request = new EmbedRequest
    {
        SchemaVersion = EmbedRequest.CurrentSchemaVersion,
        GameId = gameId,
        TextHash = "",
        Reason = "manual",
        DetectedAtUtc = DateTime.UtcNow,
    };

    await using ServiceBusSender sender = busClient.CreateSender(EmbedRequest.QueueName);
    await sender.SendMessageAsync(BuildMessage(JsonSerializer.Serialize(request, EmbedRequest.JsonOptions), request.SchemaVersion));

    Console.WriteLine($"Queued an embed request for '{gameId}'.");
    return 0;
}

// Queues a poison message, to watch where it ends up:
//   missing  a good request for a Game that does not exist. Fails on every delivery, is abandoned
//            each time, and Service Bus dead-letters it after the 5th: MaxDeliveryCountExceeded.
//   json     a body that is not JSON. The worker dead-letters it at once: BadPayload.
//   version  a request with schema version 99. The worker dead-letters it at once: UnsupportedSchemaVersion.
static async Task<int> SendBadRequest(ServiceBusClient busClient, string kind)
{
    string body;
    int schemaVersion;

    if (kind == "missing")
    {
        EmbedRequest request = new EmbedRequest
        {
            SchemaVersion = EmbedRequest.CurrentSchemaVersion,
            GameId = "No Such Game " + DateTime.UtcNow.ToString("HHmmss"),
            TextHash = "",
            Reason = "test",
            DetectedAtUtc = DateTime.UtcNow,
        };
        body = JsonSerializer.Serialize(request, EmbedRequest.JsonOptions);
        schemaVersion = request.SchemaVersion;
    }
    else if (kind == "json")
    {
        body = "this is not JSON";
        schemaVersion = EmbedRequest.CurrentSchemaVersion;
    }
    else if (kind == "version")
    {
        body = "{\"schemaVersion\":99,\"gameId\":\"Catan\"}";
        schemaVersion = 99;
    }
    else
    {
        PrintUsage();
        return 1;
    }

    await using ServiceBusSender sender = busClient.CreateSender(EmbedRequest.QueueName);
    await sender.SendMessageAsync(BuildMessage(body, schemaVersion));

    Console.WriteLine($"Queued a bad message ({kind}): {body}");
    return 0;
}

// The same shape as the Seeder's ServiceBusEmbedRequestQueue sends. The schema version is in the
// body, and again as a property so it can be read (or filtered on) without parsing the body.
static ServiceBusMessage BuildMessage(string body, int schemaVersion)
{
    ServiceBusMessage message = new ServiceBusMessage(body);
    message.ContentType = "application/json";
    message.Subject = "EmbedRequest";
    message.ApplicationProperties["schemaVersion"] = schemaVersion;
    return message;
}

// Counts the dead-letter queue by peeking: peek reads without locking or removing anything.
// (The queue's runtime properties would give the count in one call, but need the Manage right,
// which the app's Send + Listen connection string does not have.)
static async Task<int> CountDeadLetters(ServiceBusClient busClient)
{
    await using ServiceBusReceiver receiver = CreateDeadLetterReceiver(busClient);

    int count = 0;
    while (true)
    {
        // Each peek carries on from where the last one stopped
        IReadOnlyList<ServiceBusReceivedMessage> page = await receiver.PeekMessagesAsync(maxMessages: 100);
        if (page.Count == 0)
        {
            break;
        }
        count += page.Count;
    }
    return count;
}

// Prints every message in the dead-letter queue with why it is there. Read-only.
static async Task<int> ListDeadLetters(ServiceBusClient busClient)
{
    await using ServiceBusReceiver receiver = CreateDeadLetterReceiver(busClient);

    int count = 0;
    while (true)
    {
        IReadOnlyList<ServiceBusReceivedMessage> page = await receiver.PeekMessagesAsync(maxMessages: 50);
        if (page.Count == 0)
        {
            break;
        }

        foreach (ServiceBusReceivedMessage message in page)
        {
            PrintDeadLetter(message);
            count++;
        }
    }

    Console.WriteLine($"{count} message(s) in {EmbedRequest.QueueName}/$deadletterqueue.");
    if (count > 0)
    {
        Console.WriteLine("Dead-lettered messages never expire and count against the queue's size quota.");
        Console.WriteLine("Fix the cause, then: dlq resubmit (back onto the queue) or dlq purge (delete them).");
    }
    return 0;
}

static void PrintDeadLetter(ServiceBusReceivedMessage message)
{
    string lastError = "(none)";
    if (message.ApplicationProperties.TryGetValue("lastError", out object? value) && value != null)
    {
        lastError = value.ToString()!;
    }

    string body = message.Body.ToString();
    if (body.Length > 200)
    {
        body = body.Substring(0, 200) + "...";
    }

    Console.WriteLine($"#{message.SequenceNumber}  enqueued {message.EnqueuedTime.LocalDateTime:dd MMM HH:mm:ss}, delivered {message.DeliveryCount} time(s)");
    Console.WriteLine($"  reason:      {message.DeadLetterReason}");
    Console.WriteLine($"  description: {message.DeadLetterErrorDescription}");
    Console.WriteLine($"  last error:  {lastError}");
    Console.WriteLine($"  body:        {body}");
    Console.WriteLine();
}

// Moves dead-lettered embed requests back onto the main queue, for after the cause is fixed.
// Messages the worker could not read at all (BadPayload, UnsupportedSchemaVersion) are left where
// they are: resubmitting them would only dead-letter them again.
static async Task<int> ResubmitDeadLetters(ServiceBusClient busClient)
{
    await using ServiceBusReceiver receiver = CreateDeadLetterReceiver(busClient);
    await using ServiceBusSender sender = busClient.CreateSender(EmbedRequest.QueueName);

    // Messages left behind stay locked by this receiver until the end, so the next receive does
    // not return them again. The set catches one that comes back anyway because its lock ran out.
    List<ServiceBusReceivedMessage> leftBehind = new List<ServiceBusReceivedMessage>();
    HashSet<long> seen = new HashSet<long>();
    int resubmitted = 0;

    while (true)
    {
        IReadOnlyList<ServiceBusReceivedMessage> batch =
            await receiver.ReceiveMessagesAsync(maxMessages: 20, maxWaitTime: TimeSpan.FromSeconds(5));
        if (batch.Count == 0)
        {
            break;
        }

        foreach (ServiceBusReceivedMessage message in batch)
        {
            if (seen.Contains(message.SequenceNumber))
            {
                continue;
            }
            seen.Add(message.SequenceNumber);

            if (message.DeadLetterReason == "BadPayload" || message.DeadLetterReason == "UnsupportedSchemaVersion")
            {
                Console.WriteLine($"#{message.SequenceNumber}: {message.DeadLetterReason}, left in the dead-letter queue (fix the sender, or dlq purge)");
                leftBehind.Add(message);
                continue;
            }

            // A copy of the message: same body, same properties, minus the dead-letter details
            ServiceBusMessage copy = new ServiceBusMessage(message);
            copy.ApplicationProperties.Remove("DeadLetterReason");
            copy.ApplicationProperties.Remove("DeadLetterErrorDescription");
            copy.ApplicationProperties.Remove("lastError");

            // Count resubmits, so a message going round and round is easy to spot in the list
            int resubmitCount = 0;
            if (message.ApplicationProperties.TryGetValue("resubmitCount", out object? value) && value != null)
            {
                resubmitCount = Convert.ToInt32(value);
            }
            copy.ApplicationProperties["resubmitCount"] = resubmitCount + 1;

            // Send first, then complete. A crash in between leaves the request in both places,
            // which is only a duplicate (the worker handles those). The other order could lose it.
            await sender.SendMessageAsync(copy);
            await receiver.CompleteMessageAsync(message);

            Console.WriteLine($"#{message.SequenceNumber}: {message.DeadLetterReason}, resubmitted (resubmit {resubmitCount + 1})");
            resubmitted++;
        }
    }

    // Hand the left-behind messages back to the dead-letter queue straight away rather than
    // waiting for their locks to run out
    foreach (ServiceBusReceivedMessage message in leftBehind)
    {
        try
        {
            await receiver.AbandonMessageAsync(message);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageLockLost)
        {
            // Its lock already ran out, so it is back in the dead-letter queue anyway
        }
    }

    Console.WriteLine($"{resubmitted} resubmitted, {leftBehind.Count} left in the dead-letter queue.");
    return 0;
}

// Deletes every message in the dead-letter queue, after asking
static async Task<int> PurgeDeadLetters(ServiceBusClient busClient)
{
    int count = await CountDeadLetters(busClient);
    if (count == 0)
    {
        Console.WriteLine("The dead-letter queue is empty.");
        return 0;
    }

    Console.Write($"Delete all {count} message(s) in the dead-letter queue? [y/N] ");
    string? answer = Console.ReadLine();
    if (answer == null || answer.Trim().ToLowerInvariant() != "y")
    {
        Console.WriteLine("Nothing deleted.");
        return 0;
    }

    await using ServiceBusReceiver receiver = CreateDeadLetterReceiver(busClient);

    int deleted = 0;
    while (true)
    {
        IReadOnlyList<ServiceBusReceivedMessage> batch =
            await receiver.ReceiveMessagesAsync(maxMessages: 50, maxWaitTime: TimeSpan.FromSeconds(5));
        if (batch.Count == 0)
        {
            break;
        }

        foreach (ServiceBusReceivedMessage message in batch)
        {
            // Completing a dead-lettered message is how it is deleted
            await receiver.CompleteMessageAsync(message);
            deleted++;
        }
    }

    Console.WriteLine($"Deleted {deleted} message(s).");
    return 0;
}

// The dead-letter queue is a sub-queue of the main one: embed-requests/$deadletterqueue
static ServiceBusReceiver CreateDeadLetterReceiver(ServiceBusClient busClient)
{
    ServiceBusReceiverOptions options = new ServiceBusReceiverOptions
    {
        SubQueue = SubQueue.DeadLetter,
        ReceiveMode = ServiceBusReceiveMode.PeekLock,
    };
    return busClient.CreateReceiver(EmbedRequest.QueueName, options);
}

// Embeds one text. On a 429 it waits and tries again, doubling the wait each time (or using
// Retry-After if the service sends it). Throws if it gives up, so the message is abandoned.
static async Task<float[]> EmbedTextAsync(HttpClient http, string url, string text, CancellationToken cancellationToken)
{
    int maxAttempts = 5;
    int waitSeconds = 2;

    string body = JsonSerializer.Serialize(new { input = text });

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        HttpResponseMessage response = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            // Response shape: { "data": [ { "embedding": [ ...floats... ] } ], "usage": { "total_tokens": n } }
            using JsonDocument json = JsonDocument.Parse(responseText);
            JsonElement embedding = json.RootElement.GetProperty("data")[0].GetProperty("embedding");

            float[] vector = new float[embedding.GetArrayLength()];
            int i = 0;
            foreach (JsonElement number in embedding.EnumerateArray())
            {
                vector[i] = number.GetSingle();
                i++;
            }
            return vector;
        }

        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException($"Embeddings call failed: {(int)response.StatusCode} {response.StatusCode}. {responseText}");
        }

        if (attempt == maxAttempts)
        {
            break;
        }

        int delay = waitSeconds;
        if (response.Headers.RetryAfter?.Delta != null)
        {
            delay = (int)Math.Ceiling(response.Headers.RetryAfter.Delta.Value.TotalSeconds);
        }

        Console.WriteLine($"  429 Too Many Requests, waiting {delay}s (attempt {attempt}/{maxAttempts})");
        await Task.Delay(delay * 1000, cancellationToken);
        waitSeconds *= 2;
    }

    throw new HttpRequestException($"Embeddings call still throttled (429) after {maxAttempts} attempts.");
}

// The text that gets embedded for one Game. Must stay exactly the same as BuildEmbeddingText in
// MeepleLedger.Seeder/Program.cs (and EmbeddingText.Build in MeepleLedger.Functions, A-21's copy
// of this worker): the hashes in Cosmos were made by the Seeder, and if this
// builds even one character differently, every vector this worker writes looks like an edit to
// the change feed, which queues another embed, forever.
// Lines end in "\r\n" whatever the OS (the hashes were first made on Windows, and AppendLine would
// use "\n" on Linux, where A-21's Function runs).
static string BuildEmbeddingText(string name, List<string> categories, List<string> mechanics, string blurb)
{
    StringBuilder text = new StringBuilder();
    text.Append("Name: " + name + "\r\n");
    text.Append("Categories: " + string.Join(", ", categories) + "\r\n");
    text.Append("Mechanics: " + string.Join(", ", mechanics) + "\r\n");
    text.Append("\r\n");
    text.Append(blurb);
    return text.ToString();
}

// Same as HashText in the Seeder: SHA-256 of the UTF-8 text, as upper-case hex
static string HashText(string text)
{
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
    return Convert.ToHexString(hash);
}

// How alike the old and new vectors are: 1 is identical. Null if there was no old vector.
static double? CosineSimilarity(float[] a, float[] b)
{
    if (a.Length == 0 || a.Length != b.Length)
    {
        return null;
    }

    double dot = 0;
    double lengthA = 0;
    double lengthB = 0;
    for (int i = 0; i < a.Length; i++)
    {
        dot += a[i] * b[i];
        lengthA += a[i] * a[i];
        lengthB += b[i] * b[i];
    }
    return dot / (Math.Sqrt(lengthA) * Math.Sqrt(lengthB));
}

// One line for the log and for the lastError property. Cosmos exception messages run to many
// lines of diagnostics, so those are cut down to the status code.
static string ShortError(Exception ex)
{
    string text;
    if (ex is CosmosException cosmosException)
    {
        text = $"Cosmos {(int)cosmosException.StatusCode} {cosmosException.StatusCode}";
    }
    else
    {
        text = ex.GetType().Name + ": " + ex.Message;
    }

    if (text.Length > 300)
    {
        text = text.Substring(0, 300) + "...";
    }
    return text;
}

// The intent to re-embed one Game (A-10). The same shape as EmbedRequest in the Seeder, which
// sends it; keep the two in step. SchemaVersion is there from the first message, so a later
// change to the shape can be told apart from this one.
class EmbedRequest
{
    // The queue provision-servicebus.ps1 creates. The Seeder's watch-cosmos sends to the same name.
    public const string QueueName = "embed-requests";

    public const int CurrentSchemaVersion = 1;

    // camelCase on the wire. Web defaults also read property names in any case.
    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public int SchemaVersion { get; set; }
    public string GameId { get; set; } = "";
    public string TextHash { get; set; } = "";     // the hash the change feed saw; the worker re-reads the Game anyway
    public string Reason { get; set; } = "";       // "new" or "changed" from the change feed, "manual" or "test" from this worker
    public DateTime DetectedAtUtc { get; set; }
}

// The parts of a Game's Cosmos document the worker reads. Serialized in camelCase, like the Seeder's
// CosmosGameDocument; properties not listed here are left alone by the patch.
class CosmosGameDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public List<string> Mechanics { get; set; } = [];
    public string? Blurb { get; set; }
    public float[] Embedding { get; set; } = [];
    public string? EmbeddingTextHash { get; set; }
}
