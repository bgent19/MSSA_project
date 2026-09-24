using MeepleLedger.Domain;

namespace MeepleLedger.Tests
{
    public class DomainInvariantTests
    {
        [Fact]
        public void GameCollection_Add_RejectsATitleAlreadyOwned()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };
            var collection = new GameCollection();
            collection.Add(new OwnedGame { Game = catan, Condition = Condition.Good });

            var secondCopy = new OwnedGame { Game = catan, Condition = Condition.Mint };

            Assert.Throws<InvalidOperationException>(() => collection.Add(secondCopy));
            Assert.Equal(1, collection.TotalGames);
        }

        [Fact]
        public void Play_Constructor_RejectsMoreResultsThanMaxPlayers()
        {
            var patchwork = new Game { Name = "Patchwork", MinPlayers = 2, MaxPlayers = 2 };
            List<PlayerResult> results =
            [
                new PlayerResult { PlayerName = "Brett" },
                new PlayerResult { PlayerName = "Alex" },
                new PlayerResult { PlayerName = "Sam" },
            ];

            Assert.Throws<ArgumentOutOfRangeException>(() => new Play(patchwork, DateTime.Today, results));
        }

        [Fact]
        public void PlayLog_Record_RejectsAPlayWithoutTheOwner()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };
            var log = new PlayLog { OwnerName = "Brett" };
            var play = new Play(catan, DateTime.Today,
            [
                new PlayerResult { PlayerName = "Alex", IsWinner = true },
                new PlayerResult { PlayerName = "Sam" },
                new PlayerResult { PlayerName = "Jordan" },
            ]);

            Assert.Throws<InvalidOperationException>(() => log.Record(play));
            Assert.Empty(log.RecentFirst());
        }

        [Fact]
        public void PlayLog_Replace_RejectsAnEditThatDropsTheOwner()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };
            var log = new PlayLog { OwnerName = "Brett" };
            var original = new Play(catan, DateTime.Today,
            [
                new PlayerResult { PlayerName = "Brett", IsWinner = true },
                new PlayerResult { PlayerName = "Alex" },
                new PlayerResult { PlayerName = "Sam" },
            ]);
            log.Record(original);

            var edited = new Play(catan, DateTime.Today,
            [
                new PlayerResult { PlayerName = "Alex", IsWinner = true },
                new PlayerResult { PlayerName = "Sam" },
                new PlayerResult { PlayerName = "Jordan" },
            ]) { Id = original.Id };

            Assert.Throws<InvalidOperationException>(() => log.Replace(edited));
            Assert.Same(original, log.Get(original.Id));
        }

        [Fact]
        public void PlayLog_Replace_SwapsInTheEditedPlay()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };
            var log = new PlayLog { OwnerName = "Brett" };
            var original = new Play(catan, new DateTime(2026, 1, 1), [new PlayerResult { PlayerName = "Brett" }]);
            log.Record(original);

            var edited = new Play(catan, new DateTime(2026, 1, 2), [new PlayerResult { PlayerName = "Brett" }]) { Id = original.Id };
            log.Replace(edited);

            Assert.Same(edited, Assert.Single(log.RecentFirst()));
        }

        [Fact]
        public void PlayLog_Remove_DoesNotTouchTheCollection()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };
            var collection = new GameCollection();
            collection.Add(new OwnedGame { Game = catan, Condition = Condition.Good });
            var log = new PlayLog { OwnerName = "Brett" };
            var play = new Play(catan, DateTime.Today, [new PlayerResult { PlayerName = "Brett" }]);
            log.Record(play);

            log.Remove(play.Id);

            Assert.Empty(log.RecentFirst());
            Assert.True(collection.Owns("Catan"));
        }
    }
}
