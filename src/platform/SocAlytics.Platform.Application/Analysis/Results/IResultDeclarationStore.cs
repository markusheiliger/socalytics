namespace SocAlytics.Platform.Application.Analysis.Results;

public sealed record ResultObjectDeclaration(
    Guid AttemptId,
    string Name,
    long SizeBytes,
    string Digest,
    DateTimeOffset DeclaredAt);

public enum ResultDeclarationOutcome
{
    Declared,
    AlreadyDeclared,
    DigestConflict,
}

public interface IResultDeclarationStore
{
    // First declaration wins: the same digest is idempotent, a different digest is a conflict.
    Task<ResultDeclarationOutcome> DeclareAsync(ResultObjectDeclaration declaration, CancellationToken cancellationToken);

    Task<IReadOnlyList<ResultObjectDeclaration>> ListByAttemptAsync(Guid attemptId, CancellationToken cancellationToken);
}
