namespace SocAlytics.Platform.Application.Registry.QueueCredentials;

// Defaults are explicit non-production values; production values are deferred.
public sealed class AnalystWorkOptions
{
    public const string SectionName = "AnalystWork";

    public int MaxConcurrentAnalystsPerManager { get; set; } = 2;

    public TimeSpan QueueCredentialLifetime { get; set; } = TimeSpan.FromMinutes(30);

    public string QueueUrl { get; set; } = "nats://localhost:4222";

    public TimeSpan ConsumerAckWait { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan UnfitRedeliveryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
