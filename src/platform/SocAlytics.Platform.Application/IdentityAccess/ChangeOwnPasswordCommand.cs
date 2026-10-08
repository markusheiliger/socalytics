namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record ChangeOwnPasswordCommand(Guid AccountId, Guid SessionId, string CurrentPassword, string NewPassword);
