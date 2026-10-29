
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.Exporter;
using MeepleLedger.Data;
using MeepleLedger.Domain;
using MeepleLedger.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Pgvector;
using Pgvector.Npgsql;
using StackExchange.Redis;

// Where the command was run from, so a file path argument (import-ledger) still means what was typed
string startDirectory = Environment.CurrentDirectory;

// Set working directory to solution root
var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
while (currentDir.Name != "MSSA_project" && currentDir.Parent != null)
{
    currentDir = currentDir.Parent;
}
Environment.CurrentDirectory = currentDir.FullName;

if(args.FirstOrDefault() == "emit")
{
    EmitCatalog();
    EmitCollection();
    EmitLog();
    return 0;
}

if (args.FirstOrDefault() == "smoke-embed")
{
    return await SmokeEmbed();
}

if (args.FirstOrDefault() == "embed")
{
    return await Embed();
}

if (args.FirstOrDefault() == "upload-cosmos")
{
    return await UploadCosmos();
}

if (args.FirstOrDefault() == "seed-ledger")
{
    return await SeedLedger();
}

if (args.FirstOrDefault() == "import-ledger")
{
    // import-ledger <file> [scratch database]
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: import-ledger <export file> [scratch database]");
        return 1;
    }

    string? scratchDatabase = null;
    if (args.Length > 2)
    {
        scratchDatabase = args[2];
    }
    return await ImportLedger(Path.GetFullPath(args[1], startDirectory), scratchDatabase);
}

if (args.FirstOrDefault() == "measure-ru")
{
    return await MeasureRu();
}

if (args.FirstOrDefault() == "watch-cosmos")
{
    // Optional second argument: this host's instance name (defaults to the machine name)
    string instanceName = Environment.MachineName;
    if (args.Length > 1)
    {
        instanceName = args[1];
    }
    return await WatchCosmos(instanceName);
}

if (args.FirstOrDefault() == "setup-postgres")
{
    return await SetupPostgres();
}

if (args.FirstOrDefault() == "upload-postgres")
{
    return await UploadPostgres();
}

if (args.FirstOrDefault() == "measure-pgvector")
{
    return await MeasurePgvector();
}

if (args.FirstOrDefault() == "explain-postgres")
{
    return await ExplainPostgres();
}

if (args.FirstOrDefault() == "load-postgres")
{
    return await LoadPostgres();
}

if (args.FirstOrDefault() == "ping-redis")
{
    return await PingRedis();
}

if (args.FirstOrDefault() == "invalidate-cache")
{
    return await InvalidateCache();
}

if (args.FirstOrDefault() == "upload-redis")
{
    return await UploadRedis();
}

if (args.FirstOrDefault() == "compare-providers")
{
    return await CompareProviders();
}

string? username = Environment.GetEnvironmentVariable("BGG_USERNAME");
string? token = Environment.GetEnvironmentVariable("BGG_TOKEN");
string outputDir = "raw";
int targetGames = 200;
int batchSize = 20;

Directory.CreateDirectory(outputDir);

File.WriteAllText(Path.Combine(outputDir, ".gitignore"), $"*{Environment.NewLine}!.gitignore{Environment.NewLine}");

HttpClient http = new();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);


// get my collection
var collectionUrl = $"https://boardgamegeek.com/xmlapi2/collection?username={username}" +
                    "&own=1&stats=1&excludesubtype=boardgameexpansion";

string collectionResult;
while (true)
{
    var response = await http.GetAsync(collectionUrl);

    if (response.StatusCode == HttpStatusCode.Accepted) // 202, keep waiting
    {
        await Task.Delay(5000);
        continue;
    }

    if (response.StatusCode == HttpStatusCode.Unauthorized)
    {
        Console.WriteLine("401. Try again");
        return 1;
    }

    response.EnsureSuccessStatusCode(); // 200, ready to read
    collectionResult = await response.Content.ReadAsStringAsync();
    break;
}

File.WriteAllText(Path.Combine(outputDir, "collection-owned.xml"), collectionResult);

List<int> ownedIds = XDocument.Parse(collectionResult)
                              .Descendants("item")
                              .Select(item => int.Parse(item.Attribute("objectid")!.Value))
                              .Distinct()
                              .ToList();

Console.WriteLine($"Owned games: {ownedIds.Count}");

var lines = File.ReadAllLines("data/boardgames_ranks.csv", Encoding.UTF8);
var header = SplitCsvLine(lines[0]);

var idCol = Array.IndexOf(header, "id");
var rankCol = Array.IndexOf(header, "rank");
var expansionCol = Array.IndexOf(header, "is_expansion");

var rankdIds = new List<(int Id, int Rank)>();
int misaligned = 0;

foreach (var line in lines.Skip(1))
{
    var fields = SplitCsvLine(line);

    // Guard: a misaligned row reads the wrong columns, so skip it rather than guess.
    // With quoted commas handled this should fire on approximately nothing.
    if(fields.Length != header.Length)
    {
        misaligned++;
        continue;
    }

    //skip expansion
    if (fields[expansionCol] == "1")
    {
        continue;
    }

    if (!int.TryParse(fields[idCol], out var id))
    {
        continue;
    }

    if (!int.TryParse(fields[rankCol], out var rank))
    {
        continue;
    }

    // unranked games have rank 0
    // they need to be removed or else they will be the top of sorted order later
    if(rank <= 0)
    {
        continue;
    }

    // Add game to list if all od these checks pass
    rankdIds.Add((id, rank));
}

var rankedInOrder = rankdIds.OrderBy(r => r.Rank).Select(r => r.Id).ToList();
Console.WriteLine($"Ranked base games in CSV: {rankedInOrder.Count}");
// If this is not near zero, the split is still wrong
Console.WriteLine($"Rows skipped as misaligned: {misaligned}");

// Check my collection and add any games I own not in the Top
var allIds = new List<int>(ownedIds);

foreach(var id in rankedInOrder)
{
    if(allIds.Count >= targetGames)
    {
        break;
    }

    if(!allIds.Contains(id))
    {
        allIds.Add(id);
    }
}

var batchCount = (int)Math.Ceiling(allIds.Count / (double)batchSize);

Console.WriteLine();
Console.WriteLine($"Unique ids to fetch: {allIds.Count} (target {targetGames})");
Console.WriteLine($"That is {batchCount} calls to /thing, 5s apart.");
Console.Write("Continue? [y/N] ");

if (Console.ReadLine()?.Trim().ToLower() != "y")
{
    Console.WriteLine("Stopped. No API calls to /item made.");
    return 0;
}

// API calls in batchSize to /thing

for(int i = 0; i < batchCount; i++)
{
    var batch = allIds.Skip(i * batchSize).Take(batchSize);
    var path = Path.Combine(outputDir, $"thing-batch-{i + 1:D2}.xml");

    // See if file already exists
    if(File.Exists(path))
    {
        Console.WriteLine($"[{i + 1}/{batchCount}] already saved, skipping.");
        continue;
    }

    // throttle calls because I get errors if I dont
    if(i > 0)
    {
        await Task.Delay(5000); // 5s
    }

    Console.WriteLine($"[{i + 1}/{batchCount}] fetching...");

    var url = $"https://boardgamegeek.com/xmlapi2/thing?id={string.Join(',', batch)}&stats=1";
    var response = await http.GetAsync(url);

    // throttle response
    if((int)response.StatusCode >= 500)
    {
        Console.WriteLine($"Got a {(int)response.StatusCode}. That means you went too fast.");
        Console.WriteLine("Wait a few minutes and run again. Saved batches are skipped.");
        return 1;
    }

    response.EnsureSuccessStatusCode();

    // write xml response to file
    var xml = await response.Content.ReadAsStringAsync();
    File.WriteAllText(path, xml);

    Console.WriteLine($"  saved {Path.GetFileName(path)}");
}

Console.WriteLine();
Console.WriteLine($"Done. Raw XML is in the '{outputDir}' folder.");

return 0;


static void EmitCatalog()
{
    // Output file
    var fileName = "MeepleLedger/Data/CatalogSeed.cs";

    string fileHeader = """
        // <auto-generated /> — produced by MeepleLedger.Seeder. Do not edit by hand; re-emit instead.
        // to re-build: dotnet run -project MeepleLedger.Seeder -- emit
        using MeepleLedger.Domain;

        namespace MeepleLedger.Data;

        public static class CatalogSeed
        {
            public static readonly List<Game> Games =
            [

        """;

    File.WriteAllText(fileName, fileHeader);



    var files = Directory.GetFiles("raw", "thing-batch-*.xml");
    int dropped = 0;
    int parsed = 0;
    int noWeight = 0;

    var blurbFile = "data/blurbs.json";
    var blurbs = new Dictionary<string, string>();

    foreach (var file in files)
    {
        var items = XDocument.Load(file).Root!.Elements("item");

        foreach (var item in items)
        {
            parsed++;

            string name = item.Elements("name")
                              .First(n => (string?)n.Attribute("type") == "primary")
                              .Attribute("value")!.Value;

            string designer = item.Elements("link")
                                  .FirstOrDefault(l => (string?)l.Attribute("type") == "boardgamedesigner")?
                                  .Attribute("value")?.Value ?? "Unknown";

            int minPlayers = int.Parse(item.Element("minplayers")!.Attribute("value")!.Value);

            int maxPlayers = int.Parse(item.Element("maxplayers")!.Attribute("value")!.Value);
            // drop any game with maxplayers < 1 (data flaw)
            if (maxPlayers < 1)
            {
                dropped++;
                continue;
            }

            int playtimeMinutes = int.Parse(item.Element("playingtime")!.Attribute("value")!.Value);

            List<string> categories = LinkValues(item, "boardgamecategory");
            List<string> mechanics = LinkValues(item, "boardgamemechanic");

            // Absent on some items, "0" on others. Both mean nobody rated it, not that it is light
            double? weight = null;
            string? weightText = item.Element("statistics")?.Element("ratings")?
                                     .Element("averageweight")?.Attribute("value")?.Value;
            if (double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
            {
                weight = w;
            }
            else
            {
                noWeight++;
            }

            // XDocument already undid the XML layer (&#10;, &amp;). What is left is HTML (&mdash;, &quot;)
            string blurb = WebUtility.HtmlDecode(item.Element("description")?.Value ?? "").Trim();
            if (blurb.Length > 0)
            {
                if (!blurbs.TryAdd(name, blurb))
                {
                    Console.WriteLine($"duplicate name '{name}', kept the first blurb");
                }
            }

            string weightField = weight == null ? "" : $", Weight = {weight.Value.ToString(CultureInfo.InvariantCulture)}";

            File.AppendAllText(fileName, "        " +
                                        $"new Game {{ Name = {Quote(name)}, " +
                                        $"Designer = {Quote(designer)}, " +
                                        $"MinPlayers = {minPlayers}, " +
                                        $"MaxPlayers = {maxPlayers}, " +
                                        $"PlaytimeMinutes = {playtimeMinutes}{weightField},{Environment.NewLine}" +
                                        $"            Categories = [{string.Join(", ", categories.Select(Quote))}],{Environment.NewLine}" +
                                        $"            Mechanics = [{string.Join(", ", mechanics.Select(Quote))}] }},{Environment.NewLine}");
        }
    }

    string fileFooter = """
            ];
        }
        """;
    File.AppendAllText(fileName, fileFooter);

    // Blurbs go to a gitignored data file, never into the compiled seed (ADR-0003)
    Directory.CreateDirectory("data");
    File.WriteAllText(blurbFile, JsonSerializer.Serialize(blurbs, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));


    Console.WriteLine($"parsed {parsed} items from {files.Length} files");
    Console.WriteLine($"dropped {dropped} (maxplayers < 1)");
    Console.WriteLine($"{noWeight} games have no weight rating");
    Console.WriteLine($"wrote MeepleLedger/Data/CatalogSeed.cs ({parsed - dropped} games)");
    Console.WriteLine($"wrote {blurbFile} ({blurbs.Count} blurbs)");
}

static List<string> LinkValues(XElement item, string type) =>
    item.Elements("link")
        .Where(l => (string?)l.Attribute("type") == type)
        .Select(l => l.Attribute("value")!.Value)
        .ToList();

static void EmitCollection()
{
    // Output file
    var fileName = "MeepleLedger/Data/CollectionSeed.cs";

    string fileHeader = """
        // <auto-generated /> — produced by MeepleLedger.Seeder. Do not edit by hand; re-emit instead.
        // to re-build: dotnet run -project MeepleLedger.Seeder -- emit
        using MeepleLedger.Domain;

        namespace MeepleLedger.Data;

        public static class CollectionSeed
        {
            public static List<OwnedGame> Build(GameCatalog catalog) =>
            [

        """;

    File.WriteAllText(fileName, fileHeader);

    List<SeedOwnedGame> collection = ReadCollection();
    int parsed = 0;

    foreach (SeedOwnedGame ownedGame in collection)
    {
        parsed++;

        DateTime acquired = ownedGame.DateAcquired;

        File.AppendAllText(fileName, "        " +
                                    $"new OwnedGame {{ Game = Find(catalog, {Quote(ownedGame.Name)}), " +
                                    $"DateAcquired = new DateTime({acquired.Year}, {acquired.Month}, {acquired.Day}), " +
                                    $"Condition = Condition.{ownedGame.Condition}, " +
                                    $"Notes = {Quote(ownedGame.Notes)} }},{Environment.NewLine}");
    }


    string fileFooter = """     
            ];

            private static Game Find(GameCatalog catalog, string name) =>
                catalog.Games.First(g => g.Name == name);
        }
        """;
    File.AppendAllText(fileName, fileFooter);


    Console.WriteLine($"Added {parsed} games to collection seed");
}

// The Collection as it is on BGG, read from raw/collection-owned.xml. EmitCollection writes it into
// CollectionSeed.cs and seed-ledger (S-05) writes it into the Cosmos store, so both use this one
// method and can't drift apart. BGG has no condition or notes, so every Owned Game gets the same ones.
static List<SeedOwnedGame> ReadCollection()
{
    var file = Directory.GetFiles("raw", "collection-owned.xml");
    var items = XDocument.Load(file[0]).Root!.Elements("item");

    List<SeedOwnedGame> collection = [];

    foreach (var item in items)
    {
        string name = item.Elements("name").FirstOrDefault()!.Value;

        // lastmodified looks like "2024-03-17 10:22:05"; only the date part is kept
        string[] time = item.Element("status")!
                            .Attribute("lastmodified")!.Value
                            .Split(' ')[0]
                            .Split('-');

        collection.Add(new SeedOwnedGame
        {
            Name = name,
            DateAcquired = new DateTime(int.Parse(time[0]), int.Parse(time[1]), int.Parse(time[2])),
            Condition = "Good",
            Notes = "...",
        });
    }

    return collection;
}

// Copies the real Collection into the durable store's ownedGames container, once (S-05). It writes no
// Plays: the Seed's Plays are invented, and the durable Play Log starts empty. It refuses if the
// container already holds anything, so a wiped store is never silently refilled over real edits.
// Metered: one count query and one write per Owned Game.
static async Task<int> SeedLedger()
{
    // The same owner as InMemoryMeepleStore, CosmosMeepleStore.OwnerName and EmitLog; they must match
    string ownerName = "TheGentleBean";

    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? endpoint = config["Cosmos:Endpoint"];
    string? key = config["Cosmos:Key"];
    string? databaseName = config["Cosmos:Database"];

    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(databaseName))
    {
        Console.WriteLine("Missing user secrets. Set Cosmos:Endpoint, Cosmos:Key and Cosmos:Database");
        Console.WriteLine("(pwsh infra/cosmos/provision-cosmos.ps1 sets them).");
        return 1;
    }

    // The web app reads this name from its appsettings.json; a user secret can override it here
    string? containerName = config["Cosmos:OwnedGamesContainer"];
    if (string.IsNullOrWhiteSpace(containerName))
    {
        containerName = "ownedGames";
    }

    // Build every document before touching Cosmos, so a bad row stops the run with nothing written
    List<SeedOwnedGame> collection = ReadCollection();
    List<SeedOwnedGameDocument> documents = [];

    foreach (SeedOwnedGame ownedGame in collection)
    {
        // Cosmos ids cannot contain these characters, and the id is the Game name
        if (ownedGame.Name.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            Console.WriteLine($"'{ownedGame.Name}' cannot be a Cosmos id. Nothing was written.");
            return 1;
        }

        documents.Add(new SeedOwnedGameDocument
        {
            Id = ownedGame.Name,
            OwnerName = ownerName,
            GameName = ownedGame.Name,
            DateAcquired = ownedGame.DateAcquired.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Condition = ownedGame.Condition,
            Notes = ownedGame.Notes,
        });
    }

    // camelCase, the same as the web app's CosmosClient, so "ownerName" matches the partition key path
    CosmosClientOptions options = new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        },
    };

    using CosmosClient client = new CosmosClient(endpoint, key, options);
    Container container = client.GetContainer(databaseName, containerName);

    // Refuse if anything is already there. There is no --force: to seed again, empty the container by hand.
    int existing = 0;
    double totalCharge = 0;
    using (FeedIterator<int> iterator = container.GetItemQueryIterator<int>("SELECT VALUE COUNT(1) FROM c"))
    {
        while (iterator.HasMoreResults)
        {
            FeedResponse<int> page = await iterator.ReadNextAsync();
            totalCharge += page.RequestCharge;

            foreach (int count in page)
            {
                existing += count;
            }
        }
    }

    // The account name is the first part of the endpoint's host, e.g. meepleledger-cosmos
    string accountName = new Uri(endpoint).Host.Split('.')[0];

    Console.WriteLine($"Cosmos account:  {accountName} ({endpoint})");
    Console.WriteLine($"Database:        {databaseName}");
    Console.WriteLine($"Container:       {containerName}");
    Console.WriteLine();

    if (existing > 0)
    {
        Console.WriteLine($"Refusing: {containerName} already holds {existing} documents. Nothing was written.");
        Console.WriteLine("seed-ledger only seeds an empty store. Empty the container by hand to seed it again.");
        return 1;
    }

    Console.WriteLine($"About to write {documents.Count} Owned Games for {ownerName}. No Plays are written.");
    Console.Write("Continue? [y/N] ");

    if (Console.ReadLine()?.Trim().ToLower() != "y")
    {
        Console.WriteLine("Stopped. Nothing was written.");
        return 0;
    }

    PartitionKey partitionKey = new PartitionKey(ownerName);

    for (int i = 0; i < documents.Count; i++)
    {
        SeedOwnedGameDocument document = documents[i];

        // Create, not upsert: the container was empty, so a conflict here means someone else is writing too
        ItemResponse<SeedOwnedGameDocument> response = await container.CreateItemAsync(document, partitionKey);
        totalCharge += response.RequestCharge;

        Console.WriteLine($"[{i + 1}/{documents.Count}] {document.Id}");
    }

    Console.WriteLine();
    Console.WriteLine($"Wrote {documents.Count} Owned Games into {databaseName}/{containerName}");
    Console.WriteLine($"Total request charge: {totalCharge:0.00} RU");
    return 0;
}
// Loads an export file (S-06, written by the app's GET /export) into an empty durable store: the
// ownedGames and plays containers. Every row goes through the domain classes first (LedgerExport.Load:
// GameCollection.Add, PlayLog.Record), and nothing is written if any row breaks an invariant. It
// refuses a store that isn't empty: an import never merges into or overwrites real data.
// With a scratch database name it imports there instead, creating the database and the two containers
// if they don't exist, for the round-trip check. Metered: two count queries and one write per row.
static async Task<int> ImportLedger(string path, string? scratchDatabase)
{
    // The same owner as InMemoryMeepleStore, CosmosMeepleStore.OwnerName and EmitLog; they must match
    string ownerName = "TheGentleBean";

    // 1. Read and check the whole file before touching Cosmos
    if (!File.Exists(path))
    {
        Console.WriteLine($"No file at {path}");
        return 1;
    }

    LedgerExportFile file;
    LoadedLedger ledger;
    try
    {
        file = LedgerExport.FromJson(File.ReadAllText(path));
        ledger = LedgerExport.Load(file, CatalogSeed.Games);
    }
    catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is JsonException)
    {
        // ArgumentException covers ArgumentOutOfRangeException: more Players than the Game seats
        Console.WriteLine($"The file can't be imported: {ex.Message}");
        Console.WriteLine("Nothing was written.");
        return 1;
    }

    if (file.OwnerName != ownerName)
    {
        Console.WriteLine($"The file belongs to '{file.OwnerName}', but this store's owner is '{ownerName}'. Nothing was written.");
        return 1;
    }

    // TODO S-07: a Game added at runtime is in file.Games with its full facts, and Load has already put
    // it in the Catalog it checked the rows against. Writing it into the Cosmos games container (with
    // its vector and embeddingTextHash) before the rows that use it belongs to S-07, which is where the
    // live Catalog starts being loaded from that container. Until then such a file is refused.
    if (file.Games.Count > 0)
    {
        Console.WriteLine($"The file has {file.Games.Count} Games outside the compiled Catalog. Restoring them needs S-07. Nothing was written.");
        return 1;
    }

    // Build the documents from the domain objects, not from the file's rows, so what is written is
    // exactly what CosmosMeepleStore would write (trimmed names, this owner). Plays keep their Ids.
    List<OwnedGameDocument> ownedGameDocuments = [];
    List<PlayDocument> playDocuments = [];
    try
    {
        foreach (OwnedGame ownedGame in ledger.Collection.Games)
        {
            ownedGameDocuments.Add(MeepleDocuments.FromOwnedGame(ownedGame, ownerName));
        }

        foreach (Play play in ledger.PlayLog.RecentFirst())
        {
            playDocuments.Add(MeepleDocuments.FromPlay(play, ownerName));
        }
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"The file can't be imported: {ex.Message}");
        Console.WriteLine("Nothing was written.");
        return 1;
    }

    Console.WriteLine($"File:            {path}");
    Console.WriteLine($"Exported on:     {file.ExportedOn} (format version {file.FormatVersion})");
    Console.WriteLine($"Checked:         {ownedGameDocuments.Count} Owned Games and {playDocuments.Count} Plays, every row valid");
    Console.WriteLine();

    // 2. Where it goes. Environment variables are read too (Cosmos__Database=...), after user secrets
    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .AddEnvironmentVariables()
        .Build();

    string? endpoint = config["Cosmos:Endpoint"];
    string? key = config["Cosmos:Key"];
    string? databaseName = config["Cosmos:Database"];

    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(databaseName))
    {
        Console.WriteLine("Missing user secrets. Set Cosmos:Endpoint, Cosmos:Key and Cosmos:Database");
        Console.WriteLine("(pwsh infra/cosmos/provision-cosmos.ps1 sets them).");
        return 1;
    }

    if (scratchDatabase != null && scratchDatabase == databaseName)
    {
        Console.WriteLine($"'{scratchDatabase}' is the real database, not a scratch one. Leave the argument off to import into it.");
        return 1;
    }

    // The web app reads these names from its appsettings.json; a user secret can override them here
    string ownedGamesName = config["Cosmos:OwnedGamesContainer"] ?? "ownedGames";
    string playsName = config["Cosmos:PlaysContainer"] ?? "plays";

    // camelCase, the same as the web app's CosmosClient, so "ownerName" matches the partition key path
    CosmosClientOptions options = new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        },
    };

    using CosmosClient client = new CosmosClient(endpoint, key, options);
    double totalCharge = 0;

    if (scratchDatabase != null)
    {
        // Serverless accounts take no throughput, so none is passed. The partition key matches
        // provision-cosmos.ps1's store containers.
        databaseName = scratchDatabase;
        DatabaseResponse database = await client.CreateDatabaseIfNotExistsAsync(databaseName);
        totalCharge += database.RequestCharge;
        ContainerResponse ownedGamesCreated = await database.Database.CreateContainerIfNotExistsAsync(ownedGamesName, "/ownerName");
        totalCharge += ownedGamesCreated.RequestCharge;
        ContainerResponse playsCreated = await database.Database.CreateContainerIfNotExistsAsync(playsName, "/ownerName");
        totalCharge += playsCreated.RequestCharge;
    }

    Container ownedGamesContainer = client.GetContainer(databaseName, ownedGamesName);
    Container playsContainer = client.GetContainer(databaseName, playsName);

    // The account name is the first part of the endpoint's host, e.g. meepleledger-cosmos
    string accountName = new Uri(endpoint).Host.Split('.')[0];

    Console.WriteLine($"Cosmos account:  {accountName} ({endpoint})");
    if (scratchDatabase != null)
    {
        Console.WriteLine($"Database:        {databaseName} (scratch)");
    }
    else
    {
        Console.WriteLine($"Database:        {databaseName}");
    }
    Console.WriteLine($"Containers:      {ownedGamesName}, {playsName}");
    Console.WriteLine();

    // 3. Refuse a store that isn't empty. There is no --force: empty both containers by hand first
    DocumentCount ownedGamesExisting = await CountDocuments(ownedGamesContainer);
    DocumentCount playsExisting = await CountDocuments(playsContainer);
    totalCharge += ownedGamesExisting.Charge + playsExisting.Charge;

    if (ownedGamesExisting.Count > 0 || playsExisting.Count > 0)
    {
        Console.WriteLine($"Refusing: {ownedGamesName} holds {ownedGamesExisting.Count} documents and {playsName} holds {playsExisting.Count}. Nothing was written.");
        Console.WriteLine("import-ledger only loads an empty store.");
        return 1;
    }

    Console.WriteLine($"About to write {ownedGameDocuments.Count} Owned Games and {playDocuments.Count} Plays for {ownerName}.");
    Console.Write("Continue? [y/N] ");

    if (Console.ReadLine()?.Trim().ToLower() != "y")
    {
        Console.WriteLine("Stopped. Nothing was written.");
        return 0;
    }

    // 4. Write. Create, not upsert: the containers were empty, so a conflict means someone else is writing too
    PartitionKey partitionKey = new PartitionKey(ownerName);

    for (int i = 0; i < ownedGameDocuments.Count; i++)
    {
        OwnedGameDocument document = ownedGameDocuments[i];
        ItemResponse<OwnedGameDocument> response = await ownedGamesContainer.CreateItemAsync(document, partitionKey);
        totalCharge += response.RequestCharge;
        Console.WriteLine($"[Owned Game {i + 1}/{ownedGameDocuments.Count}] {document.Id}");
    }

    for (int i = 0; i < playDocuments.Count; i++)
    {
        PlayDocument document = playDocuments[i];
        ItemResponse<PlayDocument> response = await playsContainer.CreateItemAsync(document, partitionKey);
        totalCharge += response.RequestCharge;
        Console.WriteLine($"[Play {i + 1}/{playDocuments.Count}] {document.PlayedOn} {document.GameName}");
    }

    Console.WriteLine();
    Console.WriteLine($"Wrote {ownedGameDocuments.Count} Owned Games and {playDocuments.Count} Plays into {databaseName}");
    Console.WriteLine($"Total request charge: {totalCharge:0.00} RU");
    return 0;
}

