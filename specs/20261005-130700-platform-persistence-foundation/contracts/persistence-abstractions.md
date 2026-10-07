# Contract: Persistence Abstractions (Consumed by Later Features)

Namespace `SocAlytics.Platform.Application.Abstractions.Persistence` in
`SocAlytics.Platform.Application`. These are the only public persistence types.
Implementations stay internal to `SocAlytics.Platform.Infrastructure` and are
registered by `AddInfrastructure()`.

## Signatures

```csharp
public interface IUnitOfWork
{
    Task<IUnitOfWorkScope> BeginAsync(CancellationToken cancellationToken);
}

public interface IUnitOfWorkScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

public enum VersionedWriteOutcome
{
    Applied,
    NotFound,
    ConcurrencyConflict,
}

public readonly record struct VersionedWriteResult(VersionedWriteOutcome Outcome, long? Version);
```

## Semantics

| Member | Contract |
| --- | --- |
| `IUnitOfWork` lifetime | Scoped (one per HTTP request or DI scope). |
| `BeginAsync` | Opens one connection and one Read Committed transaction. Throws `InvalidOperationException` if a scope is already active in the same DI scope. |
| `CommitAsync` | Commits every change made through Infrastructure persistence code since `BeginAsync`. On failure or cancellation nothing is committed and the scope counts as rolled back. |
| `RollbackAsync` | Undoes every change. Idempotent with dispose. |
| `DisposeAsync` | Rolls back if not committed, returns the connection, and leaves the unit of work ready for a new `BeginAsync`. |
| Writes without an active scope | Infrastructure persistence code throws `InvalidOperationException`. Every state change runs inside a unit of work (FR-015). |
| `VersionedWriteResult.Applied` | The guarded write matched. `Version` is the persisted version after the write: unchanged when the write changed nothing, otherwise advanced by triggers. |
| `VersionedWriteResult.NotFound` | No record with the identity exists. `Version` is `null`. |
| `VersionedWriteResult.ConcurrencyConflict` | The record exists but the expected version (or state) did not match. `Version` is the current persisted version. The handler returns without committing, so disposal rolls back the contested change (FR-018). |

## Handler Pattern

1. Begin a scope with `await using`.
2. Perform writes through the Infrastructure persistence code for the area.
3. On any non-`Applied` outcome, return the mapped result without committing.
4. Otherwise commit.

The API maps `NotFound` to `404` and `ConcurrencyConflict` to `412` for edits
(`If-Match`) or `409` for lifecycle transitions, using RFC 9457 problem
details.
