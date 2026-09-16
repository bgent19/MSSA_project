using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

public interface IMeepleStore
{
    GameCollection Collection { get; }
    PlayLog        PlayLog    { get; }
}
