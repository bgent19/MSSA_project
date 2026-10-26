using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;

namespace MeepleLedger.Functions;

// GET /api/about says which build is running and which slot's settings it sees (A-22). It is how a
// slot swap is checked from outside:
//   - "build" is baked into the package at publish time (deploy-functions.ps1 passes -Build), so it
//     moves with the code when the slots swap
//   - "slot" is the SlotName app setting, marked as a deployment slot setting, so it stays put
// After a swap the production URL shows the staging build next to slot "production".
//
// Anonymous on purpose: it holds no secrets, and a swap check should not need a function key.
public class AboutFunction
{
    private readonly IConfiguration _config;

    public AboutFunction(IConfiguration config)
    {
        _config = config;
    }

    [Function("About")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "about")] HttpRequest httpRequest)
    {
        string build = "unknown";
        AssemblyInformationalVersionAttribute? version =
            typeof(AboutFunction).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (version != null)
        {
            build = version.InformationalVersion;
        }

        // Not set locally, so "local" there
        string slot = _config["SlotName"] ?? "local";

        // WEBSITE_SITE_NAME is set by App Service on every function app
        string app = _config["WEBSITE_SITE_NAME"] ?? "local";

        return new OkObjectResult(new { app, slot, build });
    }
}
