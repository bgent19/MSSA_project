using MeepleLedger.Storage;

namespace MeepleLedger.Tests
{
    public class BlurbQuoteTests
    {
        private const string PandemicBlurb =
            "In Pandemic, several virulent diseases have broken out simultaneously all over the world! " +
            "The players are disease-fighting specialists whose mission is to treat disease hotspots.\n\n" +
            "Players must work together, playing to their characters' strengths, as a cooperative team.";

        [Fact]
        public void Find_QuotesTheSentenceSharingTheMostAskWords()
        {
            var askWords = AskWords.FindUsefulWords("co-operative team game");

            BlurbQuote? quote = BlurbQuote.Find(PandemicBlurb, askWords);

            Assert.NotNull(quote);
            Assert.True(quote.MatchesAskWords);
            Assert.Equal("Players must work together, playing to their characters' strengths, as a cooperative team.", quote.Sentence);
        }

        [Fact]
        public void Find_QuotesTheOpeningSentence_WhenNoAskWordAppears()
        {
            var askWords = AskWords.FindUsefulWords("zombies");

            BlurbQuote? quote = BlurbQuote.Find(PandemicBlurb, askWords);

            Assert.NotNull(quote);
            Assert.False(quote.MatchesAskWords);
            Assert.Equal("In Pandemic, several virulent diseases have broken out simultaneously all over the world!", quote.Sentence);
        }

        [Fact]
        public void Find_ReturnsNull_WhenThereIsNoBlurb()
        {
            Assert.Null(BlurbQuote.Find(null, ["coop"]));
            Assert.Null(BlurbQuote.Find("   ", ["coop"]));
        }

        [Fact]
        public void Find_ShortensALongSentenceAtAWordBoundary()
        {
            string longSentence = string.Join(" ", Enumerable.Repeat("cooperative", 40)) + ".";

            BlurbQuote? quote = BlurbQuote.Find(longSentence, ["coop"]);

            Assert.NotNull(quote);
            Assert.True(quote.Sentence.Length <= 201);
            Assert.EndsWith("cooperative…", quote.Sentence);
        }
    }
}
