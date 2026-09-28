using MeepleLedger.Domain;

namespace MeepleLedger.Tests
{
    public class RecommendationDomainTests
    {
        [Fact]
        public void Table_CarriesPlayerCountMinutesAndOwnedOnly()
        {
            var table = new Table(3, 60, true);

            Assert.Equal(3, table.PlayerCount);
            Assert.Equal(60, table.MinutesAvailable);
            Assert.True(table.OwnedOnly);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Table_RejectsAPlayerCountBelowOne(int playerCount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Table(playerCount, 60, false));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-30)]
        public void Table_RejectsMinutesBelowOne(int minutes)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Table(3, minutes, false));
        }

        [Fact]
        public void Table_Admits_AGameThatFitsThePlayersAndTheTime()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4, PlaytimeMinutes = 90 };
            var table = new Table(3, 90, false);

            Assert.True(table.Admits(catan));
        }

        [Fact]
        public void Table_Admits_RejectsTooFewPlayers()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4, PlaytimeMinutes = 90 };
            var table = new Table(2, 120, false);

            Assert.False(table.Admits(catan));
        }

        [Fact]
        public void Table_Admits_RejectsTooManyPlayers()
        {
            var patchwork = new Game { Name = "Patchwork", MinPlayers = 2, MaxPlayers = 2, PlaytimeMinutes = 30 };
            var table = new Table(3, 120, false);

            Assert.False(table.Admits(patchwork));
        }

        [Fact]
        public void Table_Admits_RejectsAGameLongerThanTheTimeAvailable()
        {
            var arkNova = new Game { Name = "Ark Nova", MinPlayers = 1, MaxPlayers = 4, PlaytimeMinutes = 150 };
            var table = new Table(2, 60, false);

            Assert.False(table.Admits(arkNova));
        }

        [Fact]
        public void Table_Admits_TreatsAnUnknownMaxPlayersAsNoLimit()
        {
            var unknownMax = new Game { Name = "Mystery Box", MinPlayers = 2, MaxPlayers = 0, PlaytimeMinutes = 30 };
            var table = new Table(6, 60, false);

            Assert.True(table.Admits(unknownMax));
        }

        [Fact]
        public void Table_Admits_TreatsAnUnknownPlaytimeAsNoLimit()
        {
            var unknownPlaytime = new Game { Name = "Mystery Box", MinPlayers = 2, MaxPlayers = 4, PlaytimeMinutes = 0 };
            var table = new Table(3, 15, false);

            Assert.True(table.Admits(unknownPlaytime));
        }

        [Fact]
        public void Ask_CarriesTheQueryText()
        {
            var ask = new Ask("Something co-operative and quick for three.");

            Assert.Equal("Something co-operative and quick for three.", ask.Text);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Ask_RejectsEmptyOrWhitespaceText(string? text)
        {
            Assert.Throws<ArgumentException>(() => new Ask(text!));
        }

        [Fact]
        public void Recommendation_CarriesTheGameRankAndReason()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };

            var recommendation = new Recommendation(catan, 1, "Plays three in about an hour.");

            Assert.Same(catan, recommendation.Game);
            Assert.Equal(1, recommendation.Rank);
            Assert.Equal("Plays three in about an hour.", recommendation.Reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Recommendation_RejectsAMissingReason(string? reason)
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };

            Assert.Throws<ArgumentException>(() => new Recommendation(catan, 1, reason!));
        }

        [Fact]
        public void Recommendation_RejectsARankBelowOne()
        {
            var catan = new Game { Name = "Catan", MinPlayers = 3, MaxPlayers = 4 };

            Assert.Throws<ArgumentOutOfRangeException>(() => new Recommendation(catan, 0, "A fine choice."));
        }
    }
}
