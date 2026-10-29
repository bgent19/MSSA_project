using Azure;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Messaging.EventGrid;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using MeepleLedger;
using MeepleLedger.Components;
using MeepleLedger.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.FeatureManagement;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Pgvector.Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Settings, Key Vault references and feature flags from Azure App Configuration (A-30). Off unless
// AppConfig:Endpoint is set, so the app still runs with nothing but the code (ADR-0001). In Azure
// AppConfig__Endpoint is an app setting and the app reads the store as its managed identity
// (App Configuration Data Reader); on this machine DefaultAzureCredential falls through to your
// Azure CLI login. Try it locally with:
//   dotnet run --project MeepleLedger -- --AppConfig:Endpoint=https://meepleledger-appconfig.azconfig.io
string? appConfigEndpoint = builder.Configuration["AppConfig:Endpoint"];
bool appConfigEnabled = !string.IsNullOrWhiteSpace(appConfigEndpoint);
string appConfigLabel = "";
int appConfigRefreshSeconds = 0;

if (appConfigEnabled)
{
    // "local" in appsettings.json; the App Service app setting AppConfig__Label makes it "azure".
    // The same key can hold a different value per label, so this machine and the deployed app can
    // run different Providers from one store.
    appConfigLabel = RequiredSetting(builder.Configuration, "AppConfig:Label");

    // How old the settings may get before the next request checks the sentinel. Each check is one
    // request against the store's quota (the Free tier allows 1,000 a day), so not too short.
    appConfigRefreshSeconds = RequiredIntSetting(builder.Configuration, "AppConfig:RefreshIntervalSeconds");
    TimeSpan refreshInterval = TimeSpan.FromSeconds(appConfigRefreshSeconds);

    // Key Vault references are only links; the app reads the secret itself. So it is the APP's
    // identity that needs Key Vault Secrets User on the vault, not the store's.
    int secretRefreshMinutes = RequiredIntSetting(builder.Configuration, "KeyVault:ReloadIntervalMinutes");
    DefaultAzureCredential credential = new DefaultAzureCredential();

    builder.Configuration.AddAzureAppConfiguration(options =>
    {
        options.Connect(new Uri(appConfigEndpoint!), credential);

        // First every key with no label (shared by all), then every key with this app's label.
        // The later Select wins, so a labelled value overrides the shared one.
        options.Select(KeyFilter.Any, LabelFilter.Null);
        options.Select(KeyFilter.Any, appConfigLabel);

        // A rotated secret is picked up on the next refresh after this interval (A-29's rotation).
        options.ConfigureKeyVault(keyVault =>
        {
            keyVault.SetCredential(credential);
            keyVault.SetSecretRefreshInterval(TimeSpan.FromMinutes(secretRefreshMinutes));
        });

        // The sentinel: the ONE key watched for changes. Change any number of settings, then change
        // the sentinel, and every setting is reloaded together (refreshAll). Without a registered
        // key nothing is ever reloaded and the store is read once, at startup.
        options.ConfigureRefresh(refresh =>
        {
            refresh.Register(AppConfigKeys.Sentinel, refreshAll: true);
            refresh.SetRefreshInterval(refreshInterval);
        });

        // Feature flags live under the reserved prefix .appconfig.featureflag/ and are read here, not
        // with Select. Each label has its own copy of a flag, like the Provider setting.
        options.UseFeatureFlags(flags =>
        {
            flags.Select(KeyFilter.Any, appConfigLabel);
            flags.SetRefreshInterval(refreshInterval);
        });
    });

    // The refresher, for the middleware below and for SwitchingGameSearchIndex.
    builder.Services.AddAzureAppConfiguration();
}

// Secrets from Azure Key Vault (A-29). Off unless KeyVault:Uri is set, so the app still runs with
// nothing but the code (ADR-0001). In Azure KeyVault__Uri is an app setting, and the app reads the
// vault as its managed identity; on this machine DefaultAzureCredential falls through to your Azure
// CLI login. Added last, so a secret in the vault wins over user secrets and environment variables.
// Secret names use "--" for the colon: the secret AzureOpenAI--Key is the setting AzureOpenAI:Key.
// Since A-30 the deployed app reaches the vault through App Configuration's Key Vault references
// instead; this is still the way to read the vault directly.
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

