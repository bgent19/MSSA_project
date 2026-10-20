using System.Text;

namespace MeepleLedger.Storage;

// Word matching between an Ask and a Game's name, categories and mechanics.
// The in-memory Search Index ranks with it; every Search Index uses it to say in a Reason
// which categories and mechanics an Ask touched.
public static class AskWords
{
    // Ask words shorter than this are ignored ("a", "of", "me"...).
    private const int ShortestUsefulWord = 3;

    // Common words that say nothing about which Game to pick.
    private static readonly HashSet<string> WordsToIgnore =
    [
        "and", "the", "for", "with", "that", "this", "something", "some", "any", "anything",
        "game", "games", "play", "playing", "want", "what", "should", "can", "could", "would",
        "our", "you", "your", "are", "about", "like", "please", "tonight", "good", "fun",
        "player", "players", "people", "friends", "two", "three", "four", "five", "six",
        "quick", "short", "long", "minutes", "hour", "hours",
    ];

    // The words of the Ask worth searching for: long enough, not filler, and without a plural "s"
    // so that "cards" still finds "Card Game".
    public static List<string> FindUsefulWords(string text)
    {
        List<string> useful = [];

        foreach (string word in SplitIntoWords(text))
        {
            if (word.Length < ShortestUsefulWord || WordsToIgnore.Contains(word))
            {
                continue;
            }

            string singular = word;
            if (word.Length > ShortestUsefulWord && word.EndsWith('s') && !word.EndsWith("ss"))
            {
                singular = word.Substring(0, word.Length - 1);
            }

            if (!useful.Contains(singular))
            {
                useful.Add(singular);
            }
        }

        return useful;
    }

    // True when any word in the text begins with the given word, so "coop" finds "Cooperative Game".
    public static bool HasWordStartingWith(string text, string start)
    {
        foreach (string word in SplitIntoWords(text))
        {
            if (word.StartsWith(start, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // True when any of the Ask words starts a word in the text.
    public static bool MatchesAny(string text, List<string> askWords)
    {
        foreach (string askWord in askWords)
        {
            if (HasWordStartingWith(text, askWord))
            {
                return true;
            }
        }

        return false;
    }

    // Lower-cases the text and splits it on anything that is not a letter or digit.
    // Hyphens and apostrophes are dropped rather than split on, so "co-op" becomes "coop".
    private static List<string> SplitIntoWords(string text)
    {
        List<string> words = [];
        StringBuilder current = new StringBuilder();

        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (c == '-' || c == '\'')
            {
                // Join the two halves: "co-op" -> "coop".
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
