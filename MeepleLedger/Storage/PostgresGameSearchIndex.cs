using System.Diagnostics;
using System.Globalization;
using System.Text;
using MeepleLedger.Domain;
using Npgsql;
using Pgvector;

namespace MeepleLedger.Storage;

// The Azure Database for PostgreSQL Search Index Provider (A-13). Each Game is a row in the games
// table (infra/postgres/games-schema.sql, loaded by "MeepleLedger.Seeder -- upload-postgres") with its
// Blurb's embedding in a vector(1536) column under an HNSW vector_cosine_ops index.
// An Ask is embedded with the same deployment and the table is searched with <=> (cosine distance).
//
// Contract notes (see IGameSearchIndex):
//   - Similarity and the Table are ONE query: the Table is the WHERE, owned-only is a JOIN against
//     the Collection's names, and ORDER BY ... LIMIT does the ranking. Nothing is filtered in C#.
//   - HNSW applies the WHERE after it walks the graph, so a selective Table can leave fewer than
//     maxResults rows even when more Games fit (the filtered-ANN recall trap). Two defences:
//     hnsw.ef_search is raised for this query, and a short answer is run again as an exact scan.
//     The contract allows fewer results only when fewer Games fit, so the exact scan has the last word.
//   - ORDER BY distance is the ordering. A second sort key could stop the planner using the vector
//     index, so the Name tie-break is applied here, only between neighbours with equal distances.
//
// Connections (A-14): every Ask borrows a connection from the one pooled NpgsqlDataSource and
// gives it back at the end of SearchAsync. The repeated commands are prepared, so the server
// parses and plans them once per pooled connection instead of once per Ask. Prepared statements
// belong to one server connection; behind a transaction-mode pooler such as PgBouncer an Ask can
// land on a different one, so set Postgres:PrepareStatements to false if PgBouncer is put in front.
public class PostgresGameSearchIndex : IGameSearchIndex
{
    // pgvector's default is 40. Raised so a filtered search keeps enough candidates; see A-13.
    private const int DefaultEfSearch = 100;

    private readonly NpgsqlDataSource _dataSource;
    private readonly AskEmbedder _embedder;
    private readonly IGameCatalogSource _catalogSource;
    private readonly IMeepleStore _store;
    private readonly ILogger<PostgresGameSearchIndex> _logger;
    private readonly int _efSearch;
    private readonly bool _prepareStatements;

    // NpgsqlDataSource is a singleton (see Program.cs) that owns the connection pool.
    public PostgresGameSearchIndex(
        NpgsqlDataSource dataSource,
        AskEmbedder embedder,
        IGameCatalogSource catalogSource,
        IMeepleStore store,
        IConfiguration configuration,
        ILogger<PostgresGameSearchIndex> logger)
    {
        _dataSource = dataSource;
        _embedder = embedder;
        _catalogSource = catalogSource;
        _store = store;
        _logger = logger;

        _efSearch = DefaultEfSearch;
        string? efSearchSetting = configuration["Postgres:EfSearch"];
        if (!string.IsNullOrWhiteSpace(efSearchSetting))
        {
            _efSearch = int.Parse(efSearchSetting, CultureInfo.InvariantCulture);
        }

        // pgvector's own limits for hnsw.ef_search
        if (_efSearch < 1 || _efSearch > 1000)
        {
            throw new InvalidOperationException("Postgres:EfSearch must be between 1 and 1000.");
        }

        // On unless switched off; see the class comment about PgBouncer
        _prepareStatements = true;
        string? prepareSetting = configuration["Postgres:PrepareStatements"];
        if (!string.IsNullOrWhiteSpace(prepareSetting))
        {
            _prepareStatements = bool.Parse(prepareSetting);
        }
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

        // Nothing on the shelf means nothing can pass an owned-only Table; no need to ask Postgres.
        if (table.OwnedOnly && ownedNames.Count == 0)
        {
            return [];
        }

        float[] askVector = await _embedder.EmbedAsync(ask.Text);

        // A-31: Npgsql's own spans (one per command: the SET LOCAL, the query, the exact
        // scan when it runs) sit under this one. Program.cs subscribes to the "Npgsql" source.
        List<SearchResult> results;
        using (Activity? querySpan = SearchTelemetry.Source.StartActivity("Query the Search Index"))
        {
            querySpan?.SetTag(SearchTelemetry.ProviderTag, "postgres");
            results = await SearchAsync(new Vector(askVector), table, ownedNames, maxResults, ask);
            querySpan?.SetTag(SearchTelemetry.ResultCountTag, results.Count);
        }

        using Activity? rankSpan = SearchTelemetry.Source.StartActivity("Rank");

        PutEqualDistancesInNameOrder(results);

        List<string> askWords = AskWords.FindUsefulWords(ask.Text);
        List<Recommendation> recommendations = [];

        foreach (SearchResult result in results)
        {
            Game game = FindGame(result.Name);
            string reason = BuildReason(game, result.Blurb, askWords, table);
            recommendations.Add(new Recommendation(game, recommendations.Count + 1, reason));
        }

        rankSpan?.SetTag(SearchTelemetry.ResultCountTag, recommendations.Count);
        return recommendations;
    }