// Distributed tracing with OpenTelemetry, exported to Application Insights (A-31). Off unless
// AzureMonitor:ConnectionString is set (ADR-0001). The distro traces incoming requests, HttpClient
// calls (the embeddings call) and the Azure SDKs (Cosmos, Event Grid); the sources added below are
// our own spans (SearchTelemetry), Npgsql's, and Blazor's.
string? monitorConnectionString = builder.Configuration["AzureMonitor:ConnectionString"];
bool tracingEnabled = !string.IsNullOrWhiteSpace(monitorConnectionString);

if (tracingEnabled)
{
    // 1.0 keeps every trace: right while looking for one particular Ask. The distro's own default is
    // NOT "everything": it is a rate limit of 5 traces a second, so TracesPerSecond is cleared to make
    // the ratio the rule. Lower the ratio (0.25 keeps one trace in four) to pay for less telemetry;
    // the decision is made once, at the root, and travels with the trace (worker and Function obey it).
    double samplingRatio = RequiredRatioSetting(builder.Configuration, "AzureMonitor:SamplingRatio");

    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor(options =>
        {
            options.ConnectionString = monitorConnectionString;
            options.SamplingRatio = (float)samplingRatio;
            options.TracesPerSecond = null;
        })
        // The service name is the "cloud role name": the box this app gets on the application map.
        .ConfigureResource(resource => resource.AddService("meepleledger-web"))
        .WithTracing(tracing =>
        {
            tracing.AddSource(SearchTelemetry.SourceName);
            tracing.AddSource("Npgsql");

            // A click on a Blazor Server page is a message on the circuit's web socket, not an HTTP
            // request, so there is no request span for it. .NET 10's Blazor makes its own spans
            // (HandleEvent for the click, Navigate, StartCircuit); these two sources turn them on, and
            // the Ask hangs under the click instead of starting a trace of its own.
            tracing.AddSource("Microsoft.AspNetCore.Components");
            tracing.AddSource("Microsoft.AspNetCore.Components.Server.Circuits");
        });

    // The distro sends ILogger logs to Application Insights too. The Providers' Information lines
    // quote the Ask's text, which belongs in the console, not in a store with its own retention and
    // readers; only their warnings and errors go.
    builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("MeepleLedger.Storage", LogLevel.Warning);
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Feature flags (A-30): the Tonight screen is behind one. Without App Configuration the flags come
// from the FeatureManagement section of appsettings.json, where Tonight is on.
builder.Services.AddFeatureManagement();

builder.Services.AddSingleton<IGameCatalogSource, SeededCatalogSource>();

// Where the Collection and the Play Log live (S-04). InMemory is the default and needs nothing
// (ADR-0001): the Seed, gone at every restart. Cosmos keeps them in the Cosmos:OwnedGamesContainer
// and Cosmos:PlaysContainer containers and needs the Cosmos:* settings. Read once at startup: unlike
// the Search Index Provider, switching the store needs a restart.
// Pick it with: dotnet run --project MeepleLedger -- --MeepleStore:Provider=Cosmos
string meepleStoreProvider = builder.Configuration["MeepleStore:Provider"] ?? "InMemory";
if (meepleStoreProvider == "InMemory")
{
    builder.Services.AddSingleton<IMeepleStore, InMemoryMeepleStore>();
}
else if (meepleStoreProvider == "Cosmos")
{
    // Takes the app's one CosmosClient (registered below), so the Cosmos key is read once.
    builder.Services.AddSingleton<IMeepleStore, CosmosMeepleStore>();
}
else
{
    throw new InvalidOperationException("Unknown MeepleStore:Provider '" + meepleStoreProvider + "'. Use InMemory or Cosmos.");
}

// The Search Index Providers. Every Provider and every client is registered, but a singleton
// factory only runs the first time something asks for it, so a Provider that is never used never
// connects to anything and needs none of its settings. Which one answers is SearchIndex:Provider,
// read on every Ask by SwitchingGameSearchIndex (A-30), so it can change while the app runs.
// InMemory is the default and needs nothing (ADR-0001); Cosmos needs the Cosmos:* and AzureOpenAI:*
// settings, Postgres needs Postgres:ConnectionString and AzureOpenAI:*, Redis needs
// Redis:ConnectionString and AzureOpenAI:* (user secrets in Development).
// Pick one at startup with: dotnet run --project MeepleLedger -- --SearchIndex:Provider=Cosmos (or Postgres, Redis)
IConfiguration appConfig = builder.Configuration;

// The Redis Provider (A-17) and the Recommendation cache (A-16) share one connection to the same
// instance. ConnectionMultiplexer is thread-safe, holds the connections and is slow to build, so
// there is exactly one for the whole app. One per request is the classic Redis mistake in .NET.
builder.Services.AddSingleton<IConnectionMultiplexer>(services =>
{
    ConfigurationOptions options = ConfigurationOptions.Parse(RequiredSetting(appConfig, "Redis:ConnectionString"));

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

        // The SDK's spans (A-31) are off unless asked for, and then its query shows in the trace.
        CosmosClientTelemetryOptions = new CosmosClientTelemetryOptions
        {
            DisableDistributedTracing = false,
        },
    };

    return new CosmosClient(RequiredSetting(appConfig, "Cosmos:Endpoint"), RequiredSetting(appConfig, "Cosmos:Key"), options);
});

