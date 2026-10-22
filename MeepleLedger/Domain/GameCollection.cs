namespace MeepleLedger.Domain
{
    public class GameCollection
    {
        private readonly Dictionary<string, OwnedGame> _games = []; // Key is game title
        public int TotalGames => _games.Count;

        // Raised after a Game is added to or removed from the Collection, with the Game's name.
        // Anything that keeps answers worked out from the Collection (such as cached
        // Recommendations) listens here so it can throw them away.
        public event Action<string>? Changed;

        public IEnumerable<OwnedGame> Games => [.. _games.Values.OrderBy(og => og.Game.Name)];

        public void Add(OwnedGame game)
        {
            if (_games.ContainsKey(game.Game.Name))
            {
                throw new InvalidOperationException("Title is already owned.");
            }

            _games.Add(game.Game.Name, game);
            Changed?.Invoke(game.Game.Name);
        }

        public void Remove(string name)
        {
            if (!_games.Remove(name))
            {
                throw new InvalidOperationException("Title not found in collection.");
            }

            Changed?.Invoke(name);
        }

        public bool Owns(string title) => _games.ContainsKey(title);

        public OwnedGame? Get(string title) => _games.GetValueOrDefault(title);

        public IEnumerable<OwnedGame> Search(string term)
        {
            return [.. _games.Values.Where(og => og.Game.Search(term))];
        }

        public IEnumerable<OwnedGame> FilterByPlayerCount(int n)
        {
            return [.. _games.Values.Where((g) => ((g.Game.MinPlayers <= n) && (g.Game.MaxPlayers >= n)))];
        }

        public IEnumerable<OwnedGame> Find(string? term, int? playerCount)
        {
            IEnumerable<OwnedGame> result = Games;

            if (!string.IsNullOrWhiteSpace(term))
                result = result.Intersect(Search(term));

            if (playerCount is int n)
                result = result.Intersect(FilterByPlayerCount(n));

            return result;
        }
    }
}
