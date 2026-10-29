using System.Diagnostics;
using System.Text;
using MeepleLedger.Domain;
using Microsoft.Azure.Cosmos;

namespace MeepleLedger.Storage;

// The Cosmos DB for NoSQL Search Index Provider (A-08). Each Game is a document in the games
// container with its Blurb's embedding at /embedding (loaded by "MeepleLedger.Seeder -- upload-cosmos").
// An Ask is embedded with the same deployment and the container is searched with VectorDistance.
//
// Contract notes (see IGameSearchIndex):
//   - The Table is applied in the WHERE clause, so Cosmos only ranks Games the Table admits and
//     TOP never counts a Game that would be thrown away afterwards.
//   - The ordering is Cosmos's. ORDER BY VectorDistance (or ORDER BY RANK) cannot take a second
//     sort key, so the Name tie-break is applied here, and only between neighbours with exactly
//     equal vector scores. That only reorders results; it never drops or adds one.
//
// Hybrid search (S-02): vector distance alone ranked competitive Games above obvious co-op ones
// for a short Ask like "quick co-op for three" (A-34). When the Ask names something a category or
// mechanic in the Catalog starts with, the query ranks by RRF (reciprocal rank fusion) of the vector
// distance and a full-text (BM25) score over /facts, the categories and mechanics as one string
// (FullTextTerms picks the words; full-text search matches nothing on the /categories array itself). An Ask that names nothing of the kind (a vague mood) is ranked by the vector alone.
public class CosmosGameSearchIndex : IGameSearchIndex
{
    // How many categories and mechanics to name in a Reason when none of the Ask's words matched.
    private const int FactsToNameInReason = 2;

    private readonly Container _container;
    private readonly AskEmbedder _embedder;
    private readonly IGameCatalogSource _catalogSource;
    private readonly IMeepleStore _store;
    private readonly ILogger<CosmosGameSearchIndex> _logger;

    // CosmosClient is a singleton (see Program.cs); this class only holds a Container from it.
    public CosmosGameSearchIndex(
        CosmosClient client,
        AskEmbedder embedder,
        IGameCatalogSource catalogSource,
        IMeepleStore store,
        IConfiguration configuration,
        ILogger<CosmosGameSearchIndex> logger)
    {
        string? databaseName = configuration["Cosmos:Database"];
        string? containerName = configuration["Cosmos:Container"];

        if (string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(containerName))
        {
            throw new InvalidOperationException("The Cosmos Search Index needs Cosmos:Database and Cosmos:Container settings.");
        }

        _container = client.GetContainer(databaseName, containerName);
        _embedder = embedder;
        _catalogSource = catalogSource;
        _store = store;
        _logger = logger;
    }

    // The Ask is compared with embedded Blurbs, so it is matched by meaning.
    public bool UnderstandsMeaning => true;

    public async Task<IReadOnlyList<Recommendation>> RecommendAsync(Ask ask, Table table, int maxResults)
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

        List<string> ownedNames = FindOwnedNames();

        // Nothing on the shelf means nothing can pass an owned-only Table; no need to ask Cosmos.
        if (table.OwnedOnly && ownedNames.Count == 0)
        {
            return [];
        }

        float[] askVector = await _embedder.EmbedAsync(ask.Text);
        List<string> fullTextTerms = FullTextTerms.Find(ask.Text, _catalogSource.Catalog.Games);

        // A-31: the Cosmos SDK's own span for the query sits under this one (Program.cs turns the
        // SDK's tracing on).
        List<SearchResult> results;
        using (Activity? querySpan = SearchTelemetry.Source.StartActivity("Query the Search Index"))
        {
            querySpan?.SetTag(SearchTelemetry.ProviderTag, "cosmos");
            querySpan?.SetTag("meepleledger.cosmos.full_text_terms", fullTextTerms.Count);

            QueryDefinition query = BuildQuery(askVector, fullTextTerms, table, ownedNames, maxResults);
            results = await RunQueryAsync(query, ask, querySpan);

            querySpan?.SetTag(SearchTelemetry.ResultCountTag, results.Count);
        }

        using Activity? rankSpan = SearchTelemetry.Source.StartActivity("Rank");

        PutEqualScoresInNameOrder(results);