// How many documents a container holds, and what counting them cost
static async Task<DocumentCount> CountDocuments(Container container)
{
    DocumentCount result = new DocumentCount();

    using FeedIterator<int> iterator = container.GetItemQueryIterator<int>("SELECT VALUE COUNT(1) FROM c");
    while (iterator.HasMoreResults)
    {
        FeedResponse<int> page = await iterator.ReadNextAsync();
        result.Charge += page.RequestCharge;

        foreach (int count in page)
        {
            result.Count += count;
        }
    }

    return result;
}

static void EmitLog()
{
    // Output file
    var fileName = "MeepleLedger/Data/LogSeed.cs";

    string fileHeader = """
        // <auto-generated /> — produced by MeepleLedger.Seeder. Do not edit by hand; re-emit instead.
        // to re-build: dotnet run -project MeepleLedger.Seeder -- emit
        using MeepleLedger.Domain;

        namespace MeepleLedger.Data;

        public static class LogSeed
        {
            public static List<Play> Build(GameCatalog catalog) =>
            [

        """;

    // Synthetic
    string owner = "TheGentleBean";
    string[] players = ["TheGentleBean", "Sarah", "Marcus", "Priya", "Dave", "Elena", "Tom", "Jen"];
    string[] locations = ["Home", "Dave's house", "Board & Brew", "Library", "Vacation", "Work"];

    // Seat counts straight out of the raw XML, so a typo can't sneak past
    var minPlayers = new Dictionary<string, int>();
    var maxPlayers = new Dictionary<string, int>();

    foreach (var batchFile in Directory.GetFiles("raw", "thing-batch-*.xml"))
    {
        foreach (var item in XDocument.Load(batchFile).Root!.Elements("item"))
        {
            string name = item.Elements("name")
                              .First(n => (string?)n.Attribute("type") == "primary")
                              .Attribute("value")!.Value;

            int min = int.Parse(item.Element("minplayers")!.Attribute("value")!.Value);
            int max = int.Parse(item.Element("maxplayers")!.Attribute("value")!.Value);

            // same rule EmitCatalog uses. These games aren't in the catalog at all
            if (max < 1)
            {
                continue;
            }

            minPlayers[name] = min;
            maxPlayers[name] = max;
        }
    }

    // Names of the games I own, same file EmitCollection walks
    var ownedNames = XDocument.Load(Path.Combine("raw", "collection-owned.xml"))
                              .Root!.Elements("item")
                              .Select(i => i.Elements("name").First().Value)
                              .ToList();

    // How often each game shows up. favorites recur so MostPlayed() has something to say.
    string[] favorites = ["Wingspan", "The Crew: Mission Deep Sea", "SCOUT"];
    int[] favouriteCounts = [11, 9, 8];

    string[] regulars = ["Ark Nova", "Sea Salt & Paper", "Everdell", "Decrypto",
                         "Castle Combo", "Spirit Island", "Faraway"];

    // In the catalog, not on the shelf. These are the "played but not owned" plays
    string[] unowned = ["Brass: Birmingham", "Terraforming Mars", "7 Wonders Duel", "Scythe"];

    // Games with no score to speak of, and games where everyone wins or nobody does
    string[] noScore = ["The Crew: Mission Deep Sea", "Decrypto", "Sky Team", "Bomb Busters", "Hot Streak"];
    string[] coop = ["The Crew: Mission Deep Sea", "Sky Team", "Bomb Busters", "Spirit Island",
                     "Gloomhaven", "Gloomhaven: Jaws of the Lion", "Nemesis", "Slay the Spire: The Board Game"];

    // Build the list of games to play, one entry per play
    List<string> schedule = [];

    for (int i = 0; i < favorites.Length; i++)
    {
        for (int n = 0; n < favouriteCounts[i]; n++)
        {
            schedule.Add(favorites[i]);
        }
    }

    foreach (var name in regulars)
    {
        for (int n = 0; n < 4; n++)
        {
            schedule.Add(name);
        }
    }

    foreach (var name in unowned)
    {
        schedule.Add(name);
    }

    // Everything else I own gets one play
    foreach (var name in ownedNames)
    {
        if (!favorites.Contains(name) && !regulars.Contains(name))
        {
            schedule.Add(name);
        }
    }

    // Fixed seed, so re-emitting gives the same history and an empty diff
    var random = new Random(20260826);
    var windowStart = new DateTime(2025, 9, 1);

    List<(string Name, DateTime Date, List<(string PlayerName, int? Score, bool IsWinner)> Results, int? Duration, string? Location)> plays = [];

    foreach (var name in schedule)
    {
        var date = windowStart.AddDays(random.Next(0, 360));

        // Seat the table: me plus enough others to respect min, never more than max
        int seats = random.Next(minPlayers[name], Math.Min(maxPlayers[name], 5) + 1);
        if (seats < 1)
        {
            seats = 1;
        }

        List<string> atTable = [owner];
        while (atTable.Count < seats)
        {
            var candidate = players[random.Next(1, players.Length)];
            if (!atTable.Contains(candidate))
            {
                atTable.Add(candidate);
            }
        }

        // Who won? Co-ops are all-or-nothing, and some plays nobody bothered to write it down
        bool coopWin = random.Next(0, 10) < 6;
        bool noWinnerRecorded = random.Next(0, 10) == 0;
        int winnerSeat = random.Next(0, atTable.Count);

        List<(string PlayerName, int? Score, bool IsWinner)> results = [];
        for (int seat = 0; seat < atTable.Count; seat++)
        {
            bool isWinner;
            if (noWinnerRecorded)
            {
                isWinner = false;
            }
            else if (coop.Contains(name))
            {
                isWinner = coopWin;
            }
            else
            {
                isWinner = seat == winnerSeat;
            }

            // Some results are just a name. Testing nullability of certain fields
            int? score = null;
            if (!noScore.Contains(name) && random.Next(0, 10) < 7)
            {
                score = random.Next(20, 130);
            }

            results.Add((atTable[seat], score, isWinner));
        }

        // A winner with a lower score than the table looks generated, so give them the top one
        if (!coop.Contains(name) && !noWinnerRecorded)
        {
            var scores = results.Where(r => r.Score != null).Select(r => r.Score!.Value).ToList();
            if (scores.Count > 0 && results[winnerSeat].Score != null)
            {
                results[winnerSeat] = (results[winnerSeat].PlayerName, scores.Max() + random.Next(1, 12), true);
            }
        }

        int? duration = random.Next(0, 10) < 7 ? random.Next(4, 25) * 5 : null;
        string? location = random.Next(0, 10) < 8 ? locations[random.Next(0, locations.Length)] : null;

        plays.Add((name, date, results, duration, location));
    }

    plays = plays.OrderBy(p => p.Date).ToList();

    // Check the things that throw at runtime, before anything hits the file
    foreach (var play in plays)
    {
        if (!maxPlayers.ContainsKey(play.Name))
        {
            throw new Exception($"'{play.Name}' is not in the catalog. Find() would throw.");
        }

        if (!play.Results.Any(r => string.Equals(r.PlayerName.Trim(), owner, StringComparison.OrdinalIgnoreCase)))
        {
            throw new Exception($"A play of '{play.Name}' is missing {owner}. PlayLog.Record would throw.");
        }

        if (play.Results.Count > maxPlayers[play.Name])
        {
            throw new Exception($"A play of '{play.Name}' seats {play.Results.Count}, max is {maxPlayers[play.Name]}.");
        }

        // Same rule as the Play constructor: "Tom" and "tom " are one Player, who can't sit twice
        for (int i = 0; i < play.Results.Count; i++)
        {
            for (int j = i + 1; j < play.Results.Count; j++)
            {
                if (string.Equals(play.Results[i].PlayerName.Trim(), play.Results[j].PlayerName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"A play of '{play.Name}' has {play.Results[j].PlayerName} twice. new Play would throw.");
                }
            }
        }
    }

    File.WriteAllText(fileName, fileHeader);

    foreach (var play in plays)
    {
        File.AppendAllText(fileName,
            $"        new Play(Find(catalog, {Quote(play.Name)}), " +
            $"new DateTime({play.Date.Year}, {play.Date.Month}, {play.Date.Day}),{Environment.NewLine}" +
            $"            [{Environment.NewLine}");

        foreach (var result in play.Results)
        {
            var score = result.Score == null ? "" : $", Score = {result.Score}";
            var winner = result.IsWinner ? ", IsWinner = true" : "";

            File.AppendAllText(fileName, "                " +
                $"new PlayerResult {{ PlayerName = {Quote(result.PlayerName)}{score}{winner} }},{Environment.NewLine}");
        }

        // Named arguments, because a location with no duration can't be passed positionally
        var tail = "";
        if (play.Duration != null)
        {
            tail += $", durationMinutes: {play.Duration}";
        }
        if (play.Location != null)
        {
            tail += $", location: {Quote(play.Location)}";
        }

        File.AppendAllText(fileName, $"            ]{tail}),{Environment.NewLine}");
    }

    string fileFooter = """
            ];

            private static Game Find(GameCatalog catalog, string name) =>
                catalog.Games.First(g => g.Name == name);
        }
        """;
    File.AppendAllText(fileName, fileFooter);

    int unownedPlays = plays.Count(p => !ownedNames.Contains(p.Name));
    Console.WriteLine($"wrote MeepleLedger/Data/LogSeed.cs ({plays.Count} plays, {unownedPlays} of games not owned)");
}

