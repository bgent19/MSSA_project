namespace MeepleLedger.Domain
{
    public class Play
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public Game Game { get; set; }
        public DateTime PlayedOn { get; set; }
        public int? DurationMinutes { get; set; }
        public string? Location { get; set; }
        public List<PlayerResult> Results { get; set; }

        public IEnumerable<PlayerResult> Winners => Results.Where(r => r.IsWinner);
        public bool HasWinner => Winners.Any();

        public bool IsWonBy(string playerName) => Winners.Any(r => r.PlayerName == playerName);

        public Play(Game g, DateTime d, List<PlayerResult> r, int? durationMinutes = null, string? location = null)
        {
            Game = g;
            PlayedOn = d;
            Results = r;

            if (Game.MaxPlayers < Results.Count)
            {
                throw new ArgumentOutOfRangeException("Results", "Results cannot exceed max players for a game.");
            }

            DurationMinutes = durationMinutes;
            Location = location;
        }

    }
}
