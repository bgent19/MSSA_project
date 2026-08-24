using Microsoft.AspNetCore.Components.Routing;
using System.Globalization;

namespace MeepleLedger.Domain
{
    public class Play
    {
        public required Game Game;
        public required DateTime PlayedOn;
        public int? DurationMinutes;
        public string? Location;
        public required List<PlayerResult> Results;

        IEnumerable<PlayerResult> Winners => Results.Where(r => r.IsWinner);
        public bool? HasWinner => Winners.Any();

        public Play(Game g, DateTime d, List<PlayerResult> r, int min = 0, string l = "")
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
