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

        public bool IsWonBy(string playerName) => Winners.Any(r => PlayerNames.AreSame(r.PlayerName, playerName));

        public Play(Game g, DateTime d, List<PlayerResult> r, int? durationMinutes = null, string? location = null)
        {
            Game = g;
            PlayedOn = d;
            Results = CleanResults(r);

            if (Game.MaxPlayers < Results.Count)
            {
                throw new ArgumentOutOfRangeException("Results", "Results cannot exceed max players for a game.");
            }

            RequireEachPlayerOnce(Results);

            DurationMinutes = durationMinutes;
            Location = location;
        }

        // Copies the results with trimmed names, so a stored Play never carries surrounding spaces
        // and the caller's list is left alone.
        private static List<PlayerResult> CleanResults(List<PlayerResult> results)
        {
            List<PlayerResult> cleaned = [];
            foreach (var result in results)
            {
                cleaned.Add(new PlayerResult
                {
                    PlayerName = PlayerNames.Clean(result.PlayerName),
                    Score = result.Score,
                    IsWinner = result.IsWinner,
                });
            }
            return cleaned;
        }

        // "Tom" and "tom" are one Player, and one Player can't sit at a Play twice.
        private static void RequireEachPlayerOnce(List<PlayerResult> results)
        {
            for (int i = 0; i < results.Count; i++)
            {
                for (int j = i + 1; j < results.Count; j++)
                {
                    if (PlayerNames.AreSame(results[i].PlayerName, results[j].PlayerName))
                    {
                        throw new ArgumentException(
                            $"{results[j].PlayerName} is in this Play twice. Names that differ only in case or spaces are the same Player.");
                    }
                }
            }
        }
    }
}
