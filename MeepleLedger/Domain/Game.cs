namespace MeepleLedger.Domain
{
    public class Game
    {
        public required string Name { get; set; }
        public string? Designer { get; set; }
        public int MinPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public int PlaytimeMinutes { get; set; }

        public bool Search(string term)
        {
            return Name.Contains(term, StringComparison.OrdinalIgnoreCase)
             || (Designer?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    public class OwnedGame
    {
        public required Game Game { get; set; }
        public DateTime DateAcquired { get; set; }
        public Condition Condition { get; set; }
        public string? Notes { get; set; }
    }

    public enum Condition
    {
        Mint,
        Good,
        Played,
        Worn,
    }
}
