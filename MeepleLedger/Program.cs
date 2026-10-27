using Azure;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Messaging.EventGrid;
using MeepleLedger.Components;
using MeepleLedger.Storage;
using Microsoft.Azure.Cosmos;
using Npgsql;
using Pgvector.Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Secrets from Azure Key Vault (A-29). Off unless KeyVault:Uri is set, so the app still runs with
// nothing but the code (ADR-0001). In Azure KeyVault__Uri is an app setting, and the app reads the
// vault as its managed identity; on this machine DefaultAzureCredential falls through to your Azure
// CLI login. Added last, so a secret in the vault wins over user secrets and environment variables.
// Secret names use "--" for the colon: the secret AzureOpenAI--Key is the setting AzureOpenAI:Key.
// Try it locally with: dotnet run --project MeepleLedger -- --KeyVault:Uri=https://meepleledger-kv.vault.azure.net/
string? keyVaultUri = builder.Configuration["KeyVault:Uri"];
bool keyVaultEnabled = !string.IsNullOrWhiteSpace(keyVaultUri);
int keyVaultReloadMinutes = 0;

if (keyVaultEnabled)
{
    // The provider reads every secret once, then keeps them. Without a reload interval it never reads
    // them again, and a rotated key would only reach the app when it restarted. Each reload is one
    // list call to the vault, plus a read of any secret whose version changed.
    keyVaultReloadMinutes = RequiredIntSetting(builder.Configuration, "KeyVault:ReloadIntervalMinutes");
    AzureKeyVaultConfigurationOptions keyVaultOptions = new AzureKeyVaultConfigurationOptions
    {
        ReloadInterval = TimeSpan.FromMinutes(keyVaultReloadMinutes),
    };

    // Reads the vault now. No role on the vault (or a role assignment that has not reached it yet,
    // which takes minutes) fails the launch with a 403 here.
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri!), new DefaultAzureCredential(), keyVaultOptions);
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IGameCatalogSource, SeededCatalogSource>();
builder.Services.AddSingleton<IMeepleStore, InMemoryMeepleStore>();

// Which Search Index Provider is live. InMemory is the default and needs nothing (ADR-0001);
// Cosmos needs the Cosmos:* and AzureOpenAI:* settings, Postgres needs Postgres:ConnectionString
// and AzureOpenAI:*, Redis needs Redis:ConnectionString and AzureOpenAI:* (user secrets in Development).
// Switch with: dotnet run --project MeepleLedger -- --SearchIndex:Provider=Cosmos (or Postgres, Redis)
string searchProvider = builder.Configuration["SearchIndex:Provider"] ?? "InMemory";

// The Recommendation cache in Azure Managed Redis (A-16). Off by default, so the app still runs
// with nothing but the code (ADR-0001). Needs Redis:ConnectionString when on.
// Switch on with: dotnet run --project MeepleLedger -- --RecommendationCache:Enabled=true
string? cacheSetting = builder.Configuration["RecommendationCache:Enabled"];
bool cacheEnabled = cacheSetting != null && bool.Parse(cacheSetting);

// The Redis Provider (A-17) and the cache share one connection to the same instance.
if (searchProvider == "Redis" || cacheEnabled)
{
    IConfiguration config = builder.Configuration;

    // ConnectionMultiplexer is thread-safe, holds the connections and is slow to build, so there is
    // exactly one for the whole app. One per request is the classic Redis mistake in .NET.
    builder.Services.AddSingleton<IConnectionMultiplexer>(services =>
    {
        ConfigurationOptions options = ConfigurationOptions.Parse(RequiredSetting(config, "Redis:ConnectionString"));

        // Fail open: the app starts even when Redis is down, and keeps trying to connect in the
        // background. With FailFast a command fails at once while there is no connection, instead
        // of waiting in a queue, so an Ask goes to the Provider without a delay. The short timeouts
        // stop a slow Redis from making the cache slower than no cache. The Redis Provider has
        // nothing to fall back to, so for it an outage shows as an error on the Tonight screen.
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ConnectTimeout = 3000;
        options.SyncTimeout = 1000;
        options.AsyncTimeout = 1000;

        // StackExchange.Redis 3 talks RESP3 when the server offers it, and then FT.SEARCH answers
        // with a map instead of the flat array RedisGameSearchIndex reads. RESP2 keeps that shape.
        options.Protocol = RedisProtocol.Resp2;

        return ConnectionMultiplexer.Connect(options);
    });
}

// The class of the live Provider. It is registered as itself, and IGameSearchIndex is registered
// at the end, either as this Provider or as the Recommendation cache wrapped around it (A-16).
Type providerType;

