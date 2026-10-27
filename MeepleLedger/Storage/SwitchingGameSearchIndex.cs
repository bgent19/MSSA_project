using System.Diagnostics;
using MeepleLedger.Domain;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;

namespace MeepleLedger.Storage;

// The Search Index the pages are given (A-30). It reads SearchIndex:Provider on EVERY Ask and hands
// the Ask to that Provider, so changing the setting changes the Provider without a restart. With
// Azure App Configuration the setting is refreshed in the background (a sentinel key, see
// Program.cs); without it the setting comes from appsettings.json or the command line and never
// changes, and this behaves like a plain Provider.
//
// It is a decorator like CachedGameSearchIndex, so it implements IGameSearchIndex and no page knows
// it is there, or which Provider is live (ADR-0001, ADR-0002).
//
// Building a Provider is left to the buildProvider function from Program.cs (it knows the wiring:
// clients, the cache). Each Provider is built the first time it is asked for, then kept, so
// switching back and forth costs nothing after the first time.
//
// What a switch does to an Ask that is already running: nothing. The Ask took its Provider at the
// start and finishes on it; Providers are never disposed, so the old one is still fine to use. Only
// Asks that START after the switch go to the new Provider. Nothing is cancelled or retried.
//
// A switch to a Provider that cannot be built (a typo, a missing setting, a server that is gone)
// fails each Ask until the setting is fixed. It does NOT fall back to the last Provider that worked:
// that would hide the mistake, and the logs would say one Provider answered while another did.
public class SwitchingGameSearchIndex : IGameSearchIndex
{
    public const string ProviderSetting = "SearchIndex:Provider";
    public const string DefaultProvider = "InMemory";

    private readonly IConfiguration _config;
    private readonly Func<string, IGameSearchIndex> _buildProvider;
    private readonly IEnumerable<IConfigurationRefresher> _refreshers;
    private readonly ILogger<SwitchingGameSearchIndex> _logger;

    // The Providers built so far, by name. Several circuits can Ask at once, so it is locked.
    private readonly Dictionary<string, IGameSearchIndex> _providers = new Dictionary<string, IGameSearchIndex>();
    private readonly object _providersLock = new object();

    // The Provider the last Ask used, only to log when it changes.
    private string _lastProviderName = "";

    // refreshers is empty when App Configuration is off.
    public SwitchingGameSearchIndex(
        IConfiguration config,
        Func<string, IGameSearchIndex> buildProvider,
        IEnumerable<IConfigurationRefresher> refreshers,
        ILogger<SwitchingGameSearchIndex> logger)
    {
        _config = config;
        _buildProvider = buildProvider;
        _refreshers = refreshers;
        _logger = logger;
    }

    // Read on every render of the Tonight page. A Provider that cannot be built is reported by the
    // Ask itself, so here it only means "don't show the word-matching notice".
    public bool UnderstandsMeaning
    {
        get
        {
            try
            {
                return GetProvider(CurrentProviderName()).UnderstandsMeaning;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public async Task<IReadOnlyList<Recommendation>> RecommendAsync(Ask ask, Table table, int maxResults)
    {
        if (ask == null)
        {
            throw new ArgumentNullException(nameof(ask));
        }

        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }

        // A click on the Tonight page travels over the Blazor circuit's web socket, not as an HTTP
        // request, so the App Configuration middleware never sees it. Refresh here as well. This only
        // goes to App Configuration once the refresh interval has passed; otherwise it returns at once.
        await TryRefreshAsync();

        string providerName = CurrentProviderName();

        // The root of the Ask's trace in our code. Its parent is Blazor's own HandleEvent span (the
        // button click), when OpenTelemetry listens to Microsoft.AspNetCore.Components (Program.cs).
        using Activity? span = SearchTelemetry.Source.StartActivity("Ask");
        span?.SetTag(SearchTelemetry.ProviderTag, providerName);
        SearchTelemetry.TagAskAndTable(span, ask, table, maxResults);

        try
        {
            IGameSearchIndex provider = GetProvider(providerName);
            IReadOnlyList<Recommendation> recommendations = await provider.RecommendAsync(ask, table, maxResults);

            span?.SetTag(SearchTelemetry.ResultCountTag, recommendations.Count);
            return recommendations;
        }
        catch (Exception ex)
        {
            SearchTelemetry.RecordFailure(span, ex);
            throw;
        }
    }

    // Builds the Provider the setting names right now, so Program.cs can fail the launch on a
    // missing setting, the same as before A-30.
    public void BuildCurrentProvider()
    {
        GetProvider(CurrentProviderName());
    }

    public string CurrentProviderName()
    {
        string? name = _config[ProviderSetting];
        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultProvider;
        }

        return name.Trim();
    }

    private IGameSearchIndex GetProvider(string providerName)
    {
        lock (_providersLock)
        {
            if (providerName != _lastProviderName)
            {
                if (_lastProviderName == "")
                {
                    _logger.LogInformation("Search Index Provider: {Provider}", providerName);
                }
                else
                {
                    _logger.LogInformation(
                        "Search Index Provider switched from {OldProvider} to {NewProvider}. Asks already running finish on {OldProvider}.",
                        _lastProviderName, providerName, _lastProviderName);
                }

                _lastProviderName = providerName;
            }

            if (_providers.TryGetValue(providerName, out IGameSearchIndex? existing))
            {
                return existing;
            }

            // Throws for an unknown name or a missing setting. Nothing is stored then, so the next
            // Ask tries again: fixing the setting is enough, no restart needed.
            IGameSearchIndex provider = _buildProvider(providerName);
            _providers[providerName] = provider;
            return provider;
        }
    }

    private async Task TryRefreshAsync()
    {
        foreach (IConfigurationRefresher refresher in _refreshers)
        {
            // TryRefreshAsync never throws: a failed refresh (App Configuration down, or the Free
            // tier's daily quota used up, which is a 429) is logged by the provider and the
            // settings already loaded stay in use.
            await refresher.TryRefreshAsync();
        }
    }
}
