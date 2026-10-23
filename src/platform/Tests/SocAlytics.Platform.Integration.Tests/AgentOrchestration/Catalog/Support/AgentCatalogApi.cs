using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;

internal static class CatalogKinds
{
	public const string PromptTemplates = "prompt-templates";
	public const string ModelSelections = "model-selections";
	public const string ToolPolicies = "tool-policies";
	public const string SafetyPolicies = "safety-policies";
	public const string DimensionSets = "dimension-sets";
	public const string PhilosophyProfiles = "philosophy-profiles";
	public const string ModifierPolicies = "modifier-policies";

	// Contract folder of each path kind.
	public static string ContractFolder(string kind) => kind switch
	{
		PromptTemplates => "prompt-template",
		ModelSelections => "model-selection",
		ToolPolicies => "tool-policy",
		SafetyPolicies => "safety-policy",
		DimensionSets => "dimension-set",
		PhilosophyProfiles => "philosophy-profile",
		ModifierPolicies => "modifier-policy",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
	};
}

// A catalog document with the kind it is registered under.
internal sealed record CatalogDocument(string Kind, JsonObject Document)
{
	public string Id => Document["id"]!.GetValue<string>();

	public string Version => Document["version"]!.GetValue<string>();

	public string Digest => CatalogFixtures.DigestOf(Document);

	public CatalogDocument Edit(Action<JsonObject> change)
	{
		var copy = (JsonObject)Document.DeepClone();
		change(copy);
		return this with { Document = copy };
	}
}

// The four artifacts a specialist agent version pins.
internal sealed record SpecialistArtifacts(
	CatalogDocument Prompt, CatalogDocument Model, CatalogDocument Tools, CatalogDocument Safety)
{
	public IEnumerable<CatalogDocument> All => [Prompt, Model, Tools, Safety];
}

// Loads the valid contract examples and recomputes the digests that pin artifacts, so the examples stay the single source.
internal static class CatalogFixtures
{
	public static string ContractsRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "contracts", "agent-catalog")))
		{
			dir = dir.Parent;
		}

		return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("contracts/agent-catalog"), "contracts", "agent-catalog");
	}

	public static JsonObject LoadRaw(string contractFolder, string file, string set = "valid") =>
		(JsonObject)JsonNode.Parse(File.ReadAllText(
			Path.Combine(ContractsRoot(), contractFolder, "v1", "examples", set, file.EndsWith(".json", StringComparison.Ordinal) ? file : file + ".json")))!;

	public static CatalogDocument Artifact(string kind, string file, string set = "valid") =>
		new(kind, LoadRaw(CatalogKinds.ContractFolder(kind), file, set));

	// The digest of the canonical document, as the platform stores it.
	public static string DigestOf(JsonNode document)
	{
		using var parsed = JsonDocument.Parse(document.ToJsonString());
		return ContentDigest.Of(parsed.RootElement).ToString();
	}

	public static SpecialistArtifacts SpecialistTacticsArtifacts(string model = "simulated-minimal") => new(
		Artifact(CatalogKinds.PromptTemplates, "specialist-tactics-prompt"),
		Artifact(CatalogKinds.ModelSelections, model),
		Artifact(CatalogKinds.ToolPolicies, "specialist-tactics-tools"),
		Artifact(CatalogKinds.SafetyPolicies, "specialist-safety"));

	// An agent version document from an agent-version example with its pins set to the given artifacts and their real digests.
	public static JsonObject AgentVersion(SpecialistArtifacts artifacts, string file = "specialist-tactics-1.0.0")
	{
		var document = LoadRaw("agent-version", file);
		var pins = (JsonObject)document["artifacts"]!;
		Pin(pins, "promptTemplate", artifacts.Prompt);
		Pin(pins, "modelSelection", artifacts.Model);
		Pin(pins, "toolPolicy", artifacts.Tools);
		Pin(pins, "safetyPolicy", artifacts.Safety);
		return document;
	}

	public static void Pin(JsonObject pins, string slot, CatalogDocument artifact) =>
		pins[slot] = new JsonObject
		{
			["id"] = artifact.Id,
			["version"] = artifact.Version,
			["digest"] = artifact.Digest,
		};
}

// Typed calls for every catalog operation as the given member.
internal sealed class AgentCatalogApi(ApiSession member)
{
	private const string Root = "/api/v1/agent-catalog";

	public ApiSession Member { get; } = member;

	public Task<HttpResponseMessage> GetBaselineAsync(CancellationToken ct) =>
		SendAsync(HttpMethod.Get, $"{Root}/baseline", null, null, ct);

