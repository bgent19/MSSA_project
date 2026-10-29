using MeepleLedger.Domain;
using MeepleLedger.Storage;

namespace MeepleLedger.Tests
{
    public class FullTextTermsTests
    {
        private static readonly List<Game> Catalog =
        [
            new Game
            {
                Name = "Pandemic", MinPlayers = 2, MaxPlayers = 4, PlaytimeMinutes = 45,
                Categories = ["Medical"], Mechanics = ["Cooperative Game", "Hand Management"],
            },
            new Game
            {
                Name = "Coup", MinPlayers = 2, MaxPlayers = 6, PlaytimeMinutes = 15,
                Categories = ["Bluffing", "Card Game"], Mechanics = ["Player Elimination"],
            },
            new Game
            {
                Name = "Dominion", MinPlayers = 2, MaxPlayers = 4, PlaytimeMinutes = 30,
                Categories = ["Card Game"], Mechanics = ["Deck, Bag, and Pool Building"],
            },
        ];

        [Fact]
        public void FullTextTerms_CoOp_BecomesTheCategoryOrMechanicWordItStarts()
        {
            List<string> terms = FullTextTerms.Find("quick co-op for three", Catalog);

            Assert.Equal(["cooperative"], terms);
        }

        [Fact]
        public void FullTextTerms_WordsTheTableHandles_AreNotTerms()
        {
            // "card" is the only word here that is not filler or Table talk
            List<string> terms = FullTextTerms.Find("a quick card game for three players tonight", Catalog);

            Assert.Equal(["card"], terms);
        }

        [Fact]
        public void FullTextTerms_HyphenatedWord_FindsTheMechanicByItsHalves()
        {
            List<string> terms = FullTextTerms.Find("deck-building", Catalog);

            Assert.Equal(["deck", "building"], terms);
        }

        [Fact]
        public void FullTextTerms_VagueAsk_HasNoTerms()
        {
            List<string> terms = FullTextTerms.Find("something cutthroat with lots of backstabbing", Catalog);

            Assert.Empty(terms);
        }
    }
}
