using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// Where the Catalog comes from, and where a Game added to it is kept (S-07, ADR-0005).
public interface IGameCatalogSource
{
    GameCatalog Catalog { get; }

    // Adds a Game to the Catalog and keeps it wherever this source keeps its Games. Throws
    // InvalidOperationException if the name is already in the Catalog (or can't be stored), and
    // StoreWriteException if the Game could not be kept; then the Catalog is unchanged.
    Task AddGameAsync(Game game);
}
