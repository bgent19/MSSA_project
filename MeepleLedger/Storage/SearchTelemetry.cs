using System.Diagnostics;
using System.Net;
using MeepleLedger.Domain;
using StackExchange.Redis;

namespace MeepleLedger.Storage;

// The custom spans for answering an Ask (A-31). One ActivitySource for all of them; Program.cs
// subscribes OpenTelemetry to it by SourceName. With tracing off nothing listens, StartActivity
// returns null, and every "span?.SetTag(...)" below does nothing, so the code runs the same.
//
// The spans of one Ask, all in one trace:
//   Ask                                   SwitchingGameSearchIndex: Provider, Table, result count
//     Recommendation cache lookup         CachedGameSearchIndex (only with the cache on): hit or miss
//     Embed the Ask                       AskEmbedder, with the HTTP call to Azure OpenAI under it
//     Query the Search Index              each Provider, with the Cosmos, Npgsql or Redis call under it
//     Rank                                each Provider: tie-breaks, Reasons, ranks
//
// The Ask's TEXT is never a span attribute, only its length. Telemetry is a separate data store with
// its own retention and its own readers, and an Ask is free text a person typed.
public static class SearchTelemetry
{
    public const string SourceName = "MeepleLedger.Search";

    public static readonly ActivitySource Source = new ActivitySource(SourceName);

    // The attributes every span here may carry. "meepleledger." keeps them apart from the
    // OpenTelemetry standard names (http.*, db.*...).
    public const string ProviderTag = "meepleledger.provider";
    public const string AskLengthTag = "meepleledger.ask.length";
    public const string PlayerCountTag = "meepleledger.table.player_count";
    public const string MinutesAvailableTag = "meepleledger.table.minutes_available";
    public const string OwnedOnlyTag = "meepleledger.table.owned_only";
    public const string MaxResultsTag = "meepleledger.max_results";
    public const string ResultCountTag = "meepleledger.result_count";
    public const string CacheHitTag = "meepleledger.cache.hit";

    // Puts the Table's constraints (and the length of the Ask, not its text) on a span.
    public static void TagAskAndTable(Activity? span, Ask ask, Table table, int maxResults)
    {
        if (span == null)
        {
            return;
        }

        span.SetTag(AskLengthTag, ask.Text.Length);
        span.SetTag(PlayerCountTag, table.PlayerCount);
        span.SetTag(MinutesAvailableTag, table.MinutesAvailable);
        span.SetTag(OwnedOnlyTag, table.OwnedOnly);
        span.SetTag(MaxResultsTag, maxResults);
    }

    // StackExchange.Redis makes no spans of its own, so a Redis call gets a span from us. Kind Client
    // plus db.system.name and server.address is what Application Insights reads to draw Redis as its
    // own box on the application map. Only the host name is used: the multiplexer's Configuration
    // string has the access key in it.
    public static Activity? StartRedisSpan(string name, IConnectionMultiplexer redis)
    {
        Activity? span = Source.StartActivity(name, ActivityKind.Client);
        if (span == null)
        {
            return null;
        }

        span.SetTag("db.system.name", "redis");

        EndPoint[] endPoints = redis.GetEndPoints();
        if (endPoints.Length > 0 && endPoints[0] is DnsEndPoint dnsEndPoint)
        {
            span.SetTag("server.address", dnsEndPoint.Host);
            span.SetTag("server.port", dnsEndPoint.Port);
        }

        return span;
    }

    // Marks a span as failed, with the exception's type and message. The exporter shows it in red.
    public static void RecordFailure(Activity? span, Exception ex)
    {
        if (span == null)
        {
            return;
        }

        span.SetStatus(ActivityStatusCode.Error, ex.Message);
        span.AddException(ex);
    }
}
