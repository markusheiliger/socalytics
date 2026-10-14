using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class OpenApiSnapshotTests
{
	private const string UpdateVariable = "SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT";
	private const string RegenerationInstruction =
		$"Regenerate it with {UpdateVariable}=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests and commit the result.";

	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	[Fact]
	public async Task OpenApiDocumentMatchesCommittedSnapshot()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(
			["--SocAlytics:LocalDatabase:Persistent=false", "--SocAlytics:ApiHttpsEndpoint=false"], timeout.Token);
		await using var app = await appHost.BuildAsync(timeout.Token);

		await app.StartAsync(timeout.Token);
		await app.ResourceNotifications.WaitForResourceHealthyAsync("api", timeout.Token);

		using var client = app.CreateHttpClient("api", "http");
		var json = await client.GetStringAsync("/openapi/v1.json", timeout.Token);
		var actual = Normalize(json);

		var snapshotPath = Path.Combine(FindRepositoryRoot(), "src", "platform", "SocAlytics.Platform.Api", "openapi", "v1.json");
		if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
		{
			Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
			await File.WriteAllTextAsync(snapshotPath, actual, new UTF8Encoding(false), timeout.Token);
			return;
		}

		File.Exists(snapshotPath).ShouldBeTrue($"The OpenAPI snapshot {snapshotPath} is missing. {RegenerationInstruction}");
		var expected = (await File.ReadAllTextAsync(snapshotPath, timeout.Token)).Replace("\r\n", "\n");
		if (expected != actual)
		{
			Assert.Fail($"The OpenAPI document differs from src/platform/SocAlytics.Platform.Api/openapi/v1.json.{Environment.NewLine}"
				+ $"{Diff(expected, actual)}{Environment.NewLine}{RegenerationInstruction}");
		}
	}

	private static string Normalize(string json)
	{
		var root = JsonNode.Parse(json)!.AsObject();
		root.Remove("servers");
		var sorted = Sort(root);
		return sorted!.ToJsonString(WriteOptions).Replace("\r\n", "\n") + "\n";
	}

	private static JsonNode? Sort(JsonNode? node) => node switch
	{
		JsonObject obj => SortObject(obj),
		JsonArray array => new JsonArray(array.Select(item => Sort(item)).ToArray()),
		null => null,
		_ => node.DeepClone(),
	};

	private static JsonObject SortObject(JsonObject obj)
	{
		var result = new JsonObject();
		foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
		{
			result.Add(property.Key, Sort(property.Value));
		}

		return result;
	}

	private static string Diff(string expected, string actual)
	{
		var expectedLines = expected.Split('\n');
		var actualLines = actual.Split('\n');
		var diff = new StringBuilder();
		var shown = 0;
		for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length) && shown < 10; i++)
		{
			var e = i < expectedLines.Length ? expectedLines[i] : null;
			var a = i < actualLines.Length ? actualLines[i] : null;
			if (e == a)
			{
				continue;
			}

			diff.AppendLine($"line {i + 1}:");
			diff.AppendLine($"- {e}");
			diff.AppendLine($"+ {a}");
			shown++;
		}

		return diff.ToString();
	}

	private static string FindRepositoryRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (Directory.Exists(Path.Combine(directory.FullName, "src", "platform", "SocAlytics.Platform.Api")))
			{
				return directory.FullName;
			}
		}

		throw new InvalidOperationException("The repository root was not found above the test output directory.");
	}
}
