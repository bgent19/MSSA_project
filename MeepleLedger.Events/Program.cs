using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using StackExchange.Redis;

// MeepleLedger.Events (A-20): the subscribers for the PlayLogged events the web app publishes to the
// Event Grid topic "meepleledger-plays". infra/eventgrid/provision-eventgrid.ps1 creates the topic,
// the storage account and the subscriptions.
//
//   dotnet run --project MeepleLedger.Events                         run both queue subscribers until Enter
//   dotnet run --project MeepleLedger.Events -- webhook [status]     a webhook that answers the handshake,
//                                                                     then fails every delivery (503 by default)
//   dotnet run --project MeepleLedger.Events -- dead-letters [purge] list (or delete) dead-lettered events
//
// The two subscribers, one storage queue each, filled by their own Event Grid subscription:
//   - play-logged-cache      (subject begins with "games/"): drops the cached Recommendations (A-16)
//                            that list the Game that was played
//   - play-logged-not-owned  (advanced filter data.owned = false): prints a line about a Game you
//                            played but do not own
//
// Event Grid delivers at least once and in no particular order. Both handlers are safe to run twice
// for the same event and do not care which Play arrives first: dropping cache entries that are
// already gone does nothing, and a printed line is just a printed line.
//
// Secrets (EventStorage:ConnectionString, Redis:ConnectionString) come from the user secrets the
// Seeder and the web app share, never from a settings file.

IConfiguration config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

string command = "";
if (args.Length > 0)
{
    command = args[0];
}

if (command == "webhook")
{
    int statusCode = 503;
    if (args.Length > 1 && !int.TryParse(args[1], out statusCode))
    {
        PrintUsage();
        return 1;
    }
    return await RunFailingWebhook(statusCode);
}

string? storageConnectionString = config["EventStorage:ConnectionString"];
if (string.IsNullOrWhiteSpace(storageConnectionString))
{
    Console.WriteLine("Missing user secret EventStorage:ConnectionString (pwsh infra/eventgrid/provision-eventgrid.ps1 sets it).");
    return 1;
}

if (command == "dead-letters")
{
    bool purge = args.Length > 1 && args[1] == "purge";
    return await ListDeadLetters(storageConnectionString, purge);
}

if (command != "")
{
    PrintUsage();
    return 1;
}

string? redisConnectionString = config["Redis:ConnectionString"];
if (string.IsNullOrWhiteSpace(redisConnectionString))
{
    Console.WriteLine("Missing user secret Redis:ConnectionString (pwsh infra/redis/provision-redis.ps1 sets it).");
    return 1;
}

return await RunSubscribers(storageConnectionString, redisConnectionString);


static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project MeepleLedger.Events                           run both subscribers until Enter");
    Console.WriteLine("  dotnet run --project MeepleLedger.Events -- webhook [status]       failing webhook on port " + Names.WebhookPort);
    Console.WriteLine("  dotnet run --project MeepleLedger.Events -- dead-letters [purge]");
}

// ---------------------------------------------------------------------------------------------
// The two subscribers
// ---------------------------------------------------------------------------------------------

// Polls both queues until Enter is pressed.
static async Task<int> RunSubscribers(string storageConnectionString, string redisConnectionString)
{
    Subscriber cache = new Subscriber("cache", storageConnectionString, Names.CacheQueue);
    Subscriber notOwned = new Subscriber("not owned", storageConnectionString, Names.NotOwnedQueue);

    // The same settings as the web app's cache connection (see MeepleLedger/Program.cs): start even
    // if Redis is down, and talk RESP2.
    ConfigurationOptions redisOptions = ConfigurationOptions.Parse(redisConnectionString);
    redisOptions.AbortOnConnectFail = false;
    redisOptions.Protocol = RedisProtocol.Resp2;
    redisOptions.ConnectTimeout = 5000;
    await using ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);

    // Without Redis the cache subscriber cannot do its job, so it does not read its queue at all.
    // Its events stay in play-logged-cache until it runs with Redis, rather than failing five times
    // each and ending up in the poison queue.
    bool readCacheQueue = redis.IsConnected;
    if (readCacheQueue)
    {
        Console.WriteLine("Listening on " + Names.CacheQueue + " and " + Names.NotOwnedQueue + ".");
    }
    else
    {
        Console.WriteLine("WARNING: Redis cannot be reached, so " + Names.CacheQueue + " is not read; its events wait there.");
        Console.WriteLine("Listening on " + Names.NotOwnedQueue + " only.");
    }
    Console.WriteLine("Log a Play in the app (with --EventGrid:Enabled=true). Press Enter to stop.");

    using CancellationTokenSource stop = new CancellationTokenSource();
    Task polling = PollQueues(cache, notOwned, readCacheQueue, redis, stop.Token);

    Console.ReadLine();
    stop.Cancel();
    await polling;

    Console.WriteLine("Stopped.");
    return 0;
}

