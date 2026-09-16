using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

public interface IGameCatalogSource
{
    GameCatalog Catalog { get; }
}