    // The query that answers an Ask. The Seeder's explain-postgres step runs EXPLAIN ANALYZE on a
    // copy of it, so keep the two in step. Parameters: @askVector, @playerCount, @minutesAvailable,
    // @maxResults and, when ownedOnly, @ownedNames (text[]).
    //
    // The WHERE mirrors Table.Admits: a 0 for players or playtime means unknown in BGG data and
    // does not rule a Game out. Only the name, Blurb and distance come back, never the vector.
    private static string BuildSql(bool ownedOnly)
    {
        StringBuilder sql = new StringBuilder();
        sql.AppendLine("SELECT g.name, g.blurb, g.embedding <=> @askVector AS distance");
        sql.AppendLine("FROM games g");

        if (ownedOnly)
        {
            // The Collection lives in the app, so its names are sent as an array and joined here
            sql.AppendLine("JOIN unnest(@ownedNames) AS owned(name) ON owned.name = g.name");
        }

        sql.AppendLine("WHERE (g.min_players <= 0 OR g.min_players <= @playerCount)");
        sql.AppendLine("  AND (g.max_players <= 0 OR g.max_players >= @playerCount)");
        sql.AppendLine("  AND (g.playtime_minutes <= 0 OR g.playtime_minutes <= @minutesAvailable)");

        // <=> because the index is vector_cosine_ops. <-> (L2) would scan the whole table.
        sql.AppendLine("ORDER BY g.embedding <=> @askVector");
        sql.AppendLine("LIMIT @maxResults");
        return sql.ToString();
    }

    // Runs the query with the vector index allowed, and again as an exact scan when that came back
    // short. Both runs are in one transaction so the SET LOCAL settings end with it.
    private async Task<List<SearchResult>> SearchAsync(Vector askVector, Table table, List<string> ownedNames, int maxResults, Ask ask)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

        await SetLocalAsync(connection, transaction, _prepareStatements, "hnsw.ef_search", _efSearch.ToString(CultureInfo.InvariantCulture));
        List<SearchResult> results = await RunQueryAsync(connection, transaction, _prepareStatements, askVector, table, ownedNames, maxResults);

        if (results.Count < maxResults)
        {
            // Either fewer Games fit the Table (a true answer) or HNSW lost some to the filter.
            // An exact scan tells the two apart. At 200 rows it costs well under a millisecond.
            // Not prepared: a prepared statement can keep a plan made before enable_indexscan was
            // turned off (changing a planner setting does not make Postgres plan it again), and this
            // is the same SQL as the first run. Unprepared, the query is planned fresh with the index off.
            await SetLocalAsync(connection, transaction, false, "enable_indexscan", "off");
            List<SearchResult> exactResults = await RunQueryAsync(connection, transaction, false, askVector, table, ownedNames, maxResults);

            if (exactResults.Count > results.Count)
            {
                _logger.LogWarning(
                    "Postgres vector search for \"{Ask}\": the HNSW index found {IndexCount} Games that fit the Table, an exact scan found {ExactCount}. Using the exact scan; consider raising Postgres:EfSearch (now {EfSearch}).",
                    ask.Text, results.Count, exactResults.Count, _efSearch);
            }

            results = exactResults;
        }

        await transaction.CommitAsync();

        _logger.LogInformation(
            "Postgres vector search for \"{Ask}\" returned {Count} Games", ask.Text, results.Count);

        return results;
    }

    private static async Task<List<SearchResult>> RunQueryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, bool prepare,
        Vector askVector, Table table, List<string> ownedNames, int maxResults)
    {
        await using NpgsqlCommand command = new NpgsqlCommand(BuildSql(table.OwnedOnly), connection, transaction);
        command.Parameters.AddWithValue("askVector", askVector);
        command.Parameters.AddWithValue("playerCount", table.PlayerCount);
        command.Parameters.AddWithValue("minutesAvailable", table.MinutesAvailable);
        command.Parameters.AddWithValue("maxResults", maxResults);

        if (table.OwnedOnly)
        {
            command.Parameters.AddWithValue("ownedNames", ownedNames.ToArray());
        }

        // The first time on a pooled connection this costs a round trip to prepare. After that
        // Npgsql remembers the statement is prepared on that connection and skips the trip.
        if (prepare)
        {
            await command.PrepareAsync();
        }

        List<SearchResult> results = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            SearchResult result = new SearchResult();
            result.Name = reader.GetString(0);
            if (!reader.IsDBNull(1))
            {
                result.Blurb = reader.GetString(1);
            }
            result.Distance = reader.GetDouble(2);
            results.Add(result);
        }

        return results;
    }

    // SET LOCAL cannot take a parameter, so set_config(name, value, true) does the same job:
    // the setting lasts until the transaction ends and then goes back to the server default.
    private static async Task SetLocalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool prepare, string name, string value)
    {
        await using NpgsqlCommand command = new NpgsqlCommand("SELECT set_config(@name, @value, true)", connection, transaction);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("value", value);
        if (prepare)
        {
            await command.PrepareAsync();
        }
        await command.ExecuteNonQueryAsync();
    }

    // Postgres has already ordered the results closest first. When two neighbours have exactly the
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

    // Rows are keyed by Game name. A name that is not in the Catalog means the table is out of
    // date with the seed; fail loudly rather than quietly return fewer Recommendations.
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
            "Postgres returned '" + name + "', which is not in the Catalog. Run: dotnet run --project MeepleLedger.Seeder -- upload-postgres");
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

    // One row of the search query. With cosine distance a lower distance is a closer match.
    private class SearchResult
    {
        public string Name { get; set; } = "";
        public string? Blurb { get; set; }
        public double Distance { get; set; }
    }
}
