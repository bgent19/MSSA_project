using System.Globalization;
using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// The document shapes CosmosMeepleStore writes (S-04). The CosmosClient serializes property names in
// camelCase, so Id is "id" and OwnerName is "ownerName" (the partition key). S-05 and S-06 write the
// same shapes from the Seeder; change them together.
//
// Games are referenced by name, like the seeds' Find(catalog, name). Dates are "yyyy-MM-dd" strings,
// never full timestamps: a UTC conversion would move a late-night Play to the next day.

// One per Owned Game. Id is the Game's name.
public class OwnedGameDocument
{
    public string Id { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string GameName { get; set; } = "";
    public string DateAcquired { get; set; } = "";
    public string Condition { get; set; } = "";
    public string? Notes { get; set; }
}

// One per Play. Id is the Play's Id.
public class PlayDocument
{
    public string Id { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string GameName { get; set; } = "";
    public string PlayedOn { get; set; } = "";
    public int? DurationMinutes { get; set; }
    public string? Location { get; set; }
    public List<PlayerResultDocument> Results { get; set; } = [];
}

public class PlayerResultDocument
{
    public string PlayerName { get; set; } = "";
    public int? Score { get; set; }
    public bool IsWinner { get; set; }
}

// Turns domain objects into documents and back. Building the domain object goes through its
// constructor, so a stored Play that breaks an invariant throws here.
public static class MeepleDocuments
{
    private const string DateFormat = "yyyy-MM-dd";

    public static OwnedGameDocument FromOwnedGame(OwnedGame ownedGame, string ownerName)
    {
        RequireUsableId(ownedGame.Game.Name);

        return new OwnedGameDocument
        {
            Id = ownedGame.Game.Name,
            OwnerName = ownerName,
            GameName = ownedGame.Game.Name,
            DateAcquired = ownedGame.DateAcquired.ToString(DateFormat, CultureInfo.InvariantCulture),
            Condition = ownedGame.Condition.ToString(),
            Notes = ownedGame.Notes,
        };
    }

    public static OwnedGame ToOwnedGame(OwnedGameDocument document, GameCatalog catalog)
    {
        Condition condition;
        if (!Enum.TryParse(document.Condition, out condition))
        {
            throw new InvalidOperationException("Stored Owned Game '" + document.Id + "' has an unknown condition '" + document.Condition + "'.");
        }

        return new OwnedGame
        {
            Game = FindGame(catalog, document.GameName, "Owned Game '" + document.Id + "'"),
            DateAcquired = ParseDate(document.DateAcquired, "Owned Game '" + document.Id + "'"),
            Condition = condition,
            Notes = document.Notes,
        };
    }

    public static PlayDocument FromPlay(Play play, string ownerName)
    {
        List<PlayerResultDocument> results = [];
        foreach (PlayerResult result in play.Results)
        {
            results.Add(new PlayerResultDocument
            {
                PlayerName = result.PlayerName,
                Score = result.Score,
                IsWinner = result.IsWinner,
            });
        }

        return new PlayDocument
        {
            Id = play.Id.ToString(),
            OwnerName = ownerName,
            GameName = play.Game.Name,
            PlayedOn = play.PlayedOn.ToString(DateFormat, CultureInfo.InvariantCulture),
            DurationMinutes = play.DurationMinutes,
            Location = play.Location,
            Results = results,
        };
    }

    public static Play ToPlay(PlayDocument document, GameCatalog catalog)
    {
        string what = "Play '" + document.Id + "'";

        Guid id;
        if (!Guid.TryParse(document.Id, out id))
        {
            throw new InvalidOperationException("Stored " + what + " does not have a valid id.");
        }

        List<PlayerResult> results = [];
        foreach (PlayerResultDocument result in document.Results)
        {
            results.Add(new PlayerResult
            {
                PlayerName = result.PlayerName,
                Score = result.Score,
                IsWinner = result.IsWinner,
            });
        }

        Game game = FindGame(catalog, document.GameName, what);
        DateTime playedOn = ParseDate(document.PlayedOn, what);

        // The constructor checks the seat count and that no Player sits twice.
        return new Play(game, playedOn, results, document.DurationMinutes, document.Location) { Id = id };
    }

    // A stored row naming a Game that isn't in the Catalog fails loudly, never gets skipped.
    private static Game FindGame(GameCatalog catalog, string name, string what)
    {
        foreach (Game game in catalog.Games)
        {
            if (game.Name == name)
            {
                return game;
            }
        }

        throw new InvalidOperationException("Stored " + what + " names '" + name + "', which is not in the Catalog.");
    }

    private static DateTime ParseDate(string text, string what)
    {
        DateTime date;
        if (!DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            throw new InvalidOperationException("Stored " + what + " has a date '" + text + "' that is not " + DateFormat + ".");
        }

        return date;
    }

    // Cosmos refuses an id with / \ ? or #. No Game in the compiled Catalog has one, but a Game
    // added later might; say so plainly instead of failing with a Cosmos error.
    private static void RequireUsableId(string id)
    {
        if (id.Contains('/') || id.Contains('\\') || id.Contains('?') || id.Contains('#'))
        {
            throw new InvalidOperationException("'" + id + "' can't be a document id: it contains / \\ ? or #.");
        }
    }
}
