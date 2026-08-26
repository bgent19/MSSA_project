
using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;

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

var lines = File.ReadAllLines("data/boardgames_ranks.csv");
var header = lines[0].Split(',');

var idCol = Array.IndexOf(header, "id");
var rankCol = Array.IndexOf(header, "rank");
var expansionCol = Array.IndexOf(header, "is_expansion");

var rankdIds = new List<(int Id, int Rank)>();

foreach (var line in lines.Skip(1))
{
    var fields = line.Split(',');

    // Bug: Some titles contain commas
    // Resolution: Skip those games
    if(fields.Length != header.Length)
    {
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

            File.AppendAllText(fileName, "        " +
                                        $"new Game {{ Name = {Quote(name)}, " +
                                        $"Designer = {Quote(designer)}, " +
                                        $"MinPlayers = {minPlayers}, " +
                                        $"MaxPlayers = {maxPlayers}, " +
                                        $"PlaytimeMinutes = {playtimeMinutes} }},{Environment.NewLine}");
        }
    }

    string fileFooter = """     
            ];
        }
        """;
    File.AppendAllText(fileName, fileFooter);


    Console.WriteLine($"parsed {parsed} items from {files.Length} files");
    Console.WriteLine($"dropped {dropped} (maxplayers < 1)");
    Console.WriteLine($"wrote MeepleLedger/Data/CatalogSeed.cs ({parsed - dropped} games)");
}

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


static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";