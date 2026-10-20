using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// Sentence pieces every Search Index uses when it writes a Recommendation's Reason,
// so the Reasons read the same whichever Provider is live.
public static class ReasonText
{
    // e.g. "It seats 3 (plays 1 to 4) and takes about 45 minutes of your 60."
    public static string DescribeFit(Game game, Table table)
    {
        string text = "It seats " + table.PlayerCount;

        if (game.MinPlayers > 0 && game.MaxPlayers > 0)
        {
            text += " (plays " + game.MinPlayers + " to " + game.MaxPlayers + ")";
        }

        if (game.PlaytimeMinutes > 0)
        {
            text += " and takes about " + game.PlaytimeMinutes + " minutes of your " + table.MinutesAvailable + ".";
        }
        else
        {
            text += ", though its playtime is unknown.";
        }

        return text;
    }

    // "A", "A and B", "A, B and C"
    public static string JoinWithAnd(List<string> items)
    {
        if (items.Count == 1)
        {
            return items[0];
        }

        string allButLast = string.Join(", ", items.GetRange(0, items.Count - 1));
        return allButLast + " and " + items[items.Count - 1];
    }
}
