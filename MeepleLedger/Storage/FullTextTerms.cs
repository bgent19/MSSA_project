using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// The keyword half of hybrid search (S-02): which words to hand Cosmos DB's FullTextScore for an Ask.
//
// The Ask's useful words (AskWords drops filler and the words the Table already handles, such as
// "quick", "three" and "tonight") are matched against the words of every category and mechanic in
// the Catalog, the same "starts with" rule a Reason uses. Each category or mechanic word an Ask word
// starts becomes a term, so "co-op" ("coop") becomes "cooperative", which full-text search would
// never find by itself.
//
// Because every term is a word some category or mechanic starts with an Ask word, any Game that
// full-text search ranks up also has a category or mechanic its Reason can name.
public static class FullTextTerms
{
    public static List<string> Find(string askText, IReadOnlyList<Game> catalogGames)
    {
        List<string> askWords = AskWords.FindUsefulWords(askText);
        List<string> terms = [];

        if (askWords.Count == 0)
        {
            return terms;
        }

        foreach (string factWord in FindFactWords(catalogGames))
        {
            foreach (string askWord in askWords)
            {
                if (factWord.StartsWith(askWord, StringComparison.Ordinal) && !terms.Contains(factWord))
                {
                    terms.Add(factWord);
                }
            }
        }

        return terms;
    }

    // Every word of every category and mechanic in the Catalog, once each, in the order first seen.
    // Filler words ("game", "player") are left out: an Ask word is never one of them anyway.
    private static List<string> FindFactWords(IReadOnlyList<Game> catalogGames)
    {
        List<string> factWords = [];

        foreach (Game game in catalogGames)
        {
            List<string> facts = [];
            facts.AddRange(game.Categories);
            facts.AddRange(game.Mechanics);

            foreach (string fact in facts)
            {
                foreach (string word in AskWords.FindUsefulWords(fact))
                {
                    if (!factWords.Contains(word))
                    {
                        factWords.Add(word);
                    }
                }
            }
        }

        return factWords;
    }
}
