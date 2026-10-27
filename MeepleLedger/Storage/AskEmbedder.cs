using System.Text;
using System.Text.Json;

namespace MeepleLedger.Storage;

// Turns the text of an Ask into a vector with the same Azure OpenAI embeddings deployment the
// seeder used for the Blurbs (A-05), so the Ask and the Games are compared in the same space.
// Talks to the REST endpoint with HttpClient and System.Text.Json rather than another package.
//
// Registered as a singleton, so the one HttpClient it owns is reused for every Ask.
//
// The key is read from configuration on every Ask, not once at startup (A-29). With Key Vault the
// configuration reloads every few minutes, so after the key is rotated the next Ask sends the new
// one: no restart, no redeploy.
public class AskEmbedder
{
    private const string KeySetting = "AzureOpenAI:Key";

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly IConfiguration _config;
    private readonly ILogger<AskEmbedder> _logger;

    // The key the last Ask used, only to log when it changes. Never logged itself.
    private string _lastKey;

    public AskEmbedder(string endpoint, string deployment, IConfiguration config, ILogger<AskEmbedder> logger)
    {
        // The deployment name goes in the URL, so the app never refers to the model id directly.
        _url = endpoint.TrimEnd('/') + "/openai/deployments/" + deployment + "/embeddings?api-version=2024-10-21";

        _config = config;
        _logger = logger;
        _http = new HttpClient();

        // Read once now, so a missing key fails at startup like every other setting.
        _lastKey = ReadKey();
    }

    public async Task<float[]> EmbedAsync(string text)
    {
        string key = ReadKey();
        if (key != _lastKey)
        {
            _logger.LogInformation("The Azure OpenAI key changed since the last Ask (a Key Vault reload); using the new one.");
            _lastKey = key;
        }

        string body = JsonSerializer.Serialize(new { input = text });

        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, _url);
        request.Headers.Add("api-key", key);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _http.SendAsync(request);
        string responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "Embedding the Ask failed: " + (int)response.StatusCode + " " + response.StatusCode + ". " + responseText);
        }

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

    private string ReadKey()
    {
        string? key = _config[KeySetting];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Missing setting " + KeySetting + ". Set it with: dotnet user-secrets set " + KeySetting + " <value> --project MeepleLedger");
        }

        return key;
    }
}
