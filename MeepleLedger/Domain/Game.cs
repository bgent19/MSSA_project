namespace MeepleLedger.Domain
{
    public class Game
    {
        public required string Name { get; set; }
        public string? Designer { get; set; }
        public int MinPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public int PlaytimeMinutes { get; set; }
        public List<string> Categories { get; set; } = [];
        public List<string> Mechanics { get; set; } = [];

        // BGG average weight, 1 (light) to 5 (heavy). Null means unrated, not light.
        public double? Weight { get; set; }

        // Prose description. Not in the compiled seed (ADR-0003), so null in the in-memory provider.
        public string? Blurb { get; set; }

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
