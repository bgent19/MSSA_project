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
    }
}