static async Task PollQueues(Subscriber cache, Subscriber notOwned, bool readCacheQueue, IConnectionMultiplexer redis, CancellationToken stop)
{
    while (!stop.IsCancellationRequested)
    {
        int received = 0;

        try
        {
            if (readCacheQueue)
            {
                received += await HandleQueue(cache, redis);
            }
            received += await HandleQueue(notOwned, redis);
        }
        catch (RequestFailedException ex)
        {
            Console.WriteLine("Could not read the queues: " + ex.Message);
        }

        // Nothing waiting: look again in two seconds rather than hammer the queue.
        if (received == 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stop);
            }
            catch (OperationCanceledException)
            {
                // Enter was pressed.
            }
        }
    }
}

// Reads up to 16 messages from one queue, handles each, and deletes the ones that worked.
//
// A message that fails is left alone: it becomes visible again after 30 seconds and is tried again.
// After 5 tries it is moved to "<queue>-poison", so it cannot block the queue forever and the failed
// event ends up somewhere you can look at instead of being dropped. (The same idea as Event Grid's
// own dead-letter container, one step further along.)
static async Task<int> HandleQueue(Subscriber subscriber, IConnectionMultiplexer redis)
{
    QueueMessage[] messages = (await subscriber.Queue.ReceiveMessagesAsync(16, TimeSpan.FromSeconds(30))).Value;

    foreach (QueueMessage message in messages)
    {
        try
        {
            string body = ReadBody(message);
            List<JsonElement> events = ReadEvents(body);

            foreach (JsonElement gridEvent in events)
            {
                if (subscriber.Name == "cache")
                {
                    await HandleCacheEvent(gridEvent, redis);
                }
                else
                {
                    HandleNotOwnedEvent(gridEvent);
                }
            }

            await subscriber.Queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
        }
        catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is RedisException || ex is TimeoutException)
        {
            Console.WriteLine("[" + subscriber.Name + "] try " + message.DequeueCount + " failed: " + ex.Message);

            if (message.DequeueCount >= 5)
            {
                await subscriber.PoisonQueue.CreateIfNotExistsAsync();
                await subscriber.PoisonQueue.SendMessageAsync(message.Body);
                await subscriber.Queue.DeleteMessageAsync(message.MessageId, message.PopReceipt);
                Console.WriteLine("[" + subscriber.Name + "] gave up after 5 tries; moved it to " + subscriber.PoisonQueue.Name);
            }
        }
    }

    return messages.Length;
}

// Subscriber 1: drops the cached Recommendations that list the Game that was played.
//
// The cache (MeepleLedger/Storage/CachedGameSearchIndex.cs) writes each entry's key into a "tag" set
// per Game it lists. Those are the entries a Play of that Game affects, so those are the ones dropped.
// Every other cached answer is untouched. Today a Reason does not mention Plays, so these answers
// would come back the same; this is the hook for a Reason that does ("you last played it in May").
static async Task HandleCacheEvent(JsonElement gridEvent, IConnectionMultiplexer redis)
{
    if (!IsPlayLogged(gridEvent))
    {
        return;
    }

    JsonElement data = gridEvent.GetProperty("data");
    string game = GetText(data, "game");
    string playedOn = GetText(data, "playedOn");

    long dropped = await DropTaggedEntries(redis, Names.GameTagKeyPrefix + game);

    Console.WriteLine("[cache]     " + game + " played " + playedOn + ": dropped " + dropped + " cached answers that list it"
        + "  (event " + ShortId(gridEvent) + ", sent " + GetText(gridEvent, "eventTime") + ")");
}

// Subscriber 2: something trivial and visible. The advanced filter on its subscription means only
// Plays of Games you do not own reach this queue, so the handler does not check "owned" itself.
static void HandleNotOwnedEvent(JsonElement gridEvent)
{
    if (!IsPlayLogged(gridEvent))
    {
        return;
    }

    JsonElement data = gridEvent.GetProperty("data");
    string game = GetText(data, "game");
    string playedOn = GetText(data, "playedOn");

    Console.WriteLine("[not owned] You played " + game + " on " + playedOn + " and it isn't on your shelf. Wishlist it?"
        + "  (event " + ShortId(gridEvent) + ")");
}

