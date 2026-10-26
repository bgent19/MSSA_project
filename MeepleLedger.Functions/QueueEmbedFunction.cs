using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MeepleLedger.Functions;

// POST /api/games/{name}/embed queues an embed request for one Game, so a vector can be refreshed
// on demand without editing the Blurb. The name is the Game's Cosmos id (upload-cosmos uses the name
// as the id). Spaces and punctuation are fine URL-encoded; a name with a "/" in it cannot be routed.
//
// No SDK client code at all:
//   - the Cosmos INPUT binding reads the Game (null if there is no such id), so an unknown name gets
//     a 404 here instead of a poison message that fails 5 times
//   - the Service Bus OUTPUT binding sends the message: whatever is in QueueEmbedOutput.Message when
//     the function returns, the host sends to embed-requests. Null sends nothing.
public class QueueEmbedFunction
{
    private readonly ILogger<QueueEmbedFunction> _logger;

    public QueueEmbedFunction(ILogger<QueueEmbedFunction> logger)
    {
        _logger = logger;
    }

    // %CosmosDatabase% and %CosmosContainer% are app setting names, filled in by the host.
    // {name} comes from the route.
    [Function("QueueEmbed")]
    public QueueEmbedOutput Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "games/{name}/embed")] HttpRequest httpRequest,
        [CosmosDBInput("%CosmosDatabase%", "%CosmosContainer%", Connection = "CosmosConnection", Id = "{name}", PartitionKey = "{name}")] CosmosGameDocument? game,
        string name)
    {
        QueueEmbedOutput output = new QueueEmbedOutput();

        if (game == null)
        {
            output.Response = new NotFoundObjectResult(new { error = $"No Game with id '{name}' in Cosmos. Nothing queued." });
            output.Message = null;
            return output;
        }

        EmbedRequest request = new EmbedRequest
        {
            SchemaVersion = EmbedRequest.CurrentSchemaVersion,
            GameId = game.Id,
            TextHash = "",
            Reason = "api",
            DetectedAtUtc = DateTime.UtcNow,
        };

        output.Message = JsonSerializer.Serialize(request, EmbedRequest.JsonOptions);
        output.Response = new AcceptedResult();

        _logger.LogInformation("Queued an embed request for '{GameId}'", game.Id);
        return output;
    }
}

// The function's two results. The host turns each property into what its attribute says.
public class QueueEmbedOutput
{
    [HttpResult]
    public IActionResult Response { get; set; } = new OkResult();

    // Sent as the message body. A string output binding cannot set the message's Subject, ContentType
    // or the schemaVersion application property that the Worker's "send" sets. The function that reads
    // it only looks at the body, so nothing breaks, but it is a real difference.
    [ServiceBusOutput(EmbedRequest.QueueName, Connection = "ServiceBusConnection")]
    public string? Message { get; set; }
}
