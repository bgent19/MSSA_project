using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MeepleLedger.Functions;

// The Service Bus trigger on "embed-requests". This replaces the Worker's RunWorker: no
// ServiceBusClient, no processor options, no event handlers, no start/stop, no Console.ReadLine.
// The host receives the messages, renews their locks and runs this once per message.
//
// The settings the Worker wrote in C# live in host.json now:
//   ServiceBusProcessorOptions.MaxConcurrentCalls = 4             -> serviceBus.maxConcurrentCalls
//   ServiceBusProcessorOptions.MaxAutoLockRenewalDuration = 5 min -> serviceBus.maxAutoLockRenewalDuration
//   ServiceBusProcessorOptions.AutoCompleteMessages = false       -> serviceBus.autoCompleteMessages (true here)
//   ServiceBusProcessorOptions.ReceiveMode = PeekLock             -> always PeekLock for a trigger
// The max delivery count (5) and the lock duration (1 min) were never in the Worker: they are on the
// queue (provision-servicebus.ps1), so they carry over unchanged.
//
// Each message still ends one of three ways, the same as A-19:
//   - Complete:    the function returns normally and the HOST completes the message
//                  (autoCompleteMessages is true). There is no CompleteMessageAsync call here.
//   - Abandon:     something failed that might work next time. Abandoned explicitly, with the error
//                  saved on the message, after a 5 second pause. After the 5th delivery Service Bus
//                  moves it to the dead-letter queue: MaxDeliveryCountExceeded.
//   - Dead-letter: the message itself is wrong. Dead-lettered explicitly, at once, with a reason.
// The host does not try to complete a message the function already settled through messageActions.
public class EmbedRequestFunction
{
    private readonly GameEmbedder _embedder;
    private readonly ILogger<EmbedRequestFunction> _logger;

    public EmbedRequestFunction(GameEmbedder embedder, ILogger<EmbedRequestFunction> logger)
    {
        _embedder = embedder;
        _logger = logger;
    }

    // "ServiceBusConnection" is the NAME of an app setting (in local.settings.json locally), not the
    // connection string itself. The host reads it, not this process.
    [Function("EmbedRequest")]
    public async Task Run(
        [ServiceBusTrigger(EmbedRequest.QueueName, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        string label = $"#{message.SequenceNumber} (delivery {message.DeliveryCount})";

        // One span per delivery, in the trace of whoever queued the message (A-31). The Cosmos read,
        // the embeddings call and the vector patch all hang under it.
        using Activity? span = FunctionTelemetry.StartConsumerSpan("Embed request", message);

        // 1. Read the body. A body that cannot be read now will not be readable on the 5th try
        //    either, so dead-letter it straight away with a reason. Throwing would only abandon it,
        //    and it would come back 4 more times first.
        EmbedRequest? request = null;
        string? badPayload = null;
        try
        {
            request = JsonSerializer.Deserialize<EmbedRequest>(message.Body.ToString(), EmbedRequest.JsonOptions);
        }
        catch (JsonException ex)
        {
            badPayload = "The body is not an EmbedRequest in JSON: " + ex.Message;
        }

        if (badPayload == null && request == null)
        {
            badPayload = "The body is empty or null.";
        }

        if (badPayload != null)
        {
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "BadPayload", deadLetterErrorDescription: badPayload, cancellationToken: cancellationToken);
            _logger.LogWarning("{Label} dead-lettered: BadPayload. {Description}", label, badPayload);
            FunctionTelemetry.SetOutcome(span, "dead-lettered", "BadPayload");
            return;
        }

        // 2. The schema version. This function reads version 1. A missing field reads as 0.
        if (request!.SchemaVersion != EmbedRequest.CurrentSchemaVersion)
        {
            string description = $"This function reads schema version {EmbedRequest.CurrentSchemaVersion}; the message is version {request.SchemaVersion} (0 means the field is missing).";
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "UnsupportedSchemaVersion", deadLetterErrorDescription: description, cancellationToken: cancellationToken);
            _logger.LogWarning("{Label} dead-lettered: UnsupportedSchemaVersion. {Description}", label, description);
            FunctionTelemetry.SetOutcome(span, "dead-lettered", "UnsupportedSchemaVersion");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.GameId))
        {
            string description = "The request has no gameId.";
            await messageActions.DeadLetterMessageAsync(message, deadLetterReason: "BadPayload", deadLetterErrorDescription: description, cancellationToken: cancellationToken);
            _logger.LogWarning("{Label} dead-lettered: BadPayload. {Description}", label, description);
            FunctionTelemetry.SetOutcome(span, "dead-lettered", "BadPayload");
            return;
        }

        span?.SetTag("meepleledger.game", request.GameId);

        // 3. Do the work. Anything that fails from here on might work next time, so it is abandoned
        //    and Service Bus dead-letters it after the 5th delivery.
        try
        {
            string outcome = await _embedder.EmbedGameAsync(request.GameId, cancellationToken);
            _logger.LogInformation("{Label} '{GameId}': {Outcome}. Completed by the host.", label, request.GameId, outcome);
            FunctionTelemetry.SetOutcome(span, "completed", null);
        }
        catch (Exception ex)
        {
            string error = ShortError(ex);
            _logger.LogWarning("{Label} '{GameId}': failed: {Error}", label, request.GameId, error);
            span?.AddException(ex);
            FunctionTelemetry.SetOutcome(span, "abandoned", error);

            // Abandon puts the message straight back, so without a pause a short outage would use up
            // all 5 deliveries in a second. The host keeps renewing the lock while we wait.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The host is stopping: abandon now rather than after the pause
            }

            // Abandoned by hand rather than by throwing, only so the last error is saved on the
            // message for the Worker's "dlq" command. A throw would also abandon it (the host does
            // that), but Service Bus's own reason would then be all the dead-letter queue shows.
            Dictionary<string, object> properties = new Dictionary<string, object>();
            properties["lastError"] = error;
            await messageActions.AbandonMessageAsync(message, properties, CancellationToken.None);
            _logger.LogWarning("{Label} abandoned, back on the queue", label);
        }
    }

    // One line for the log and for the lastError property. Cosmos exception messages run to many
    // lines of diagnostics, so those are cut down to the status code.
    private static string ShortError(Exception ex)
    {
        string text;
        if (ex is CosmosException cosmosException)
        {
            text = $"Cosmos {(int)cosmosException.StatusCode} {cosmosException.StatusCode}";
        }
        else
        {
            text = ex.GetType().Name + ": " + ex.Message;
        }

        if (text.Length > 300)
        {
            text = text.Substring(0, 300) + "...";
        }
        return text;
    }
}
