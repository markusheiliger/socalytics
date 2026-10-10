namespace SocAlytics.Platform.Application.Registry;

public sealed class StampOptions
{
    public const string SectionName = "Stamp";

    public string Id { get; set; } = string.Empty;

    // Absolute https origin (http only in Development).
    public string PublicBaseUri { get; set; } = string.Empty;

    public Uri TokenUri => Build("/api/v1/analyst-managers/token");

    public Uri ActivationUri => Build("/api/v1/analyst-managers/pairings/activation");

    public Uri PairingUri => Build("/api/v1/analyst-managers/pairings");

    public Uri SelfUri(string relativePath) =>
        Build("/api/v1/analyst-managers/self/" + relativePath.TrimStart('/'));

    public Uri VerificationUri(RegistryOptions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Build(registry.Pairing.VerificationPath);
    }

    public Uri Build(string path) =>
        new(PublicBaseUri.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute);
}
