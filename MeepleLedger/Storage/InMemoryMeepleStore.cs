
using MeepleLedger.Data;
using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

public class InMemoryMeepleStore : IMeepleStore
{
    public GameCollection Collection { get; } = new();
    public PlayLog PlayLog { get; } = new() { OwnerName="TheGentleBean"};

    public InMemoryMeepleStore(IGameCatalogSource catalogSource)
    {
        var catalog = catalogSource.Catalog;

        foreach(var owned in CollectionSeed.Build(catalog))
        {
            Collection.Add(owned);
        }

        foreach(var play in LogSeed.Build(catalog))
        {
            PlayLog.Record(play);
        }
    }

    // Nothing is kept beyond the Collection and the Play Log themselves, so there is nothing to save.
    public Task SaveOwnedGameAsync(OwnedGame ownedGame) => Task.CompletedTask;

    public Task RemoveOwnedGameAsync(string gameName) => Task.CompletedTask;

    public Task SavePlayAsync(Play play) => Task.CompletedTask;

    public Task RemovePlayAsync(Guid playId) => Task.CompletedTask;
}
