using System.Text;
using System.Text.Json;

namespace MeepleLedger.Storage;

// Turns the text of an Ask into a vector with the same Azure OpenAI embeddings deployment the
// seeder used for the Blurbs (A-05), so the Ask and the Games are compared in the same space.
// Talks to the REST endpoint with HttpClient and System.Text.Json rather than another package.
//
// Registered as a singleton, so the one HttpClient it owns is reused for every Ask.
public class AskEmbedder
{
    private readonly HttpClient _http;
    private readonly string _url;

    public AskEmbedder(string endpoint, string key, string deployment)
    {
        // The deployment name goes in the URL, so the app never refers to the model id directly.
        _url = endpoint.TrimEnd('/') + "/openai/deployments/" + deployment + "/embeddings?api-version=2024-10-21";

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("api-key", key);
    }

    public async Task<float[]> EmbedAsync(string text)
    {
        string body = JsonSerializer.Serialize(new { input = text });
        StringContent content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _http.PostAsync(_url, content);
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
}