// One-off check that the embeddings deployment is reachable (A-04).
// Reads the endpoint, key and deployment name from .NET user secrets, embeds one short Ask,
// and prints how many dimensions came back. text-embedding-3-small should return 1536.
static async Task<int> SmokeEmbed()
{
    EmbeddingSettings? settings = LoadEmbeddingSettings();
    if (settings == null)
    {
        return 1;
    }

    string url = settings.Url;
    string deployment = settings.Deployment;

    HttpClient http = new();
    http.DefaultRequestHeaders.Add("api-key", settings.Key);

    string body = JsonSerializer.Serialize(new { input = "a quick co-op game for three players" });
    var response = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    string responseText = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"Request failed: {(int)response.StatusCode} {response.StatusCode}");
        Console.WriteLine(responseText);
        return 1;
    }

    // Response shape: { "data": [ { "embedding": [ ...floats... ] } ], "usage": { "total_tokens": n } }
    using JsonDocument json = JsonDocument.Parse(responseText);
    JsonElement embedding = json.RootElement.GetProperty("data")[0].GetProperty("embedding");
    int dimensions = embedding.GetArrayLength();
    int tokens = json.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32();

    Console.WriteLine($"deployment: {deployment}");
    Console.WriteLine($"dimensions: {dimensions}");
    Console.WriteLine($"tokens used: {tokens}");
    return 0;
}

// Reads the Azure OpenAI endpoint, key and deployment name from .NET user secrets.
// Returns null (after printing how to fix it) if any of them is missing.
static EmbeddingSettings? LoadEmbeddingSettings()
{
    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? endpoint = config["AzureOpenAI:Endpoint"];
    string? key = config["AzureOpenAI:Key"];
    string? deployment = config["AzureOpenAI:EmbeddingDeployment"];

    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(deployment))
    {
        Console.WriteLine("Missing user secrets. Set AzureOpenAI:Endpoint, AzureOpenAI:Key and AzureOpenAI:EmbeddingDeployment");
        Console.WriteLine("with: dotnet user-secrets set <name> <value> --project MeepleLedger.Seeder");
        return null;
    }

    // The deployment name goes in the URL, so the app never refers to the model id directly.
    string url = endpoint.TrimEnd('/') + "/openai/deployments/" + deployment + "/embeddings?api-version=2024-10-21";

    return new EmbeddingSettings { Url = url, Key = key, Deployment = deployment };
}

// Turns every Blurb in data/blurbs.json into a vector and writes them to data/vectors.json (A-05).
// Like fetch, this is the slow, metered step, so it runs once and the file is reused by every Provider.
static async Task<int> Embed()
{
    string blurbFile = "data/blurbs.json";
    string vectorFile = "data/vectors.json";
    int batchSize = 20;

    // text-embedding-3-small list price: $0.02 per 1M tokens
    double dollarsPerMillionTokens = 0.02;

    if (!File.Exists(blurbFile))
    {
        Console.WriteLine($"{blurbFile} not found. Run the emit step first.");
        return 1;
    }

    var blurbs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(blurbFile))!;

    // Categories and mechanics are not in blurbs.json, so read them from the same raw XML emit uses
    var categoriesByName = new Dictionary<string, List<string>>();
    var mechanicsByName = new Dictionary<string, List<string>>();

    foreach (var batchFile in Directory.GetFiles("raw", "thing-batch-*.xml"))
    {
        foreach (var item in XDocument.Load(batchFile).Root!.Elements("item"))
        {
            string name = item.Elements("name")
                              .First(n => (string?)n.Attribute("type") == "primary")
                              .Attribute("value")!.Value;

            categoriesByName[name] = LinkValues(item, "boardgamecategory");
            mechanicsByName[name] = LinkValues(item, "boardgamemechanic");
        }
    }

    // Load what a previous run already embedded, so we can resume
    var vectors = new Dictionary<string, GameVector>();
    if (File.Exists(vectorFile))
    {
        vectors = JsonSerializer.Deserialize<Dictionary<string, GameVector>>(File.ReadAllText(vectorFile))!;
    }

    // Work out which games still need a vector
    var namesToEmbed = new List<string>();
    var textsToEmbed = new List<string>();
    var hashesToEmbed = new List<string>();
    int alreadyDone = 0;

    foreach (var pair in blurbs)
    {
        string name = pair.Key;

        List<string> categories = [];
        if (categoriesByName.ContainsKey(name))
        {
            categories = categoriesByName[name];
        }

        List<string> mechanics = [];
        if (mechanicsByName.ContainsKey(name))
        {
            mechanics = mechanicsByName[name];
        }

        string text = BuildEmbeddingText(name, categories, mechanics, pair.Value);
        string hash = HashText(text);

        // Resume is keyed on the hash of the text, not the name, so a changed Blurb gets re-embedded
        if (vectors.ContainsKey(name) && vectors[name].Hash == hash)
        {
            alreadyDone++;
            continue;
        }

        namesToEmbed.Add(name);
        textsToEmbed.Add(text);
        hashesToEmbed.Add(hash);
    }

    // Drop vectors for games that are no longer in blurbs.json
    int removed = 0;
    foreach (var name in vectors.Keys.ToList())
    {
        if (!blurbs.ContainsKey(name))
        {
            vectors.Remove(name);
            removed++;
        }
    }

    Console.WriteLine($"{blurbs.Count} blurbs, {alreadyDone} already embedded, {namesToEmbed.Count} to embed");
    if (removed > 0)
    {
        Console.WriteLine($"removed {removed} vectors for games no longer in {blurbFile}");
    }

    int totalTokens = 0;

    if (namesToEmbed.Count > 0)
    {
        EmbeddingSettings? settings = LoadEmbeddingSettings();
        if (settings == null)
        {
            return 1;
        }

        HttpClient http = new();
        http.DefaultRequestHeaders.Add("api-key", settings.Key);

        int batchCount = (int)Math.Ceiling(namesToEmbed.Count / (double)batchSize);

        for (int b = 0; b < batchCount; b++)
        {
            int start = b * batchSize;
            int count = Math.Min(batchSize, namesToEmbed.Count - start);
            List<string> batchTexts = textsToEmbed.GetRange(start, count);

            Console.WriteLine($"[{b + 1}/{batchCount}] embedding {count} games...");

            string? responseText = await PostWithRetry(http, settings.Url, batchTexts);
            if (responseText == null)
            {
                Console.WriteLine("Stopped. Vectors saved so far are kept; run again to resume.");
                return 1;
            }

            // Response shape: { "data": [ { "index": i, "embedding": [...] } ], "usage": { "total_tokens": n } }
            using JsonDocument json = JsonDocument.Parse(responseText);

            foreach (JsonElement row in json.RootElement.GetProperty("data").EnumerateArray())
            {
                // "index" is the position in the batch we sent, so use it rather than trusting the order
                int index = row.GetProperty("index").GetInt32();
                JsonElement embedding = row.GetProperty("embedding");

                float[] vector = new float[embedding.GetArrayLength()];
                int i = 0;
                foreach (JsonElement number in embedding.EnumerateArray())
                {
                    vector[i] = number.GetSingle();
                    i++;
                }

                string name = namesToEmbed[start + index];
                vectors[name] = new GameVector { Hash = hashesToEmbed[start + index], Vector = vector };
            }

            totalTokens += json.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32();

            // Save after every batch, so a crash or a Ctrl+C loses at most one batch
            File.WriteAllText(vectorFile, JsonSerializer.Serialize(vectors));
        }
    }
    else
    {
        // Still write the file, in case games were removed above
        File.WriteAllText(vectorFile, JsonSerializer.Serialize(vectors));
    }

    double cost = totalTokens * dollarsPerMillionTokens / 1_000_000;

    Console.WriteLine();
    Console.WriteLine($"wrote {vectorFile} ({vectors.Count} vectors)");
    Console.WriteLine($"tokens used this run: {totalTokens}");
    Console.WriteLine($"estimated cost: ${cost.ToString("0.000000", CultureInfo.InvariantCulture)}");

    // 200 Blurbs should be well under a cent. More than that means something is looping.
    if (cost >= 0.01)
    {
        Console.WriteLine("WARNING: cost is a cent or more. That is far more than 200 Blurbs should need.");
    }

    return 0;
}

// "Cooperative Game, Card Game, Hand Management": the categories then the mechanics, for the
// full-text policy on /facts (S-02).
static string BuildFacts(List<string> categories, List<string> mechanics)
{
    List<string> facts = [];
    facts.AddRange(categories);
    facts.AddRange(mechanics);
    return string.Join(", ", facts);
}

// The text that gets embedded for one Game: name, categories and mechanics as well as the Blurb,
// so a search for "deck building" can match on the mechanic even if the Blurb never says it.
// MeepleLedger.Worker and MeepleLedger.Functions (EmbeddingText.Build) have copies that must build
// exactly the same text, or the hashes differ and every vector they write back looks like an edit
// to watch-cosmos (A-19). Keep the three in step.
// Lines end in "\r\n" whatever the OS: the hashes were first made on Windows, and AppendLine would
// use "\n" on Linux (where A-21's Function runs).
static string BuildEmbeddingText(string name, List<string> categories, List<string> mechanics, string blurb)
{
    var text = new StringBuilder();
    text.Append($"Name: {name}\r\n");
    text.Append($"Categories: {string.Join(", ", categories)}\r\n");
    text.Append($"Mechanics: {string.Join(", ", mechanics)}\r\n");
    text.Append("\r\n");
    text.Append(blurb);
    return text.ToString();
}

static string HashText(string text)
{
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
    return Convert.ToHexString(hash);
}

// Sends one batch of texts to the embeddings endpoint. On a 429 it waits and tries again,
// doubling the wait each time (or using Retry-After if the service sends it).
// Returns the response body, or null if it gave up.
static async Task<string?> PostWithRetry(HttpClient http, string url, List<string> texts)
{
    int maxAttempts = 6;
    int waitSeconds = 2;

    string body = JsonSerializer.Serialize(new { input = texts });

    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        var response = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        string responseText = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            return responseText;
        }

        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            Console.WriteLine($"Request failed: {(int)response.StatusCode} {response.StatusCode}");
            Console.WriteLine(responseText);
            return null;
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
        await Task.Delay(delay * 1000);
        waitSeconds *= 2;
    }

    Console.WriteLine($"Still getting 429 after {maxAttempts} attempts. Giving up.");
    return null;
}

// Loads every Game, with its Blurb and vector, into the Cosmos DB games container (A-08).
// One-shot and safe to run again: each Game is upserted by id, and any document whose id is not
// a Game any more (like the hand-written one from A-07) is deleted, so the container matches
// data/vectors.json exactly. Metered, but 200 upserts on serverless cost a fraction of a cent.
static async Task<int> UploadCosmos()
{
    string blurbFile = "data/blurbs.json";
    string vectorFile = "data/vectors.json";

    if (!File.Exists(vectorFile))
    {
        Console.WriteLine($"{vectorFile} not found. Run the embed step first.");
        return 1;
    }

    var vectors = JsonSerializer.Deserialize<Dictionary<string, GameVector>>(File.ReadAllText(vectorFile))!;

    var blurbs = new Dictionary<string, string>();
    if (File.Exists(blurbFile))
    {
        blurbs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(blurbFile))!;
    }

    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? endpoint = config["Cosmos:Endpoint"];
    string? key = config["Cosmos:Key"];
    string? databaseName = config["Cosmos:Database"];
    string? containerName = config["Cosmos:Container"];

    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key)
        || string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(containerName))
    {
        Console.WriteLine("Missing user secrets. Set Cosmos:Endpoint, Cosmos:Key, Cosmos:Database and Cosmos:Container");
        Console.WriteLine("(pwsh infra/cosmos/provision-cosmos.ps1 sets all four).");
        return 1;
    }

    // The same game facts emit writes into CatalogSeed.cs, read from the same raw XML
    List<CosmosGameDocument> documents = [];
    int noVector = 0;

    foreach (var batchFile in Directory.GetFiles("raw", "thing-batch-*.xml"))
    {
        foreach (var item in XDocument.Load(batchFile).Root!.Elements("item"))
        {
            string name = item.Elements("name")
                              .First(n => (string?)n.Attribute("type") == "primary")
                              .Attribute("value")!.Value;

            int maxPlayers = int.Parse(item.Element("maxplayers")!.Attribute("value")!.Value);

            // emit drops these too, so they are not in the Catalog
            if (maxPlayers < 1)
            {
                continue;
            }

            if (!vectors.ContainsKey(name))
            {
                Console.WriteLine($"no vector for '{name}', skipped. Run the embed step.");
                noVector++;
                continue;
            }

            // Cosmos ids cannot contain these characters, and the id is the Game name
            if (name.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            {
                Console.WriteLine($"'{name}' cannot be a Cosmos id, skipped.");
                continue;
            }

            // Same rule as emit: absent or 0 means unrated, not light
            double? weight = null;
            string? weightText = item.Element("statistics")?.Element("ratings")?
                                     .Element("averageweight")?.Attribute("value")?.Value;
            if (double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
            {
                weight = w;
            }

            string? blurb = null;
            if (blurbs.ContainsKey(name))
            {
                blurb = blurbs[name];
            }

            string designer = item.Elements("link")
                                  .FirstOrDefault(l => (string?)l.Attribute("type") == "boardgamedesigner")?
                                  .Attribute("value")?.Value ?? "Unknown";

            documents.Add(new CosmosGameDocument
            {
                Id = name,
                Name = name,
                Designer = designer,
                MinPlayers = int.Parse(item.Element("minplayers")!.Attribute("value")!.Value),
                MaxPlayers = maxPlayers,
                PlaytimeMinutes = int.Parse(item.Element("playingtime")!.Attribute("value")!.Value),
                Categories = LinkValues(item, "boardgamecategory"),
                Mechanics = LinkValues(item, "boardgamemechanic"),
                Facts = BuildFacts(LinkValues(item, "boardgamecategory"), LinkValues(item, "boardgamemechanic")),
                Weight = weight,
                Blurb = blurb,
                Embedding = vectors[name].Vector,
                EmbeddingTextHash = vectors[name].Hash,
            });
        }
    }

    Console.WriteLine($"{documents.Count} games to upsert ({noVector} skipped for want of a vector)");

    // camelCase property names, so they match the container's vector path (/embedding)
    // and the query in CosmosGameSearchIndex (c.minPlayers, c.playtimeMinutes...)
    CosmosClientOptions options = new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        },
    };

    using CosmosClient client = new CosmosClient(endpoint, key, options);
    Container container = client.GetContainer(databaseName, containerName);

    double totalCharge = 0;
    var uploadedIds = new HashSet<string>();

    for (int i = 0; i < documents.Count; i++)
    {
        CosmosGameDocument document = documents[i];

        // The partition key is /id, so the partition key value is the id itself
        ItemResponse<CosmosGameDocument> response =
            await container.UpsertItemAsync(document, new PartitionKey(document.Id));

        totalCharge += response.RequestCharge;
        uploadedIds.Add(document.Id);

        if ((i + 1) % 20 == 0 || i == documents.Count - 1)
        {
            Console.WriteLine($"[{i + 1}/{documents.Count}] upserted, {response.RequestCharge:0.00} RU for '{document.Id}'");
        }
    }

    // Find anything that is not one of these Games, so a search never returns a stray document
    var strayIds = new List<string>();
    using (FeedIterator<IdOnly> iterator = container.GetItemQueryIterator<IdOnly>("SELECT c.id FROM c"))
    {
        while (iterator.HasMoreResults)
        {
            FeedResponse<IdOnly> page = await iterator.ReadNextAsync();
            totalCharge += page.RequestCharge;

            foreach (IdOnly row in page)
            {
                if (!uploadedIds.Contains(row.Id))
                {
                    strayIds.Add(row.Id);
                }
            }
        }
    }

    foreach (string strayId in strayIds)
    {
        ItemResponse<CosmosGameDocument> response =
            await container.DeleteItemAsync<CosmosGameDocument>(strayId, new PartitionKey(strayId));
        totalCharge += response.RequestCharge;
        Console.WriteLine($"deleted stray document '{strayId}'");
    }

    Console.WriteLine();
    Console.WriteLine($"upserted {documents.Count} games into {databaseName}/{containerName}");
    Console.WriteLine($"total request charge: {totalCharge:0.00} RU (average {totalCharge / Math.Max(1, documents.Count):0.00} per upsert)");
    return 0;
}

