namespace MeepleLedger.Domain
{
    public class GameCatalog
    {
        private IReadOnlyList<Game> Games { get; set; }

        public IEnumerable<Game> Search(SearchType t, string name)
        {
            if(t == SearchType.Title)
            {
                return Games.Where(g => g.Name == name);
            }
            else
            {
                return Games.Where(g => g.Designer == name);
            }
        }

        public GameCatalog ByPlayerCount(int n)
        {
           return new GameCatalog { Games = (IReadOnlyList<Game>)
                                            this.Games.Where(g => (g.MinPlayers <= n) &&
                                                                  (g.MaxPlayers >= n)) };
        }
    }

    public class GameCollection
    {
        private Dictionary<string, OwnedGame> _games { get; set; } // Key is game title
        public int TotalGames { get; private set; }

        public void Add(OwnedGame game)
        {
            if(!_games.TryAdd(game.Game.Name, game))
            {
                throw new InvalidOperationException("Title is already owned.");
            }

            TotalGames++;
        }

        public void Remove(string name)
        {
            if (!_games.Remove(name))
            {
                throw new InvalidOperationException("Title not found in collection.");
            }

            TotalGames--;
        }

        public IEnumerable<KeyValuePair<string,OwnedGame>> Search(SearchType t, string name)
        {
            if (t == SearchType.Title)
            {
                return _games.Where(g => g.Value.Game.Name == name);
            }
            else
            {
                return _games.Where(g => g.Value.Game.Designer == name);
            }
        }

        public GameCollection FilterByPlayercount(int n)
        {
            return new GameCollection()
            {
                _games = (Dictionary<string,OwnedGame>)
                         _games.Where((g) => ((g.Value.Game.MinPlayers <= n) &&
                                              (g.Value.Game.MaxPlayers >= n)))
            };
        }
    }

    public enum SearchType
    {
        Title,
        Desinger,
    }
}
