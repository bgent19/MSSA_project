using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MeepleLedger.Domain;
using StackExchange.Redis;

namespace MeepleLedger.Storage;

// The Azure Managed Redis Search Index Provider (A-17). Each Game is a hash at "game:<name>"
// (loaded by "MeepleLedger.Seeder -- upload-redis") with its Blurb's embedding in the "embedding"
// field as raw bytes: 1536 little-endian float32 values, not a JSON array. The RediSearch index
// "idx:games" covers those hashes with a FLAT (exact) COSINE vector field.
// An Ask is embedded with the same deployment and the index is searched with one FT.SEARCH.
//
// Contract notes (see IGameSearchIndex):
//   - Similarity and the Table are ONE query: the Table is the filter in front of "=>", and KNN
//     only ranks the hashes that pass it. Nothing is filtered in C#.
//   - FLAT, not HNSW: at 200 vectors a full scan is microseconds, and an exact search cannot lose
//     Games to a filter the way HNSW did in Postgres (A-13), so no fallback is needed.
//   - The vector ordering is the ordering. The Name tie-break is applied here, only between
//     neighbours with exactly equal distances.
//
// Clustering: the instance is EnterpriseCluster, so the hashes are spread over shards. With that
// policy the search module runs distributed: each shard indexes its own hashes and FT.SEARCH is sent
// to every shard and merged, so no hash tags are needed to keep index and data together.
public class RedisGameSearchIndex : IGameSearchIndex
{
    // The Seeder creates the index under this name over this key prefix; keep the two in step.
    public const string IndexName = "idx:games";

    private readonly IConnectionMultiplexer _redis;
    private readonly AskEmbedder _embedder;
    private readonly IGameCatalogSource _catalogSource;
    private readonly IMeepleStore _store;
    private readonly ILogger<RedisGameSearchIndex> _logger;

    // The ConnectionMultiplexer is a singleton (see Program.cs), shared with the Recommendation cache.
    public RedisGameSearchIndex(
        IConnectionMultiplexer redis,
        AskEmbedder embedder,
        IGameCatalogSource catalogSource,
        IMeepleStore store,
        ILogger<RedisGameSearchIndex> logger)
    {
        _redis = redis;
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

        // Nothing on the shelf means nothing can pass an owned-only Table; no need to ask Redis.
        if (table.OwnedOnly && ownedNames.Count == 0)
        {
            return [];
        }

        float[] askVector = await _embedder.EmbedAsync(ask.Text);

        List<SearchResult> results = await SearchAsync(askVector, table, ownedNames, maxResults, ask);

        PutEqualDistancesInNameOrder(results);

        List<string> askWords = AskWords.FindUsefulWords(ask.Text);
        List<Recommendation> recommendations = [];

        foreach (SearchResult result in results)
        {
            Game game = FindGame(result.Name);
            string reason = BuildReason(game, result.Blurb, askWords, table);
            recommendations.Add(new Recommendation(game, recommendations.Count + 1, reason));
        }

        return recommendations;
    }

    // The query string that answers an Ask. The Seeder's compare-providers step runs a copy of it,
    // so keep the two in step. For example, 3 players, owned only, 10 results:
    //
    //   (@min_players:[-inf $players] (@max_players:[-inf 0] | @max_players:[$players +inf])
    //    @playtime_minutes:[-inf $minutes] @name:{Azul|Ticket\ to\ Ride})=>[KNN 10 @embedding $askVector AS distance]
    //
    // The part before "=>" is the Table: terms side by side are AND, "|" is OR. It mirrors
    // Table.Admits, where 0 means unknown and does not rule a Game out. For min_players and
    // playtime_minutes a 0 already falls inside [-inf $players] and [-inf $minutes] (a Table has at
    // least one player and one minute), so only max_players needs the explicit OR.
    //
    // k goes into the string itself, not into PARAMS. The vector and the numbers go into PARAMS.
    private static string BuildQuery(Table table, List<string> ownedNames, int maxResults)
    {
        StringBuilder query = new StringBuilder();
        query.Append('(');
        query.Append("@min_players:[-inf $players]");
        query.Append(" (@max_players:[-inf 0] | @max_players:[$players +inf])");
        query.Append(" @playtime_minutes:[-inf $minutes]");

        if (table.OwnedOnly)
        {
            // The Collection lives in the app, so its names are sent as a tag list: {A|B|C}
            query.Append(" @name:{");
            for (int i = 0; i < ownedNames.Count; i++)
            {
                if (i > 0)
                {
                    query.Append('|');
                }
                query.Append(EscapeTagValue(ownedNames[i]));
            }
            query.Append('}');
        }

        query.Append(")=>[KNN ");
        query.Append(maxResults.ToString(CultureInfo.InvariantCulture));
        query.Append(" @embedding $askVector AS distance]");
        return query.ToString();
    }