// Measures the request charge of the Tonight query and a few comparisons for A-09, and prints a
// table to paste into the ticket. Read-only apart from three writes: one upsert of a Game back over itself,
// and a copy of it created and deleted (to price writes under the current indexing policy).
// Metered, but each run is well under 1,000 RU.
//
// It reads, in order:
//   1. the account's default consistency and whether an indexing policy change is still being applied
//   2. the vector query: the A-08 query, the whole document projected, and the same query with no Table in the WHERE
//   3. a point read and the vector query at each of the five consistency levels, asked per request
//   4. the same lookup as a single-partition query, a cross-partition query and a point read
//   5. one upsert of an unchanged Game, and one create and delete of a copy of it
static async Task<int> MeasureRu()
{
    // The Ask and Table the A-08 ticket judged the Tonight screen on: "quick co-op for three"
    string askText = "quick co-op for three";
    int playerCount = 3;
    int minutesAvailable = 60;
    int maxResults = 10;

    // Charges are steady, but run each one a few times so a one-off is easy to spot
    int repeats = 3;

    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? endpoint = config["Cosmos:Endpoint"];
    string? key = config["Cosmos:Key"];
    string? databaseName = config["Cosmos:Database"];
    string? containerName = config["Cosmos:Container"];

    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key)
        || string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(containerName))
    {
        Console.WriteLine("Missing user secrets. Set Cosmos:Endpoint, Cosmos:Key, Cosmos:Database and Cosmos:Container");
        Console.WriteLine("(pwsh infra/cosmos/provision-cosmos.ps1 sets all four).");
        return 1;
    }

    EmbeddingSettings? embeddingSettings = LoadEmbeddingSettings();
    if (embeddingSettings == null)
    {
        return 1;
    }

    // Embed the Ask once; every vector query below reuses it
    HttpClient http = new();
    http.DefaultRequestHeaders.Add("api-key", embeddingSettings.Key);
    string? embeddingResponse = await PostWithRetry(http, embeddingSettings.Url, [askText]);
    if (embeddingResponse == null)
    {
        return 1;
    }

    float[] askVector;
    using (JsonDocument json = JsonDocument.Parse(embeddingResponse))
    {
        JsonElement embedding = json.RootElement.GetProperty("data")[0].GetProperty("embedding");
        askVector = new float[embedding.GetArrayLength()];
        int i = 0;
        foreach (JsonElement number in embedding.EnumerateArray())
        {
            askVector[i] = number.GetSingle();
            i++;
        }
    }

    CosmosClientOptions options = new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        },
    };

    using CosmosClient client = new CosmosClient(endpoint, key, options);
    Container container = client.GetContainer(databaseName, containerName);

    // 1. Where the account and container stand
    AccountProperties account = await client.ReadAccountAsync();
    ConsistencyLevel accountLevel = account.Consistency.DefaultConsistencyLevel;
    Console.WriteLine($"account default consistency: {accountLevel}");

    // Indexing changes apply in the background. A number taken before this reaches 100 is a
    // number for the old index, so stop rather than record it.
    ContainerResponse containerResponse = await container.ReadContainerAsync(
        new ContainerRequestOptions { PopulateQuotaInfo = true });
    string? progress = containerResponse.Headers["x-ms-documentdb-collection-index-transformation-progress"];
    Console.WriteLine($"index transformation progress: {progress ?? "(not reported)"}%");
    if (progress != null && progress != "100")
    {
        Console.WriteLine("The indexing policy is still being applied. Run this again once it reaches 100.");
        return 1;
    }

    IndexingPolicy indexing = containerResponse.Resource.IndexingPolicy;
    Console.WriteLine($"indexing: {indexing.IncludedPaths.Count} included path(s), "
                      + $"{indexing.ExcludedPaths.Count} excluded, {indexing.CompositeIndexes.Count} composite");
    foreach (IncludedPath path in indexing.IncludedPaths)
    {
        Console.WriteLine($"  include {path.Path}");
    }

    Console.WriteLine();

    // Every result row is read as a JObject (the SDK serializer is Newtonsoft), so SELECT * and SELECT c.id can share one method
    string tableWhere =
        "WHERE (c.minPlayers <= 0 OR c.minPlayers <= @playerCount) "
        + "AND (c.maxPlayers <= 0 OR c.maxPlayers >= @playerCount) "
        + "AND (c.playtimeMinutes <= 0 OR c.playtimeMinutes <= @minutesAvailable) ";
    string orderBy = "ORDER BY VectorDistance(c.embedding, @askVector)";

    string a08Sql = "SELECT TOP @maxResults c.id, VectorDistance(c.embedding, @askVector) AS score FROM c " + tableWhere + orderBy;
    string selectStarSql = "SELECT TOP @maxResults c AS game, VectorDistance(c.embedding, @askVector) AS score FROM c " + tableWhere + orderBy;
    string noTableSql = "SELECT TOP @maxResults c.id, VectorDistance(c.embedding, @askVector) AS score FROM c " + orderBy;

    // 2. The vector query and two variations of it
    Console.WriteLine("== vector query (Ask \"" + askText + "\", " + playerCount + " players, " + minutesAvailable + " minutes, TOP " + maxResults + ") ==");

    QueryDefinition a08Query = BuildMeasureQuery(a08Sql, askVector, playerCount, minutesAvailable, maxResults);
    QueryCharge a08 = await MeasureQuery(container, a08Query, null, null, repeats);
    PrintCharge("A-08 query (id + score)", a08);

    QueryDefinition selectStarQuery = BuildMeasureQuery(selectStarSql, askVector, playerCount, minutesAvailable, maxResults);
    PrintCharge("whole document (vector sent back)", await MeasureQuery(container, selectStarQuery, null, null, repeats));

    QueryDefinition noTableQuery = BuildMeasureQuery(noTableSql, askVector, playerCount, minutesAvailable, maxResults);
    PrintCharge("id + score, no Table in WHERE", await MeasureQuery(container, noTableQuery, null, null, repeats));

    // Index metrics say which paths the WHERE used and which index it wished it had
    QueryRequestOptions metricsOptions = new QueryRequestOptions { PopulateIndexMetrics = true };
    using (FeedIterator<JObject> iterator = container.GetItemQueryIterator<JObject>(a08Query, null, metricsOptions))
    {
        FeedResponse<JObject> page = await iterator.ReadNextAsync();
        Console.WriteLine();
        Console.WriteLine("index metrics for the A-08 query:");
        Console.WriteLine(page.IndexMetrics);
    }

    Console.WriteLine();

    // 3. Consistency, asked for per request. A request may relax the account's level but never
    //    strengthen it, so the stronger levels only run when the account itself is that strong.
    string sampleId = a08.FirstId;
    if (sampleId == "")
    {
        Console.WriteLine("The A-08 query returned no Games, so there is nothing to read. Run upload-cosmos first.");
        return 1;
    }

    Console.WriteLine($"== consistency per request (account is {accountLevel}; point read of '{sampleId}') ==");

    ConsistencyLevel[] levels =
    [
        ConsistencyLevel.Strong,
        ConsistencyLevel.BoundedStaleness,
        ConsistencyLevel.Session,
        ConsistencyLevel.ConsistentPrefix,
        ConsistencyLevel.Eventual,
    ];

    foreach (ConsistencyLevel level in levels)
    {
        try
        {
            double readCharge = 0;
            for (int i = 0; i < repeats; i++)
            {
                ItemResponse<JObject> read = await container.ReadItemAsync<JObject>(
                    sampleId, new PartitionKey(sampleId), new ItemRequestOptions { ConsistencyLevel = level });
                readCharge = read.RequestCharge;
            }

            QueryCharge queryCharge = await MeasureQuery(container, a08Query, null, level, repeats);
            Console.WriteLine($"  {level,-17} point read {readCharge,6:0.00} RU   vector query {queryCharge.Last,7:0.00} RU");
        }
        catch (Exception ex)
        {
            // Asking for a stronger level than the account's is refused
            string firstLine = ex.Message.Split('\n')[0].Trim();
            Console.WriteLine($"  {level,-17} refused: {firstLine}");
        }
    }

    Console.WriteLine();

    // 4. One Game found three ways. The partition key is /id, so a query that names the
    //    partition key value goes to one partition; one that does not is sent to all of them.
    Console.WriteLine($"== single-partition vs cross-partition (looking up '{sampleId}') ==");

    ItemResponse<JObject> pointRead = await container.ReadItemAsync<JObject>(sampleId, new PartitionKey(sampleId));
    Console.WriteLine($"  point read (id + partition key)       {pointRead.RequestCharge,7:0.00} RU");

    QueryDefinition byName = new QueryDefinition("SELECT c.id, c.name FROM c WHERE c.name = @name")
        .WithParameter("@name", sampleId);

    QueryCharge singlePartition = await MeasureQuery(container, byName, new PartitionKey(sampleId), null, repeats);
    PrintCharge("query, partition key given", singlePartition);

    QueryCharge crossPartition = await MeasureQuery(container, byName, null, null, repeats);
    PrintCharge("query, no partition key (fan-out)", crossPartition);

    Console.WriteLine();

    // 5. The price of one write under the current indexing policy: read a Game and put it back
    //    unchanged. Fewer indexed paths should make this cheaper.
    Console.WriteLine("== one upsert of an unchanged Game ==");
    ItemResponse<JObject> before = await container.ReadItemAsync<JObject>(sampleId, new PartitionKey(sampleId));
    ItemResponse<JObject> upsert = await container.UpsertItemAsync(before.Resource, new PartitionKey(sampleId));
    Console.WriteLine($"  upsert '{sampleId}'  {upsert.RequestCharge,7:0.00} RU");

    // An unchanged upsert may not touch the index at all, so also price a brand-new document:
    // a copy of the same Game under a probe id, deleted again straight away.
    string probeId = "A-09 write probe";
    JObject probe = (JObject)before.Resource.DeepClone();
    probe["id"] = probeId;
    ItemResponse<JObject> create = await container.CreateItemAsync(probe, new PartitionKey(probeId));
    ItemResponse<JObject> delete = await container.DeleteItemAsync<JObject>(probeId, new PartitionKey(probeId));
    Console.WriteLine($"  create a new copy       {create.RequestCharge,7:0.00} RU");
    Console.WriteLine($"  delete it again         {delete.RequestCharge,7:0.00} RU");

    return 0;
}

// The measurement queries all take the same parameters; Cosmos ignores the ones a query does not use
static QueryDefinition BuildMeasureQuery(string sql, float[] askVector, int playerCount, int minutesAvailable, int maxResults)
{
    return new QueryDefinition(sql)
        .WithParameter("@maxResults", maxResults)
        .WithParameter("@askVector", askVector)
        .WithParameter("@playerCount", playerCount)
        .WithParameter("@minutesAvailable", minutesAvailable);
}

// Runs a query to the end the given number of times, adding up the charge of every page.
// partitionKey null means cross-partition; consistency null means the account's level.
static async Task<QueryCharge> MeasureQuery(Container container, QueryDefinition query, PartitionKey? partitionKey,
    ConsistencyLevel? consistency, int repeats)
{
    QueryCharge charge = new QueryCharge();

    QueryRequestOptions requestOptions = new QueryRequestOptions
    {
        PartitionKey = partitionKey,
        ConsistencyLevel = consistency,
    };

    for (int run = 0; run < repeats; run++)
    {
        double runCharge = 0;
        int rows = 0;
        int pages = 0;

        using FeedIterator<JObject> iterator = container.GetItemQueryIterator<JObject>(query, null, requestOptions);
        while (iterator.HasMoreResults)
        {
            FeedResponse<JObject> page = await iterator.ReadNextAsync();
            runCharge += page.RequestCharge;
            pages++;

            foreach (JObject row in page)
            {
                if (rows == 0 && charge.FirstId == "" && row["id"] != null)
                {
                    charge.FirstId = row["id"]!.ToString();
                }

                rows++;
            }
        }

        charge.Last = runCharge;
        charge.Rows = rows;
        charge.Pages = pages;
        if (run == 0 || runCharge < charge.Lowest)
        {
            charge.Lowest = runCharge;
        }

        if (runCharge > charge.Highest)
        {
            charge.Highest = runCharge;
        }
    }

    return charge;
}

static void PrintCharge(string label, QueryCharge charge)
{
    string spread = "";
    if (charge.Highest - charge.Lowest > 0.005)
    {
        spread = $"  (range {charge.Lowest:0.00}-{charge.Highest:0.00})";
    }

    Console.WriteLine($"  {label,-38}{charge.Last,7:0.00} RU  {charge.Rows} rows, {charge.Pages} page(s){spread}");
}

// Hosts a change feed processor on the games container (A-10). It runs until Enter is pressed.
// When a Game is added or its Blurb edited, the handler records the intent to re-embed it on an
// IEmbedRequestQueue. It never calls the embeddings API itself: detecting a change and doing the
// slow, metered work are kept apart. Since A-19 the queue is the Service Bus queue embed-requests
// (MeepleLedger.Worker does the embedding); without the ServiceBus secret it is still the local file.
//
// Things worth knowing about the change feed:
//   - Deletes never reach the handler. The feed only carries creates and updates; a delete would
//     have to be a soft-delete flag (an update) to be seen.
//   - Every write shows up, including a write that only replaced the vector. The handler compares
//     the hash of the Game's text with the hash the vector was made from, so a vector write-back
//     (A-19) does not queue another embed and loop forever.
//   - The processor saves how far it has read in the leases container. A restart carries on from
//     there. The very first run starts from "now", not from the beginning, so it never re-queues
//     all 200 Games.
//   - Hosts that share the processor name split the work between them. A host with a different
//     processor name gets every change again.
static async Task<int> WatchCosmos(string instanceName)
{
    // The processor name is what the leases are saved under. Change it and the processor starts
    // over with no leases, as if it were a brand-new consumer.
    string processorName = "embed-requests";

    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? endpoint = config["Cosmos:Endpoint"];
    string? key = config["Cosmos:Key"];
    string? databaseName = config["Cosmos:Database"];
    string? containerName = config["Cosmos:Container"];
    string? leaseContainerName = config["Cosmos:LeaseContainer"];

    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key)
        || string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(containerName)
        || string.IsNullOrWhiteSpace(leaseContainerName))
    {
        Console.WriteLine("Missing user secrets. Set Cosmos:Endpoint, Cosmos:Key, Cosmos:Database, Cosmos:Container");
        Console.WriteLine("and Cosmos:LeaseContainer (pwsh infra/cosmos/provision-cosmos.ps1 sets all five).");
        return 1;
    }

    // A-31: with the AzureMonitor:ConnectionString secret, every batch that queues an embed request is
    // the START of a trace: this batch, the Service Bus send, then the worker or the Function that
    // embeds it and patches the vector back, all under one trace id. Disposed when the watcher stops,
    // which sends any spans still waiting.
    using TracerProvider? tracerProvider = StartChangeFeedTracing(config);

    // camelCase, the same as upload-cosmos, so the documents read back into CosmosGameDocument
    CosmosClientOptions options = new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        },
    };

    using CosmosClient client = new CosmosClient(endpoint, key, options);
    Container games = client.GetContainer(databaseName, containerName);
    Container leases = client.GetContainer(databaseName, leaseContainerName);

    // Fail with a clear message if the leases container was never created, rather than a 404
    // from somewhere inside the processor
    try
    {
        await leases.ReadContainerAsync();
    }
    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
    {
        Console.WriteLine($"The container '{leaseContainerName}' does not exist. Run pwsh infra/cosmos/provision-cosmos.ps1 to create it.");
        return 1;
    }

    // A-19: "recording the intent" means publishing an embed request to Service Bus, for
    // MeepleLedger.Worker. Without the secret it falls back to a log line and a local file, so the
    // watcher still runs with no bus.
    IEmbedRequestQueue queue;
    string queueDescription;
    ServiceBusClient? busClient = null;
    string? busConnectionString = config["ServiceBus:ConnectionString"];
    if (string.IsNullOrWhiteSpace(busConnectionString))
    {
        Console.WriteLine("No ServiceBus:ConnectionString secret, so embed requests go to a local file.");
        queue = new LocalEmbedRequestQueue("data/embed-requests.jsonl");
        queueDescription = "data/embed-requests.jsonl";
    }
    else
    {
        busClient = new ServiceBusClient(busConnectionString);
        queue = new ServiceBusEmbedRequestQueue(busClient.CreateSender(ServiceBusEmbedRequestQueue.QueueName));
        queueDescription = $"the Service Bus queue '{ServiceBusEmbedRequestQueue.QueueName}'";
    }

    // A-16: any change to a Game also drops the web app's cached Recommendations. Optional: without
    // the Redis secret the watcher still queues embeds and the cache entries end with their TTL.
    // One multiplexer for the whole run, shared by every batch.
    ConnectionMultiplexer? redis = null;
    string? redisConnectionString = config["Redis:ConnectionString"];
    if (string.IsNullOrWhiteSpace(redisConnectionString))
    {
        Console.WriteLine("No Redis:ConnectionString secret, so the Recommendation cache will not be invalidated.");
    }
    else
    {
        ConfigurationOptions redisOptions = ConfigurationOptions.Parse(redisConnectionString);
        redisOptions.AbortOnConnectFail = false;
        redis = await ConnectionMultiplexer.ConnectAsync(redisOptions);
    }

    // Called with each batch of changed documents. Throwing from here means the batch is retried,
    // so the handler only does cheap work. A send to Service Bus that fails throws, and the retry
    // sends the whole batch again: duplicates the worker is built to shrug off.
    async Task HandleChangesAsync(ChangeFeedProcessorContext context, IReadOnlyCollection<CosmosGameDocument> changes, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {changes.Count} change(s) on lease {context.LeaseToken}, {context.Headers.RequestCharge:0.00} RU to read");

        // The root of the trace (A-31). Each embed request sent below is a child of it, and carries
        // the trace on to whoever takes it off the queue.
        using Activity? batchSpan = SeederTelemetry.Source.StartActivity("Change feed batch", ActivityKind.Consumer);
        batchSpan?.SetTag("meepleledger.lease", context.LeaseToken);
        batchSpan?.SetTag("meepleledger.change_count", changes.Count);
        batchSpan?.SetTag("meepleledger.cosmos.request_charge", context.Headers.RequestCharge);
        int queued = 0;

        foreach (CosmosGameDocument game in changes)
        {
            if (string.IsNullOrWhiteSpace(game.Blurb))
            {
                Console.WriteLine($"  '{game.Id}': no Blurb, nothing to embed");
                continue;
            }

            string textHash = HashText(BuildEmbeddingText(game.Name, game.Categories, game.Mechanics, game.Blurb));

            if (textHash == game.EmbeddingTextHash)
            {
                Console.WriteLine($"  '{game.Id}': text unchanged, vector is up to date");
                continue;
            }

            // A Game with no hash at all has never been embedded from this text: a new Game
            string reason = "changed";
            if (game.EmbeddingTextHash == null)
            {
                reason = "new";
            }

            EmbedRequest request = new EmbedRequest
            {
                SchemaVersion = 1,
                GameId = game.Id,
                TextHash = textHash,
                Reason = reason,
                DetectedAtUtc = DateTime.UtcNow,
            };

            await queue.SendAsync(request);
            queued++;
        }

        batchSpan?.SetTag("meepleledger.queued_count", queued);
        if (batchSpan != null && queued > 0)
        {
            Console.WriteLine($"  trace id {batchSpan.TraceId}");
        }

        // Any write to a Game can change what the Tonight cache holds: an edited Blurb (its
        // quote in a Reason now and, once re-embedded, its vector), a vector written back, or a
        // changed fact. A new vector can move a Game into any answer, so every entry goes:
        // one bump of the generation per batch.
        if (redis != null)
        {
            await BumpCacheGeneration(redis);
        }
    }

    // No WithStartTime: a processor with no leases yet starts from now. Calling
    // WithStartFromBeginning() would replay the whole container (and re-queue every Game) the
    // first time it ran.
    ChangeFeedProcessor processor = games
        .GetChangeFeedProcessorBuilder<CosmosGameDocument>(processorName, HandleChangesAsync)
        .WithInstanceName(instanceName)
        .WithLeaseContainer(leases)
        .WithLeaseAcquireNotification(leaseToken =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] instance '{instanceName}' took lease {leaseToken}");
            return Task.CompletedTask;
        })
        .WithLeaseReleaseNotification(leaseToken =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] instance '{instanceName}' let go of lease {leaseToken}");
            return Task.CompletedTask;
        })
        .WithErrorNotification((leaseToken, exception) =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] error on lease {leaseToken}: {exception.Message}");
            return Task.CompletedTask;
        })
        .Build();

    await processor.StartAsync();

    Console.WriteLine($"Watching {databaseName}/{containerName} as processor '{processorName}', instance '{instanceName}'.");
    Console.WriteLine($"Leases are kept in {databaseName}/{leaseContainerName}. New Games and edited Blurbs are queued to {queueDescription}.");
    Console.WriteLine("Press Enter to stop.");
    Console.ReadLine();

    // Stopping cleanly releases the leases, so another instance (or the next run) can pick them up
    await processor.StopAsync();
    if (redis != null)
    {
        await redis.DisposeAsync();
    }
    if (busClient != null)
    {
        // Also closes the sender it made
        await busClient.DisposeAsync();
    }
    Console.WriteLine("Stopped.");
    return 0;
}

