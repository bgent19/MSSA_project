namespace MeepleLedger.Domain
{
    public class GameCatalog(IEnumerable<Game> games)
    {
        public IReadOnlyList<Game> Games { get; } = [.. games];

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
