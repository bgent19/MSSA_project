namespace MeepleLedger.Domain
{
    public class Play
    {
        public Game Game { get; set; }
        public DateTime PlayedOn { get; set; }
        public int? DurationMinutes { get; set; }
        public string? Location { get; set; }
        public List<PlayerResult> Results { get; set; }

        public IEnumerable<PlayerResult> Winners => Results.Where(r => r.IsWinner);
        public bool HasWinner => Winners.Any();

        public Play(Game g, DateTime d, List<PlayerResult> r, int? min = null, string? l = null)
        {
            Game = g;
            PlayedOn = d;
            Results = r;

            if (Game.MaxPlayers < Results.Count)
            {
                throw new ArgumentOutOfRangeException("Results", "Results cannot exceed max players for a game.");
            }

            DurationMinutes = min;
            Location = l;
        }

    }
}
