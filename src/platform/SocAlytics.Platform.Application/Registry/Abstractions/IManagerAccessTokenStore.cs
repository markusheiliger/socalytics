using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Abstractions;

public sealed record ManagerAccessToken(
    byte[] TokenHash, Guid RegistrationId, string Jkt, string Scope, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

public sealed record ManagerAccessTokenLookup(ManagerAccessToken Token, AnalystManagerRegistration Registration);

public interface IManagerAccessTokenStore
{
    Task InsertAsync(ManagerAccessToken token, CancellationToken cancellationToken);

    Task<ManagerAccessTokenLookup?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    Task<int> DeleteForRegistrationAsync(Guid registrationId, CancellationToken cancellationToken);

    Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
