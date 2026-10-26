using System.Globalization;
using System.Text.Json;
using Azure.Messaging.EventGrid;
using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// Publishes a PlayLogged event to the Event Grid custom topic every time a Play is recorded (A-20).
// Switched on with EventGrid:Enabled; see Program.cs. It listens to PlayLog.Recorded, so the Log a
// Play page has no idea events exist (ADR-0001: no UI knows which Provider is live).
//
// Event Grid vs Service Bus: an embed request (A-19) is work I want done, and exactly one worker must
// do it. PlayLogged is "something happened": the app does not know or care who listens, and nobody
// listening is fine. That is why it is a fire-and-forget event and not a queue message.
//
// Fire and forget: the Play is already in the Play Log before this runs, and publishing never
// blocks or fails the page. If Event Grid is down, slow or misconfigured, the error is logged and
// the event is lost. Nothing here retries beyond the SDK's own two short retries.
//
// The event is thin on purpose: it says which Play, which Game, when, and whether you own the Game.
// It does not carry the Results, the location or the duration. A subscriber that wants detail reads
// it from the store; a fat event leaks the model and ages badly when the model changes.
public class PlayLoggedPublisher
{
    // The type string carries a version. A breaking change to the data gets a new type
    // (MeepleLedger.PlayLogged.v2), so a subscriber filtering on v1 never receives data it cannot read.
    // MeepleLedger.Events filters on this exact string and has its own copy; keep the two in step.
    public const string EventType = "MeepleLedger.PlayLogged.v1";
    public const string DataVersion = "1";

    private readonly EventGridPublisherClient _client;
    private readonly IMeepleStore _store;
    private readonly ILogger<PlayLoggedPublisher> _logger;

    // camelCase, so the data reads { "playId": ..., "game": ... } and the advanced filter on the
    // subscription can name the key as data.owned.
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    // The EventGridPublisherClient is a singleton (see Program.cs): it is thread-safe and holds the
    // HTTP connection, so one is shared by every Play.
    public PlayLoggedPublisher(EventGridPublisherClient client, IMeepleStore store, ILogger<PlayLoggedPublisher> logger)
    {
        _client = client;
        _store = store;
        _logger = logger;

        store.PlayLog.Recorded += OnPlayRecorded;
    }

    private void OnPlayRecorded(Play play)
    {
        // An event handler cannot be awaited, and the page must not wait for Event Grid anyway.
        // PublishAsync catches and logs its own errors, so nothing is left unobserved.
        _ = PublishAsync(play);
    }

    private async Task PublishAsync(Play play)
    {
        try
        {
            EventGridEvent playLogged = BuildEvent(play);
            await _client.SendEventAsync(playLogged);

            _logger.LogInformation("Published {EventType} for {Game} on {PlayedOn:d} (subject {Subject})",
                EventType, play.Game.Name, play.PlayedOn, playLogged.Subject);
        }
        catch (Exception ex)
        {
            // Every exception, not only RequestFailedException: a bad endpoint, a timeout or a DNS
            // failure must never surface on the page, because the Play is already saved.
            _logger.LogWarning("Could not publish {EventType} for {Game}; the Play is saved but no event was sent. {Message}",
                EventType, play.Game.Name, ex.Message);
        }
    }

    private EventGridEvent BuildEvent(Play play)
    {
        PlayLoggedData data = new PlayLoggedData();
        data.PlayId = play.Id.ToString();
        data.Game = play.Game.Name;
        data.PlayedOn = play.PlayedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        data.Owned = _store.Collection.Owns(play.Game.Name);

        // The subject reads like a path, most general part first, so a subscription can filter on a
        // prefix: "games/" for every Play, "games/Wingspan/" for Plays of one Game.
        string subject = "games/" + play.Game.Name + "/plays/" + data.PlayId;

        BinaryData body = BinaryData.FromObjectAsJson(data, JsonOptions);
        return new EventGridEvent(subject, EventType, DataVersion, body);
    }

    // The data of a PlayLogged event, version 1.
    private class PlayLoggedData
    {
        public string PlayId { get; set; } = "";
        public string Game { get; set; } = "";
        public string PlayedOn { get; set; } = "";
        public bool Owned { get; set; }
    }
}
