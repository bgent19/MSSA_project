using MeepleLedger.Components;
using MeepleLedger.Storage;
using Microsoft.Azure.Cosmos;
using Npgsql;
using Pgvector.Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IGameCatalogSource, SeededCatalogSource>();
builder.Services.AddSingleton<IMeepleStore, InMemoryMeepleStore>();

// Which Search Index Provider is live. InMemory is the default and needs nothing (ADR-0001);
// Cosmos needs the Cosmos:* and AzureOpenAI:* settings, Postgres needs Postgres:ConnectionString
// and AzureOpenAI:* (user secrets in Development).
// Switch with: dotnet run --project MeepleLedger -- --SearchIndex:Provider=Cosmos (or Postgres)
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
else if (searchProvider == "Postgres")
{
    IConfiguration config = builder.Configuration;

    // NpgsqlDataSource owns the connection pool, so there is exactly one for the whole app.
    // A data source per request would build a new, empty pool each time and pooling would never happen.
    // UseVector maps pgvector's vector type, so the Ask's embedding is sent as a parameter.
    builder.Services.AddSingleton(services =>
    {
        NpgsqlDataSourceBuilder dataSourceBuilder = new NpgsqlDataSourceBuilder(RequiredSetting(config, "Postgres:ConnectionString"));

        // Pool and timeout settings come from appsettings.json and are added to the secret
        // connection string here (A-14). Each Postgres connection is a server process, and a B1ms
        // server allows 50 connections, 15 of them kept by Azure, so the pool must stay well under 35.
        dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = RequiredIntSetting(config, "Postgres:MinPoolSize");
        dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = RequiredIntSetting(config, "Postgres:MaxPoolSize");
        dataSourceBuilder.ConnectionStringBuilder.CommandTimeout = RequiredIntSetting(config, "Postgres:CommandTimeoutSeconds");

        dataSourceBuilder.UseVector();
        return dataSourceBuilder.Build();
    });

    builder.Services.AddSingleton(services => new AskEmbedder(
        RequiredSetting(config, "AzureOpenAI:Endpoint"),
        RequiredSetting(config, "AzureOpenAI:Key"),
        RequiredSetting(config, "AzureOpenAI:EmbeddingDeployment")));

    builder.Services.AddSingleton<IGameSearchIndex, PostgresGameSearchIndex>();
}
else
{
    throw new InvalidOperationException("Unknown SearchIndex:Provider '" + searchProvider + "'. Use InMemory, Cosmos or Postgres.");
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

// Reads a setting a cloud Provider cannot run without, and says which one is missing.
static string RequiredSetting(IConfiguration config, string name)
{
    string? value = config[name];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException("Missing setting " + name + ". Set it with: dotnet user-secrets set " + name + " <value> --project MeepleLedger");
    }

    return value;
}

// The same, for a whole-number setting such as a pool size.
static int RequiredIntSetting(IConfiguration config, string name)
{
    string value = RequiredSetting(config, name);
    if (!int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out int number))
    {
        throw new InvalidOperationException("Setting " + name + " must be a whole number, not '" + value + "'.");
    }

    return number;
}
