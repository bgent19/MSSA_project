using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

public interface IGameCatalogsource
{
    GameCatalog Catalog { get; }
}
