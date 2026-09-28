
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;

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