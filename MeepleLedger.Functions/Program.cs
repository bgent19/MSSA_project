using MeepleLedger.Functions;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// MeepleLedger.Functions (A-21): the A-19 embed worker as Azure Functions on the isolated worker model.
//
//   EmbedRequest   Service Bus trigger on "embed-requests": embeds the Game and patches the vector
//                  (and the hash of its text) back onto its Cosmos document. Replaces the Worker's
//                  hand-built ServiceBusProcessor.
//   QueueEmbed     POST /api/games/{name}/embed: queues an embed request for one Game, through a
//                  Service Bus OUTPUT binding (no ServiceBusClient or sender in this project).
//   About          GET /api/about: the build and slot name, to check a slot swap (A-22).
//
// Run it with Core Tools: write local.settings.json once (pwsh MeepleLedger.Functions/write-local-settings.ps1),
// then "func start" from this folder. The Worker's send, send-bad and dlq commands still work against
// the same queue, so they are the way to drive and check this.
//
// In Azure (A-22): infra/functions/provision-functions.ps1 creates the function apps and their app
// settings, infra/functions/deploy-functions.ps1 publishes this project to them.

FunctionsApplicationBuilder builder = FunctionsApplication.CreateBuilder(args);

// ASP.NET Core integration, so the HTTP trigger takes an HttpRequest and returns an IActionResult
builder.ConfigureFunctionsWebApplication();

// One embedder (one HttpClient, one CosmosClient) for every invocation, like the Worker's one per run
builder.Services.AddSingleton<GameEmbedder>();

builder.Build().Run();
