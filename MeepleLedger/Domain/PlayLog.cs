namespace MeepleLedger.Domain
{
    public class PlayLog
    {
        public required string OwnerName { get; set; }
        private readonly List<Play> _plays = [];

        public void Record(Play p)
        {
            RequireOwner(p);
            _plays.Add(p);
        }

        public Play? Get(Guid id) => _plays.Find(p => p.Id == id);

        // Swaps in the play with the same Id; construct a fresh Play so the seat invariant is re-checked.
        public void Replace(Play p)
        {
            var index = _plays.FindIndex(existing => existing.Id == p.Id);
            if (index < 0)
            {
                throw new InvalidOperationException("Play not found in log.");
            }

            RequireOwner(p);
            _plays[index] = p;
        }

        public void Remove(Guid id)
        {
            if (_plays.RemoveAll(p => p.Id == id) == 0)
            {
                throw new InvalidOperationException("Play not found in log.");
            }
        }

        private void RequireOwner(Play p)
        {
            if(!p.Results.Exists(pr => pr.PlayerName == OwnerName))
            {
                throw new InvalidOperationException("You must be recorded in a game you wish to log.");
            }
        }

        public IEnumerable<Play> ForGame(Game g)
        {
            return RecentFirst().Where(p => p.Game == g);
        }

        public IEnumerable<Game> GamesPlayed()
        {
            return _plays.Select(p => p.Game).Distinct().OrderBy(g => g.Name);
        }

        public IEnumerable<Play> RecentFirst()
        {
            return _plays.OrderByDescending(static p => p.PlayedOn);
        }

        public WinRecord WinRecordFor(Game g)
        {
            var plays = ForGame(g).ToList();
            return new WinRecord(plays.Count(p => p.IsWonBy(OwnerName)), plays.Count);
        }

        public IEnumerable<Game> MostPlayed()
        {
            return _plays.GroupBy(p => p.Game).OrderByDescending(g => g.Count()).Select(g => g.Key);
        }
    }
}