// Deletes every cache entry whose key is in the tag set, then takes those keys out of the set.
// The same steps as CachedGameSearchIndex.DropTaggedEntriesAsync. DEL of several keys is split
// across the shards by the proxy, so it is safe on the EnterpriseCluster policy.
static async Task<long> DropTaggedEntries(IConnectionMultiplexer redis, string tagKey)
{
    IDatabase database = redis.GetDatabase();

    RedisValue[] members = await database.SetMembersAsync(tagKey);
    if (members.Length == 0)
    {
        return 0;
    }

    RedisKey[] entryKeys = new RedisKey[members.Length];
    for (int i = 0; i < members.Length; i++)
    {
        entryKeys[i] = members[i].ToString();
    }

    long deleted = await database.KeyDeleteAsync(entryKeys);
    await database.SetRemoveAsync(tagKey, members);
    return deleted;
}

// Only version 1 of PlayLogged is understood. The subscriptions already filter on this exact type,
// so anything else here means a subscription was changed by hand; it is skipped, not failed.
static bool IsPlayLogged(JsonElement gridEvent)
{
    string eventType = GetText(gridEvent, "eventType");
    if (eventType == Names.PlayLoggedEventType)
    {
        return true;
    }

    Console.WriteLine("Skipping an event of type '" + eventType + "'; only " + Names.PlayLoggedEventType + " is handled.");
    return false;
}

// Event Grid writes the event's JSON into the queue message as Base64 text (seen on the first live
// run). A message written by hand, for example in the portal, is often plain JSON, so both are accepted.
static string ReadBody(QueueMessage message)
{
    string text = message.Body.ToString().Trim();
    if (text.StartsWith('{') || text.StartsWith('['))
    {
        return text;
    }

    byte[] bytes = Convert.FromBase64String(text);
    return Encoding.UTF8.GetString(bytes);
}

// One event, or an array of them (a webhook always gets an array; a queue message holds one).
static List<JsonElement> ReadEvents(string json)
{
    // Clone, because the elements must outlive the JsonDocument they came from.
    using JsonDocument document = JsonDocument.Parse(json);
    List<JsonElement> events = [];

    if (document.RootElement.ValueKind == JsonValueKind.Array)
    {
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            events.Add(item.Clone());
        }
    }
    else
    {
        events.Add(document.RootElement.Clone());
    }

    return events;
}

// ---------------------------------------------------------------------------------------------
// The deliberately failing webhook
// ---------------------------------------------------------------------------------------------

// A webhook that does exactly one thing right: it answers the validation handshake, so Event Grid
// accepts the subscription. After that it fails every delivery with the given status code and prints
// when each attempt arrived, so the retry schedule can be watched as it happens.
//
//   503 (the default): Event Grid retries, waiting longer each time, until the subscription's
//        max delivery attempts or event time to live runs out, then dead-letters the event.
//   400: Event Grid does not retry a 400, 401, 403, 404 or 413 from a webhook (retrying cannot fix
//        them). The event goes straight to the dead-letter container after the first attempt.
//
// Expose it with a tunnel (devtunnel host -p 5095 --allow-anonymous) and pass the https URL plus
// /api/events to provision-eventgrid.ps1 -BrokenEndpoint.
static async Task<int> RunFailingWebhook(int statusCode)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://localhost:" + Names.WebhookPort);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    WebApplication app = builder.Build();

    // When each event was first seen, by event id, to show how long after the first try each retry came.
    Dictionary<string, DateTime> firstSeen = new Dictionary<string, DateTime>();
    object firstSeenLock = new object();

    app.MapPost("/api/events", async (HttpRequest request) =>
    {
        string body;
        using (StreamReader reader = new StreamReader(request.Body))
        {
            body = await reader.ReadToEndAsync();
        }

        List<JsonElement> events = ReadEvents(body);
        DateTime now = DateTime.Now;

        // The handshake. Event Grid sends it once, while the subscription is being created, and the
        // subscription is only created if the answer echoes the validation code back.
        if (request.Headers["aeg-event-type"] == "SubscriptionValidation")
        {
            JsonElement data = events[0].GetProperty("data");
            string validationCode = GetText(data, "validationCode");
            Console.WriteLine(now.ToString("HH:mm:ss") + "  handshake: answered validation code " + validationCode);
            return Results.Json(new { validationResponse = validationCode });
        }

        // Event Grid counts the deliveries it has already tried for this event.
        string deliveryCount = request.Headers["aeg-delivery-count"].ToString();

        foreach (JsonElement gridEvent in events)
        {
            string id = GetText(gridEvent, "id");
            DateTime first;
            lock (firstSeenLock)
            {
                if (!firstSeen.TryGetValue(id, out first))
                {
                    first = now;
                    firstSeen[id] = now;
                }
            }

            int secondsSinceFirst = (int)(now - first).TotalSeconds;
            string game = GetText(gridEvent.GetProperty("data"), "game");

            Console.WriteLine(now.ToString("HH:mm:ss") + "  attempt (aeg-delivery-count " + deliveryCount + ") for "
                + game + ", event " + ShortId(gridEvent) + ", +" + secondsSinceFirst + " s since the first try -> " + statusCode);
        }

        return Results.StatusCode(statusCode);
    });

    await app.StartAsync();
    Console.WriteLine("Failing webhook on http://localhost:" + Names.WebhookPort + "/api/events, answering " + statusCode + " to every delivery.");
    Console.WriteLine("Expose it with: devtunnel host -p " + Names.WebhookPort + " --allow-anonymous. Press Enter to stop.");

    Console.ReadLine();
    await app.StopAsync();
    return 0;
}

