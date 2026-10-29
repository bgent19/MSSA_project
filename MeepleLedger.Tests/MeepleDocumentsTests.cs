using MeepleLedger.Domain;
using MeepleLedger.Storage;

namespace MeepleLedger.Tests
{
    // S-04: what the Cosmos store writes, and that a stored row is checked by the domain when read back.
    public class MeepleDocumentsTests
    {
        private static readonly Game Catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };
        private static readonly GameCatalog Catalog = new GameCatalog([Catan]);

        [Fact]
        public void Play_RoundTrip_KeepsEveryField()
        {
            var play = new Play(Catan, new DateTime(2026, 10, 29), [
                new PlayerResult { PlayerName = "Brett", Score = 10, IsWinner = true },
                new PlayerResult { PlayerName = "Alex", Score = 8 },
            ], durationMinutes: 75, location: "Library");

            PlayDocument document = MeepleDocuments.FromPlay(play, "Brett");
            Play back = MeepleDocuments.ToPlay(document, Catalog);

            Assert.Equal(play.Id.ToString(), document.Id);
            Assert.Equal("Brett", document.OwnerName);
            Assert.Equal(play.Id, back.Id);
            Assert.Same(Catan, back.Game);
            Assert.Equal(play.PlayedOn, back.PlayedOn);
            Assert.Equal(75, back.DurationMinutes);
            Assert.Equal("Library", back.Location);
            Assert.Equal(2, back.Results.Count);
            Assert.Equal("Brett", back.Results[0].PlayerName);
            Assert.Equal(10, back.Results[0].Score);
            Assert.True(back.Results[0].IsWinner);
            Assert.False(back.Results[1].IsWinner);
        }

        [Fact]
        public void Play_DateIsStoredWithoutATime()
        {
            // A late-night Play must stay on its own day, whatever the time zone.
            var play = new Play(Catan, new DateTime(2026, 10, 29, 23, 45, 0), [new PlayerResult { PlayerName = "Brett" }]);

            PlayDocument document = MeepleDocuments.FromPlay(play, "Brett");

            Assert.Equal("2026-10-29", document.PlayedOn);
        }

        [Fact]
        public void OwnedGame_RoundTrip_KeepsEveryField()
        {
            var owned = new OwnedGame { Game = Catan, DateAcquired = new DateTime(2024, 12, 25), Condition = Condition.Played, Notes = "Gift" };

            OwnedGameDocument document = MeepleDocuments.FromOwnedGame(owned, "Brett");
            OwnedGame back = MeepleDocuments.ToOwnedGame(document, Catalog);

            Assert.Equal("Catan", document.Id);
            Assert.Equal("2024-12-25", document.DateAcquired);
            Assert.Equal("Played", document.Condition);
            Assert.Same(Catan, back.Game);
            Assert.Equal(owned.DateAcquired, back.DateAcquired);
            Assert.Equal(Condition.Played, back.Condition);
            Assert.Equal("Gift", back.Notes);
        }

        [Fact]
        public void ToPlay_FailsLoudlyForAGameNotInTheCatalog()
        {
            var document = new PlayDocument
            {
                Id = Guid.NewGuid().ToString(),
                OwnerName = "Brett",
                GameName = "Not A Real Game",
                PlayedOn = "2026-10-29",
                Results = [new PlayerResultDocument { PlayerName = "Brett" }],
            };

            Assert.Throws<InvalidOperationException>(() => MeepleDocuments.ToPlay(document, Catalog));
        }

        [Fact]
        public void ToPlay_RejectsAStoredPlayThatBreaksAnInvariant()
        {
            // Five Players at a four-seat Game: the Play constructor refuses it.
            var document = new PlayDocument
            {
                Id = Guid.NewGuid().ToString(),
                OwnerName = "Brett",
                GameName = "Catan",
                PlayedOn = "2026-10-29",
                Results =
                [
                    new PlayerResultDocument { PlayerName = "Brett" },
                    new PlayerResultDocument { PlayerName = "Alex" },
                    new PlayerResultDocument { PlayerName = "Sam" },
                    new PlayerResultDocument { PlayerName = "Jordan" },
                    new PlayerResultDocument { PlayerName = "Priya" },
                ],
            };

            Assert.Throws<ArgumentOutOfRangeException>(() => MeepleDocuments.ToPlay(document, Catalog));
        }
    }
}
