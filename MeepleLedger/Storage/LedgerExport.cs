using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// The export file (S-06): the Collection and the Play Log as one JSON file, so the ledger survives
// losing the Cosmos account, the subscription or Azure altogether. The app writes it (GET /export) and
// the Seeder's import-ledger reads it back into an empty store. The Seeder compiles this same file (a
// linked file in MeepleLedger.Seeder.csproj), so the two halves can't drift apart.
//
// The file is personal data (it holds your friends' names): never commit one. .gitignore has
// meepleledger-export-*.json.
//
// Rows use the same shapes as the Cosmos documents (MeepleDocuments.cs), so an imported row is
// exactly what CosmosMeepleStore would have written.

public class LedgerExportFile
{
    public int FormatVersion { get; set; }
    public string ExportedOn { get; set; } = "";
    public string OwnerName { get; set; } = "";

    // Games the Owned Games and Plays refer to that are not in the compiled Catalog (CatalogSeed),
    // with their full facts, so a fresh store can be given them before the rows that use them.
    // Always empty until Games can be added at runtime (S-07).
    public List<ExportedGame> Games { get; set; } = [];

    public List<OwnedGameDocument> OwnedGames { get; set; } = [];
    public List<PlayDocument> Plays { get; set; } = [];
}

// One Game's facts, for a Game outside the compiled Catalog.
public class ExportedGame
{
    public string Name { get; set; } = "";
    public string? Designer { get; set; }
    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public int PlaytimeMinutes { get; set; }
    public List<string> Categories { get; set; } = [];
    public List<string> Mechanics { get; set; } = [];
    public double? Weight { get; set; }
    public string? Blurb { get; set; }
}

// What an export file turns into once every row has been through the domain classes.
public class LoadedLedger
{
    public required GameCatalog Catalog { get; init; }
    public required GameCollection Collection { get; init; }
    public required PlayLog PlayLog { get; init; }
}

public static class LedgerExport
{
    // The only format this code reads. A change to the file's shape gets a new number, and import
    // refuses any number it doesn't know rather than guessing.
    public const int FormatVersion = 1;

    // Indented, camelCase, "\n" line endings (not Environment.NewLine, so the app on Linux and on
    // Windows write the same bytes), and accented names left readable instead of escaped.
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The download's file name, e.g. meepleledger-export-2026-10-29.json.
    public static string FileName(DateTime exportedOn)
    {
        return "meepleledger-export-" + exportedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json";
    }

    // Builds the file from the Collection and the Play Log. Rows are in a fixed order (Owned Games by
    // name, Plays by date then id), so two exports of the same data are byte-identical.
    public static LedgerExportFile Build(GameCollection collection, PlayLog playLog, IEnumerable<Game> compiledCatalog, DateTime exportedOn)
    {
        string ownerName = playLog.OwnerName;

        List<OwnedGameDocument> ownedGames = [];
        foreach (OwnedGame ownedGame in collection.Games)
        {
            ownedGames.Add(MeepleDocuments.FromOwnedGame(ownedGame, ownerName));
        }
        ownedGames.Sort(CompareOwnedGames);

        List<PlayDocument> plays = [];
        foreach (Play play in playLog.RecentFirst())
        {
            plays.Add(MeepleDocuments.FromPlay(play, ownerName));
        }
        plays.Sort(ComparePlays);

        return new LedgerExportFile
        {
            FormatVersion = FormatVersion,
            ExportedOn = exportedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            OwnerName = ownerName,
            Games = GamesOutsideCatalog(collection, playLog, compiledCatalog),
            OwnedGames = ownedGames,
            Plays = plays,
        };
    }

    public static string ToJson(LedgerExportFile file)
    {
        // The file ends with a newline, like any text file.
        return JsonSerializer.Serialize(file, JsonOptions) + "\n";
    }

    // Reads a file, refusing one in a format this code doesn't know.
    public static LedgerExportFile FromJson(string json)
    {
        LedgerExportFile? file = JsonSerializer.Deserialize<LedgerExportFile>(json, JsonOptions);
        if (file == null)
        {
            throw new InvalidOperationException("The file is empty.");
        }

        if (file.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException(
                "The file is format version " + file.FormatVersion + "; this code only reads version " + FormatVersion + ".");
        }

        if (file.Games == null || file.OwnedGames == null || file.Plays == null)
        {
            throw new InvalidOperationException("The file is missing its games, ownedGames or plays list.");
        }

        return file;
    }