	public Task<HttpResponseMessage> RegisterArtifactAsync(string kind, JsonNode document, CancellationToken ct) =>
		SendAsync(HttpMethod.Post, $"{Root}/artifacts/{kind}", document, null, ct);

	public Task<HttpResponseMessage> RegisterArtifactAsync(CatalogDocument artifact, CancellationToken ct) =>
		RegisterArtifactAsync(artifact.Kind, artifact.Document, ct);

	public Task<HttpResponseMessage> ListArtifactsAsync(string kind, string? id, CancellationToken ct) =>
		SendAsync(HttpMethod.Get, $"{Root}/artifacts/{kind}{Query(("id", id))}", null, null, ct);

	public Task<HttpResponseMessage> GetArtifactAsync(string kind, string id, string version, string? ifNoneMatch, CancellationToken ct) =>
		SendAsync(HttpMethod.Get, $"{Root}/artifacts/{kind}/{Uri.EscapeDataString(id)}/versions/{Uri.EscapeDataString(version)}", null,
			ifNoneMatch is null ? null : ("If-None-Match", ifNoneMatch), ct);

	public Task<HttpResponseMessage> RegisterAgentVersionAsync(string? version, JsonNode document, CancellationToken ct) =>
		SendAsync(HttpMethod.Post, $"{Root}/agent-versions{Query(("version", version))}", document, null, ct);

	public Task<HttpResponseMessage> ListAgentVersionsAsync(string? agentId, string? state, CancellationToken ct) =>
		SendAsync(HttpMethod.Get, $"{Root}/agent-versions{Query(("agentId", agentId), ("state", state))}", null, null, ct);

	public Task<HttpResponseMessage> GetAgentVersionAsync(string agentId, string version, string? ifNoneMatch, CancellationToken ct) =>
		SendAsync(HttpMethod.Get, AgentVersionPath(agentId, version), null, ifNoneMatch is null ? null : ("If-None-Match", ifNoneMatch), ct);

	// A null ifMatch sends no precondition; a null acknowledged sends no body.
	public Task<HttpResponseMessage> ApproveAgentVersionAsync(
		string agentId, string version, string? ifMatch, bool? differentiationReviewAcknowledged, CancellationToken ct) =>
		SendAsync(HttpMethod.Post, $"{AgentVersionPath(agentId, version)}/approval",
			differentiationReviewAcknowledged is { } acknowledged
				? new JsonObject { ["differentiationReviewAcknowledged"] = acknowledged }
				: null,
			ifMatch is null ? null : ("If-Match", ifMatch), ct);

	public Task<HttpResponseMessage> RetireAgentVersionAsync(string agentId, string version, string? ifMatch, CancellationToken ct) =>
		SendAsync(HttpMethod.Post, $"{AgentVersionPath(agentId, version)}/retirement", null,
			ifMatch is null ? null : ("If-Match", ifMatch), ct);

	// Registers every artifact in order and fails when one is not accepted.
	public async Task RegisterAllAsync(IEnumerable<CatalogDocument> artifacts, CancellationToken ct)
	{
		foreach (var artifact in artifacts)
		{
			using var response = await RegisterArtifactAsync(artifact, ct);
			response.EnsureSuccessStatusCode();
		}
	}

	// Registers the specialist's artifacts and its agent version and returns the version's response.
	public async Task<HttpResponseMessage> RegisterSpecialistAsync(SpecialistArtifacts artifacts, string version, CancellationToken ct)
	{
		await RegisterAllAsync(artifacts.All, ct);
		return await RegisterAgentVersionAsync(version, CatalogFixtures.AgentVersion(artifacts), ct);
	}

	private static string AgentVersionPath(string agentId, string version) =>
		$"{Root}/agent-versions/{Uri.EscapeDataString(agentId)}/versions/{Uri.EscapeDataString(version)}";

	private static string Query(params (string Name, string? Value)[] parameters)
	{
		var parts = parameters.Where(p => p.Value is not null).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}").ToArray();
		return parts.Length == 0 ? string.Empty : "?" + string.Join('&', parts);
	}

	private Task<HttpResponseMessage> SendAsync(
		HttpMethod method, string path, JsonNode? body, (string Name, string Value)? header, CancellationToken ct)
	{
		var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", Member.AntiforgeryToken);
		}

		if (header is { } h)
		{
			request.Headers.TryAddWithoutValidation(h.Name, h.Value);
		}

		return Member.Client.SendAsync(request, ct);
	}
}
