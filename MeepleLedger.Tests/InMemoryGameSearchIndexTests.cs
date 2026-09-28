using MeepleLedger.Domain;
using MeepleLedger.Storage;

namespace MeepleLedger.Tests
{
    public class InMemoryGameSearchIndexTests
    {
        private static readonly Game Pandemic = new Game
        {
            Name = "Pandemic", MinPlayers = 2, MaxPlayers = 4, PlaytimeMinutes = 45,
            Mechanics = ["Cooperative Game", "Hand Management"],
        };

        private static readonly Game Hanabi = new Game
        {
            Name = "Hanabi", MinPlayers = 2, MaxPlayers = 5, PlaytimeMinutes = 25,
            Categories = ["Card Game"], Mechanics = ["Cooperative Game"],
        };

        private static readonly Game Catan = new Game
        {
            Name = "Catan", MinPlayers = 3, MaxPlayers = 4, PlaytimeMinutes = 90,
            Categories = ["Negotiation"], Mechanics = ["Dice Rolling", "Trading"],
        };

        private static readonly Game SpiritIsland = new Game
        {
            Name = "Spirit Island", MinPlayers = 1, MaxPlayers = 4, PlaytimeMinutes = 120,
            Mechanics = ["Cooperative Game"],
        };

        private static InMemoryGameSearchIndex BuildIndex(List<Game> catalogGames, List<Game> ownedGames)
        {
            FakeStore store = new FakeStore();
            foreach (Game game in ownedGames)
            {
                store.Collection.Add(new OwnedGame { Game = game, Condition = Condition.Good });
            }

            return new InMemoryGameSearchIndex(new FakeCatalogSource(catalogGames), store);
        }

        [Fact]
        public async Task OwnedOnlyTable_NeverReturnsAnUnownedGame()
        {
            var index = BuildIndex([Pandemic, Hanabi, Catan], [Catan]);
            var table = new Table(3, 120, ownedOnly: true);

            // The Ask points straight at the unowned co-op games; they must still be left out.
            var results = await index.RecommendAsync(new Ask("cooperative card game"), table, 10);

            Assert.Single(results);
            Assert.Same(Catan, results[0].Game);
        }

        [Fact]
        public async Task OwnedOnlyTable_NeverReturnsAnUnownedGame_AgainstTheSeededData()
        {
            var catalogSource = new SeededCatalogSource();
            var store = new InMemoryMeepleStore(catalogSource);
            var index = new InMemoryGameSearchIndex(catalogSource, store);
            var table = new Table(4, 240, ownedOnly: true);

            var results = await index.RecommendAsync(new Ask("cooperative strategy with cards"), table, 1000);

            Assert.NotEmpty(results);
            foreach (Recommendation recommendation in results)
            {
                Assert.True(store.Collection.Owns(recommendation.Game.Name), recommendation.Game.Name + " is not owned.");
            }
        }

        [Fact]
        public async Task CatalogTable_MayReturnUnownedGames()
        {
            var index = BuildIndex([Pandemic, Hanabi, Catan], [Catan]);
            var table = new Table(3, 120, ownedOnly: false);

            var results = await index.RecommendAsync(new Ask("cooperative"), table, 10);

            Assert.Equal(3, results.Count);
        }

        [Fact]
        public async Task TableIsAppliedBeforeRanking_SoTheLimitIsFilledFromGamesThatFit()
        {
            // Spirit Island is the best text match but is too long for the Table. If the index
            // ranked first and filtered after, asking for two would bring back only one.
            var index = BuildIndex([SpiritIsland, Pandemic, Catan], []);
            var table = new Table(3, 90, ownedOnly: false);

            var results = await index.RecommendAsync(new Ask("spirit island cooperative"), table, 2);

            Assert.Equal(2, results.Count);
            Assert.Same(Pandemic, results[0].Game);
            Assert.Same(Catan, results[1].Game);
        }

        [Fact]
        public async Task GamesTheTableDoesNotAdmit_AreNeverReturned()
        {
            var index = BuildIndex([Pandemic, Hanabi, Catan], []);
            var table = new Table(5, 60, ownedOnly: false);

            var results = await index.RecommendAsync(new Ask("cooperative"), table, 10);

            Assert.Single(results);
            Assert.Same(Hanabi, results[0].Game);
        }

        [Fact]
        public async Task BetterMatchesRankFirst()
        {
            var index = BuildIndex([Catan, Pandemic, Hanabi], []);
            var table = new Table(3, 120, ownedOnly: false);

            // Hanabi matches "cooperative" and "card"; Pandemic only "cooperative"; Catan nothing.
            var results = await index.RecommendAsync(new Ask("co-operative cards"), table, 10);

            Assert.Same(Hanabi, results[0].Game);
            Assert.Same(Pandemic, results[1].Game);
            Assert.Same(Catan, results[2].Game);
        }

        [Fact]
        public async Task Ties_AreBrokenByName()
        {
            var index = BuildIndex([Pandemic, Catan, Hanabi], []);
            var table = new Table(3, 120, ownedOnly: false);

            // Nothing matches, so every Game ties on score.
            var results = await index.RecommendAsync(new Ask("zzzz"), table, 10);

            Assert.Equal("Catan", results[0].Game.Name);
            Assert.Equal("Hanabi", results[1].Game.Name);
            Assert.Equal("Pandemic", results[2].Game.Name);
        }

        [Fact]
        public async Task RanksStartAtOneWithNoGaps()
        {
            var index = BuildIndex([Pandemic, Hanabi, Catan], []);
            var table = new Table(3, 120, ownedOnly: false);

            var results = await index.RecommendAsync(new Ask("cooperative"), table, 10);

            for (int i = 0; i < results.Count; i++)
            {
                Assert.Equal(i + 1, results[i].Rank);
            }
        }

        [Fact]
        public async Task Reason_NamesWhatMatchedAndHowItFits()
        {
            var index = BuildIndex([Hanabi], [Hanabi]);
            var table = new Table(3, 60, ownedOnly: true);

            var results = await index.RecommendAsync(new Ask("co-op card game"), table, 10);

            Assert.Equal(
                "Matches your Ask on the Cooperative Game mechanic and the Card Game category. "
                + "It seats 3 (plays 2 to 5) and takes about 25 minutes of your 60. It is on your shelf.",
                results[0].Reason);
        }

        [Fact]
        public async Task RejectsAMaxResultsBelowOne()
        {
            var index = BuildIndex([Pandemic], []);
            var table = new Table(3, 60, ownedOnly: false);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => index.RecommendAsync(new Ask("cooperative"), table, 0));
        }

        private class FakeCatalogSource : IGameCatalogSource
        {
            public GameCatalog Catalog { get; }

            public FakeCatalogSource(List<Game> games)
            {
                Catalog = new GameCatalog(games);
            }
        }

        private class FakeStore : IMeepleStore
        {
            public GameCollection Collection { get; } = new();
            public PlayLog PlayLog { get; } = new() { OwnerName = "TheGentleBean" };
        }
    }
}