    // Replays every row through the domain classes: the Games first (so the rows that use them can
    // find them), then GameCollection.Add for each Owned Game and PlayLog.Record for each Play. Any
    // row that breaks an invariant throws, so the caller writes nothing. Each Play keeps its Id.
    public static LoadedLedger Load(LedgerExportFile file, IEnumerable<Game> compiledCatalog)
    {
        List<Game> games = [];
        HashSet<string> names = [];
        foreach (Game game in compiledCatalog)
        {
            games.Add(game);
            names.Add(game.Name);
        }

        foreach (ExportedGame exported in file.Games)
        {
            if (!names.Add(exported.Name))
            {
                throw new InvalidOperationException("The file's Game '" + exported.Name + "' is already in the Catalog.");
            }

            games.Add(ToGame(exported));
        }

        GameCatalog catalog = new GameCatalog(games);

        GameCollection collection = new GameCollection();
        foreach (OwnedGameDocument document in file.OwnedGames)
        {
            collection.Add(MeepleDocuments.ToOwnedGame(document, catalog));
        }

        // PlayLog.Record doesn't look at Ids, but two Plays with one Id would be one document in Cosmos.
        PlayLog playLog = new PlayLog { OwnerName = file.OwnerName };
        HashSet<string> playIds = [];
        foreach (PlayDocument document in file.Plays)
        {
            if (!playIds.Add(document.Id))
            {
                throw new InvalidOperationException("Two Plays in the file have the id '" + document.Id + "'.");
            }

            playLog.Record(MeepleDocuments.ToPlay(document, catalog));
        }

        return new LoadedLedger { Catalog = catalog, Collection = collection, PlayLog = playLog };
    }

    // Every Game an Owned Game or a Play names that the compiled Catalog doesn't have, by name.
    private static List<ExportedGame> GamesOutsideCatalog(GameCollection collection, PlayLog playLog, IEnumerable<Game> compiledCatalog)
    {
        HashSet<string> compiledNames = [];
        foreach (Game game in compiledCatalog)
        {
            compiledNames.Add(game.Name);
        }

        List<Game> referenced = [];
        foreach (OwnedGame ownedGame in collection.Games)
        {
            referenced.Add(ownedGame.Game);
        }
        foreach (Play play in playLog.RecentFirst())
        {
            referenced.Add(play.Game);
        }

        List<ExportedGame> outside = [];
        HashSet<string> added = [];
        foreach (Game game in referenced)
        {
            if (compiledNames.Contains(game.Name) || added.Contains(game.Name))
            {
                continue;
            }

            added.Add(game.Name);
            outside.Add(FromGame(game));
        }

        outside.Sort(CompareGames);
        return outside;
    }

    private static ExportedGame FromGame(Game game)
    {
        return new ExportedGame
        {
            Name = game.Name,
            Designer = game.Designer,
            MinPlayers = game.MinPlayers,
            MaxPlayers = game.MaxPlayers,
            PlaytimeMinutes = game.PlaytimeMinutes,
            Categories = [.. game.Categories],
            Mechanics = [.. game.Mechanics],
            Weight = game.Weight,
            Blurb = game.Blurb,
        };
    }

    private static Game ToGame(ExportedGame exported)
    {
        return new Game
        {
            Name = exported.Name,
            Designer = exported.Designer,
            MinPlayers = exported.MinPlayers,
            MaxPlayers = exported.MaxPlayers,
            PlaytimeMinutes = exported.PlaytimeMinutes,
            Categories = [.. exported.Categories],
            Mechanics = [.. exported.Mechanics],
            Weight = exported.Weight,
            Blurb = exported.Blurb,
        };
    }

    // Ordinal comparisons, so the order never depends on the machine's culture.
    private static int CompareGames(ExportedGame first, ExportedGame second)
    {
        return string.CompareOrdinal(first.Name, second.Name);
    }

    private static int CompareOwnedGames(OwnedGameDocument first, OwnedGameDocument second)
    {
        return string.CompareOrdinal(first.GameName, second.GameName);
    }

    // "yyyy-MM-dd" strings sort the same as the dates they hold.
    private static int ComparePlays(PlayDocument first, PlayDocument second)
    {
        int byDate = string.CompareOrdinal(first.PlayedOn, second.PlayedOn);
        if (byDate != 0)
        {
            return byDate;
        }

        return string.CompareOrdinal(first.Id, second.Id);
    }
}
