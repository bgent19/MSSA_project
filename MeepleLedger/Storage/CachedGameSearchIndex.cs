using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeepleLedger.Domain;
using StackExchange.Redis;

namespace MeepleLedger.Storage;

// A cache of Recommendations in Azure Managed Redis, in front of whichever Search Index Provider
// is live (A-16). It is a decorator: it implements IGameSearchIndex by wrapping another
// IGameSearchIndex, so every Provider gets the same cache and none of them knows it is there
// (ADR-0002). Switched on with RecommendationCache:Enabled; see Program.cs.
//
// The cache key is built from the Provider, the Ask, EVERY field of the Table and maxResults.
// A field missing from the key is a correctness bug: leave out OwnedOnly and a shelf-only Ask is
// answered with a cached list full of Games you do not own.
//
// Expiry: every entry lives for RecommendationCache:TtlMinutes (60 in appsettings.json). Why an hour:
//   - The answers only change when the Collection or a Blurb changes, and both are invalidated
//     below, so the TTL is a backstop for a change nobody told the cache about (for example a row
//     edited by hand in Postgres), not the main way entries go stale. An hour of staleness in the
//     worst case is fine for "what should we play tonight?".
//   - A game night is planned within an evening; the same Ask and Table come back within minutes
//     (a reload, a second device), rarely the next day. Keeping entries longer buys few hits.
//   - The instance is 0.5 GB with the NoEviction policy, which RediSearch needs. Redis will never
//     drop old keys to make room; once memory is full, writes fail. The TTL is what keeps it from
//     filling up. One entry is about 3 KB, so an hour of traffic is nowhere near the limit.
//
// Invalidation:
//   - A Game added to or removed from the Collection: every owned-only entry (the Game may now
//     pass, or no longer pass, the Table) and every entry that lists that Game (its Reason says
//     whether it is on your shelf). Each entry's key is written into "tag" sets as it is cached:
//     one set for owned-only entries and one per Game. Invalidation reads a set and deletes the
//     keys in it. Never KEYS: it walks the whole keyspace and blocks the server while it does.
//   - A Blurb edited: the Game gets a new vector, which can move it into or out of ANY cached
//     answer, so every entry is affected. Rather than find and delete them all, every key carries
//     a generation number (GenerationKey). The Seeder raises it (watch-cosmos on any change to a
//     Game, or invalidate-cache by hand); from then on no old key is ever looked up again and the
//     old entries expire on their own.
//
// Redis being down must never take the Tonight page down with it, so the cache fails open: any
// Redis error is logged and the Ask goes straight to the Provider.
//
// Clustering: the instance is EnterpriseCluster, so keys live on different shards. Nothing here
// uses MULTI or a multi-key command other than DEL, which the proxy splits for us.
public class CachedGameSearchIndex : IGameSearchIndex
{
    // The Seeder raises this number to drop every cached answer at once. The Seeder does not
    // reference this project, so it has its own copy of the name; keep the two in step.
    public const string GenerationKey = "tonight:generation";

    private const string EntryKeyPrefix = "tonight:entry:";
    private const string OwnedOnlyTagKey = "tonight:tag:owned-only";
    private const string GameTagKeyPrefix = "tonight:tag:game:";

    private readonly IGameSearchIndex _inner;
    private readonly string _providerName;
    private readonly IConnectionMultiplexer _redis;
    private readonly IGameCatalogSource _catalogSource;
    private readonly TimeSpan _timeToLive;
    private readonly ILogger<CachedGameSearchIndex> _logger;

    // Goes up by one every time the Collection changes. An Ask that was being answered while the
    // Collection changed may hold an out-of-date answer, so it is not written to the cache.
    private int _collectionVersion;

    // The ConnectionMultiplexer is a singleton (see Program.cs). It is thread-safe and holds the
    // connections, so one is shared by every request; building one per Ask would run out of
    // connections on a small tier.
    public CachedGameSearchIndex(
        IGameSearchIndex inner,
        string providerName,
        IConnectionMultiplexer redis,
        IGameCatalogSource catalogSource,
        IMeepleStore store,
        TimeSpan timeToLive,
        ILogger<CachedGameSearchIndex> logger)
    {
        _inner = inner;
        _providerName = providerName.ToLowerInvariant();
        _redis = redis;
        _catalogSource = catalogSource;
        _timeToLive = timeToLive;
        _logger = logger;

        store.Collection.Changed += OnCollectionChanged;
    }

