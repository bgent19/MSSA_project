using System.Net;
using MeepleLedger.Domain;
using Microsoft.Azure.Cosmos;

namespace MeepleLedger.Storage;

// The durable store (S-04): the Collection and the Play Log, kept in two Cosmos containers so a Play
// logged tonight survives a restart and a deploy. Chosen with MeepleStore:Provider=Cosmos; the
// in-memory store stays the default (ADR-0001).
//
// At startup every document is read and replayed through GameCollection.Add and PlayLog.Record, so a
// stored row that breaks an invariant (or names a Game the Catalog doesn't have) fails the launch,
// the same as a bad seed row. An empty store gives an empty Collection and Play Log; nothing is seeded.
//
// After that, the pages save each change explicitly (see the save rule in IMeepleStore). Nothing here
// listens to the domain's events: loading raises Collection.Changed for every Owned Game, and saving
// on that event would rewrite every document at every startup.
//
// Both containers use the partition key /ownerName, and every document has the same owner: one
// logical partition, which holds 20 GB. That is thousands of years of game nights.
public class CosmosMeepleStore : IMeepleStore
{
    // The same owner as InMemoryMeepleStore and the Seeder's EmitLog; they must match.
    public const string OwnerName = "TheGentleBean";

    // How long one save may take before the page is told it failed. Without a limit, a dropped
    // network keeps the SDK retrying and the button looks dead.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly Container _ownedGames;
    private readonly Container _plays;
    private readonly PartitionKey _partitionKey = new PartitionKey(OwnerName);
    private readonly ILogger<CosmosMeepleStore> _logger;

    public GameCollection Collection { get; } = new();
    public PlayLog PlayLog { get; } = new() { OwnerName = OwnerName };

    // CosmosClient is the app's one client (see Program.cs); this class only holds Containers from it.
    // The Catalog source is resolved before the store, so the Catalog is loaded first.
    public CosmosMeepleStore(
        CosmosClient client,
        IGameCatalogSource catalogSource,
        IConfiguration configuration,
        ILogger<CosmosMeepleStore> logger)
    {
        string databaseName = RequireSetting(configuration, "Cosmos:Database");
        string ownedGamesName = RequireSetting(configuration, "Cosmos:OwnedGamesContainer");
        string playsName = RequireSetting(configuration, "Cosmos:PlaysContainer");

        _ownedGames = client.GetContainer(databaseName, ownedGamesName);
        _plays = client.GetContainer(databaseName, playsName);
        _logger = logger;

        // The store is built once, at startup, before the app serves anything, so waiting here is
        // safe. It is what makes a bad stored row fail the launch.
        LoadAsync(catalogSource.Catalog).GetAwaiter().GetResult();

        _logger.LogInformation(
            "Loaded {OwnedGames} Owned Games from {OwnedGamesContainer} and {Plays} Plays from {PlaysContainer}.",
            Collection.TotalGames, ownedGamesName, PlayLog.RecentFirst().Count(), playsName);
    }

    public async Task SaveOwnedGameAsync(OwnedGame ownedGame)
    {
        try
        {
            OwnedGameDocument document = MeepleDocuments.FromOwnedGame(ownedGame, OwnerName);
            using CancellationTokenSource timeout = new CancellationTokenSource(WriteTimeout);
            await _ownedGames.UpsertItemAsync(document, _partitionKey, cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            throw WriteFailed("save " + ownedGame.Game.Name, ex);
        }
    }

    public async Task RemoveOwnedGameAsync(string gameName)
    {
        try
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(WriteTimeout);
            await _ownedGames.DeleteItemAsync<OwnedGameDocument>(gameName, _partitionKey, cancellationToken: timeout.Token);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone, which is what was asked for.
        }
        catch (Exception ex)
        {
            throw WriteFailed("remove " + gameName, ex);
        }
    }

    public async Task SavePlayAsync(Play play)
    {
        try
        {
            PlayDocument document = MeepleDocuments.FromPlay(play, OwnerName);
            using CancellationTokenSource timeout = new CancellationTokenSource(WriteTimeout);
            await _plays.UpsertItemAsync(document, _partitionKey, cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            throw WriteFailed("save the Play of " + play.Game.Name, ex);
        }
    }

    public async Task RemovePlayAsync(Guid playId)
    {
        try
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(WriteTimeout);
            await _plays.DeleteItemAsync<PlayDocument>(playId.ToString(), _partitionKey, cancellationToken: timeout.Token);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone, which is what was asked for.
        }
        catch (Exception ex)
        {
            throw WriteFailed("remove the Play", ex);
        }
    }

    // Reads every document and replays it through the domain. Owned Games first, then Plays; the
    // two are independent, so the order only matters for the log line.
    private async Task LoadAsync(GameCatalog catalog)
    {
        List<OwnedGameDocument> ownedGameDocuments = await ReadAllAsync<OwnedGameDocument>(_ownedGames);
        foreach (OwnedGameDocument document in ownedGameDocuments)
        {
            Collection.Add(MeepleDocuments.ToOwnedGame(document, catalog));
        }

        List<PlayDocument> playDocuments = await ReadAllAsync<PlayDocument>(_plays);
        foreach (PlayDocument document in playDocuments)
        {
            PlayLog.Record(MeepleDocuments.ToPlay(document, catalog));
        }
    }

    // Every document in the owner's partition, page by page.
    private async Task<List<T>> ReadAllAsync<T>(Container container)
    {
        List<T> documents = [];
        QueryRequestOptions options = new QueryRequestOptions { PartitionKey = _partitionKey };
        FeedIterator<T> iterator = container.GetItemQueryIterator<T>(new QueryDefinition("SELECT * FROM c"), requestOptions: options);

        while (iterator.HasMoreResults)
        {
            FeedResponse<T> page = await iterator.ReadNextAsync();
            foreach (T document in page)
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    // Logs the real error and gives the page one exception type it can catch without knowing Cosmos.
    private StoreWriteException WriteFailed(string action, Exception ex)
    {
        _logger.LogWarning(ex, "Could not {Action} in Cosmos.", action);
        return new StoreWriteException("Couldn't " + action + ": the change was not saved. Try again in a moment.", ex);
    }

    private static string RequireSetting(IConfiguration configuration, string name)
    {
        string? value = configuration[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The Cosmos store needs the setting " + name + ".");
        }

        return value;
    }
}
