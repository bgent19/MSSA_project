using System.Diagnostics;
using System.Net;
using MeepleLedger.Domain;
using Microsoft.Azure.Cosmos;

namespace MeepleLedger.Storage;

// The live Catalog (S-07, ADR-0005): read from the Cosmos games container at startup instead of
// CatalogSeed, so it holds the 200 Games upload-cosmos loaded plus every Game added since. Chosen with
// MeepleStore:Provider=Cosmos, beside CosmosMeepleStore: the Owned Games and Plays stored there may
// name a Game only the container has.
//
// Adding a Game embeds it at once and writes it with its vector and embeddingTextHash, so the Cosmos
// Search Index can return it on the next Ask without a restart. Postgres and Redis never hear about
// it; they are study Providers that don't run in production (ADR-0004), and that drift is accepted.
public class CosmosGameCatalogSource : IGameCatalogSource
{
    // How long the write may take before the page is told it failed (the same as CosmosMeepleStore).
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    // Only the facts: SELECT * would bring back 1536 floats per Game that the Catalog never uses.
    private const string LoadQuery =
        "SELECT c.id, c.name, c.designer, c.minPlayers, c.maxPlayers, c.playtimeMinutes, " +
        "c.categories, c.mechanics, c.weight, c.blurb FROM c";

    private readonly Container _container;
    private readonly Func<AskEmbedder> _getEmbedder;
    private readonly ILogger<CosmosGameCatalogSource> _logger;

    public GameCatalog Catalog { get; }

    // getEmbedder is called only when a Game is added, so the store starts without the AzureOpenAI:*
    // settings, and only adding a Game needs them.
    public CosmosGameCatalogSource(
        CosmosClient client,
        Func<AskEmbedder> getEmbedder,
        IConfiguration configuration,
        ILogger<CosmosGameCatalogSource> logger)
    {
        string? databaseName = configuration["Cosmos:Database"];
        string? containerName = configuration["Cosmos:Container"];

        if (string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(containerName))
        {
            throw new InvalidOperationException("The Cosmos Catalog needs Cosmos:Database and Cosmos:Container settings.");
        }

        _container = client.GetContainer(databaseName, containerName);
        _getEmbedder = getEmbedder;
        _logger = logger;

        // Built once, at startup, before the app serves anything, so waiting here is safe.
        List<Game> games = LoadAsync().GetAwaiter().GetResult();
        Catalog = new GameCatalog(games);

        if (games.Count == 0)
        {
            _logger.LogWarning(
                "The {Container} container has no Games. Load them with: dotnet run --project MeepleLedger.Seeder -- upload-cosmos",
                containerName);
        }
        else
        {
            _logger.LogInformation("Loaded {Count} Games into the Catalog from {Container}.", games.Count, containerName);
        }
    }

    public async Task AddGameAsync(Game game)
    {
        // The duplicate check first, so a Game already there costs no embeddings call.
        Game? existing = Catalog.Find(game.Name);
        if (existing != null)
        {
            throw new InvalidOperationException(existing.Name + " is already in the Catalog.");
        }

        // The id is the Game's name, and Cosmos refuses an id with these in it.
        if (game.Name.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            throw new InvalidOperationException("'" + game.Name + "' can't be stored: the name contains / \\ ? or #.");
        }

        GameDocument document = await BuildDocumentAsync(game);

        try
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(WriteTimeout);

            // Create, not upsert: a document already there means the Game is already in the Catalog
            // (added from another tab since this page loaded), and it must not be overwritten.
            await _container.CreateItemAsync(document, new PartitionKey(document.Id), cancellationToken: timeout.Token);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(game.Name + " is already in the Catalog.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write {Game} to the games container.", game.Name);
            throw new StoreWriteException("Couldn't save " + game.Name + ": it was not added. Try again in a moment.", ex);
        }

        // Only once it is stored, so the Catalog never shows a Game that would be gone after a restart.
        Catalog.Add(game);
        _logger.LogInformation("Added {Game} to the Catalog and the games container.", game.Name);
    }

    // Embeds the Game the way the Seeder embeds Blurbs, and builds the document upload-cosmos would write.
    private async Task<GameDocument> BuildDocumentAsync(Game game)
    {
        string text = EmbeddingText.Build(game.Name, game.Categories, game.Mechanics, game.Blurb ?? "");

        float[] vector;
        try
        {
            vector = await _getEmbedder().EmbedAsync(text, "Embed a new Game");
        }
        catch (Exception ex)
        {
            // A missing AzureOpenAI setting, a network error or an error answer: all mean not added.
            Activity.Current?.SetStatus(ActivityStatusCode.Error, "Embedding failed");
            _logger.LogWarning(ex, "Could not embed {Game}.", game.Name);
            throw new StoreWriteException("Couldn't embed " + game.Name + " for Tonight: it was not added. Try again in a moment.", ex);
        }

        return new GameDocument
        {
            Id = game.Name,
            Name = game.Name,
            Designer = game.Designer,
            MinPlayers = game.MinPlayers,
            MaxPlayers = game.MaxPlayers,
            PlaytimeMinutes = game.PlaytimeMinutes,
            Categories = [.. game.Categories],
            Mechanics = [.. game.Mechanics],
            Facts = EmbeddingText.BuildFacts(game.Categories, game.Mechanics),
            Weight = game.Weight,
            Blurb = game.Blurb,
            Embedding = vector,
            EmbeddingTextHash = EmbeddingText.Hash(text),
            AddedAtRuntime = true,
        };
    }

    // Every Game in the container, sorted by name so the Catalog's order doesn't depend on Cosmos.
    // A cross-partition query (the partition key is /id), once, at startup.
    private async Task<List<Game>> LoadAsync()
    {
        List<Game> games = [];
        double requestCharge = 0;

        using FeedIterator<GameDocument> iterator = _container.GetItemQueryIterator<GameDocument>(new QueryDefinition(LoadQuery));
        while (iterator.HasMoreResults)
        {
            FeedResponse<GameDocument> page = await iterator.ReadNextAsync();
            requestCharge += page.RequestCharge;

            foreach (GameDocument document in page)
            {
                games.Add(ToGame(document));
            }
        }

        games.Sort(CompareByName);
        _logger.LogInformation("Reading the Catalog cost {RequestCharge} RU.", requestCharge);
        return games;
    }

    private static Game ToGame(GameDocument document)
    {
        // A hand-written document may have no lists at all; the Game still gets empty ones.
        if (document.Categories == null)
        {
            document.Categories = [];
        }

        if (document.Mechanics == null)
        {
            document.Mechanics = [];
        }

        return new Game
        {
            Name = document.Name,
            Designer = document.Designer,
            MinPlayers = document.MinPlayers,
            MaxPlayers = document.MaxPlayers,
            PlaytimeMinutes = document.PlaytimeMinutes,
            Categories = [.. document.Categories],
            Mechanics = [.. document.Mechanics],
            Weight = document.Weight,
            Blurb = document.Blurb,
        };
    }

    private static int CompareByName(Game first, Game second)
    {
        return string.CompareOrdinal(first.Name, second.Name);
    }
}
