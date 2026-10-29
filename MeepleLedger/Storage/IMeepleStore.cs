using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// The Collection and the Play Log, and how a change to them is saved (S-04).
//
// The save rule:
//   - Add or edit: call the domain method first (that is where the invariants are checked), then the
//     store's Save method. If the save throws a StoreWriteException, undo the domain change and show
//     the message, so the screen still matches what is stored.
//   - Remove: call the store's Remove method first, then the domain method. A removal has no invariant
//     to check, and undoing one would mean recording the Play again (which raises PlayLog.Recorded).
//
// The in-memory store keeps nothing, so its Save and Remove methods do nothing.
public interface IMeepleStore
{
    GameCollection Collection { get; }
    PlayLog        PlayLog    { get; }

    // Adds or replaces the stored Owned Game with this Game's name.
    Task SaveOwnedGameAsync(OwnedGame ownedGame);

    // Removes the stored Owned Game with this Game's name. Not stored is not an error.
    Task RemoveOwnedGameAsync(string gameName);

    // Adds or replaces the stored Play with this Play's Id.
    Task SavePlayAsync(Play play);

    // Removes the stored Play with this Id. Not stored is not an error.
    Task RemovePlayAsync(Guid playId);
}
