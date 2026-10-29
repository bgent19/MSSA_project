using MeepleLedger.Data;
using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// The compiled Catalog (CatalogSeed), for the in-memory store (ADR-0001).
public class SeededCatalogSource : IGameCatalogSource
{
    public GameCatalog Catalog { get; } = new GameCatalog(CatalogSeed.Games);

    // In memory only: an added Game is gone at the next restart. That is right for the offline demo,
    // and CatalogSeed is never regenerated from added Games (ADR-0005).
    public Task AddGameAsync(Game game)
    {
        Catalog.Add(game);
        return Task.CompletedTask;
    }
}