    // In a tag query, spaces and punctuation have meanings of their own ("|" is OR, "-" is NOT,
    // a space ends the value...), so every ASCII character that is not a letter or digit gets a
    // backslash. "Sea Salt & Paper" becomes "Sea\ Salt\ \&\ Paper".
    // Characters outside ASCII are left alone: they have no meaning in the query, and a backslash in
    // front of one stops it matching ("Dune: Imperium – Uprising", with an en dash, found nothing).
    private static string EscapeTagValue(string value)
    {
        StringBuilder escaped = new StringBuilder();
        foreach (char c in value)
        {
            if (c < 128 && !char.IsLetterOrDigit(c))
            {
                escaped.Append('\\');
            }
            escaped.Append(c);
        }

        return escaped.ToString();
    }

    // RediSearch only matches a vector sent as raw bytes: float32, little-endian, one after another.
    // A vector sent as text ("[0.1, 0.2, ...]") is not an error, it just never matches anything.
    private static byte[] ToBytes(float[] vector)
    {
        byte[] bytes = new byte[vector.Length * 4];
        for (int i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), vector[i]);
        }

        return bytes;
    }

    private async Task<List<SearchResult>> SearchAsync(float[] askVector, Table table, List<string> ownedNames, int maxResults, Ask ask)
    {
        // FT.SEARCH idx:games <query>
        //   PARAMS 6 players <n> minutes <n> askVector <bytes>
        //   SORTBY distance ASC         closest first (with COSINE, a lower distance is a closer match)
        //   LIMIT 0 <maxResults>        the default page is 10, so ask for all k
        //   RETURN 3 name blurb distance    never the 6 KB vector
        //   DIALECT 2                   needed for the KNN syntax and for $params
        object[] arguments =
        [
            IndexName,
            BuildQuery(table, ownedNames, maxResults),
            "PARAMS", "6",
            "players", table.PlayerCount.ToString(CultureInfo.InvariantCulture),
            "minutes", table.MinutesAvailable.ToString(CultureInfo.InvariantCulture),
            "askVector", ToBytes(askVector),
            "SORTBY", "distance", "ASC",
            "LIMIT", "0", maxResults.ToString(CultureInfo.InvariantCulture),
            "RETURN", "3", "name", "blurb", "distance",
            "DIALECT", "2",
        ];

        RedisResult reply;
        try
        {
            reply = await _redis.GetDatabase().ExecuteAsync("FT.SEARCH", arguments);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("no such index", StringComparison.OrdinalIgnoreCase)
                                              || ex.Message.Contains("unknown index", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Redis index " + IndexName + " does not exist. Run: dotnet run --project MeepleLedger.Seeder -- upload-redis", ex);
        }

        List<SearchResult> results = ReadSearchReply(reply);

        _logger.LogInformation(
            "Redis vector search for \"{Ask}\" returned {Count} Games", ask.Text, results.Count);

        return results;
    }

    // The reply (RESP2) is one flat array: the total count, then for each hash its key followed by
    // an array of field names and values:
    //   [ 2, "game:Azul", ["name", "Azul", "blurb", "...", "distance", "0.41"], "game:Patchwork", [...] ]
    // A hash with no Blurb simply has no "blurb" pair.
    // Under RESP3 the reply is a map instead; Program.cs asks for RESP2 so this shape is what comes back.
    private static List<SearchResult> ReadSearchReply(RedisResult reply)
    {
        RedisResult[] items = (RedisResult[])reply!;
        List<SearchResult> results = [];

        if (items.Length == 0 || items[0].Resp2Type != ResultType.Integer)
        {
            throw new InvalidOperationException(
                "FT.SEARCH did not answer in the RESP2 shape. Is the Redis connection set to Protocol = RedisProtocol.Resp2?");
        }

        for (int i = 1; i + 1 < items.Length; i += 2)
        {
            RedisResult[] fields = (RedisResult[])items[i + 1]!;
            SearchResult result = new SearchResult();

            for (int f = 0; f + 1 < fields.Length; f += 2)
            {
                string fieldName = fields[f].ToString() ?? "";
                string fieldValue = fields[f + 1].ToString() ?? "";

                if (fieldName == "name")
                {
                    result.Name = fieldValue;
                }
                else if (fieldName == "blurb")
                {
                    result.Blurb = fieldValue;
                }
                else if (fieldName == "distance")
                {
                    result.Distance = double.Parse(fieldValue, CultureInfo.InvariantCulture);
                }
            }

            results.Add(result);
        }

        return results;
    }

    // Redis has already ordered the results closest first. When two neighbours have exactly the
    // same distance, the Name that sorts first (ordinal, ignoring case) goes first, as the
    // IGameSearchIndex contract asks. Results with different distances are never swapped.
    private static void PutEqualDistancesInNameOrder(List<SearchResult> results)
    {
        for (int i = 1; i < results.Count; i++)
        {
            int j = i;
            while (j > 0
                   && results[j].Distance == results[j - 1].Distance
                   && string.Compare(results[j].Name, results[j - 1].Name, StringComparison.OrdinalIgnoreCase) < 0)
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

    // Hashes are keyed by Game name. A name that is not in the Catalog means Redis is out of date
    // with the seed; fail loudly rather than quietly return fewer Recommendations.
    private Game FindGame(string name)
    {
        foreach (Game game in _catalogSource.Catalog.Games)
        {
            if (game.Name == name)
            {
                return game;
            }
        }

        throw new InvalidOperationException(
            "Redis returned '" + name + "', which is not in the Catalog. Run: dotnet run --project MeepleLedger.Seeder -- upload-redis");
    }

    // The Reason quotes the part of the Blurb behind the match (the Blurb is what was embedded),
    // never the raw distance, which means nothing to a person.
    private string BuildReason(Game game, string? blurb, List<string> askWords, Table table)
    {
        StringBuilder reason = new StringBuilder();
        BlurbQuote? quote = BlurbQuote.Find(blurb, askWords);

        if (quote == null)
        {
            reason.Append("Close in meaning to your Ask. ");
        }
        else if (quote.MatchesAskWords)
        {
            reason.Append("Close in meaning to your Ask; its description says “" + quote.Sentence + "”" + StopAfter(quote.Sentence) + " ");
        }
        else
        {
            reason.Append("Its description is close in meaning to your Ask: “" + quote.Sentence + "”" + StopAfter(quote.Sentence) + " ");
        }

        reason.Append(ReasonText.DescribeFit(game, table));

        if (_store.Collection.Owns(game.Name))
        {
            reason.Append(" It is on your shelf.");
        }

        return reason.ToString();
    }

    // A quote that already ends a sentence needs no full stop after it; one cut from a heading does.
    private static string StopAfter(string sentence)
    {
        char last = sentence[sentence.Length - 1];
        if (last == '.' || last == '!' || last == '?' || last == '…')
        {
            return "";
        }

        return ".";
    }

    // One hit of the search: the Game's name, its Blurb and how far it is from the Ask.
    // With COSINE a lower distance is a closer match.
    private class SearchResult
    {
        public string Name { get; set; } = "";
        public string? Blurb { get; set; }
        public double Distance { get; set; }
    }
}