    // The cache does not change what the Search Index can do.
    public bool UnderstandsMeaning => _inner.UnderstandsMeaning;

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

        Stopwatch stopwatch = Stopwatch.StartNew();

        // Null when Redis could not be reached; then the cache is skipped for this Ask.
        string? key = await TryBuildKeyAsync(ask, table, maxResults);

        if (key != null)
        {
            List<Recommendation>? cached = await TryReadAsync(key);
            if (cached != null)
            {
                _logger.LogInformation(
                    "Recommendation cache HIT for \"{Ask}\" ({Players} players, {Minutes} min, owned only: {OwnedOnly}) in {Milliseconds} ms",
                    ask.Text, table.PlayerCount, table.MinutesAvailable, table.OwnedOnly, stopwatch.ElapsedMilliseconds);
                return cached;
            }
        }

        int versionBefore = Volatile.Read(ref _collectionVersion);

        IReadOnlyList<Recommendation> recommendations = await _inner.RecommendAsync(ask, table, maxResults);

        _logger.LogInformation(
            "Recommendation cache MISS for \"{Ask}\" ({Players} players, {Minutes} min, owned only: {OwnedOnly}); the {Provider} Provider answered in {Milliseconds} ms",
            ask.Text, table.PlayerCount, table.MinutesAvailable, table.OwnedOnly, _providerName, stopwatch.ElapsedMilliseconds);

        if (key != null)
        {
            if (Volatile.Read(ref _collectionVersion) == versionBefore)
            {
                await TryWriteAsync(key, table, recommendations);
            }
            else
            {
                _logger.LogInformation("The Collection changed while \"{Ask}\" was being answered; not caching it", ask.Text);
            }
        }

