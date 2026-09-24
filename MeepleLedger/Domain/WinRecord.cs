namespace MeepleLedger.Domain
{
    public record WinRecord(int Wins, int Plays)
    {
        // Null when there are no plays, so a never-played game has no win rate rather than a divide by zero
        public double? Rate => Plays == 0 ? null : (double)Wins / Plays;
    }
}
