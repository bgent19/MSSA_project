using MeepleLedger.Data;
using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

public class SeededCatalogSource : IGameCatalogSource
{
    public GameCatalog Catalog { get; } = new GameCatalog(CatalogSeed.Games);
}