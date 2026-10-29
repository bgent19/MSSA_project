namespace MeepleLedger.Domain
{
    // Every Game the app knows about. The compiled Catalog (CatalogSeed) is fixed; the live Catalog
    // grows when the owner adds a Game from BoardGameGeek (ADR-0005). Games are never removed.
    public class GameCatalog
    {
        // Replaced with a new list on every Add, never changed in place: a page on another circuit
        // may be looping over the old list at that moment, and changing a list being looped over throws.
        private List<Game> _games;

        // Only one Add at a time, so two at once can't both pass the duplicate check.
        private readonly object _addLock = new object();

        public GameCatalog(IEnumerable<Game> games)
        {
            _games = [.. games];
        }

        public IReadOnlyList<Game> Games => _games;

        // Raised after a Game is added, with the Game's name. Anything that keeps answers worked out
        // from the Catalog (such as cached Recommendations) listens here so it can throw them away.
        public event Action<string>? Added;

        // Adds a Game. A name already in the Catalog is refused: the Collection and the Play Log find
        // Games by name, so two Games with one name would be one Game to them.
        public void Add(Game game)
        {
            lock (_addLock)
            {
                if (Find(game.Name) != null)
                {
                    throw new InvalidOperationException(game.Name + " is already in the Catalog.");
                }

                List<Game> games = [.. _games];
                games.Add(game);
                _games = games;
            }

            Added?.Invoke(game.Name);
        }

        // The Game with exactly this name, or null.
        public Game? Find(string name)
        {
            foreach (Game game in _games)
            {
                if (game.Name == name)
                {
                    return game;
                }
            }

            return null;
        }

        public IEnumerable<Game> Search(string term)
        {
            return [.. Games.Where(g => g.Search(term))];
        }

        public IEnumerable<Game> ByPlayerCount(int n)
        {
            return [.. Games.Where(g => (g.MinPlayers <= n) && (g.MaxPlayers >= n))];
        }
    }
}