// ---------------------------------------------------------------------------------------------
// Dead letters
// ---------------------------------------------------------------------------------------------

// Event Grid writes each event that ran out of attempts (or got a non-retryable answer) to the
// dead-letter container as a JSON blob, under <TOPIC>/<SUBSCRIPTION>/yyyy/MM/dd/HH/. The event is
// wrapped with why and how often delivery failed. It can take up to about 5 minutes after the last
// attempt for the blob to appear.
static async Task<int> ListDeadLetters(string storageConnectionString, bool purge)
{
    BlobContainerClient container = new BlobContainerClient(storageConnectionString, Names.DeadLetterContainer);

    int count = 0;
    await foreach (BlobItem blob in container.GetBlobsAsync())
    {
        count++;
        Console.WriteLine(blob.Name);

        BlobDownloadResult download = (await container.GetBlobClient(blob.Name).DownloadContentAsync()).Value;
        List<JsonElement> events = ReadEvents(download.Content.ToString());

        foreach (JsonElement gridEvent in events)
        {
            Console.WriteLine("  " + GetText(gridEvent, "eventType") + "  subject " + GetText(gridEvent, "subject"));
            Console.WriteLine("    reason:        " + GetText(gridEvent, "deadLetterReason"));
            Console.WriteLine("    attempts:      " + GetText(gridEvent, "deliveryAttempts"));
            Console.WriteLine("    last outcome:  " + GetText(gridEvent, "lastDeliveryOutcome") + " (HTTP " + GetText(gridEvent, "lastHttpStatusCode") + ")");
            Console.WriteLine("    published:     " + GetText(gridEvent, "publishTime"));
            Console.WriteLine("    last attempt:  " + GetText(gridEvent, "lastDeliveryAttemptTime"));
        }

        if (purge)
        {
            await container.DeleteBlobAsync(blob.Name);
            Console.WriteLine("  deleted");
        }
    }

    if (count == 0)
    {
        Console.WriteLine("No dead-lettered events in " + Names.DeadLetterContainer + ".");
    }
    else
    {
        Console.WriteLine(count + " dead-letter blob(s)" + (purge ? ", all deleted." : "."));
    }

    return 0;
}

// ---------------------------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------------------------

// A property as text, or "" if it is missing. Numbers are returned as written.
static string GetText(JsonElement element, string name)
{
    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value))
    {
        return "";
    }

    if (value.ValueKind == JsonValueKind.String)
    {
        return value.GetString() ?? "";
    }

    return value.ToString();
}

static string ShortId(JsonElement gridEvent)
{
    string id = GetText(gridEvent, "id");
    if (id.Length > 8)
    {
        return id.Substring(0, 8);
    }
    return id;
}

// Names shared with other parts of the repo. This project does not reference them, so it keeps copies.
static class Names
{
    // What the web app publishes (PlayLoggedPublisher.EventType); keep the two in step.
    public const string PlayLoggedEventType = "MeepleLedger.PlayLogged.v1";

    // The queues and container provision-eventgrid.ps1 creates; keep the two in step.
    public const string CacheQueue = "play-logged-cache";
    public const string NotOwnedQueue = "play-logged-not-owned";
    public const string DeadLetterContainer = "eventgrid-deadletter";

    // CachedGameSearchIndex's tag set prefix (one set per Game); keep the two in step.
    public const string GameTagKeyPrefix = "tonight:tag:game:";

    public const int WebhookPort = 5095;
}

// One subscriber's queue, and where its messages go when they keep failing.
class Subscriber
{
    public string Name { get; }
    public QueueClient Queue { get; }
    public QueueClient PoisonQueue { get; }

    public Subscriber(string name, string storageConnectionString, string queueName)
    {
        Name = name;
        Queue = new QueueClient(storageConnectionString, queueName);
        PoisonQueue = new QueueClient(storageConnectionString, queueName + "-poison");
    }
}
