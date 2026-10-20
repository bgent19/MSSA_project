using MeepleLedger.Components;
using MeepleLedger.Storage;
using Microsoft.Azure.Cosmos;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IGameCatalogSource, SeededCatalogSource>();
builder.Services.AddSingleton<IMeepleStore, InMemoryMeepleStore>();

// Which Search Index Provider is live. InMemory is the default and needs nothing (ADR-0001);
// Cosmos needs the Cosmos:* and AzureOpenAI:* settings (user secrets in Development).
// Switch with: dotnet run --project MeepleLedger -- --SearchIndex:Provider=Cosmos
string searchProvider = builder.Configuration["SearchIndex:Provider"] ?? "InMemory";

if (searchProvider == "InMemory")
{
    builder.Services.AddSingleton<IGameSearchIndex, InMemoryGameSearchIndex>();
}
else if (searchProvider == "Cosmos")
{
    IConfiguration config = builder.Configuration;

    // CosmosClient is thread-safe and expensive to build (it holds connections and caches),
    // so there is exactly one for the whole app. Transient or scoped would exhaust sockets.
    builder.Services.AddSingleton(services =>
    {
        CosmosClientOptions options = new CosmosClientOptions
        {
            // camelCase matches the documents the seeder uploads (minPlayers, embedding...)
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
            },
        };

        return new CosmosClient(RequiredSetting(config, "Cosmos:Endpoint"), RequiredSetting(config, "Cosmos:Key"), options);
    });

    builder.Services.AddSingleton(services => new AskEmbedder(
        RequiredSetting(config, "AzureOpenAI:Endpoint"),
        RequiredSetting(config, "AzureOpenAI:Key"),
        RequiredSetting(config, "AzureOpenAI:EmbeddingDeployment")));

    builder.Services.AddSingleton<IGameSearchIndex, CosmosGameSearchIndex>();
}
else
{
    throw new InvalidOperationException("Unknown SearchIndex:Provider '" + searchProvider + "'. Use InMemory or Cosmos.");
}

var app = builder.Build();

// Get a startup fail if Data is not good
app.Services.GetRequiredService<IMeepleStore>();

// Get a startup fail if the Search Index Provider is missing a setting
app.Services.GetRequiredService<IGameSearchIndex>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Reads a setting the Cosmos Provider cannot run without, and says which one is missing.
static string RequiredSetting(IConfiguration config, string name)
{
    string? value = config[name];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException("Missing setting " + name + ". Set it with: dotnet user-secrets set " + name + " <value> --project MeepleLedger");
    }

    return value;
}
