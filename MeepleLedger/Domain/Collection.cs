using Microsoft.AspNetCore.Components.Forms;

namespace MeepleLedger.Domain
{
    public class GameCatalog
    {
        private IReadOnlyList<Game> Games { get; }

        public IEnumerable<Game> Search(string term)
        {
            term = string.Concat(term.Where(c => !char.IsWhiteSpace(c))).ToLower();

            return null;
        }

        public IEnumerable<Game> ByPlayerCount(int n)
        {
           return Games.Where(g => (g.MinPlayers <= n) && (g.MaxPlayers >= n));
        }
    }

    public class GameCollection
    {
        private Dictionary<string, OwnedGame> _games; // Key is game title
        public int TotalGames => _games.Count();

        public void Add(OwnedGame game)
        {
            if(_games.ContainsKey(game.Game.Name))
            {
                throw new InvalidOperationException("Title is already owned.");
            }

            _games.Add(game.Game.Name, game);
        }

        public void Remove(string name)
        {
            if (!_games.Remove(name))
            {
                throw new InvalidOperationException("Title not found in collection.");
            }
        }

        public IEnumerable<OwnedGame> Search(string term)
        {
            term = string.Concat(term.Where(c => !char.IsWhiteSpace(c))).ToLower();

            return null;
        }

        public IEnumerable<OwnedGame> FilterByPlayerCount(int n)
        {
            return _games.Where((g) => ((g.Value.Game.MinPlayers <= n) && (g.Value.Game.MaxPlayers >= n)))
                         .Select(v => v.Value);
        }
    }

}
