namespace SocAlytics.Platform.Application.Messaging;

public sealed class OutboxPublicationOptions
{
    public const string SectionName = "Messaging:Outbox";

    public int PollIntervalSeconds { get; set; } = 1;

    public int BatchSize { get; set; } = 50;

    public int MaxAttempts { get; set; } = 8;

    public int BackoffBaseSeconds { get; set; } = 2;

    public int BackoffMaxSeconds { get; set; } = 300;
}
