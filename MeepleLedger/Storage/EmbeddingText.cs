using System.Security.Cryptography;
using System.Text;

namespace MeepleLedger.Storage;

// What gets embedded for a Game added at runtime (S-07), and the hash written beside its vector.
//
// The FOURTH copy: BuildEmbeddingText and HashText in MeepleLedger.Seeder/Program.cs and
// MeepleLedger.Worker/Program.cs, and EmbeddingText in MeepleLedger.Functions/EmbedRequest.cs, must
// all build exactly the same text. If this one differs by a single character, the hash it writes
// won't match what watch-cosmos works out, and the change feed queues the Game to be embedded again.
// Keep all four in step.
public static class EmbeddingText
{
    // Lines end in "\r\n" whatever the OS: the hashes were first made on Windows, and AppendLine
    // would write "\n" on Linux, where the app runs in a container.
    public static string Build(string name, List<string> categories, List<string> mechanics, string blurb)
    {
        StringBuilder text = new StringBuilder();
        text.Append("Name: " + name + "\r\n");
        text.Append("Categories: " + string.Join(", ", categories) + "\r\n");
        text.Append("Mechanics: " + string.Join(", ", mechanics) + "\r\n");
        text.Append("\r\n");
        text.Append(blurb);
        return text.ToString();
    }

    // Same as HashText in the Seeder: SHA-256 of the UTF-8 text, as upper-case hex.
    public static string Hash(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash);
    }

    // "Cooperative Game, Card Game, Hand Management": the categories then the mechanics, for the
    // full-text policy on /facts (S-02). Same as BuildFacts in the Seeder.
    public static string BuildFacts(List<string> categories, List<string> mechanics)
    {
        List<string> facts = [];
        facts.AddRange(categories);
        facts.AddRange(mechanics);
        return string.Join(", ", facts);
    }
}