// Starts OpenTelemetry for watch-cosmos when the AzureMonitor:ConnectionString secret is set (A-31),
// or returns null. The service name is the watcher's box on the Application Insights application map.
//
// Only our own spans and the Service Bus SDK's are traced. Not the Cosmos SDK's: the change feed
// processor polls the container every few seconds, and each poll would be a trace of its own.
static TracerProvider? StartChangeFeedTracing(IConfiguration config)
{
    string? connectionString = config["AzureMonitor:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine("No AzureMonitor:ConnectionString secret, so no traces (A-31).");
        return null;
    }

    // The Azure SDKs only make spans with this switch on
    AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

    string serviceName = "meepleledger-change-feed";
    Console.WriteLine($"Traces go to Application Insights (service {serviceName}).");

    return Sdk.CreateTracerProviderBuilder()
        .ConfigureResource(resource => resource.AddService(serviceName))
        .AddSource(SeederTelemetry.SourceName)
        .AddSource("Azure.Messaging.ServiceBus.*")
        .AddAzureMonitorTraceExporter(options => options.ConnectionString = connectionString)
        .Build();
}

// Creates the pgvector extension on the Flexible Server and checks it is there (A-11).
// The provision script only allow-lists VECTOR in azure.extensions; the extension itself is
// created per database, from a client, which is this. Also prints the memory settings A-12's
// HNSW build depends on. Safe to run again.
static async Task<int> SetupPostgres()
{
    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? connectionString = config["Postgres:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine("Missing user secret Postgres:ConnectionString");
        Console.WriteLine("(pwsh infra/postgres/provision-postgres.ps1 sets it).");
        return 1;
    }

    await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
    await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();

    try
    {
        await using NpgsqlCommand create = new("CREATE EXTENSION IF NOT EXISTS vector", connection);
        await create.ExecuteNonQueryAsync();
    }
    catch (PostgresException ex)
    {
        // The usual cause is the other half of the setup: VECTOR missing from azure.extensions.
        Console.WriteLine($"CREATE EXTENSION failed: {ex.MessageText}");
        Console.WriteLine("Check that VECTOR is in the azure.extensions server parameter");
        Console.WriteLine("(pwsh infra/postgres/provision-postgres.ps1 adds it).");
        return 1;
    }

    await using NpgsqlCommand versionQuery = new("SELECT extversion FROM pg_extension WHERE extname = 'vector'", connection);
    object? version = await versionQuery.ExecuteScalarAsync();
    if (version == null)
    {
        Console.WriteLine("The vector extension is not installed, even after CREATE EXTENSION.");
        return 1;
    }

    Console.WriteLine($"server:                {connection.Host} / {connection.Database}");
    Console.WriteLine($"postgres version:      {connection.PostgreSqlVersion}");
    Console.WriteLine($"vector extversion:     {version}");

    // The settings that decide whether an HNSW build fits in memory or spills to disk
    string[] settingNames = ["shared_buffers", "work_mem", "maintenance_work_mem", "max_parallel_maintenance_workers"];
    foreach (string settingName in settingNames)
    {
        await using NpgsqlCommand show = new("SELECT current_setting(@name)", connection);
        show.Parameters.AddWithValue("name", settingName);
        object? value = await show.ExecuteScalarAsync();
        Console.WriteLine($"{settingName + ":",-22} {value}");
    }

    return 0;
}

// Opens a connection to the Flexible Server with the pgvector type mapping turned on, so a
// float[] can be sent as a real vector parameter instead of 1536 numbers pasted into the SQL.
// Returns null (after saying why) when the connection string is missing.
static NpgsqlDataSource? CreatePostgresDataSource()
{
    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? connectionString = config["Postgres:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine("Missing user secret Postgres:ConnectionString");
        Console.WriteLine("(pwsh infra/postgres/provision-postgres.ps1 sets it).");
        return null;
    }

    NpgsqlDataSourceBuilder builder = new NpgsqlDataSourceBuilder(connectionString);
    builder.UseVector();
    return builder.Build();
}

// Creates the games table from infra/postgres/games-schema.sql and loads one row per Game from the
// raw XML, data/blurbs.json and data/vectors.json (A-12). The same Games, facts and vectors as
// upload-cosmos. Safe to run again: rows are upserted by name, and any row that is not one of
// these Games is deleted, so a search never returns a stray. Metered (the server bills while it runs).
static async Task<int> UploadPostgres()
{
    string schemaFile = "infra/postgres/games-schema.sql";

    List<PostgresGameRow>? rows = LoadGameRows();
    if (rows == null)
    {
        return 1;
    }

    NpgsqlDataSource? dataSource = CreatePostgresDataSource();
    if (dataSource == null)
    {
        return 1;
    }

    await using (dataSource)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();

        // The schema file holds several statements; Npgsql sends them as one batch
        await using (NpgsqlCommand schema = new(File.ReadAllText(schemaFile), connection))
        {
            await schema.ExecuteNonQueryAsync();
        }

        // If the schema file just created the extension, this connection opened before the vector
        // type existed and cannot map it yet. Reloading the types fixes that.
        await connection.ReloadTypesAsync();
        Console.WriteLine($"schema applied from {schemaFile}");

        // One transaction, so a failure part-way leaves the table as it was
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        string upsertSql = """
            INSERT INTO games (name, designer, min_players, max_players, playtime_minutes,
                               categories, mechanics, weight, blurb, embedding, embedding_text_hash)
            VALUES (@name, @designer, @minPlayers, @maxPlayers, @playtimeMinutes,
                    @categories, @mechanics, @weight, @blurb, @embedding, @embeddingTextHash)
            ON CONFLICT (name) DO UPDATE SET
                designer = EXCLUDED.designer,
                min_players = EXCLUDED.min_players,
                max_players = EXCLUDED.max_players,
                playtime_minutes = EXCLUDED.playtime_minutes,
                categories = EXCLUDED.categories,
                mechanics = EXCLUDED.mechanics,
                weight = EXCLUDED.weight,
                blurb = EXCLUDED.blurb,
                embedding = EXCLUDED.embedding,
                embedding_text_hash = EXCLUDED.embedding_text_hash
            """;

        var names = new List<string>();

        for (int i = 0; i < rows.Count; i++)
        {
            PostgresGameRow row = rows[i];

            await using NpgsqlCommand upsert = new(upsertSql, connection, transaction);
            upsert.Parameters.AddWithValue("name", row.Name);
            upsert.Parameters.AddWithValue("designer", row.Designer);
            upsert.Parameters.AddWithValue("minPlayers", row.MinPlayers);
            upsert.Parameters.AddWithValue("maxPlayers", row.MaxPlayers);
            upsert.Parameters.AddWithValue("playtimeMinutes", row.PlaytimeMinutes);
            upsert.Parameters.AddWithValue("categories", row.Categories.ToArray());
            upsert.Parameters.AddWithValue("mechanics", row.Mechanics.ToArray());

            // A null has no type of its own, so DBNull stands in for an unrated weight or a missing Blurb
            if (row.Weight == null)
            {
                upsert.Parameters.AddWithValue("weight", NpgsqlTypes.NpgsqlDbType.Real, DBNull.Value);
            }
            else
            {
                upsert.Parameters.AddWithValue("weight", row.Weight.Value);
            }

            if (row.Blurb == null)
            {
                upsert.Parameters.AddWithValue("blurb", NpgsqlTypes.NpgsqlDbType.Text, DBNull.Value);
            }
            else
            {
                upsert.Parameters.AddWithValue("blurb", row.Blurb);
            }

            upsert.Parameters.AddWithValue("embedding", new Vector(row.Embedding));
            upsert.Parameters.AddWithValue("embeddingTextHash", row.EmbeddingTextHash);
            await upsert.ExecuteNonQueryAsync();

            names.Add(row.Name);

            if ((i + 1) % 20 == 0 || i == rows.Count - 1)
            {
                Console.WriteLine($"[{i + 1}/{rows.Count}] upserted");
            }
        }

        // Delete anything that is not one of these Games
        await using (NpgsqlCommand deleteStrays = new("DELETE FROM games WHERE NOT (name = ANY(@names))", connection, transaction))
        {
            deleteStrays.Parameters.AddWithValue("names", names.ToArray());
            int deleted = await deleteStrays.ExecuteNonQueryAsync();
            if (deleted > 0)
            {
                Console.WriteLine($"deleted {deleted} stray row(s)");
            }
        }

        await transaction.CommitAsync();

        // Fresh statistics, so the planner knows how many rows there are when it picks a plan
        await using (NpgsqlCommand analyze = new("ANALYZE games", connection))
        {
            await analyze.ExecuteNonQueryAsync();
        }

        Console.WriteLine();
        Console.WriteLine($"upserted {rows.Count} games into {connection.Database}.games");
    }

    return 0;
}

// Loads one row per Game from the raw XML, data/blurbs.json and data/vectors.json: the same Games
// and facts emit writes into CatalogSeed.cs, with the Blurb and vector added. upload-postgres and
// upload-redis both load these, so the two stores hold the same data. Returns null when there are
// no vectors yet.
static List<PostgresGameRow>? LoadGameRows()
{
    string blurbFile = "data/blurbs.json";
    string vectorFile = "data/vectors.json";

    if (!File.Exists(vectorFile))
    {
        Console.WriteLine($"{vectorFile} not found. Run the embed step first.");
        return null;
    }

    var vectors = JsonSerializer.Deserialize<Dictionary<string, GameVector>>(File.ReadAllText(vectorFile))!;

    var blurbs = new Dictionary<string, string>();
    if (File.Exists(blurbFile))
    {
        blurbs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(blurbFile))!;
    }

    // The same game facts emit writes into CatalogSeed.cs, read from the same raw XML
    List<PostgresGameRow> rows = [];
    int noVector = 0;

    foreach (var batchFile in Directory.GetFiles("raw", "thing-batch-*.xml"))
    {
        foreach (var item in XDocument.Load(batchFile).Root!.Elements("item"))
        {
            string name = item.Elements("name")
                              .First(n => (string?)n.Attribute("type") == "primary")
                              .Attribute("value")!.Value;

            int maxPlayers = int.Parse(item.Element("maxplayers")!.Attribute("value")!.Value);

            // emit drops these too, so they are not in the Catalog
            if (maxPlayers < 1)
            {
                continue;
            }

            if (!vectors.ContainsKey(name))
            {
                Console.WriteLine($"no vector for '{name}', skipped. Run the embed step.");
                noVector++;
                continue;
            }

            // Same rule as emit: absent or 0 means unrated, not light
            float? weight = null;
            string? weightText = item.Element("statistics")?.Element("ratings")?
                                     .Element("averageweight")?.Attribute("value")?.Value;
            if (float.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
            {
                weight = w;
            }

            string? blurb = null;
            if (blurbs.ContainsKey(name))
            {
                blurb = blurbs[name];
            }

            string designer = item.Elements("link")
                                  .FirstOrDefault(l => (string?)l.Attribute("type") == "boardgamedesigner")?
                                  .Attribute("value")?.Value ?? "Unknown";

            rows.Add(new PostgresGameRow
            {
                Name = name,
                Designer = designer,
                MinPlayers = int.Parse(item.Element("minplayers")!.Attribute("value")!.Value),
                MaxPlayers = maxPlayers,
                PlaytimeMinutes = int.Parse(item.Element("playingtime")!.Attribute("value")!.Value),
                Categories = LinkValues(item, "boardgamecategory"),
                Mechanics = LinkValues(item, "boardgamemechanic"),
                Weight = weight,
                Blurb = blurb,
                Embedding = vectors[name].Vector,
                EmbeddingTextHash = vectors[name].Hash,
            });
        }
    }

    Console.WriteLine($"{rows.Count} games to upsert ({noVector} skipped for want of a vector)");
    return rows;
}

// Compares the two pgvector index types against an exact search for A-12, and prints a table to
// paste into the ticket. Needs the rows loaded first (upload-postgres). Metered.
//
// It runs the same nearest-neighbour searches four ways:
//   1. with no vector index at all: a sequential scan, exact, the ceiling for recall
//   2. with an IVFFlat index, at two ivfflat.probes settings
//   3. with an HNSW index, at two hnsw.ef_search settings
// Recall is each run's top 10 compared with the sequential scan's top 10 for the same search.
// The searches use a sample of the Games' own vectors as the query, so no embeddings calls are made.
//
// It leaves the HNSW index in place (the A-12 decision) and the IVFFlat index dropped, the same
// state games-schema.sql creates.
static async Task<int> MeasurePgvector()
{
    int resultsPerSearch = 10;
    int searchesRepeated = 3;   // each search is timed this many times and averaged

    NpgsqlDataSource? dataSource = CreatePostgresDataSource();
    if (dataSource == null)
    {
        return 1;
    }

    await using (dataSource)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();

        // Every 10th Game by name becomes a search
        var queryNames = new List<string>();
        var queryVectors = new List<Vector>();
        int rowCount = 0;

        await using (NpgsqlCommand select = new("SELECT name, embedding FROM games ORDER BY name", connection))
        await using (NpgsqlDataReader reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (rowCount % 10 == 0)
                {
                    queryNames.Add(reader.GetString(0));
                    queryVectors.Add(reader.GetFieldValue<Vector>(1));
                }
                rowCount++;
            }
        }

        if (rowCount == 0)
        {
            Console.WriteLine("The games table is empty. Run upload-postgres first.");
            return 1;
        }

        Console.WriteLine($"{rowCount} games, {queryVectors.Count} searches, top {resultsPerSearch}, each timed {searchesRepeated} times");

        var report = new List<string>();
        report.Add("| Search | Build time | Index size | Avg execution | Recall@10 | Plan |");
        report.Add("|---|---|---|---|---|---|");

        // 1. No vector index: the exact answer
        await ExecutePostgres(connection, "DROP INDEX IF EXISTS games_embedding_hnsw");
        await ExecutePostgres(connection, "DROP INDEX IF EXISTS games_embedding_ivfflat");

        var exactResults = new List<List<string>>();
        for (int i = 0; i < queryVectors.Count; i++)
        {
            exactResults.Add(await SearchPostgres(connection, queryVectors[i], resultsPerSearch));
        }

        PgvectorRun exactRun = await MeasurePostgresSearches(connection, queryVectors, exactResults, resultsPerSearch, searchesRepeated, "Seq Scan");
        report.Add($"| Sequential scan (no index) | - | - | {exactRun.AverageMs:0.000} ms | {exactRun.Recall:P0} | {exactRun.PlanSummary} |");

        long tableBytes = await ScalarPostgres<long>(connection, "SELECT pg_table_size('games')");

        // At this row count the planner would rather scan 200 rows than use any index, which would
        // make every index run below secretly a sequential scan. Turning seq scans off for this
        // session forces the index, and the plan check confirms it.
        await ExecutePostgres(connection, "SET enable_seqscan = off");

        // 2. IVFFlat. pgvector suggests lists = rows / 1000 below a million rows, which gives 1 list
        //    here: one cluster holding every row, an index in name only. sqrt(rows) gives about 14
        //    clusters, enough to see probes trade recall for speed. Built after the rows are loaded,
        //    because its centroids come from the rows present at build time.
        int lists = (int)Math.Round(Math.Sqrt(rowCount));
        Console.WriteLine($"building IVFFlat (lists = {lists})...");
        double ivfflatBuildMs = await TimePostgres(connection,
            $"CREATE INDEX games_embedding_ivfflat ON games USING ivfflat (embedding vector_cosine_ops) WITH (lists = {lists})");
        long ivfflatBytes = await ScalarPostgres<long>(connection, "SELECT pg_relation_size('games_embedding_ivfflat')");

        int[] probeSettings = [1, (int)Math.Ceiling(Math.Sqrt(lists))];
        foreach (int probes in probeSettings)
        {
            await ExecutePostgres(connection, $"SET ivfflat.probes = {probes}");
            PgvectorRun run = await MeasurePostgresSearches(connection, queryVectors, exactResults, resultsPerSearch, searchesRepeated, "games_embedding_ivfflat");
            report.Add($"| IVFFlat lists={lists}, probes={probes} | {ivfflatBuildMs:0} ms | {FormatBytes(ivfflatBytes)} | {run.AverageMs:0.000} ms | {run.Recall:P0} | {run.PlanSummary} |");
        }

        await ExecutePostgres(connection, "RESET ivfflat.probes");
        await ExecutePostgres(connection, "DROP INDEX games_embedding_ivfflat");

        // 3. HNSW, at pgvector's default build settings (the same as games-schema.sql)
        Console.WriteLine("building HNSW (m = 16, ef_construction = 64)...");
        double hnswBuildMs = await TimePostgres(connection,
            "CREATE INDEX games_embedding_hnsw ON games USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64)");
        long hnswBytes = await ScalarPostgres<long>(connection, "SELECT pg_relation_size('games_embedding_hnsw')");

        // ef_search is the candidate list size: 40 is the default, 10 is the least that can still
        // return 10 results
        int[] efSearchSettings = [10, 40];
        foreach (int efSearch in efSearchSettings)
        {
            await ExecutePostgres(connection, $"SET hnsw.ef_search = {efSearch}");
            PgvectorRun run = await MeasurePostgresSearches(connection, queryVectors, exactResults, resultsPerSearch, searchesRepeated, "games_embedding_hnsw");
            report.Add($"| HNSW m=16, ef_construction=64, ef_search={efSearch} | {hnswBuildMs:0} ms | {FormatBytes(hnswBytes)} | {run.AverageMs:0.000} ms | {run.Recall:P0} | {run.PlanSummary} |");
        }

        await ExecutePostgres(connection, "RESET hnsw.ef_search");
        await ExecutePostgres(connection, "RESET enable_seqscan");

        Console.WriteLine();
        foreach (string line in report)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine($"Table data (heap + TOAST, no indexes): {FormatBytes(tableBytes)}");
        Console.WriteLine("Build time is measured from this machine, so it includes one round trip to the server.");
        Console.WriteLine("Avg execution is the server's own Execution Time from EXPLAIN ANALYZE, with no network in it.");
        Console.WriteLine("Left in place: games_embedding_hnsw. Dropped: games_embedding_ivfflat.");
    }

    return 0;
}