if (searchProvider == "InMemory")
{
    builder.Services.AddSingleton<InMemoryGameSearchIndex>();
    providerType = typeof(InMemoryGameSearchIndex);
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
        RequiredSetting(config, "AzureOpenAI:EmbeddingDeployment"),
        config,
        services.GetRequiredService<ILogger<AskEmbedder>>()));

    builder.Services.AddSingleton<CosmosGameSearchIndex>();
    providerType = typeof(CosmosGameSearchIndex);
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
        RequiredSetting(config, "AzureOpenAI:EmbeddingDeployment"),
        config,
        services.GetRequiredService<ILogger<AskEmbedder>>()));

    builder.Services.AddSingleton<PostgresGameSearchIndex>();
    providerType = typeof(PostgresGameSearchIndex);
}
else if (searchProvider == "Redis")
{
    IConfiguration config = builder.Configuration;

    // The IConnectionMultiplexer is registered above, shared with the Recommendation cache.
    builder.Services.AddSingleton(services => new AskEmbedder(
        RequiredSetting(config, "AzureOpenAI:Endpoint"),
        RequiredSetting(config, "AzureOpenAI:EmbeddingDeployment"),
        config,
        services.GetRequiredService<ILogger<AskEmbedder>>()));

    builder.Services.AddSingleton<RedisGameSearchIndex>();
    providerType = typeof(RedisGameSearchIndex);
}
else
{
    throw new InvalidOperationException("Unknown SearchIndex:Provider '" + searchProvider + "'. Use InMemory, Cosmos, Postgres or Redis.");
}

if (cacheEnabled)
{
    IConfiguration config = builder.Configuration;

    TimeSpan timeToLive = TimeSpan.FromMinutes(RequiredIntSetting(config, "RecommendationCache:TtlMinutes"));

    builder.Services.AddSingleton<IGameSearchIndex>(services => new CachedGameSearchIndex(
        (IGameSearchIndex)services.GetRequiredService(providerType),
        searchProvider,
        services.GetRequiredService<IConnectionMultiplexer>(),
        services.GetRequiredService<IGameCatalogSource>(),
        services.GetRequiredService<IMeepleStore>(),
        timeToLive,
        services.GetRequiredService<ILogger<CachedGameSearchIndex>>()));
}
else
{
    builder.Services.AddSingleton<IGameSearchIndex>(services => (IGameSearchIndex)services.GetRequiredService(providerType));
}

// PlayLogged events to an Event Grid custom topic (A-20). Off by default, so the app still runs with
// nothing but the code (ADR-0001). Needs EventGrid:TopicEndpoint and EventGrid:TopicKey when on
// (pwsh infra/eventgrid/provision-eventgrid.ps1 sets both).
// Switch on with: dotnet run --project MeepleLedger -- --EventGrid:Enabled=true
string? eventGridSetting = builder.Configuration["EventGrid:Enabled"];
bool eventGridEnabled = eventGridSetting != null && bool.Parse(eventGridSetting);

if (eventGridEnabled)
{
    IConfiguration config = builder.Configuration;

    // One client for the whole app; it is thread-safe and keeps its HTTP connection.
    builder.Services.AddSingleton(services =>
    {
        // Publishing is fire and forget, but a hung call still holds a task open. Two quick retries
        // and a 5 second timeout per try keep a dead topic from piling them up.
        EventGridPublisherClientOptions options = new EventGridPublisherClientOptions();
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);

        return new EventGridPublisherClient(
            new Uri(RequiredSetting(config, "EventGrid:TopicEndpoint")),
            new AzureKeyCredential(RequiredSetting(config, "EventGrid:TopicKey")),
            options);
    });

    builder.Services.AddSingleton<PlayLoggedPublisher>();
}

var app = builder.Build();

// Say where each secret is read from (the provider, never the value), so the log shows Key Vault is
// really in use.
if (keyVaultEnabled)
{
    app.Logger.LogInformation("Secrets come from Key Vault {Uri}, reloaded every {Minutes} minutes.", keyVaultUri, keyVaultReloadMinutes);

    string[] secretSettings = { "AzureOpenAI:Key", "Cosmos:Key", "Postgres:ConnectionString", "Redis:ConnectionString", "EventGrid:TopicKey" };
    foreach (string name in secretSettings)
    {
        app.Logger.LogInformation("  {Setting} is read from {Source}", name, SettingSource(builder.Configuration, name));
    }
}

// Get a startup fail if Data is not good
app.Services.GetRequiredService<IMeepleStore>();

// Get a startup fail if the Search Index Provider is missing a setting
app.Services.GetRequiredService<IGameSearchIndex>();

// Nothing asks for the publisher; building it is what hooks it to PlayLog.Recorded. This also gets a
// startup fail if an EventGrid setting is missing.
if (eventGridEnabled)
{
    app.Services.GetRequiredService<PlayLoggedPublisher>();
}

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

// Which configuration provider a setting is read from. When several have it the last one added wins,
// so look from the end. Returns the provider's name only, never the value.
static string SettingSource(IConfigurationRoot config, string name)
{
    List<IConfigurationProvider> providers = config.Providers.ToList();
    for (int i = providers.Count - 1; i >= 0; i--)
    {
        if (providers[i].TryGet(name, out string? value))
        {
            return providers[i].ToString() ?? providers[i].GetType().Name;
        }
    }

    return "nowhere (not set)";
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
