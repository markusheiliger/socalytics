namespace SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

// One artifact of an agent version as pinned by an invocation at submission.
public sealed record InvocationPin(AgentCatalogRecordKind Kind, string ArtifactId, string ArtifactVersion, string Digest);

// The agent version and the artifacts an invocation was submitted against; never changes afterwards.
public sealed record InvocationPinSet
{
	public InvocationPinSet(string agentId, string agentVersion, string agentVersionDigest, IEnumerable<InvocationPin> artifacts)
	{
		ArgumentException.ThrowIfNullOrEmpty(agentId);
		ArgumentException.ThrowIfNullOrEmpty(agentVersion);
		ArgumentException.ThrowIfNullOrEmpty(agentVersionDigest);
		ArgumentNullException.ThrowIfNull(artifacts);

		var pins = artifacts.OrderBy(pin => pin.Kind).ToList();
		if (pins.Count == 0 || pins.Select(pin => pin.Kind).Distinct().Count() != pins.Count)
		{
			throw new ArgumentException("A pin set holds exactly one pin per artifact kind and at least one pin.", nameof(artifacts));
		}

		AgentId = agentId;
		AgentVersion = agentVersion;
		AgentVersionDigest = agentVersionDigest;
		Artifacts = pins;
	}

	public string AgentId { get; }

	public string AgentVersion { get; }

	public string AgentVersionDigest { get; }

	public IReadOnlyList<InvocationPin> Artifacts { get; }
}