// The nearest-neighbour search A-12 measures: the closest Games by cosine distance (<=>), which is
// the operator vector_cosine_ops indexes. Returns the names in order, nearest first.
static async Task<List<string>> SearchPostgres(NpgsqlConnection connection, Vector queryVector, int resultsPerSearch)
{
    var names = new List<string>();

    await using NpgsqlCommand search = new("SELECT name FROM games ORDER BY embedding <=> @query LIMIT @limit", connection);
    search.Parameters.AddWithValue("query", queryVector);
    search.Parameters.AddWithValue("limit", resultsPerSearch);

    await using NpgsqlDataReader reader = await search.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        names.Add(reader.GetString(0));
    }

    return names;
}

// Runs every search under the current settings: once for its results (to score recall against the
// exact results) and several times under EXPLAIN ANALYZE for the server's execution time and plan.
// expectedPlan is the text the plan should contain: an index name, or "Seq Scan".
static async Task<PgvectorRun> MeasurePostgresSearches(NpgsqlConnection connection, List<Vector> queryVectors,
    List<List<string>> exactResults, int resultsPerSearch, int searchesRepeated, string expectedPlan)
{
    double totalMs = 0;
    int timings = 0;
    int found = 0;
    int plansAsExpected = 0;

    for (int i = 0; i < queryVectors.Count; i++)
    {
        // Recall: how many of the exact top 10 this run also returned
        List<string> names = await SearchPostgres(connection, queryVectors[i], resultsPerSearch);
        foreach (string name in names)
        {
            if (exactResults[i].Contains(name))
            {
                found++;
            }
        }

        for (int repeat = 0; repeat < searchesRepeated; repeat++)
        {
            await using NpgsqlCommand explain = new("EXPLAIN ANALYZE SELECT name FROM games ORDER BY embedding <=> @query LIMIT @limit", connection);
            explain.Parameters.AddWithValue("query", queryVectors[i]);
            explain.Parameters.AddWithValue("limit", resultsPerSearch);

            bool planAsExpected = false;

            // EXPLAIN ANALYZE returns the plan as rows of text. The last one reads
            // "Execution Time: 0.123 ms".
            await using NpgsqlDataReader reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string line = reader.GetString(0).Trim();

                if (line.Contains(expectedPlan))
                {
                    planAsExpected = true;
                }

                if (line.StartsWith("Execution Time:"))
                {
                    string number = line.Replace("Execution Time:", "").Replace("ms", "").Trim();
                    totalMs += double.Parse(number, CultureInfo.InvariantCulture);
                    timings++;
                }
            }

            if (planAsExpected)
            {
                plansAsExpected++;
            }
        }
    }

    PgvectorRun run = new PgvectorRun();
    run.AverageMs = totalMs / Math.Max(1, timings);
    run.Recall = (double)found / (queryVectors.Count * resultsPerSearch);

    int totalPlans = queryVectors.Count * searchesRepeated;
    if (plansAsExpected == totalPlans)
    {
        run.PlanSummary = $"{expectedPlan} in every plan";
    }
    else
    {
        // The index was not used for some searches, so those timings and results are a seq scan's
        run.PlanSummary = $"WARNING: {expectedPlan} in only {plansAsExpected} of {totalPlans} plans";
    }

    return run;
}

// Runs EXPLAIN ANALYZE on the Tonight query of PostgresGameSearchIndex (A-13) for three Tables, to
// see whether the vector index is really used and whether a filter costs results. Metered.
//
// For each Table it prints how many Games fit, then runs the query four ways:
//   planner's choice       the server's own plan choice, at the app's ef_search (Postgres:EfSearch)
//   HNSW, ef_search=40     enable_seqscan and enable_bitmapscan off, so the vector index is forced,
//                          at pgvector's default. Seq scan alone is not enough: with a WHERE, the
//                          planner goes round it with a bitmap scan on the B-tree indexes.
//   HNSW, ef_search=100    the same at the app's setting
//   exact                  enable_indexscan off: the app's fallback when an answer comes back short
// A forced-HNSW run that returns fewer rows than the exact run is the filtered-ANN recall trap.
// The query vector is a Game's own vector, so no embeddings call is made.
static async Task<int> ExplainPostgres()
{
    int maxResults = 10;

    NpgsqlDataSource? dataSource = CreatePostgresDataSource();
    if (dataSource == null)
    {
        return 1;
    }

    // The owned names, as the app's Collection has them (CollectionSeed is emitted from this file)
    var ownedNames = new List<string>();
    foreach (var item in XDocument.Load("raw/collection-owned.xml").Root!.Elements("item"))
    {
        ownedNames.Add(item.Elements("name").First().Value);
    }

    await using (dataSource)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();

        Vector? queryVector = null;
        string queryName = "";
        await using (NpgsqlCommand select = new("SELECT name, embedding FROM games ORDER BY name LIMIT 1", connection))
        await using (NpgsqlDataReader reader = await select.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                queryName = reader.GetString(0);
                queryVector = reader.GetFieldValue<Vector>(1);
            }
        }

        if (queryVector == null)
        {
            Console.WriteLine("The games table is empty. Run upload-postgres first.");
            return 1;
        }

        Console.WriteLine($"query vector: the embedding of '{queryName}', top {maxResults}, {ownedNames.Count} owned Games");

        var tables = new List<ExplainTable>
        {
            new ExplainTable { Label = "4 players, 120 minutes, whole Catalog", PlayerCount = 4, MinutesAvailable = 120, OwnedOnly = false },
            new ExplainTable { Label = "1 player, 30 minutes, whole Catalog (selective)", PlayerCount = 1, MinutesAvailable = 30, OwnedOnly = false },
            new ExplainTable { Label = "3 players, 120 minutes, owned only", PlayerCount = 3, MinutesAvailable = 120, OwnedOnly = true },
        };

        // Each run: a label, then pairs of setting name and value for SET LOCAL
        var runs = new List<string[]>
        {
            new[] { "planner's choice", "hnsw.ef_search", "100" },
            new[] { "HNSW, ef_search=40", "hnsw.ef_search", "40", "enable_seqscan", "off", "enable_bitmapscan", "off" },
            new[] { "HNSW, ef_search=100", "hnsw.ef_search", "100", "enable_seqscan", "off", "enable_bitmapscan", "off" },
            new[] { "exact", "enable_indexscan", "off" },
        };

        foreach (ExplainTable table in tables)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {table.Label} ===");

            await using (NpgsqlCommand count = new(TonightSql(table.OwnedOnly, countOnly: true), connection))
            {
                AddTonightParameters(count, queryVector, table, ownedNames, maxResults);
                long fits = (long)(await count.ExecuteScalarAsync())!;
                Console.WriteLine($"{fits} Games fit this Table");
            }

            foreach (string[] run in runs)
            {
                // Each run is its own transaction, so its SET LOCAL settings end with it
                await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

                for (int i = 1; i + 1 < run.Length; i += 2)
                {
                    await using NpgsqlCommand set = new("SELECT set_config(@name, @value, true)", connection, transaction);
                    set.Parameters.AddWithValue("name", run[i]);
                    set.Parameters.AddWithValue("value", run[i + 1]);
                    await set.ExecuteNonQueryAsync();
                }

                await using NpgsqlCommand explain = new("EXPLAIN (ANALYZE, BUFFERS) " + TonightSql(table.OwnedOnly, countOnly: false), connection, transaction);
                AddTonightParameters(explain, queryVector, table, ownedNames, maxResults);

                var planLines = new List<string>();
                await using (NpgsqlDataReader reader = await explain.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        planLines.Add(reader.GetString(0));
                    }
                }

                await transaction.RollbackAsync();

                // The scan nodes say how the rows were found, e.g. "Index Scan using games_embedding_hnsw on games g"
                var scans = new List<string>();
                string executionTime = "?";
                foreach (string line in planLines)
                {
                    string node = line.Trim();
                    if (node.StartsWith("->"))
                    {
                        node = node.Substring(2).Trim();
                    }

                    if (node.Contains("Scan") && node.Contains(" on "))
                    {
                        string scan = node.Split("  (")[0];
                        if (!scans.Contains(scan))
                        {
                            scans.Add(scan);
                        }
                    }

                    if (line.Trim().StartsWith("Execution Time:"))
                    {
                        executionTime = line.Trim().Replace("Execution Time:", "").Trim();
                    }
                }

                // The top node (Limit) reads "(actual time=... rows=N loops=1)": N is what the query returned
                string rowsReturned = "?";
                int actualAt = planLines[0].IndexOf("actual");
                if (actualAt >= 0)
                {
                    int rowsAt = planLines[0].IndexOf("rows=", actualAt);
                    if (rowsAt >= 0)
                    {
                        rowsReturned = planLines[0].Substring(rowsAt + 5).Split(' ')[0];
                    }
                }

                Console.WriteLine($"{run[0],-22} {rowsReturned,4} rows  {executionTime,10}  {string.Join(" + ", scans)}");

                // The full plans of the planner's own choice and of forced HNSW, to paste into the ticket
                if (run[0] == "planner's choice" || run[0] == "HNSW, ef_search=40")
                {
                    foreach (string line in planLines)
                    {
                        Console.WriteLine("    " + HideVector(line));
                    }
                }
            }
        }
    }

    return 0;
}

// EXPLAIN prints the parameter values in the plan, so the query vector shows up as 1536 numbers.
// This puts "[1536 floats]" in their place.
static string HideVector(string planLine)
{
    int start = planLine.IndexOf("'[");
    int end = planLine.IndexOf("]'::vector");
    if (start < 0 || end < start)
    {
        return planLine;
    }

    return planLine.Substring(0, start) + "'[1536 floats]" + planLine.Substring(end + 1);
}

// A copy of the query in PostgresGameSearchIndex.BuildSql (the Seeder does not reference the web
// project), so keep the two in step. countOnly swaps the select list for count(*) and drops
// ORDER BY and LIMIT, to count the Games that fit the Table.
static string TonightSql(bool ownedOnly, bool countOnly)
{
    var sql = new StringBuilder();
    if (countOnly)
    {
        sql.AppendLine("SELECT count(*)");
    }
    else
    {
        sql.AppendLine("SELECT g.name, g.blurb, g.embedding <=> @askVector AS distance");
    }

    sql.AppendLine("FROM games g");

    if (ownedOnly)
    {
        sql.AppendLine("JOIN unnest(@ownedNames) AS owned(name) ON owned.name = g.name");
    }

    sql.AppendLine("WHERE (g.min_players <= 0 OR g.min_players <= @playerCount)");
    sql.AppendLine("  AND (g.max_players <= 0 OR g.max_players >= @playerCount)");
    sql.AppendLine("  AND (g.playtime_minutes <= 0 OR g.playtime_minutes <= @minutesAvailable)");

    if (!countOnly)
    {
        sql.AppendLine("ORDER BY g.embedding <=> @askVector");
        sql.AppendLine("LIMIT @maxResults");
    }

    return sql.ToString();
}

static void AddTonightParameters(NpgsqlCommand command, Vector askVector, ExplainTable table, List<string> ownedNames, int maxResults)
{
    // The count query uses neither the vector nor the limit; an unused parameter is harmless
    command.Parameters.AddWithValue("askVector", askVector);
    command.Parameters.AddWithValue("playerCount", table.PlayerCount);
    command.Parameters.AddWithValue("minutesAvailable", table.MinutesAvailable);
    command.Parameters.AddWithValue("maxResults", maxResults);

    if (table.OwnedOnly)
    {
        command.Parameters.AddWithValue("ownedNames", ownedNames.ToArray());
    }
}

// A crude load test of the Tonight query (A-14): 50 Asks at once, in rounds, run three ways:
//   no pooling          Pooling=false: every Ask opens (and closes) its own server connection
//   pooled              the app's pool settings from MeepleLedger/appsettings.json
//   pooled + prepared   the same pool, with the commands prepared as PostgresGameSearchIndex does
// Each Ask does what the app's SearchAsync does on its normal path: open a connection, begin a
// transaction, SET LOCAL hnsw.ef_search, run the query, commit. The time covers all of that, seen
// from this machine, so it includes the network. Round 1 is reported on its own because it pays
// for opening the pool's connections; the later rounds show the steady state.
// The Ask vectors are 50 Games' own vectors, so no embeddings calls are made (in the app the
// embeddings call is a separate HTTP request and is not part of the connection story). Metered.
static async Task<int> LoadPostgres()
{
    int concurrentAsks = 50;
    int rounds = 5;
    int maxResults = 10;

    // The pool settings are read from the web app's own appsettings.json, so the test uses
    // exactly what the app uses. The connection string comes from user secrets as usual.
    // AddJsonFile looks next to the exe unless given a full path, hence GetFullPath.
    IConfiguration config = new ConfigurationBuilder()
        .AddJsonFile(Path.GetFullPath("MeepleLedger/appsettings.json"))
        .AddUserSecrets<Program>()
        .Build();

    string? connectionString = config["Postgres:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine("Missing user secret Postgres:ConnectionString");
        Console.WriteLine("(pwsh infra/postgres/provision-postgres.ps1 sets it).");
        return 1;
    }

    int minPoolSize = int.Parse(config["Postgres:MinPoolSize"]!, CultureInfo.InvariantCulture);
    int maxPoolSize = int.Parse(config["Postgres:MaxPoolSize"]!, CultureInfo.InvariantCulture);
    int commandTimeoutSeconds = int.Parse(config["Postgres:CommandTimeoutSeconds"]!, CultureInfo.InvariantCulture);
    string efSearch = config["Postgres:EfSearch"]!;

    // A separate data source for looking at the server, so it is not counted in any pool under test
    NpgsqlDataSourceBuilder monitorBuilder = new NpgsqlDataSourceBuilder(connectionString);
    monitorBuilder.UseVector();
    await using NpgsqlDataSource monitor = monitorBuilder.Build();

    var askVectors = new List<Vector>();
    await using (NpgsqlConnection connection = await monitor.OpenConnectionAsync())
    {
        await using (NpgsqlCommand select = new("SELECT embedding FROM games ORDER BY name LIMIT @count", connection))
        {
            select.Parameters.AddWithValue("count", concurrentAsks);
            await using NpgsqlDataReader reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                askVectors.Add(reader.GetFieldValue<Vector>(0));
            }
        }

        if (askVectors.Count == 0)
        {
            Console.WriteLine("The games table is empty. Run upload-postgres first.");
            return 1;
        }

        string maxConnections = await ScalarPostgres<string>(connection, "SHOW max_connections");
        Console.WriteLine($"server max_connections: {maxConnections} (Azure keeps some of these for itself)");
    }

    // The same wide Table for every Ask; the Asks differ by vector
    var table = new ExplainTable { Label = "4 players, 120 minutes", PlayerCount = 4, MinutesAvailable = 120, OwnedOnly = false };
    string sql = TonightSql(table.OwnedOnly, countOnly: false);

    Console.WriteLine($"{concurrentAsks} concurrent Asks x {rounds} rounds, Table: {table.Label}, top {maxResults}");
    Console.WriteLine($"pool: min {minPoolSize}, max {maxPoolSize}; command timeout {commandTimeoutSeconds} s; ef_search {efSearch}");
    Console.WriteLine();

    var report = new List<string>();
    report.Add("| Run | Round 1 p50 | Round 1 p95 | Round 1 max | Later p50 | Later p95 | Later max | Later Asks/s | Failed | Server connections after |");
    report.Add("|---|---|---|---|---|---|---|---|---|---|");

    string[] runNames = ["no pooling", "pooled", "pooled + prepared"];
    foreach (string runName in runNames)
    {
        NpgsqlConnectionStringBuilder settings = new NpgsqlConnectionStringBuilder(connectionString);
        settings.CommandTimeout = commandTimeoutSeconds;
        if (runName == "no pooling")
        {
            settings.Pooling = false;
        }
        else
        {
            settings.MinPoolSize = minPoolSize;
            settings.MaxPoolSize = maxPoolSize;
        }

        bool prepare = runName == "pooled + prepared";

        NpgsqlDataSourceBuilder builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
        builder.UseVector();
        await using NpgsqlDataSource dataSource = builder.Build();

        Console.WriteLine($"running: {runName}...");

        var firstRound = new List<double>();
        var laterRounds = new List<double>();
        double laterWallMs = 0;
        int failed = 0;
        string firstError = "";

        for (int round = 1; round <= rounds; round++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Start all the Asks, then wait for all of them
            var asks = new List<Task<LoadAskResult>>();
            for (int i = 0; i < concurrentAsks; i++)
            {
                Vector askVector = askVectors[i % askVectors.Count];
                asks.Add(RunLoadAsk(dataSource, sql, askVector, table, maxResults, efSearch, prepare));
            }
            LoadAskResult[] results = await Task.WhenAll(asks);

            stopwatch.Stop();

            foreach (LoadAskResult result in results)
            {
                if (result.Error != null)
                {
                    failed++;
                    if (firstError == "")
                    {
                        firstError = result.Error;
                    }
                    continue;
                }

                if (round == 1)
                {
                    firstRound.Add(result.Milliseconds);
                }
                else
                {
                    laterRounds.Add(result.Milliseconds);
                }
            }

            if (round > 1)
            {
                laterWallMs += stopwatch.Elapsed.TotalMilliseconds;
            }
        }

        // How many connections this run left open on the server: the pool's idle ones (the
        // monitor's own connection is taken off the count)
        long serverConnections;
        await using (NpgsqlConnection connection = await monitor.OpenConnectionAsync())
        {
            serverConnections = await ScalarPostgres<long>(connection,
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND usename = current_user");
            serverConnections = serverConnections - 1;
        }

        double asksPerSecond = 0;
        if (laterWallMs > 0)
        {
            asksPerSecond = laterRounds.Count / (laterWallMs / 1000.0);
        }

        report.Add($"| {runName} | {FormatMs(Percentile(firstRound, 50))} | {FormatMs(Percentile(firstRound, 95))} | {FormatMs(Percentile(firstRound, 100))} " +
                   $"| {FormatMs(Percentile(laterRounds, 50))} | {FormatMs(Percentile(laterRounds, 95))} | {FormatMs(Percentile(laterRounds, 100))} " +
                   $"| {asksPerSecond:0} | {failed} of {concurrentAsks * rounds} | {serverConnections} |");

        if (firstError != "")
        {
            Console.WriteLine($"  first failure: {firstError}");
        }
    }

    Console.WriteLine();
    foreach (string line in report)
    {
        Console.WriteLine(line);
    }

    Console.WriteLine();
    Console.WriteLine("Times are per Ask from this machine: waiting for a connection + the queries + the network.");
    Console.WriteLine($"Round 1 opens connections; 'Later' is rounds 2 to {rounds} together.");
    return 0;
}

