namespace MeepleLedger.Storage;

// One Game in the Cosmos games container, the shape upload-cosmos writes (CosmosGameDocument in the
// Seeder; keep the two in step). The CosmosClient writes property names in camelCase, so Id is "id"
// (the partition key, /id) and Embedding is "embedding" (the vector path).
public class GameDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Designer { get; set; }
    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public int PlaytimeMinutes { get; set; }
    public List<string> Categories { get; set; } = [];
    public List<string> Mechanics { get; set; } = [];

    // Categories and mechanics as one string, for hybrid search's FullTextScore (S-02).
    public string Facts { get; set; } = "";

    public double? Weight { get; set; }
    public string? Blurb { get; set; }

    // Null when only the facts were read (loading the Catalog never reads the 1536 floats).
    public float[]? Embedding { get; set; }

    // The hash of the text the vector was made from (EmbeddingText.Hash). watch-cosmos compares it
    // with the Game's current text, so a Game written with a matching hash is not queued again.
    public string? EmbeddingTextHash { get; set; }

    // True for a Game added from BoardGameGeek while the app ran (S-07). upload-cosmos deletes any
    // document that isn't one of the compiled Games, except these.
    public bool? AddedAtRuntime { get; set; }
}
