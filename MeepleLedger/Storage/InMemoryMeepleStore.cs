
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
}