// Connects to Azure Managed Redis over TLS and checks it is ready for A-16 and A-17 (A-15):
// PING must return PONG, and MODULE LIST must include the search module (named "search" by the
// server, "RediSearch" in Azure). Also prints how the client sees the topology: with the Enterprise
// clustering policy it looks like one standalone server even though it is sharded inside.
static async Task<int> PingRedis()
{
    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? connectionString = config["Redis:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine("Missing user secret Redis:ConnectionString");
        Console.WriteLine("(pwsh infra/redis/provision-redis.ps1 sets it).");
        return 1;
    }

    ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);
    if (!options.Ssl)
    {
        Console.WriteLine("The connection string does not have ssl=True. The instance only accepts TLS.");
        return 1;
    }

    await using ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(options);
    IDatabase database = redis.GetDatabase();

    // PING sent as a raw command so we see the server's literal reply, not just a round-trip time
    RedisResult pingReply = await database.ExecuteAsync("PING");
    string pingText = pingReply.ToString() ?? "";
    Console.WriteLine($"PING:              {pingText}");
    if (pingText != "PONG")
    {
        Console.WriteLine("Expected PONG.");
        return 1;
    }

    TimeSpan roundTrip = await database.PingAsync();
    Console.WriteLine($"round trip:        {roundTrip.TotalMilliseconds:F1} ms");

    EndPoint[] endpoints = redis.GetEndPoints();
    IServer server = redis.GetServer(endpoints[0]);
    Console.WriteLine($"endpoint:          {endpoints[0]}");
    Console.WriteLine($"server type:       {server.ServerType} (EnterpriseCluster shows as Standalone)");
    Console.WriteLine($"redis version:     {server.Version}");

    // MODULE LIST returns one array per module: [name, <name>, ver, <version>, ...]
    RedisResult modulesReply = await database.ExecuteAsync("MODULE", "LIST");
    RedisResult[] modules = (RedisResult[])modulesReply!;
    bool hasSearch = false;
    Console.WriteLine("modules:");
    foreach (RedisResult module in modules)
    {
        RedisResult[] fields = (RedisResult[])module!;
        string name = "";
        string version = "";
        for (int i = 0; i + 1 < fields.Length; i += 2)
        {
            string fieldName = fields[i].ToString() ?? "";
            if (fieldName == "name")
            {
                name = fields[i + 1].ToString() ?? "";
            }
            if (fieldName == "ver")
            {
                version = fields[i + 1].ToString() ?? "";
            }
        }
        Console.WriteLine($"  {name,-16} ver {version}");

        if (name.ToLower() == "search" || name.ToLower() == "ft")
        {
            hasSearch = true;
        }
    }

    if (!hasSearch)
    {
        Console.WriteLine("The search module is not loaded. Modules can only be added when the instance is");
        Console.WriteLine("created: delete it and run pwsh infra/redis/provision-redis.ps1 again.");
        return 1;
    }

    Console.WriteLine("RediSearch is available.");
    return 0;
}

// Opens the Redis connection from the Redis:ConnectionString user secret.
// Returns null (after saying why) when the secret is missing.
static async Task<ConnectionMultiplexer?> ConnectRedis()
{
    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? connectionString = config["Redis:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine("Missing user secret Redis:ConnectionString");
        Console.WriteLine("(pwsh infra/redis/provision-redis.ps1 sets it).");
        return null;
    }

    // StackExchange.Redis 3 talks RESP3 when the server offers it, and then FT.SEARCH and FT.INFO
    // answer with maps instead of the flat arrays read below. RESP2 keeps the documented shape.
    ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);
    options.Protocol = RedisProtocol.Resp2;

    return await ConnectionMultiplexer.ConnectAsync(options);
}

// Loads every Game into Azure Managed Redis as a hash and builds the RediSearch vector index over
// them (A-17). The same Games, facts and vectors as upload-postgres. Safe to run again:
//   - each hash is written over, and any "game:*" key that is not one of these Games is deleted
//   - FT.CREATE fails on an index that already exists, so the index is dropped first (without DD,
//     which would delete the hashes too) and created again. That also means a change to the schema
//     below takes effect on the next run. While it rebuilds, a running app's Asks fail for a moment.
// Ends with a self-check: a Game's own vector must find that Game first.
// Metered (the instance bills while it exists, not per command).
static async Task<int> UploadRedis()
{
    // The same names as RedisGameSearchIndex in the web project; keep them in step.
    string indexName = "idx:games";
    string keyPrefix = "game:";
    int dimensions = 1536;

    List<PostgresGameRow>? rows = LoadGameRows();
    if (rows == null)
    {
        return 1;
    }

    // A vector of the wrong length is stored without complaint and then left out of the index
    foreach (PostgresGameRow row in rows)
    {
        if (row.Embedding.Length != dimensions)
        {
            Console.WriteLine($"'{row.Name}' has a {row.Embedding.Length}-dimension vector, not {dimensions}. Run the embed step again.");
            return 1;
        }
    }

    ConnectionMultiplexer? redis = await ConnectRedis();
    if (redis == null)
    {
        return 1;
    }

    await using (redis)
    {
        IDatabase database = redis.GetDatabase();

        // 1. One hash per Game. Every command touches one key, so none can hit CROSSSLOT on the
        //    clustered instance. They are sent together and awaited together (pipelined).
        HashSet<string> expectedKeys = [];
        List<Task> writes = [];

        foreach (PostgresGameRow row in rows)
        {
            string key = keyPrefix + row.Name;
            expectedKeys.Add(key);

            List<HashEntry> fields =
            [
                new HashEntry("name", row.Name),
                new HashEntry("min_players", row.MinPlayers),
                new HashEntry("max_players", row.MaxPlayers),
                new HashEntry("playtime_minutes", row.PlaytimeMinutes),
                new HashEntry("embedding", VectorToBytes(row.Embedding)),
                new HashEntry("embedding_text_hash", row.EmbeddingTextHash),
            ];

            if (row.Blurb != null)
            {
                fields.Add(new HashEntry("blurb", row.Blurb));
            }

            writes.Add(database.HashSetAsync(key, fields.ToArray()));

            // HSET only sets the fields it is given, so a Blurb that has gone away must be removed
            if (row.Blurb == null)
            {
                writes.Add(database.HashDeleteAsync(key, "blurb"));
            }
        }

        await Task.WhenAll(writes);
        Console.WriteLine($"wrote {rows.Count} hashes under {keyPrefix}*");

        // 2. Delete anything under the prefix that is not one of these Games. KeysAsync uses SCAN,
        //    a page at a time, never KEYS (which blocks the server while it walks every key).
        IServer server = redis.GetServer(redis.GetEndPoints()[0]);
        List<RedisKey> strays = [];
        await foreach (RedisKey key in server.KeysAsync(pattern: keyPrefix + "*", pageSize: 500))
        {
            if (!expectedKeys.Contains(key.ToString()))
            {
                strays.Add(key);
            }
        }

        foreach (RedisKey stray in strays)
        {
            await database.KeyDeleteAsync(stray);
        }

        if (strays.Count > 0)
        {
            Console.WriteLine($"deleted {strays.Count} stray key(s)");
        }

        // 3. The index. FT._LIST names every index; drop ours if it is there, then create it.
        RedisResult[] indexes = (RedisResult[])(await database.ExecuteAsync("FT._LIST"))!;
        bool indexExists = false;
        foreach (RedisResult index in indexes)
        {
            if (index.ToString() == indexName)
            {
                indexExists = true;
            }
        }

        if (indexExists)
        {
            // No DD: drop the index only, keep the hashes
            await database.ExecuteAsync("FT.DROPINDEX", indexName);
            Console.WriteLine($"dropped the existing index {indexName}");
        }

        // FT.CREATE idx:games ON HASH PREFIX 1 game: SCHEMA
        //   name TAG SEPARATOR | CASESENSITIVE      exact names for the owned-only filter. The default
        //                                            separator is a comma, which would cut
        //                                            "Air, Land, & Sea" into three tags
        //   min_players NUMERIC                      the Table filters
        //   max_players NUMERIC
        //   playtime_minutes NUMERIC
        //   embedding VECTOR FLAT 6 TYPE FLOAT32 DIM 1536 DISTANCE_METRIC COSINE
        //                                            FLAT is an exact scan: at 200 vectors it costs
        //                                            microseconds and never misses a Game to a filter.
        //                                            COSINE matches the other two stores. The 6 is the
        //                                            number of words that follow it.
        // blurb and embedding_text_hash are stored in the hash but not indexed.
        await database.ExecuteAsync("FT.CREATE",
            indexName, "ON", "HASH", "PREFIX", "1", keyPrefix, "SCHEMA",
            "name", "TAG", "SEPARATOR", "|", "CASESENSITIVE",
            "min_players", "NUMERIC",
            "max_players", "NUMERIC",
            "playtime_minutes", "NUMERIC",
            "embedding", "VECTOR", "FLAT", "6",
                "TYPE", "FLOAT32",
                "DIM", dimensions.ToString(CultureInfo.InvariantCulture),
                "DISTANCE_METRIC", "COSINE");
        Console.WriteLine($"created index {indexName}");

        // 4. FT.CREATE indexes the existing hashes in the background. Wait until it has them all.
        string expectedCount = rows.Count.ToString(CultureInfo.InvariantCulture);
        string numDocs = "";
        string failures = "";
        for (int attempt = 0; attempt < 60; attempt++)
        {
            RedisResult[] info = (RedisResult[])(await database.ExecuteAsync("FT.INFO", indexName))!;
            numDocs = ReadInfoField(info, "num_docs");
            failures = ReadInfoField(info, "hash_indexing_failures");

            if (numDocs == expectedCount)
            {
                break;
            }

            await Task.Delay(500);
        }

        Console.WriteLine($"indexed {numDocs} of {rows.Count} hashes, {failures} indexing failure(s)");
        if (numDocs != expectedCount)
        {
            Console.WriteLine("The index does not hold every Game. A failure usually means an embedding field");
            Console.WriteLine("that is not 1536 float32 values written as raw bytes.");
            return 1;
        }

        // 5. Self-check: a Game's own vector must come back first.
        //    A vector stored as text instead of bytes would make this return nothing.
        PostgresGameRow sample = rows[0];
        RedisResult reply = await database.ExecuteAsync("FT.SEARCH",
            indexName, "*=>[KNN 1 @embedding $askVector AS distance]",
            "PARAMS", "2", "askVector", VectorToBytes(sample.Embedding),
            "SORTBY", "distance", "ASC",
            "RETURN", "2", "name", "distance",
            "DIALECT", "2");

        List<string> found = ReadRedisSearchNames(reply);
        if (found.Count == 0 || found[0] != sample.Name)
        {
            Console.WriteLine($"Self-check failed: searching with the vector of '{sample.Name}' did not find it first.");
            return 1;
        }

        Console.WriteLine($"self-check: the vector of '{sample.Name}' finds '{found[0]}' first");

        // Cached Recommendations were made from the old data; drop them
        await BumpCacheGeneration(redis);

        Console.WriteLine();
        Console.WriteLine($"loaded {rows.Count} games into Redis, searchable through {indexName}");
    }

    return 0;
}

// RediSearch takes a vector as raw bytes: float32 values, little-endian, one after another.
// A vector sent as a JSON array is stored without complaint and never matches anything.
static byte[] VectorToBytes(float[] vector)
{
    byte[] bytes = new byte[vector.Length * 4];
    for (int i = 0; i < vector.Length; i++)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), vector[i]);
    }

    return bytes;
}

// FT.INFO answers with one flat array of names and values: [name, value, name, value, ...].
// Returns the value for the given name as text, or "" when the server did not send it.
static string ReadInfoField(RedisResult[] info, string fieldName)
{
    for (int i = 0; i + 1 < info.Length; i += 2)
    {
        if (info[i].ToString() == fieldName)
        {
            return info[i + 1].ToString() ?? "";
        }
    }

    return "";
}

// The "name" field of each hit in an FT.SEARCH reply, in order. The reply is
// [total, key, [field, value, ...], key, [field, value, ...], ...]
static List<string> ReadRedisSearchNames(RedisResult reply)
{
    RedisResult[] items = (RedisResult[])reply!;
    List<string> names = [];

    for (int i = 1; i + 1 < items.Length; i += 2)
    {
        RedisResult[] fields = (RedisResult[])items[i + 1]!;
        for (int f = 0; f + 1 < fields.Length; f += 2)
        {
            if (fields[f].ToString() == "name")
            {
                names.Add(fields[f + 1].ToString() ?? "");
            }
        }
    }

    return names;
}

