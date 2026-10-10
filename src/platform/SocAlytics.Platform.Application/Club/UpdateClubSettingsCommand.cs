namespace SocAlytics.Platform.Application.Club;

public sealed record UpdateClubSettingsCommand(string? DisplayName, long ExpectedVersion);
