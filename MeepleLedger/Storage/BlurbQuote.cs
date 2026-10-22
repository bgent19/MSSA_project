using System.Text;

namespace MeepleLedger.Storage;

// Picks the sentence of a Blurb to quote in a Reason (A-13): the sentence that shares the most
// words with the Ask, or the opening sentence when none of them do.
//
// The vector match is made against the whole Blurb, so no single sentence "is" the match. The
// sentence that repeats the Ask's own words is the part a person can check, which is why it is
// the one quoted. MatchesAskWords says which of the two cases happened, so the Reason can be
// worded honestly.
public class BlurbQuote
{
    // Long sentences are cut to about this many characters, at a word boundary.
    private const int LongestQuote = 200;

    public string Sentence { get; }

    // True when the quoted sentence contains at least one of the Ask's words.
    public bool MatchesAskWords { get; }

    private BlurbQuote(string sentence, bool matchesAskWords)
    {
        Sentence = sentence;
        MatchesAskWords = matchesAskWords;
    }

    // Returns null when there is no Blurb to quote.
    public static BlurbQuote? Find(string? blurb, List<string> askWords)
    {
        if (string.IsNullOrWhiteSpace(blurb))
        {
            return null;
        }

        List<string> sentences = SplitIntoSentences(blurb);
        if (sentences.Count == 0)
        {
            return null;
        }

        // The sentence matching the most different Ask words wins; on a tie, the earlier one.
        string bestSentence = sentences[0];
        int bestCount = 0;

        foreach (string sentence in sentences)
        {
            int count = 0;
            foreach (string askWord in askWords)
            {
                if (AskWords.HasWordStartingWith(sentence, askWord))
                {
                    count++;
                }
            }

            if (count > bestCount)
            {
                bestSentence = sentence;
                bestCount = count;
            }
        }

        return new BlurbQuote(Shorten(bestSentence), bestCount > 0);
    }

    // Splits on line breaks and on ". ", "! " and "? ", and squeezes runs of spaces into one.
    // Crude (it splits "Dr. Who" in two), but a Blurb is marketing prose, not legal text.
    private static List<string> SplitIntoSentences(string blurb)
    {
        List<string> sentences = [];
        StringBuilder current = new StringBuilder();

        for (int i = 0; i < blurb.Length; i++)
        {
            char c = blurb[i];

            if (c == '\n' || c == '\r')
            {
                AddSentence(sentences, current);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // One space between words, however many the Blurb had
                if (current.Length > 0 && current[current.Length - 1] != ' ')
                {
                    current.Append(' ');
                }
                continue;
            }

            current.Append(c);

            bool endsSentence = c == '.' || c == '!' || c == '?';
            bool nextIsSpaceOrEnd = i + 1 >= blurb.Length || char.IsWhiteSpace(blurb[i + 1]);
            if (endsSentence && nextIsSpaceOrEnd)
            {
                AddSentence(sentences, current);
            }
        }

        AddSentence(sentences, current);
        return sentences;
    }

    private static void AddSentence(List<string> sentences, StringBuilder current)
    {
        string sentence = current.ToString().Trim();
        current.Clear();

        // Skips blank lines and leftovers like a lone "CARDS:" heading
        if (sentence.Length > 0 && sentence.Contains(' '))
        {
            sentences.Add(sentence);
        }
    }

    private static string Shorten(string sentence)
    {
        if (sentence.Length <= LongestQuote)
        {
            return sentence;
        }

        int cut = sentence.LastIndexOf(' ', LongestQuote);
        if (cut < 1)
        {
            cut = LongestQuote;
        }

        return sentence.Substring(0, cut).TrimEnd(',', ';', ':') + "…";
    }
}
