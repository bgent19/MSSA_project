namespace MeepleLedger.Domain
{
    public class PlayLog
    {
        public required string OwnerName { get; set; }
        private readonly List<Play> _plays = [];

        public void Record(Play p)
        {
            if(!p.Results.Exists(pr => pr.PlayerName == OwnerName))
            {
                throw new InvalidOperationException("You must be recorded in a game you wish to log.");
            }

            _plays.Add(p);
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
