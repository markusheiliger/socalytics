using Microsoft.Extensions.Configuration;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

internal static class TestWorkflowSettings
{
	private const string DevelopmentSettings = "appsettings.Development.json";

	public static IReadOnlyDictionary<string, string?> Values { get; } = Load();

	private static Dictionary<string, string?> Load()
	{
		var relative = Path.Combine("src", "platform", "SocAlytics.Platform.Api", DevelopmentSettings);
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (!File.Exists(Path.Combine(directory!.FullName, relative)))
		{
			directory = directory.Parent;
		}

		var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(directory.FullName, relative)).Build();
		return configuration.GetSection("Analysis").AsEnumerable(true)
			.Select(pair => (Key: "Analysis:" + pair.Key, pair.Value))
			.Where(pair => pair.Key.StartsWith("Analysis:DefaultWorkflow", StringComparison.Ordinal))
			.ToDictionary(pair => pair.Key, pair => pair.Value);
	}
}
