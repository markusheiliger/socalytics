namespace SocAlytics.Platform.SimulatedAgentWorkload;

public sealed class SimulatedAgentWorkloadOptions
{
	public const string SectionName = "SimulatedAgentWorkload";

	/// <summary>Origin of the platform API; it must equal the origin the platform expects in workload proofs.</summary>
	public string ApiBaseAddress { get; set; } = string.Empty;

	public string ClientId { get; set; } = "simulated-agent-workload";

	/// <summary>Base64-encoded PKCS#8 ECDSA P-256 private key matching the public JWK configured for the client.</summary>
	public string PrivateKey { get; set; } = string.Empty;

	public string NatsUrl { get; set; } = string.Empty;

	/// <summary>Stream and consumer used when the platform issues no queue credentials (authentication mode None).</summary>
	public string Stream { get; set; } = "AGENT_WORK";

	public string Consumer { get; set; } = "agent-workload";

	public TimeSpan FetchExpiry { get; set; } = TimeSpan.FromSeconds(5);

	public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
}
