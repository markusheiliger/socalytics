namespace SocAlytics.Platform.Domain.Club;

public sealed record Club(
    Guid Id,
    DisplayName DisplayName,
    Guid BootstrapAdminAccountId,
    DateTimeOffset CreatedAt,
    long Version);
