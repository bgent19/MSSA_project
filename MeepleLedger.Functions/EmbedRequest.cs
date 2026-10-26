using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeepleLedger.Functions;

// The intent to re-embed one Game (A-10). The same shape as EmbedRequest in the Seeder and the Worker;
// keep all three in step.
public class EmbedRequest
{
    // The queue provision-servicebus.ps1 creates. The Seeder's watch-cosmos and the Worker use the same name.
    public const string QueueName = "embed-requests";

    public const int CurrentSchemaVersion = 1;

    // camelCase on the wire. Web defaults also read property names in any case.
    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public int SchemaVersion { get; set; }
    public string GameId { get; set; } = "";
    public string TextHash { get; set; } = "";     // the hash the change feed saw; the function re-reads the Game anyway
    public string Reason { get; set; } = "";       // "new" or "changed" from the change feed, "manual" or "test" from the Worker, "api" from QueueEmbed
    public DateTime DetectedAtUtc { get; set; }
}

// The parts of a Game's Cosmos document the functions read. Properties not listed here are left
// alone by the patch.
public class CosmosGameDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Categories { get; set; } = [];
    public List<string> Mechanics { get; set; } = [];
    public string? Blurb { get; set; }
    public float[] Embedding { get; set; } = [];
    public string? EmbeddingTextHash { get; set; }
}

public static class EmbeddingText
{
    // The text that gets embedded for one Game. Must stay exactly the same as BuildEmbeddingText in
    // MeepleLedger.Seeder/Program.cs and MeepleLedger.Worker/Program.cs: the hashes in Cosmos were
    // made by the Seeder, and if this builds even one character differently, every vector written
    // here looks like an edit to the change feed, which queues another embed, forever.
    // Lines end in "\r\n" whatever the OS: a Function App runs on Linux (A-22), where AppendLine
    // would write "\n".
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

    // Same as HashText in the Seeder: SHA-256 of the UTF-8 text, as upper-case hex
    public static string Hash(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash);
    }
}
