using System.Text;
using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// The default Search Index Provider (ADR-0001). It needs no Azure resources and no embeddings:
// it scores Games by how many words of the Ask appear in their name, categories and mechanics.
//
// It cannot understand meaning and does not pretend to. "Quick co-op for three" finds cooperative
// games through the word "coop", and the Table handles "for three" and "quick"; anything subtler
// than that is out of reach without Blurbs and embeddings.
public class InMemoryGameSearchIndex : IGameSearchIndex
{
    private const int NameMatchPoints = 3;
    private const int CategoryMatchPoints = 2;
    private const int MechanicMatchPoints = 2;

    // Ask words shorter than this are ignored ("a", "of", "me"...).
    private const int ShortestUsefulWord = 3;

    // Common words that say nothing about which Game to pick.
    private static readonly HashSet<string> WordsToIgnore =
    [
        "and", "the", "for", "with", "that", "this", "something", "some", "any", "anything",
        "game", "games", "play", "playing", "want", "what", "should", "can", "could", "would",
        "our", "you", "your", "are", "about", "like", "please", "tonight", "good", "fun",
        "player", "players", "people", "friends", "two", "three", "four", "five", "six",
        "quick", "short", "long", "minutes", "hour", "hours",
    ];

    private readonly IGameCatalogSource _catalogSource;
    private readonly IMeepleStore _store;

    public InMemoryGameSearchIndex(IGameCatalogSource catalogSource, IMeepleStore store)
    {
        _catalogSource = catalogSource;
        _store = store;
    }

    public Task<IReadOnlyList<Recommendation>> RecommendAsync(Ask ask, Table table, int maxResults)
    {
        if (ask == null)
        {
            throw new ArgumentNullException(nameof(ask));
        }

        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        if (maxResults < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), "Ask for at least one Recommendation.");
        }

        // Step 1: apply the Table first, so ranking only ever sees Games that are allowed.
        List<Game> candidates = FindGamesTheTableAllows(table);

        // Step 2: score every candidate against the words of the Ask.
        List<string> askWords = FindUsefulWords(ask.Text);
        List<ScoredGame> scoredGames = [];
        foreach (Game game in candidates)
        {
            scoredGames.Add(ScoreGame(game, askWords));
        }

        // Step 3: best score first; ties go to the Name that sorts first.
        scoredGames.Sort(CompareScoredGames);

        // Step 4: hand out ranks 1, 2, 3... up to maxResults.
        List<Recommendation> recommendations = [];
        for (int i = 0; i < scoredGames.Count && i < maxResults; i++)
        {
            ScoredGame scored = scoredGames[i];
            string reason = BuildReason(scored, table);
            recommendations.Add(new Recommendation(scored.Game, i + 1, reason));
        }

        return Task.FromResult<IReadOnlyList<Recommendation>>(recommendations);
    }

    private List<Game> FindGamesTheTableAllows(Table table)
    {
        List<Game> allowed = [];

        foreach (Game game in _catalogSource.Catalog.Games)
        {
            if (!table.Admits(game))
            {
                continue;
            }

            if (table.OwnedOnly && !_store.Collection.Owns(game.Name))
            {
                continue;
            }

            allowed.Add(game);
        }

        return allowed;
    }

    private static ScoredGame ScoreGame(Game game, List<string> askWords)
    {
        ScoredGame scored = new ScoredGame(game);

        foreach (string askWord in askWords)
        {
            if (HasWordStartingWith(game.Name, askWord))
            {
                scored.Score += NameMatchPoints;
                scored.AddMatch("its name");
            }

            foreach (string category in game.Categories)
            {
                if (HasWordStartingWith(category, askWord))
                {
                    scored.Score += CategoryMatchPoints;
                    scored.AddMatch("the " + category + " category");
                }
            }

            foreach (string mechanic in game.Mechanics)
            {
                if (HasWordStartingWith(mechanic, askWord))
                {
                    scored.Score += MechanicMatchPoints;
                    scored.AddMatch("the " + mechanic + " mechanic");
                }
            }
        }

        return scored;
    }

    // Higher score first. On a tie, alphabetical by Name (ignoring case), then by exact Name,
    // so the order is the same on every run.
    private static int CompareScoredGames(ScoredGame a, ScoredGame b)
    {
        if (a.Score != b.Score)
        {
            return b.Score.CompareTo(a.Score);
        }

        int byName = string.Compare(a.Game.Name, b.Game.Name, StringComparison.OrdinalIgnoreCase);
        if (byName != 0)
        {
            return byName;
        }

        return string.CompareOrdinal(a.Game.Name, b.Game.Name);
    }

    private string BuildReason(ScoredGame scored, Table table)
    {
        StringBuilder reason = new StringBuilder();

        if (scored.Matches.Count > 0)
        {
            reason.Append("Matches your Ask on " + JoinWithAnd(scored.Matches) + ". ");
        }
        else
        {
            reason.Append("Nothing in your Ask matched its name, categories or mechanics, but it fits your Table. ");
        }

        reason.Append(DescribeFit(scored.Game, table));

        if (_store.Collection.Owns(scored.Game.Name))
        {
            reason.Append(" It is on your shelf.");
        }

        return reason.ToString();
    }

    // e.g. "It seats 3 (plays 1 to 4) and takes about 45 minutes of your 60."
    private static string DescribeFit(Game game, Table table)
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
    private static string JoinWithAnd(List<string> items)
    {
        if (items.Count == 1)
        {
            return items[0];
        }

        string allButLast = string.Join(", ", items.GetRange(0, items.Count - 1));
        return allButLast + " and " + items[items.Count - 1];
    }

    // The words of the Ask worth searching for: long enough, not filler, and without a plural "s"
    // so that "cards" still finds "Card Game".
    private static List<string> FindUsefulWords(string text)
    {
        List<string> useful = [];

        foreach (string word in SplitIntoWords(text))
        {
            if (word.Length < ShortestUsefulWord || WordsToIgnore.Contains(word))
            {
                continue;
            }

            string singular = word;
            if (word.Length > ShortestUsefulWord && word.EndsWith('s') && !word.EndsWith("ss"))
            {
                singular = word.Substring(0, word.Length - 1);
            }

            if (!useful.Contains(singular))
            {
                useful.Add(singular);
            }
        }

        return useful;
    }

    // True when any word in the text begins with the given word, so "coop" finds "Cooperative Game".
    private static bool HasWordStartingWith(string text, string start)
    {
        foreach (string word in SplitIntoWords(text))
        {
            if (word.StartsWith(start, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Lower-cases the text and splits it on anything that is not a letter or digit.
    // Hyphens and apostrophes are dropped rather than split on, so "co-op" becomes "coop".
    private static List<string> SplitIntoWords(string text)
    {
        List<string> words = [];
        StringBuilder current = new StringBuilder();

        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (c == '-' || c == '\'')
            {
                // Join the two halves: "co-op" -> "coop".
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    // A Game together with its score for one Ask and the facts that earned it.
    private class ScoredGame
    {
        public Game Game { get; }
        public int Score { get; set; }
        public List<string> Matches { get; } = [];

        public ScoredGame(Game game)
        {
            Game = game;
        }

        public void AddMatch(string match)
        {
            if (!Matches.Contains(match))
            {
                Matches.Add(match);
            }
        }
    }
}
