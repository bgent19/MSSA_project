using Microsoft.AspNetCore.Identity;

namespace MeepleLedger.Domain
{
    public class PlayLog
    {
        public required string OwnerName;
        private readonly List<Play> _plays = [];

        public void Record(Play p)
        {
            if(!p.Results.Exists(pr => pr.PlayerName == OwnerName))
            {
                throw new InvalidOperationException("You must be recorded in a game you wish to log.");
            }

            _plays.Append(p);
        }

        public IEnumerable<Play> ForGame(Game g)
        {
            return _plays.Where(p => p.Game == g);
        }

        public void RecentFirst()
        {
            _plays.OrderBy(static p => p.PlayedOn);
        }

        public void MostPlayed()
        {
            _plays.GroupBy(p => p.Game).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        }
    }
}
