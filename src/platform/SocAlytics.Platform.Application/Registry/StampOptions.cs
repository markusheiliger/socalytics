namespace SocAlytics.Platform.Application.Registry;

public sealed class StampOptions
{
    public const string SectionName = "Stamp";

    public string Id { get; set; } = string.Empty;

    // Absolute https origin (http only in Development).
    public string PublicBaseUri { get; set; } = string.Empty;

    public Uri TokenUri => Build("/api/v1/analyst-managers/token");

    public Uri WorkloadTokenUri => Build("/api/v1/agent-orchestration/workload/token");

    public Uri ActivationUri => Build("/api/v1/analyst-managers/pairings/activation");

    public Uri PairingUri => Build("/api/v1/analyst-managers/pairings");

    public Uri SelfUri(string relativePath) =>
        Build("/api/v1/analyst-managers/self/" + relativePath.TrimStart('/'));

    public Uri VerificationUri(RegistryOptions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var baseUri = string.IsNullOrWhiteSpace(registry.Pairing.VerificationBaseUri)
            ? PublicBaseUri
            : registry.Pairing.VerificationBaseUri;
        return new(baseUri.TrimEnd('/') + "/" + registry.Pairing.VerificationPath.TrimStart('/'), UriKind.Absolute);
    }

    public Uri Build(string path) =>
        new(PublicBaseUri.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute);
}
