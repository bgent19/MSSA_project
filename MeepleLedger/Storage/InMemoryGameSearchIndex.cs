using System.Diagnostics;
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

    private readonly IGameCatalogSource _catalogSource;
    private readonly IMeepleStore _store;

    public InMemoryGameSearchIndex(IGameCatalogSource catalogSource, IMeepleStore store)
    {
        _catalogSource = catalogSource;
        _store = store;
    }

    // Word matching only: there are no Blurbs and no embeddings here.
    public bool UnderstandsMeaning => false;

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

        // Steps 1 and 2 are the "query" of this Provider, and step 3 onwards its ranking, so they
        // get the same two spans as the cloud Providers (A-31) and their traces compare side by side.
        List<ScoredGame> scoredGames = [];
        using (Activity? querySpan = SearchTelemetry.Source.StartActivity("Query the Search Index"))
        {
            querySpan?.SetTag(SearchTelemetry.ProviderTag, "inmemory");

            // Step 1: apply the Table first, so ranking only ever sees Games that are allowed.
            List<Game> candidates = FindGamesTheTableAllows(table);

            // Step 2: score every candidate against the words of the Ask.
            List<string> askWords = AskWords.FindUsefulWords(ask.Text);
            foreach (Game game in candidates)
            {
                scoredGames.Add(ScoreGame(game, askWords));
            }

            querySpan?.SetTag(SearchTelemetry.ResultCountTag, scoredGames.Count);
        }

        using Activity? rankSpan = SearchTelemetry.Source.StartActivity("Rank");

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

        rankSpan?.SetTag(SearchTelemetry.ResultCountTag, recommendations.Count);
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
            if (AskWords.HasWordStartingWith(game.Name, askWord))
            {
                scored.Score += NameMatchPoints;
                scored.AddMatch("its name");
            }

            foreach (string category in game.Categories)
            {
                if (AskWords.HasWordStartingWith(category, askWord))
                {
                    scored.Score += CategoryMatchPoints;
                    scored.AddMatch("the " + category + " category");
                }
            }

            foreach (string mechanic in game.Mechanics)
            {
                if (AskWords.HasWordStartingWith(mechanic, askWord))
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
            reason.Append("Matches your Ask on " + ReasonText.JoinWithAnd(scored.Matches) + ". ");
        }
        else
        {
            reason.Append("Nothing in your Ask matched its name, categories or mechanics, but it fits your Table. ");
        }

        reason.Append(ReasonText.DescribeFit(scored.Game, table));

        if (_store.Collection.Owns(scored.Game.Name))
        {
            reason.Append(" It is on your shelf.");
        }

        return reason.ToString();
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
