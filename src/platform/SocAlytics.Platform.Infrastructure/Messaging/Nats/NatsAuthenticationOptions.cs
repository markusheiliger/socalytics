namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

internal enum NatsAuthenticationMode
{
    /// <summary>No authentication; allowed only in the Development and Test environments.</summary>
    None,
    Jwt,
}

internal sealed class NatsAuthenticationOptions
{
    public const string SectionName = "Nats:Authentication";

    public NatsAuthenticationMode Mode { get; set; } = NatsAuthenticationMode.None;

    public string? OperatorSeed { get; set; }

    public string? AccountSeed { get; set; }

    public string? SystemAccountSeed { get; set; }
}
