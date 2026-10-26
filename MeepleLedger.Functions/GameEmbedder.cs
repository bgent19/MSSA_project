using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeepleLedger.Functions;

// Embeds one Game and writes the vector back to its Cosmos document. The same steps as the Worker's
// EmbedGameAsync (A-19), moved into a class so DI can hand one shared instance to every invocation.
//
// The write-back stays SDK code, not a Cosmos output binding: the output binding can only upsert the
// whole document, and the write here must be a patch guarded by the ETag that was read, or an edit
// made between the read and the write is lost.
public class GameEmbedder
{
    private readonly HttpClient _http;
    private readonly string _embeddingsUrl;
    private readonly Container _games;
    private readonly ILogger<GameEmbedder> _logger;

    public GameEmbedder(IConfiguration config, ILogger<GameEmbedder> logger)
    {
        _logger = logger;

        string openAiEndpoint = RequireSetting(config, "AzureOpenAI:Endpoint");
        string openAiKey = RequireSetting(config, "AzureOpenAI:Key");
        string deployment = RequireSetting(config, "AzureOpenAI:EmbeddingDeployment");

        // The same connection string the Cosmos input binding uses, so the account is configured once
        string cosmosConnection = RequireSetting(config, "CosmosConnection");
        string databaseName = RequireSetting(config, "CosmosDatabase");
        string containerName = RequireSetting(config, "CosmosContainer");

        // The deployment name goes in the URL, so this never refers to the model id directly
        _embeddingsUrl = openAiEndpoint.TrimEnd('/') + "/openai/deployments/" + deployment + "/embeddings?api-version=2024-10-21";

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("api-key", openAiKey);

        // camelCase, the same as upload-cosmos, so the documents read back into CosmosGameDocument
        CosmosClientOptions cosmosOptions = new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
            },
        };

        CosmosClient cosmos = new CosmosClient(cosmosConnection, cosmosOptions);
        _games = cosmos.GetContainer(databaseName, containerName);
    }

    // Embeds one Game and writes the vector back. Returns what happened, for the log line.
    // Safe to run twice for the same Game: Service Bus delivers at least once, so duplicates are
    // normal, and the second run finds the vector up to date and pays for nothing.
    public async Task<string> EmbedGameAsync(string gameId, CancellationToken cancellationToken)
    {
        // Read the Game as it is now, rather than trusting the request: the Blurb may have been
        // edited again since this was queued. A Game that is not there throws a 404, which the
        // function abandons like any other failure.
        ItemResponse<CosmosGameDocument> read =
            await _games.ReadItemAsync<CosmosGameDocument>(gameId, new PartitionKey(gameId), cancellationToken: cancellationToken);
        CosmosGameDocument game = read.Resource;

        if (string.IsNullOrWhiteSpace(game.Blurb))
        {
            return "no Blurb, nothing to embed";
        }

        string text = EmbeddingText.Build(game.Name, game.Categories, game.Mechanics, game.Blurb);
        string textHash = EmbeddingText.Hash(text);

        if (textHash == game.EmbeddingTextHash)
        {
            return "vector already up to date, nothing to do";
        }

        float[] vector = await EmbedTextAsync(text, cancellationToken);

        // Write only the two fields that change, and the hash WITH the vector: the change feed sees
        // this write too, and only the matching hash stops it queueing another embed (A-10).
        List<PatchOperation> operations = new List<PatchOperation>();
        operations.Add(PatchOperation.Set("/embedding", vector));
        operations.Add(PatchOperation.Set("/embeddingTextHash", textHash));

        // Only if the document is still the version we read. If someone edited the Game in between,
        // Cosmos refuses with 412, the message is abandoned, and the next delivery reads the new text.
        PatchItemRequestOptions patchOptions = new PatchItemRequestOptions
        {
            IfMatchEtag = read.ETag,
            EnableContentResponseOnWrite = false,
        };

        ItemResponse<CosmosGameDocument> written = await _games.PatchItemAsync<CosmosGameDocument>(
            gameId, new PartitionKey(gameId), operations, patchOptions, cancellationToken);

        double charge = read.RequestCharge + written.RequestCharge;
        double? similarity = CosineSimilarity(game.Embedding, vector);
        if (similarity == null)
        {
            return $"embedded (no old vector), {charge:0.00} RU";
        }
        return $"re-embedded, similarity to the old vector {similarity:0.0000}, {charge:0.00} RU";
    }

    // Embeds one text. On a 429 it waits and tries again, doubling the wait each time (or using
    // Retry-After if the service sends it). Throws if it gives up, so the message is abandoned.
    // Worst case is about 30 seconds of waiting, well inside the 5 minute function timeout.
    private async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken)
    {
        int maxAttempts = 5;
        int waitSeconds = 2;

        string body = JsonSerializer.Serialize(new { input = text });

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            HttpResponseMessage response = await _http.PostAsync(_embeddingsUrl, new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                // Response shape: { "data": [ { "embedding": [ ...floats... ] } ], "usage": { "total_tokens": n } }
                using JsonDocument json = JsonDocument.Parse(responseText);
                JsonElement embedding = json.RootElement.GetProperty("data")[0].GetProperty("embedding");

                float[] vector = new float[embedding.GetArrayLength()];
                int i = 0;
                foreach (JsonElement number in embedding.EnumerateArray())
                {
                    vector[i] = number.GetSingle();
                    i++;
                }
                return vector;
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException($"Embeddings call failed: {(int)response.StatusCode} {response.StatusCode}. {responseText}");
            }

            if (attempt == maxAttempts)
            {
                break;
            }

            int delay = waitSeconds;
            if (response.Headers.RetryAfter?.Delta != null)
            {
                delay = (int)Math.Ceiling(response.Headers.RetryAfter.Delta.Value.TotalSeconds);
            }

            _logger.LogWarning("429 Too Many Requests, waiting {Delay}s (attempt {Attempt}/{MaxAttempts})", delay, attempt, maxAttempts);
            await Task.Delay(delay * 1000, cancellationToken);
            waitSeconds *= 2;
        }

        throw new HttpRequestException($"Embeddings call still throttled (429) after {maxAttempts} attempts.");
    }

    // A missing setting fails the first invocation with its name, rather than a null reference later
    private static string RequireSetting(IConfiguration config, string name)
    {
        string? value = config[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing setting {name}. Locally, run pwsh MeepleLedger.Functions/write-local-settings.ps1; in Azure, run pwsh infra/functions/provision-functions.ps1 (code deploys do not carry app settings).");
        }
        return value;
    }

    // How alike the old and new vectors are: 1 is identical. Null if there was no old vector.
    private static double? CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return null;
        }

        double dot = 0;
        double lengthA = 0;
        double lengthB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            lengthA += a[i] * a[i];
            lengthB += b[i] * b[i];
        }
        return dot / (Math.Sqrt(lengthA) * Math.Sqrt(lengthB));
    }
}