// Runs the same Ask and Table against all three cloud Search Indexes and prints the latency and
// what each query costs, for the A-17 comparison. The Ask is embedded once and the same vector is
// sent to each store, so the times are for the store alone; the embeddings call is timed on its own
// (every Provider pays it). Without an embeddings resource a Game's own vector stands in for the Ask. Each store gets one warm-up run (opening connections) and then 20 timed
// runs one after another, seen from this machine, so the network is in every number. A store whose
// secrets are missing, or that cannot be reached, is skipped. Metered: up to 1 embeddings call,
// 21 Cosmos queries, and the Postgres server and Redis instance must be running.
static async Task<int> CompareProviders()
{
    // The A-08/A-09 Ask and Table, across the whole Catalog
    string askText = "quick co-op for three";
    int playerCount = 3;
    int minutesAvailable = 60;
    int maxResults = 10;
    int runs = 20;

    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    float[]? askVector = null;
    string askDescription = "Ask \"" + askText + "\"";

    // Embed the Ask once; every store gets the same vector
    EmbeddingSettings? embeddingSettings = LoadEmbeddingSettings();
    if (embeddingSettings != null)
    {
        try
        {
            HttpClient http = new();
            http.DefaultRequestHeaders.Add("api-key", embeddingSettings.Key);
            var embedStopwatch = System.Diagnostics.Stopwatch.StartNew();
            string? embeddingResponse = await PostWithRetry(http, embeddingSettings.Url, [askText]);
            embedStopwatch.Stop();

            if (embeddingResponse != null)
            {
                using JsonDocument json = JsonDocument.Parse(embeddingResponse);
                JsonElement embedding = json.RootElement.GetProperty("data")[0].GetProperty("embedding");
                askVector = new float[embedding.GetArrayLength()];
                int i = 0;
                foreach (JsonElement number in embedding.EnumerateArray())
                {
                    askVector[i] = number.GetSingle();
                    i++;
                }

                Console.WriteLine($"embeddings call (paid by every Provider): {embedStopwatch.Elapsed.TotalMilliseconds:0} ms");
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"embeddings call failed: {ex.Message}");
        }
    }

    // No embeddings resource (for example after a teardown): use a Game's own vector as the Ask,
    // as load-postgres does. The stores do the same work either way; only the answers differ.
    if (askVector == null)
    {
        string vectorFile = "data/vectors.json";
        if (!File.Exists(vectorFile))
        {
            Console.WriteLine($"{vectorFile} not found either. Run the embed step first.");
            return 1;
        }

        var vectors = JsonSerializer.Deserialize<Dictionary<string, GameVector>>(File.ReadAllText(vectorFile))!;
        string standIn = "Pandemic";
        if (!vectors.ContainsKey(standIn))
        {
            standIn = vectors.Keys.First();
        }

        askVector = vectors[standIn].Vector;
        askDescription = "the vector of '" + standIn + "' standing in for an Ask";
    }

    Console.WriteLine($"{askDescription}, {playerCount} players, {minutesAvailable} minutes, top {maxResults}, whole Catalog");
    Console.WriteLine();

    List<ProviderTiming> timings = [];

    // Cosmos DB: the query in CosmosGameSearchIndex.BuildQuery
    string? cosmosEndpoint = config["Cosmos:Endpoint"];
    string? cosmosKey = config["Cosmos:Key"];
    string? cosmosDatabase = config["Cosmos:Database"];
    string? cosmosContainer = config["Cosmos:Container"];
    if (string.IsNullOrWhiteSpace(cosmosEndpoint) || string.IsNullOrWhiteSpace(cosmosKey)
        || string.IsNullOrWhiteSpace(cosmosDatabase) || string.IsNullOrWhiteSpace(cosmosContainer))
    {
        Console.WriteLine("Cosmos: skipped, the Cosmos:* user secrets are missing");
    }
    else
    {
        try
        {
            CosmosClientOptions options = new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
                },
            };

            using CosmosClient client = new CosmosClient(cosmosEndpoint, cosmosKey, options);
            Container container = client.GetContainer(cosmosDatabase, cosmosContainer);

            string sql = "SELECT TOP @maxResults c.id, VectorDistance(c.embedding, @askVector) AS score FROM c "
                + "WHERE (c.minPlayers <= 0 OR c.minPlayers <= @playerCount) "
                + "AND (c.maxPlayers <= 0 OR c.maxPlayers >= @playerCount) "
                + "AND (c.playtimeMinutes <= 0 OR c.playtimeMinutes <= @minutesAvailable) "
                + "ORDER BY VectorDistance(c.embedding, @askVector)";
            QueryDefinition query = BuildMeasureQuery(sql, askVector, playerCount, minutesAvailable, maxResults);

            ProviderTiming timing = new ProviderTiming { Name = "Cosmos DB (serverless)" };
            double charge = 0;

            for (int run = 0; run <= runs; run++)
            {
                List<string> names = [];
                charge = 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                using FeedIterator<JObject> iterator = container.GetItemQueryIterator<JObject>(query);
                while (iterator.HasMoreResults)
                {
                    FeedResponse<JObject> page = await iterator.ReadNextAsync();
                    charge += page.RequestCharge;
                    foreach (JObject row in page)
                    {
                        names.Add(row["id"]!.ToString());
                    }
                }

                stopwatch.Stop();

                // Run 0 is the warm-up
                if (run > 0)
                {
                    timing.Milliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
                timing.TopNames = names;
            }

            timing.CostPerAsk = $"{charge:0.00} RU";
            timings.Add(timing);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cosmos: skipped, {ex.Message}");
        }
    }

    // PostgreSQL: the query in PostgresGameSearchIndex.BuildSql (via TonightSql)
    NpgsqlDataSource? dataSource = CreatePostgresDataSource();
    if (dataSource != null)
    {
        try
        {
            await using (dataSource)
            {
                await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
                ExplainTable table = new ExplainTable { Label = "compare", PlayerCount = playerCount, MinutesAvailable = minutesAvailable, OwnedOnly = false };

                ProviderTiming timing = new ProviderTiming { Name = "PostgreSQL (B1ms, pgvector)" };

                for (int run = 0; run <= runs; run++)
                {
                    List<string> names = [];
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    await using (NpgsqlCommand command = new(TonightSql(false, false), connection))
                    {
                        AddTonightParameters(command, new Vector(askVector), table, [], maxResults);
                        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            names.Add(reader.GetString(0));
                        }
                    }

                    stopwatch.Stop();
                    if (run > 0)
                    {
                        timing.Milliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);
                    }
                    timing.TopNames = names;
                }

                timing.CostPerAsk = "flat hourly";
                timings.Add(timing);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PostgreSQL: skipped, {ex.Message}");
        }
    }

    // Redis: the query in RedisGameSearchIndex.BuildQuery
    ConnectionMultiplexer? redis = null;
    try
    {
        redis = await ConnectRedis();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Redis: skipped, {ex.Message}");
    }

    if (redis != null)
    {
        try
        {
            await using (redis)
            {
                IDatabase database = redis.GetDatabase();
                byte[] askBytes = VectorToBytes(askVector);
                string query = "(@min_players:[-inf $players] (@max_players:[-inf 0] | @max_players:[$players +inf]) "
                    + "@playtime_minutes:[-inf $minutes])=>[KNN " + maxResults + " @embedding $askVector AS distance]";

                ProviderTiming timing = new ProviderTiming { Name = "Redis (Managed, Balanced B0)" };

                for (int run = 0; run <= runs; run++)
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    RedisResult reply = await database.ExecuteAsync("FT.SEARCH",
                        "idx:games", query,
                        "PARAMS", "6",
                        "players", playerCount.ToString(CultureInfo.InvariantCulture),
                        "minutes", minutesAvailable.ToString(CultureInfo.InvariantCulture),
                        "askVector", askBytes,
                        "SORTBY", "distance", "ASC",
                        "LIMIT", "0", maxResults.ToString(CultureInfo.InvariantCulture),
                        "RETURN", "3", "name", "blurb", "distance",
                        "DIALECT", "2");
                    stopwatch.Stop();

                    if (run > 0)
                    {
                        timing.Milliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);
                    }
                    timing.TopNames = ReadRedisSearchNames(reply);
                }

                timing.CostPerAsk = "flat hourly";
                timings.Add(timing);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Redis: skipped, {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("| Provider | p50 | p95 | Fastest | Cost per Ask | Results | Top 3 |");
    Console.WriteLine("|---|---|---|---|---|---|---|");
    foreach (ProviderTiming timing in timings)
    {
        List<string> topThree = [];
        for (int i = 0; i < timing.TopNames.Count && i < 3; i++)
        {
            topThree.Add(timing.TopNames[i]);
        }

        Console.WriteLine($"| {timing.Name} | {FormatMs(Percentile(timing.Milliseconds, 50))} | {FormatMs(Percentile(timing.Milliseconds, 95))} "
            + $"| {FormatMs(Percentile(timing.Milliseconds, 0))} | {timing.CostPerAsk} | {timing.TopNames.Count} | {string.Join(", ", topThree)} |");
    }

    Console.WriteLine();
    Console.WriteLine($"{runs} timed runs each after one warm-up; times are from this machine and include the network.");
    return 0;
}

// Drops every cached Recommendation in the web app's Tonight cache (A-16), for a change the cache
// cannot see for itself: a Blurb edited in Postgres (which has no change feed), or a re-run of
// upload-cosmos or upload-postgres. watch-cosmos does the same on its own for Cosmos changes.
static async Task<int> InvalidateCache()
{
    IConfiguration config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();

    string? connectionString = config["Redis:ConnectionString"];
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.WriteLine("Missing user secret Redis:ConnectionString");
        Console.WriteLine("(pwsh infra/redis/provision-redis.ps1 sets it).");
        return 1;
    }

    await using ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
    long generation = await BumpCacheGeneration(redis);
    if (generation < 0)
    {
        return 1;
    }

    return 0;
}

// Raises the generation number that the web app's CachedGameSearchIndex puts in every cache key.
// From then on no old key is looked up again, and the old entries expire with their TTL. One INCR,
// whatever the number of entries: no KEYS, no SCAN. Returns the new generation, or -1 if Redis
// could not be reached (the change still reaches the cache, at the latest when the TTL runs out).
static async Task<long> BumpCacheGeneration(IConnectionMultiplexer redis)
{
    // The same name as CachedGameSearchIndex.GenerationKey; the Seeder does not reference the web
    // project, so keep the two in step.
    string generationKey = "tonight:generation";

    try
    {
        long generation = await redis.GetDatabase().StringIncrementAsync(generationKey);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Recommendation cache invalidated: generation is now {generation}");
        return generation;
    }
    catch (Exception ex) when (ex is RedisException || ex is TimeoutException)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Could not invalidate the Recommendation cache: {ex.Message}");
        return -1;
    }
}

// One Ask in the load test, the same steps as PostgresGameSearchIndex.SearchAsync without the
// exact-scan fallback. Never throws: a failure is returned so the other Asks carry on.
static async Task<LoadAskResult> RunLoadAsk(NpgsqlDataSource dataSource, string sql, Vector askVector,
    ExplainTable table, int maxResults, string efSearch, bool prepare)
{
    var result = new LoadAskResult();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        await using (NpgsqlCommand set = new("SELECT set_config(@name, @value, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("name", "hnsw.ef_search");
            set.Parameters.AddWithValue("value", efSearch);
            if (prepare)
            {
                await set.PrepareAsync();
            }
            await set.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand query = new(sql, connection, transaction))
        {
            AddTonightParameters(query, askVector, table, [], maxResults);
            if (prepare)
            {
                await query.PrepareAsync();
            }

            await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // Read the row as the app does, so the data really comes back over the wire
                reader.GetString(0);
            }
        }

        await transaction.CommitAsync();
    }
    catch (Exception ex)
    {
        result.Error = ex.GetType().Name + ": " + ex.Message;
    }

    stopwatch.Stop();
    result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
    return result;
}

// The value below which the given percent of the times fall (100 gives the largest).
// Returns -1 when there are no times, which FormatMs shows as "-".
static double Percentile(List<double> times, int percent)
{
    if (times.Count == 0)
    {
        return -1;
    }

    var sorted = new List<double>(times);
    sorted.Sort();

    int index = (int)Math.Ceiling(percent / 100.0 * sorted.Count) - 1;
    if (index < 0)
    {
        index = 0;
    }

    return sorted[index];
}

static string FormatMs(double milliseconds)
{
    if (milliseconds < 0)
    {
        return "-";
    }
    return $"{milliseconds:0} ms";
}

static async Task ExecutePostgres(NpgsqlConnection connection, string sql)
{
    await using NpgsqlCommand command = new(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static async Task<T> ScalarPostgres<T>(NpgsqlConnection connection, string sql)
{
    await using NpgsqlCommand command = new(sql, connection);
    object? value = await command.ExecuteScalarAsync();
    return (T)value!;
}

// Runs one statement and returns how long it took in milliseconds, seen from this machine
static async Task<double> TimePostgres(NpgsqlConnection connection, string sql)
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    await ExecutePostgres(connection, sql);
    stopwatch.Stop();
    return stopwatch.Elapsed.TotalMilliseconds;
}

static string FormatBytes(long bytes)
{
    if (bytes < 1024 * 1024)
    {
        return $"{bytes / 1024.0:0.0} KB";
    }
    return $"{bytes / (1024.0 * 1024.0):0.00} MB";
}

// Split one CSV line on commas, except commas inside double quotes.
// "Air, Land, & Sea" stays one field, and "" inside quotes is a literal quote.
static string[] SplitCsvLine(string line)
{
    var fields = new List<string>();
    var field = new StringBuilder();
    bool inQuotes = false;

    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];

        if (inQuotes)
        {
            if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                field.Append('"');
                i++;
            }
            else if (c == '"')
            {
                inQuotes = false;
            }
            else
            {
                field.Append(c);
            }
        }
        else if (c == '"')
        {
            inQuotes = true;
        }
        else if (c == ',')
        {
            fields.Add(field.ToString());
            field.Clear();
        }
        else
        {
            field.Append(c);
        }
    }

    fields.Add(field.ToString());
    return fields.ToArray();
}

static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

// Where to send embeddings requests, read from user secrets
class EmbeddingSettings
{
    public string Url { get; set; } = "";
    public string Key { get; set; } = "";
    public string Deployment { get; set; } = "";
}

// One entry in data/vectors.json, keyed by Game name.
// Hash is the SHA-256 of the text that was embedded, so a changed Blurb is spotted on the next run.
class GameVector
{
    public string Hash { get; set; } = "";
    public float[] Vector { get; set; } = [];
}

// One Game as stored in the Cosmos DB games container. Serialized in camelCase, so "embedding"
// matches the container's vector policy path.
class CosmosGameDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Designer { get; set; } = "";
    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public int PlaytimeMinutes { get; set; }
    public List<string> Categories { get; set; } = [];
    public List<string> Mechanics { get; set; } = [];

    // Categories and mechanics as one string, the field hybrid search scores with FullTextScore (S-02).
    // Full-text search in Cosmos matches nothing on an array path like /mechanics/[], so it needs a string.
    public string Facts { get; set; } = "";

    public double? Weight { get; set; }
    public string? Blurb { get; set; }
    public float[] Embedding { get; set; } = [];

    // The hash of the text the vector was made from (the same hash as in data/vectors.json).
    // The change feed handler compares it with the document's current text to tell a real edit
    // from a write that only replaced the vector (A-10).
    public string? EmbeddingTextHash { get; set; }
}

// One Owned Game as read from raw/collection-owned.xml (see ReadCollection)
class SeedOwnedGame
{
    public string Name { get; set; } = "";
    public DateTime DateAcquired { get; set; }
    public string Condition { get; set; } = "";   // a Condition enum name, e.g. "Good"
    public string Notes { get; set; } = "";
}

// One Owned Game as stored in the ownedGames container. A copy of the web project's OwnedGameDocument
// (MeepleLedger/Storage/MeepleDocuments.cs): same properties, serialized in camelCase. Keep them in step.
class SeedOwnedGameDocument
{
    public string Id { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string GameName { get; set; } = "";
    public string DateAcquired { get; set; } = "";
    public string Condition { get; set; } = "";
    public string? Notes { get; set; }
}

// What CountDocuments found in one container (import-ledger)
class DocumentCount
{
    public int Count { get; set; }
    public double Charge { get; set; }
}

// One Game as stored in the PostgreSQL games table (infra/postgres/games-schema.sql).
// upload-redis writes the same rows as Redis hashes.
class PostgresGameRow
{
    public string Name { get; set; } = "";
    public string Designer { get; set; } = "";
    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public int PlaytimeMinutes { get; set; }
    public List<string> Categories { get; set; } = [];
    public List<string> Mechanics { get; set; } = [];
    public float? Weight { get; set; }
    public string? Blurb { get; set; }
    public float[] Embedding { get; set; } = [];
    public string EmbeddingTextHash { get; set; } = "";
}

// One store's runs in compare-providers: each run's time, what one Ask costs, and the last answer
class ProviderTiming
{
    public string Name { get; set; } = "";
    public List<double> Milliseconds { get; set; } = [];
    public string CostPerAsk { get; set; } = "";
    public List<string> TopNames { get; set; } = [];
}

// One Table explain-postgres runs the Tonight query for
class ExplainTable
{
    public string Label { get; set; } = "";
    public int PlayerCount { get; set; }
    public int MinutesAvailable { get; set; }
    public bool OwnedOnly { get; set; }
}

// One Ask in load-postgres: how long it took, or why it failed
class LoadAskResult
{
    public double Milliseconds { get; set; }
    public string? Error { get; set; }
}

// What measure-pgvector records for one way of running the searches
class PgvectorRun
{
    public double AverageMs { get; set; }
    public double Recall { get; set; }        // 0 to 1, against the sequential scan's results
    public string PlanSummary { get; set; } = "";
}

// One row of "SELECT c.id FROM c"
class IdOnly
{
    public string Id { get; set; } = "";
}

// What measure-ru records for one query: the charge of the last run, the cheapest and dearest of
// all runs, and the first id returned (used as the sample Game for the point reads)
class QueryCharge
{
    public double Last { get; set; }
    public double Lowest { get; set; }
    public double Highest { get; set; }
    public int Rows { get; set; }
    public int Pages { get; set; }
    public string FirstId { get; set; } = "";
}

// The Seeder's spans (A-31): only watch-cosmos makes any. With tracing off nothing listens,
// StartActivity returns null, and every "span?." call does nothing.
static class SeederTelemetry
{
    public const string SourceName = "MeepleLedger.ChangeFeed";

    public static readonly ActivitySource Source = new ActivitySource(SourceName);
}

// The intent to re-embed one Game, as the change feed handler records it (A-10). A-19 sends the
// same shape over Service Bus, so it carries a schema version from the start. MeepleLedger.Worker
// has a copy of this class that reads it; keep the two in step.
class EmbedRequest
{
    public int SchemaVersion { get; set; }
    public string GameId { get; set; } = "";
    public string TextHash { get; set; } = "";
    public string Reason { get; set; } = "";       // "new" or "changed"
    public DateTime DetectedAtUtc { get; set; }
}

// Where the change feed handler records an EmbedRequest. This is the seam: the handler only knows
// it can send a request, so A-19 put a Service Bus queue behind it without touching the handler.
interface IEmbedRequestQueue
{
    Task SendAsync(EmbedRequest request);
}

// A-19: publishes each request to the Service Bus queue, where MeepleLedger.Worker picks it up.
// The message is built the same way as the worker's own "send" command builds it.
class ServiceBusEmbedRequestQueue : IEmbedRequestQueue
{
    // The queue provision-servicebus.ps1 creates. MeepleLedger.Worker reads the same name.
    public const string QueueName = "embed-requests";

    // camelCase on the wire, the same as the worker (which would read any case)
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    private readonly ServiceBusSender sender;

    public ServiceBusEmbedRequestQueue(ServiceBusSender sender)
    {
        this.sender = sender;
    }

    public async Task SendAsync(EmbedRequest request)
    {
        // A-31: a Producer span for the send. The Service Bus SDK's own send span hangs under it.
        using Activity? span = SeederTelemetry.Source.StartActivity("Queue embed request", ActivityKind.Producer);
        span?.SetTag("meepleledger.game", request.GameId);
        span?.SetTag("messaging.system", "servicebus");
        span?.SetTag("messaging.destination.name", QueueName);

        ServiceBusMessage message = new ServiceBusMessage(JsonSerializer.Serialize(request, JsonOptions));
        message.ContentType = "application/json";
        message.Subject = "EmbedRequest";

        // The schema version is in the body, and again as a property so it can be read (or
        // filtered on) without parsing the body
        message.ApplicationProperties["schemaVersion"] = request.SchemaVersion;

        // The trace context, so the worker or the Function joins this trace. A message carries no
        // trace unless it is put there; without this the two sides are unrelated traces. Activity.Id
        // is already in the W3C traceparent format: 00-<trace id>-<span id>-<flags>.
        if (span != null)
        {
            message.ApplicationProperties["traceparent"] = span.Id;
        }

        await sender.SendMessageAsync(message);
        Console.WriteLine($"  '{request.GameId}': {request.Reason}, embed request published (hash {request.TextHash.Substring(0, 8)})");
    }
}

// The stand-in before A-19, still used when there is no Service Bus secret: writes a log line and
// appends the request to a local JSON Lines file (one request per line), so the queued requests
// are still there after the host stops.
class LocalEmbedRequestQueue : IEmbedRequestQueue
{
    private readonly string filePath;

    // The processor can call the handler for different leases at the same time
    private readonly object fileLock = new object();

    public LocalEmbedRequestQueue(string filePath)
    {
        this.filePath = filePath;
    }

    public Task SendAsync(EmbedRequest request)
    {
        string line = JsonSerializer.Serialize(request);

        lock (fileLock)
        {
            File.AppendAllText(filePath, line + Environment.NewLine);
        }

        Console.WriteLine($"  '{request.GameId}': {request.Reason}, embed request queued (hash {request.TextHash.Substring(0, 8)})");
        return Task.CompletedTask;
    }
}
