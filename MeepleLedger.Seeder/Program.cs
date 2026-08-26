
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
    emit();
    return 0;
}

string? username = Environment.GetEnvironmentVariable("BGG_USERNAME");
string? token = Environment.GetEnvironmentVariable("BGG_TOKEN");
string outputDir = "raw";
int targetGames = 200;
int batchSize = 20;

Directory.CreateDirectory(outputDir);

File.WriteAllText(Path.Combine(outputDir, ".gitignore"), "*\n!.gitignore\n");

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
        Console.WriteLine("Wait a few minutes and run again - saved batches are skipped.");
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


static void emit()
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
                                        $"PlaytimeMinutes = {playtimeMinutes} }},\n");
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

static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";