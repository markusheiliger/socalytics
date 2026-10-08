namespace SocAlytics.Platform.Domain.Club;

public sealed record Season(
    Guid Id,
    DisplayName Name,
    SeasonState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? ArchivedAt,
    long Version)
{
    public bool CanActivate => State == SeasonState.Draft;

    public bool CanArchive => State == SeasonState.Active;
}
