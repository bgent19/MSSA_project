namespace MeepleLedger.Domain
{
    public class Game
    {
        public required string Name;
        int MinPlayers;
        int MaxPlayers;
        int PlayTimeMinutes;
    }

    public class OwnedGame
    {
        public required Game Game;
        public DateTime DateAcquired;
        public Condition Condition;
        public string Notes;
    }

    public enum Condition
    {
        Mint,
        Good,
        Played,
        Worn,
    }
}
