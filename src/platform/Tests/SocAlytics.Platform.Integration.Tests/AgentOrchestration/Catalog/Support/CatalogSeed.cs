namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;

internal static class CatalogSeed
{
	// Starts the host with an empty catalog: the Test seed of the baseline specialists stays off.
	public static void Off(Dictionary<string, string?> settings) => settings["AgentOrchestration:Catalog:SeedEnabled"] = "false";
}
