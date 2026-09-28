namespace MeepleLedger.Domain
{
    // The constraints of one game night: who is at the table, how long you have,
    // and whether the answer must come from the Collection or may come from the whole Catalog.
    public class Table
    {
        public int PlayerCount { get; }
        public int MinutesAvailable { get; }
        public bool OwnedOnly { get; }

        public Table(int playerCount, int minutesAvailable, bool ownedOnly)
        {
            if (playerCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(playerCount), "A Table needs at least one player.");
            }

            if (minutesAvailable < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(minutesAvailable), "A Table needs at least one minute.");
            }

            PlayerCount = playerCount;
            MinutesAvailable = minutesAvailable;
            OwnedOnly = ownedOnly;
        }

        // Answers whether a Game fits the physical constraints of this Table: the player count
        // and the time available. It does not check OwnedOnly, because ownership is a fact about
        // the Collection, not about the Game.
        //
        // BGG data can hold 0 for MinPlayers, MaxPlayers or PlaytimeMinutes when the value is
        // unknown. An unknown value is treated as "no limit", so the Game is not ruled out by
        // missing data.
        public bool Admits(Game game)
        {
            if (game.MinPlayers > 0 && PlayerCount < game.MinPlayers)
            {
                return false;
            }

            if (game.MaxPlayers > 0 && PlayerCount > game.MaxPlayers)
            {
                return false;
            }

            if (game.PlaytimeMinutes > 0 && game.PlaytimeMinutes > MinutesAvailable)
            {
                return false;
            }

            return true;
        }
    }
}
