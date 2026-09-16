
using MeepleLedger.Data;
using MeepleLedger.Domain;
using Microsoft.VisualBasic;

namespace MeepleLedger.Storage;

public class InMemoryMeepleStore : IMeepleStore
{
    public GameCollection Collection { get; set; } = new();
    public PlayLog PlayLog { get; } = new() { OwnerName="TheGentleBean"};

    public InMemoryMeepleStore(IGameCatalogsource catalogSource)
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
