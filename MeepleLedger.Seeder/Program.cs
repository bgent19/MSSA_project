
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

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



    var file = Directory.GetFiles("raw", "collection-owned.xml");
    int parsed = 0;
    
    var items = XDocument.Load(file[0]).Root!.Elements("item");

    foreach (var item in items)
    {
        parsed++;

        string name = item.Elements("name").FirstOrDefault()!.Value;

        string[] time = item.Element("status")!
                            .Attribute("lastmodified")!.Value
                            .Split(' ')[0]
                            .Split('-');

        File.AppendAllText(fileName, "        " +
                                    $"new OwnedGame {{ Game = Find(catalog, {Quote(name)}), " +
                                    $"DateAcquired = new DateTime({int.Parse(time[0])}, {int.Parse(time[1])}, {int.Parse(time[2])}), " +
                                    $"Condition = Condition.Good, " +
                                    $"Notes = {Quote("...")} }},{Environment.NewLine}");
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

        if (!play.Results.Any(r => r.PlayerName == owner))
        {
            throw new Exception($"A play of '{play.Name}' is missing {owner}. PlayLog.Record would throw.");
        }

        if (play.Results.Count > maxPlayers[play.Name])
        {
            throw new Exception($"A play of '{play.Name}' seats {play.Results.Count}, max is {maxPlayers[play.Name]}.");
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

// The text that gets embedded for one Game: name, categories and mechanics as well as the Blurb,
// so a search for "deck building" can match on the mechanic even if the Blurb never says it.
static string BuildEmbeddingText(string name, List<string> categories, List<string> mechanics, string blurb)
{
    var text = new StringBuilder();
    text.AppendLine($"Name: {name}");
    text.AppendLine($"Categories: {string.Join(", ", categories)}");
    text.AppendLine($"Mechanics: {string.Join(", ", mechanics)}");
    text.AppendLine();
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
// slow, metered work are kept apart, so A-19 can swap the local queue for Service Bus.
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

    // Until A-19, "recording the intent" means a log line and a line in a local file
    IEmbedRequestQueue queue = new LocalEmbedRequestQueue("data/embed-requests.jsonl");

    // Called with each batch of changed documents. Throwing from here means the batch is retried,
    // so the handler only does cheap work that cannot really fail.
    async Task HandleChangesAsync(ChangeFeedProcessorContext context, IReadOnlyCollection<CosmosGameDocument> changes, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {changes.Count} change(s) on lease {context.LeaseToken}, {context.Headers.RequestCharge:0.00} RU to read");

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
    Console.WriteLine($"Leases are kept in {databaseName}/{leaseContainerName}. New Games and edited Blurbs are queued to data/embed-requests.jsonl.");
    Console.WriteLine("Press Enter to stop.");
    Console.ReadLine();

    // Stopping cleanly releases the leases, so another instance (or the next run) can pick them up
    await processor.StopAsync();
    Console.WriteLine("Stopped.");
    return 0;
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
    public double? Weight { get; set; }
    public string? Blurb { get; set; }
    public float[] Embedding { get; set; } = [];

    // The hash of the text the vector was made from (the same hash as in data/vectors.json).
    // The change feed handler compares it with the document's current text to tell a real edit
    // from a write that only replaced the vector (A-10).
    public string? EmbeddingTextHash { get; set; }
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

// The intent to re-embed one Game, as the change feed handler records it (A-10). A-19 sends the
// same shape over Service Bus, so it carries a schema version from the start.
class EmbedRequest
{
    public int SchemaVersion { get; set; }
    public string GameId { get; set; } = "";
    public string TextHash { get; set; } = "";
    public string Reason { get; set; } = "";       // "new" or "changed"
    public DateTime DetectedAtUtc { get; set; }
}

// Where the change feed handler records an EmbedRequest. This is the seam: the handler only knows
// it can send a request, so A-19 can put a Service Bus queue behind it without touching the handler.
interface IEmbedRequestQueue
{
    Task SendAsync(EmbedRequest request);
}

// The stand-in until A-19: writes a log line and appends the request to a local JSON Lines file
// (one request per line), so the queued requests are still there after the host stops.
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
