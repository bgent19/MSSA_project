namespace MeepleLedger.Domain
{
    public class GameCollection
    {
        private readonly Dictionary<string, OwnedGame> _games = []; // Key is game title
        public int TotalGames => _games.Count;

        public void Add(OwnedGame game)
        {
            if (_games.ContainsKey(game.Game.Name))
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
            return [.. _games.Values.Where(og => og.Game.Search(term))];
        }

        public IEnumerable<OwnedGame> FilterByPlayerCount(int n)
        {
            return [.. _games.Values.Where((g) => ((g.Game.MinPlayers <= n) && (g.Game.MaxPlayers >= n)))];
        }
    }

}