// NpgsqlDataSource owns the connection pool, so there is exactly one for the whole app.
// A data source per request would build a new, empty pool each time and pooling would never happen.
// UseVector maps pgvector's vector type, so the Ask's embedding is sent as a parameter.
builder.Services.AddSingleton(services =>
{
    NpgsqlDataSourceBuilder dataSourceBuilder = new NpgsqlDataSourceBuilder(RequiredSetting(appConfig, "Postgres:ConnectionString"));

    // Pool and timeout settings come from appsettings.json and are added to the secret
    // connection string here (A-14). Each Postgres connection is a server process, and a B1ms
    // server allows 50 connections, 15 of them kept by Azure, so the pool must stay well under 35.
    dataSourceBuilder.ConnectionStringBuilder.MinPoolSize = RequiredIntSetting(appConfig, "Postgres:MinPoolSize");
    dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = RequiredIntSetting(appConfig, "Postgres:MaxPoolSize");
    dataSourceBuilder.ConnectionStringBuilder.CommandTimeout = RequiredIntSetting(appConfig, "Postgres:CommandTimeoutSeconds");

    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});

// One embedder for the three cloud Providers: they all embed the Ask the same way.
builder.Services.AddSingleton(services => new AskEmbedder(
    RequiredSetting(appConfig, "AzureOpenAI:Endpoint"),
    RequiredSetting(appConfig, "AzureOpenAI:EmbeddingDeployment"),
    appConfig,
    services.GetRequiredService<ILogger<AskEmbedder>>()));

builder.Services.AddSingleton<InMemoryGameSearchIndex>();
builder.Services.AddSingleton<CosmosGameSearchIndex>();
builder.Services.AddSingleton<PostgresGameSearchIndex>();
builder.Services.AddSingleton<RedisGameSearchIndex>();

// The Recommendation cache in Azure Managed Redis (A-16). Off by default, so the app still runs
// with nothing but the code (ADR-0001). Needs Redis:ConnectionString when on. Read once at startup:
// unlike the Provider, turning the cache on or off needs a restart.
// Switch on with: dotnet run --project MeepleLedger -- --RecommendationCache:Enabled=true
string? cacheSetting = builder.Configuration["RecommendationCache:Enabled"];
bool cacheEnabled = cacheSetting != null && bool.Parse(cacheSetting);

// What the pages get as IGameSearchIndex: the switch, which builds each Provider (wrapped in the
// cache when it is on) the first time the setting names it.
builder.Services.AddSingleton(services =>
{
    List<IConfigurationRefresher> refreshers = new List<IConfigurationRefresher>();
    IConfigurationRefresherProvider? refresherProvider = services.GetService<IConfigurationRefresherProvider>();
    if (refresherProvider != null)
    {
        refreshers.AddRange(refresherProvider.Refreshers);
    }

    return new SwitchingGameSearchIndex(
        appConfig,
        providerName => BuildProvider(services, providerName, cacheEnabled, appConfig),
        refreshers,
        services.GetRequiredService<ILogger<SwitchingGameSearchIndex>>());
});
builder.Services.AddSingleton<IGameSearchIndex>(services => services.GetRequiredService<SwitchingGameSearchIndex>());

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

// Say where each setting that matters is read from (the provider, never the value), so the log shows
// App Configuration and Key Vault are really in use.
if (appConfigEnabled)
{
    app.Logger.LogInformation(
        "Settings come from App Configuration {Endpoint}, label {Label}, sentinel checked at most every {Seconds} seconds.",
        appConfigEndpoint, appConfigLabel, appConfigRefreshSeconds);
}

