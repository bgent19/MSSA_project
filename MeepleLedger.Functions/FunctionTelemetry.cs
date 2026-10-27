using System.Diagnostics;
using Azure.Messaging.ServiceBus;

namespace MeepleLedger.Functions;

// The functions' own spans (A-31). Program.cs subscribes OpenTelemetry to SourceName when
// APPLICATIONINSIGHTS_CONNECTION_STRING is set. With tracing off nothing listens, StartActivity
// returns null, and every "span?." call does nothing.
//
// The Worker has a copy of StartConsumerSpan (WorkerTelemetry); keep the two in step.
public static class FunctionTelemetry
{
    public const string SourceName = "MeepleLedger.Functions";

    public static readonly ActivitySource Source = new ActivitySource(SourceName);

    // A span for handling one message, whose PARENT is the span that sent it: the message's
    // traceparent property (the Seeder's watch-cosmos and the Worker's send put it there), or
    // Diagnostic-Id, the name the Service Bus SDK uses for the same value.
    //
    // Why not just trust the host: the host's trigger span is not promised to be a child of the
    // sender's span (newer messaging conventions LINK the two instead), and a link is not the one
    // trace id the change feed -> Service Bus -> Function -> vector write path needs. With the parent
    // set here it always is. Without either property the span joins the host's trace instead.
    public static Activity? StartConsumerSpan(string name, ServiceBusReceivedMessage message)
    {
        ActivityContext parent = default;
        string[] propertyNames = { "traceparent", "Diagnostic-Id" };
        foreach (string propertyName in propertyNames)
        {
            if (message.ApplicationProperties.TryGetValue(propertyName, out object? value)
                && value is string text
                && ActivityContext.TryParse(text, null, out ActivityContext parsed))
            {
                parent = parsed;
                break;
            }
        }

        Activity? span = Source.StartActivity(name, ActivityKind.Consumer, parent);
        span?.SetTag("messaging.system", "servicebus");
        span?.SetTag("messaging.destination.name", EmbedRequest.QueueName);
        span?.SetTag("messaging.message.id", message.MessageId);
        span?.SetTag("meepleledger.delivery_count", message.DeliveryCount);
        return span;
    }

    // How the message ended: completed, abandoned or dead-lettered. Anything but completed is an error.
    public static void SetOutcome(Activity? span, string outcome, string? reason)
    {
        if (span == null)
        {
            return;
        }

        span.SetTag("meepleledger.outcome", outcome);
        if (outcome != "completed")
        {
            span.SetStatus(ActivityStatusCode.Error, reason);
        }
    }
}