        return recommendations;
    }

    // Reads the current generation and builds the key for this Ask and Table.
    private async Task<string?> TryBuildKeyAsync(Ask ask, Table table, int maxResults)
    {
        long generation = 0;

        try
        {
            RedisValue value = await _redis.GetDatabase().StringGetAsync(GenerationKey);
            if (!value.IsNull)
            {
                generation = (long)value;
            }
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogUnavailable(ex);
            return null;
        }

        return BuildKey(generation, ask, table, maxResults);
    }

    // For example: tonight:entry:postgres:g3:9f2c...:players=3:minutes=60:owned=True:max=10
    //
    // The Ask is hashed because it is free text: it can be long and can contain ':'. It is hashed
    // exactly as typed (only trimmed, which Ask already does), because the Provider embeds it as
    // typed and "Coop" and "coop" are not promised to give the same answer.
    private string BuildKey(long generation, Ask ask, Table table, int maxResults)
    {
        string askHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ask.Text)));

        StringBuilder key = new StringBuilder();
        key.Append(EntryKeyPrefix);
        key.Append(_providerName);
        key.Append(":g").Append(generation.ToString(CultureInfo.InvariantCulture));
        key.Append(':').Append(askHash);

        // Every field of the Table, one by one. A new field on Table must be added here too.
        key.Append(":players=").Append(table.PlayerCount.ToString(CultureInfo.InvariantCulture));
        key.Append(":minutes=").Append(table.MinutesAvailable.ToString(CultureInfo.InvariantCulture));
        key.Append(":owned=").Append(table.OwnedOnly ? "True" : "False");

        // Not part of the Table, but it changes the answer, so it is part of the key.
        key.Append(":max=").Append(maxResults.ToString(CultureInfo.InvariantCulture));

        return key.ToString();
    }

    // Returns the cached Recommendations, or null on a miss (or when Redis cannot be reached).
    private async Task<List<Recommendation>?> TryReadAsync(string key)
    {
        RedisValue value;
        try
        {
            value = await _redis.GetDatabase().StringGetAsync(key);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            LogUnavailable(ex);
            return null;
        }

        if (value.IsNull)
        {
            return null;
        }

        List<CachedRecommendation>? entries = JsonSerializer.Deserialize<List<CachedRecommendation>>(value.ToString());
        if (entries == null)
        {
            return null;
        }

        // Only names are cached; the Games come from the Catalog, the same objects every page uses.
        List<Recommendation> recommendations = [];
        foreach (CachedRecommendation entry in entries)
        {
            Game? game = FindGame(entry.Name);
            if (game == null)
            {
                // The Catalog has changed since this was cached; treat it as a miss.
                return null;
            }

            recommendations.Add(new Recommendation(game, entry.Rank, entry.Reason));
        }

        return recommendations;
    }

    // Writes the answer with its TTL and records its key in the tag sets used for invalidation.
    // The commands are sent together and awaited together: StackExchange.Redis pipelines them on
    // the one connection, so this costs about one round trip, not one per command.
    private async Task TryWriteAsync(string key, Table table, IReadOnlyList<Recommendation> recommendations)
    {
        List<CachedRecommendation> entries = [];
        foreach (Recommendation recommendation in recommendations)
        {
            CachedRecommendation entry = new CachedRecommendation();
            entry.Name = recommendation.Game.Name;
            entry.Rank = recommendation.Rank;
            entry.Reason = recommendation.Reason;
            entries.Add(entry);
        }

        string json = JsonSerializer.Serialize(entries);
        IDatabase database = _redis.GetDatabase();
        List<Task> writes = [];

        writes.Add(database.StringSetAsync(key, json, _timeToLive));

        if (table.OwnedOnly)
        {
            AddToTag(database, writes, OwnedOnlyTagKey, key);
        }

        foreach (Recommendation recommendation in recommendations)
        {
            AddToTag(database, writes, GameTagKeyPrefix + recommendation.Game.Name, key);
        }

        try
        {
            await Task.WhenAll(writes);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            // If the entry was written but a tag was not, invalidation can miss it; the TTL still ends it.
            LogUnavailable(ex);
        }
    }

    // A tag set lives as long as the newest entry in it. Keys of entries that have already
    // expired stay in the set until then, which is harmless: deleting a missing key does nothing.
    private void AddToTag(IDatabase database, List<Task> writes, string tagKey, string entryKey)
    {
        writes.Add(database.SetAddAsync(tagKey, entryKey));
        writes.Add(database.KeyExpireAsync(tagKey, _timeToLive));
    }

    private void OnCollectionChanged(string gameName)
    {
        Interlocked.Increment(ref _collectionVersion);

        // An event handler cannot be awaited, so the Redis work runs on its own. It catches and
        // logs its own errors, so nothing is left unobserved.
        _ = InvalidateForCollectionChangeAsync(gameName);
    }

    private async Task InvalidateForCollectionChangeAsync(string gameName)
    {
        try
        {
            long dropped = await DropTaggedEntriesAsync(OwnedOnlyTagKey);
            dropped += await DropTaggedEntriesAsync(GameTagKeyPrefix + gameName);

            _logger.LogInformation(
                "Recommendation cache: the Collection changed ({Game}), dropped {Count} cached answers", gameName, dropped);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            _logger.LogWarning(
                "Recommendation cache: could not invalidate after the Collection changed ({Game}); entries end with their TTL. {Message}",
                gameName, ex.Message);
        }
    }

    // Deletes every entry whose key is in the tag set, then takes those keys out of the set.
    // SREM of only the keys that were read (rather than deleting the whole set) keeps a key that
    // was tagged in between, so a later invalidation still finds it.
    private async Task<long> DropTaggedEntriesAsync(string tagKey)
    {
        IDatabase database = _redis.GetDatabase();

        RedisValue[] members = await database.SetMembersAsync(tagKey);
        if (members.Length == 0)
        {
            return 0;
        }

        RedisKey[] entryKeys = new RedisKey[members.Length];
        for (int i = 0; i < members.Length; i++)
        {
            entryKeys[i] = members[i].ToString();
        }

        long deleted = await database.KeyDeleteAsync(entryKeys);
        await database.SetRemoveAsync(tagKey, members);
        return deleted;
    }

    private Game? FindGame(string name)
    {
        foreach (Game game in _catalogSource.Catalog.Games)
        {
            if (game.Name == name)
            {
                return game;
            }
        }

        return null;
    }

    // The errors StackExchange.Redis throws when the server cannot be reached or does not answer
    // in time. RedisTimeoutException is a TimeoutException, not a RedisException, so both are checked.
    private static bool IsRedisFailure(Exception ex)
    {
        return ex is RedisException || ex is TimeoutException;
    }

    private void LogUnavailable(Exception ex)
    {
        _logger.LogWarning("Recommendation cache unavailable, asking the Provider directly: {Message}", ex.Message);
    }

    // What is stored for each Recommendation: the Game's name (not the whole Game), rank and Reason.
    private class CachedRecommendation
    {
        public string Name { get; set; } = "";
        public int Rank { get; set; }
        public string Reason { get; set; } = "";
    }
}
