using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;

public enum ExternalIdentityInsertOutcome
{
    Inserted,
    IdentityAlreadyLinked,
    AccountProviderAlreadyLinked,
}

public interface IExternalIdentityStore
{
    Task<ExternalIdentityLink?> FindByIssuerAndSubjectAsync(string issuer, string subject, CancellationToken cancellationToken);

    /// <summary>Inserts the link; uniqueness violations come back as distinct outcomes instead of exceptions.</summary>
    Task<ExternalIdentityInsertOutcome> InsertAsync(ExternalIdentityLink link, CancellationToken cancellationToken);

    /// <summary>Deletes the link when the member's version matches; returns false when it does not exist or its version differs.</summary>
    Task<bool> DeleteAsync(Guid linkId, long expectedVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExternalIdentityLink>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken);
}