if (keyVaultEnabled)
{
    app.Logger.LogInformation("Secrets come from Key Vault {Uri}, reloaded every {Minutes} minutes.", keyVaultUri, keyVaultReloadMinutes);
}

if (appConfigEnabled || keyVaultEnabled)
{
    string[] settingsToTrace = { SwitchingGameSearchIndex.ProviderSetting, "MeepleStore:Provider", "AzureOpenAI:Key", "Cosmos:Key", "Postgres:ConnectionString", "Redis:ConnectionString", "EventGrid:TopicKey" };
    foreach (string name in settingsToTrace)
    {
        app.Logger.LogInformation("  {Setting} is read from {Source}", name, SettingSource(builder.Configuration, name));
    }
}

if (tracingEnabled)
{
    app.Logger.LogInformation("Traces go to Application Insights (service meepleledger-web).");
}

// Get a startup fail if Data is not good: a bad seed row, or (on the Cosmos store) a stored row that
// breaks an invariant or names a Game the Catalog doesn't have.
app.Services.GetRequiredService<IMeepleStore>();
app.Logger.LogInformation("The Collection and the Play Log are kept by the {Provider} store.", meepleStoreProvider);

// Get a startup fail if the Provider the setting names right now is missing a setting. A Provider
// switched to later fails its Asks instead (see SwitchingGameSearchIndex).
app.Services.GetRequiredService<SwitchingGameSearchIndex>().BuildCurrentProvider();

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

// A-30: on a request, once the refresh interval has passed, checks the sentinel (and the feature
// flags) in the background; the request itself does not wait. Page loads and enhanced navigation
// are requests, so the nav menu picks up a flipped flag on the next page after the refresh.
if (appConfigEnabled)
{
    app.UseAzureAppConfiguration();
}

app.UseAntiforgery();

app.MapStaticAssets();

// The export (S-06): the Collection and the Play Log as one JSON file download. A plain GET, so the
// Collection page's link needs no JavaScript. Works on every store; on the in-memory one it exports the
// Seed. The Seeder's import-ledger reads the file back into an empty store.
app.MapGet("/export", (IMeepleStore store) =>
{
    DateTime today = DateTime.Today;
    LedgerExportFile file = LedgerExport.Build(store.Collection, store.PlayLog, MeepleLedger.Data.CatalogSeed.Games, today);
    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(LedgerExport.ToJson(file));
    return Results.File(bytes, "application/json", LedgerExport.FileName(today));
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Builds the Provider with this name, wrapped in the Recommendation cache when the cache is on.
// Called by SwitchingGameSearchIndex the first time a Provider is asked for.
static IGameSearchIndex BuildProvider(IServiceProvider services, string providerName, bool cacheEnabled, IConfiguration config)
{
    IGameSearchIndex provider;
    if (providerName == "InMemory")
    {
        provider = services.GetRequiredService<InMemoryGameSearchIndex>();
    }
    else if (providerName == "Cosmos")
    {
        provider = services.GetRequiredService<CosmosGameSearchIndex>();
    }
    else if (providerName == "Postgres")
    {
        provider = services.GetRequiredService<PostgresGameSearchIndex>();
    }
    else if (providerName == "Redis")
    {
        provider = services.GetRequiredService<RedisGameSearchIndex>();
    }
    else
    {
        throw new InvalidOperationException("Unknown " + SwitchingGameSearchIndex.ProviderSetting + " '" + providerName + "'. Use InMemory, Cosmos, Postgres or Redis.");
    }

    if (!cacheEnabled)
    {
        return provider;
    }

    // One cache in front of each Provider. Each keys its entries by its own Provider name, so a
    // switch never answers from another Provider's entries.
    TimeSpan timeToLive = TimeSpan.FromMinutes(RequiredIntSetting(config, "RecommendationCache:TtlMinutes"));

    return new CachedGameSearchIndex(
        provider,
        providerName,
        services.GetRequiredService<IConnectionMultiplexer>(),
        services.GetRequiredService<IGameCatalogSource>(),
        services.GetRequiredService<IMeepleStore>(),
        timeToLive,
        services.GetRequiredService<ILogger<CachedGameSearchIndex>>());
}

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

// The same, for a number between 0 and 1 such as a sampling ratio.
static double RequiredRatioSetting(IConfiguration config, string name)
{
    string value = RequiredSetting(config, name);
    if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double number) || number < 0 || number > 1)
    {
        throw new InvalidOperationException("Setting " + name + " must be a number from 0 to 1, not '" + value + "'.");
    }

    return number;
}
