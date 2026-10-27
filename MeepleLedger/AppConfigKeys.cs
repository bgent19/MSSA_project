namespace MeepleLedger;

// Names in Azure App Configuration that the code refers to (A-30). The provisioning scripts in
// infra/appconfig/ write the same names; keep them in step.
public static class AppConfigKeys
{
    // The key the app watches. Bumping it (infra/appconfig/set-provider.ps1 does) makes every app
    // reload all of its settings at the next refresh. The worker watches it too.
    public const string Sentinel = "MeepleLedger:Sentinel";

    // The feature flag in front of the Tonight screen: its nav link and the page itself.
    public const string TonightFeature = "Tonight";
}