        List<string> askWords = AskWords.FindUsefulWords(ask.Text);
        List<Recommendation> recommendations = [];

        foreach (SearchResult result in results)
        {
            Game? game = _catalogSource.Catalog.Find(result.Id);
            if (game == null)
            {
                // A Game added at runtime (S-07) while this app runs on the compiled Catalog
                // (MeepleStore:Provider=InMemory). The two Catalogs drift by design (ADR-0005), so it
                // is left out rather than failing the Ask. With the Cosmos store the Catalog is read
                // from the same container and this doesn't happen.
                _logger.LogWarning(
                    "Cosmos returned '{Game}', which is not in this app's Catalog; left out. If it should be, run: dotnet run --project MeepleLedger.Seeder -- upload-cosmos",
                    result.Id);
                continue;
            }

            string reason = BuildReason(game, askWords, table);
            recommendations.Add(new Recommendation(game, recommendations.Count + 1, reason));
        }

        rankSpan?.SetTag(SearchTelemetry.ResultCountTag, recommendations.Count);
        return recommendations;
    }

    // The Table's rules, mirrored from Table.Admits, go in the WHERE so they run before ranking.
    // A 0 for players or playtime means unknown in BGG data and does not rule a Game out.
    // Only the id and the score are projected: SELECT * would send 1536 floats back per Game.
    // FullTextScore can only appear in ORDER BY RANK, so the projected score is always the vector's.
    private static QueryDefinition BuildQuery(float[] askVector, List<string> fullTextTerms, Table table, List<string> ownedNames, int maxResults)
    {
        StringBuilder sql = new StringBuilder();
        sql.AppendLine("SELECT TOP @maxResults c.id, VectorDistance(c.embedding, @askVector) AS score");
        sql.AppendLine("FROM c");
        sql.AppendLine("WHERE (c.minPlayers <= 0 OR c.minPlayers <= @playerCount)");
        sql.AppendLine("  AND (c.maxPlayers <= 0 OR c.maxPlayers >= @playerCount)");
        sql.AppendLine("  AND (c.playtimeMinutes <= 0 OR c.playtimeMinutes <= @minutesAvailable)");

        if (table.OwnedOnly)
        {
            sql.AppendLine("  AND ARRAY_CONTAINS(@ownedNames, c.id)");
        }

        // e.g. "@term0, @term1" - one parameter per term, so no Ask text is ever pasted into the SQL.
        string termParameters = "";
        for (int i = 0; i < fullTextTerms.Count; i++)
        {
            if (i > 0)
            {
                termParameters += ", ";
            }

            termParameters += "@term" + i;
        }

        if (fullTextTerms.Count == 0)
        {
            // No ASC or DESC: VectorDistance ordering always puts the closest match first.
            sql.AppendLine("ORDER BY VectorDistance(c.embedding, @askVector)");
        }
        else
        {
            // RRF adds 1 / (k + rank) from each list, so a Game near the top of both wins.
            // ORDER BY RANK needs the TOP above; the WHERE still applies first.
            // The weights count the full-text rank twice: with equal weights the vector's top three
            // still beat every co-op Game for "quick co-op for three"; heavier than 2 pushed
            // Clank! out of "a deck-building game for two" (S-02 benchmark).
            sql.AppendLine("ORDER BY RANK RRF(");
            sql.AppendLine("  VectorDistance(c.embedding, @askVector),");
            sql.AppendLine("  FullTextScore(c.facts, " + termParameters + "),");
            sql.AppendLine("  [1, 2])");
        }

        QueryDefinition query = new QueryDefinition(sql.ToString())
            .WithParameter("@maxResults", maxResults)
            .WithParameter("@askVector", askVector)
            .WithParameter("@playerCount", table.PlayerCount)
            .WithParameter("@minutesAvailable", table.MinutesAvailable);

        if (table.OwnedOnly)
        {
            query = query.WithParameter("@ownedNames", ownedNames);
        }

        for (int i = 0; i < fullTextTerms.Count; i++)
        {
            query = query.WithParameter("@term" + i, fullTextTerms[i]);
        }

        return query;
    }

    // Runs the query, reading every page, and logs the request charge so there is an RU baseline (A-09).
    private async Task<List<SearchResult>> RunQueryAsync(QueryDefinition query, Ask ask, Activity? querySpan)
    {
        List<SearchResult> results = [];
        double requestCharge = 0;

        using FeedIterator<SearchResult> iterator = _container.GetItemQueryIterator<SearchResult>(query);
        while (iterator.HasMoreResults)
        {
            FeedResponse<SearchResult> page = await iterator.ReadNextAsync();
            requestCharge += page.RequestCharge;

            foreach (SearchResult result in page)
            {
                results.Add(result);
            }
        }

        _logger.LogInformation(
            "Cosmos search for \"{Ask}\" returned {Count} Games for {RequestCharge} RU",
            ask.Text, results.Count, requestCharge);

        // The charge on the span too, so a trace shows what the Ask cost, not only how long it took
        querySpan?.SetTag("meepleledger.cosmos.request_charge", requestCharge);

        return results;
    }

    // Cosmos has already ordered the results best first. When two neighbours have exactly the
    // same score, the Name that sorts first (ordinal, ignoring case) goes first, as the
    // IGameSearchIndex contract asks. Results with different scores are never swapped.
    private static void PutEqualScoresInNameOrder(List<SearchResult> results)
    {
        for (int i = 1; i < results.Count; i++)
        {
            int j = i;
            while (j > 0
                   && results[j].Score == results[j - 1].Score
                   && string.Compare(results[j].Id, results[j - 1].Id, StringComparison.OrdinalIgnoreCase) < 0)
            {
                SearchResult earlier = results[j - 1];
                results[j - 1] = results[j];
                results[j] = earlier;
                j--;
            }
        }
    }

    private List<string> FindOwnedNames()
    {
        List<string> names = [];
        foreach (OwnedGame owned in _store.Collection.Games)
        {
            names.Add(owned.Game.Name);
        }

        return names;
    }

    // The Reason names the categories and mechanics behind the match, never the raw distance,
    // which means nothing to a person (and something different in every Provider).
    private string BuildReason(Game game, List<string> askWords, Table table)
    {
        List<string> matched = [];
        foreach (string category in game.Categories)
        {
            if (AskWords.MatchesAny(category, askWords))
            {
                matched.Add("the " + category + " category");
            }
        }

        foreach (string mechanic in game.Mechanics)
        {
            if (AskWords.MatchesAny(mechanic, askWords))
            {
                matched.Add("the " + mechanic + " mechanic");
            }
        }

        StringBuilder reason = new StringBuilder();

        if (matched.Count > 0)
        {
            reason.Append("Close in meaning to your Ask, and matches it on " + ReasonText.JoinWithAnd(matched) + ". ");
        }
        else
        {
            reason.Append("Its description is close in meaning to your Ask");
            reason.Append(DescribeWhatItIs(game));
            reason.Append(". ");
        }

        reason.Append(ReasonText.DescribeFit(game, table));

        if (_store.Collection.Owns(game.Name))
        {
            reason.Append(" It is on your shelf.");
        }

        return reason.ToString();
    }

    // e.g. "; it is tagged Card Game and Fantasy, built on Hand Management and Set Collection"
    private static string DescribeWhatItIs(Game game)
    {
        List<string> categories = FirstFew(game.Categories);
        List<string> mechanics = FirstFew(game.Mechanics);

        if (categories.Count == 0 && mechanics.Count == 0)
        {
            return "";
        }

        string text = "; it is";
        if (categories.Count > 0)
        {
            text += " tagged " + ReasonText.JoinWithAnd(categories);

            if (mechanics.Count > 0)
            {
                text += ",";
            }
        }

        if (mechanics.Count > 0)
        {
            text += " built on " + ReasonText.JoinWithAnd(mechanics);
        }

        return text;
    }

    private static List<string> FirstFew(List<string> items)
    {
        List<string> few = [];
        for (int i = 0; i < items.Count && i < FactsToNameInReason; i++)
        {
            few.Add(items[i]);
        }

        return few;
    }

    // One row of the search query: the Game's id (its Name) and how close it is to the Ask.
    // With cosine distance a higher score is a closer match.
    private class SearchResult
    {
        public string Id { get; set; } = "";
        public double Score { get; set; }
    }
}
