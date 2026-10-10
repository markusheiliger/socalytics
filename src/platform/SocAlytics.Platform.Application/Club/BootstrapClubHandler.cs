using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Domain.IdentityAccess;
using ClubEntity = SocAlytics.Platform.Domain.Club.Club;

namespace SocAlytics.Platform.Application.Club;

public enum BootstrapOutcome
{
    Created,
    AlreadyEstablished,
    Conflict,
    NotConfigured,
    InvalidConfiguration,
}

/// <summary>Result of the bootstrap step: <see cref="OpenScope"/> is the still-open unit of work, or null when it was rolled back.</summary>
public sealed record BootstrapStepResult(BootstrapOutcome Outcome, IUnitOfWorkScope? OpenScope)
{
    public bool IsUnitOfWorkOpen => OpenScope is not null;
}

/// <param name="RecoveryRefusalReason">Null when no directive was given or it was applied.</param>
public sealed record BootstrapRunResult(OperationResult<BootstrapOutcome> Outcome, string? RecoveryRefusalReason);

public sealed class BootstrapClubHandler(
    IUnitOfWork unitOfWork,
    IClubHierarchyStore clubs,
    IMemberAccountStore members,
    IAccountCredentialService credentials,
    IAuditTrail audit,
    ApplyBreakGlassRecoveryHandler recovery,
    TimeProvider time)
{
    public async Task<OperationResult<BootstrapOutcome>> HandleAsync(BootstrapClubCommand command, CancellationToken cancellationToken)
    {
        var run = await RunAsync(command, cancellationToken);
        return run.Outcome;
    }

    /// <summary>Runs bootstrap and then the optional recovery step; the bootstrap outcome is unaffected by recovery.</summary>
    public async Task<BootstrapRunResult> RunAsync(BootstrapClubCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        for (var attempt = 0; ; attempt++)
        {
            var step = await BootstrapAsync(command, cancellationToken);
            BreakGlassRecoveryResult? recovered = null;
            var scope = step.OpenScope;
            try
            {
                if (command.Recovery is not null)
                {
                    if (scope is null)
                    {
                        scope = await unitOfWork.BeginAsync(cancellationToken);
                        await clubs.LockBootstrapAsync(cancellationToken);
                    }

                    recovered = await recovery.ApplyWithinCurrentUnitOfWorkAsync(command.Recovery, cancellationToken);
                }

                if (scope is not null)
                {
                    await scope.CommitAsync(cancellationToken);
                }
            }
            catch (UniqueViolationException) when (attempt == 0 && command.Recovery is not null)
            {
                // Disposal below rolls back the whole unit of work; the second run finds the id used.
                continue;
            }
            finally
            {
                if (scope is not null)
                {
                    await scope.DisposeAsync();
                }
            }

            return new BootstrapRunResult(step.Outcome, recovered?.RefusalReason);
        }
    }

    /// <summary>Runs the bootstrap step in its own unit of work and reports whether that unit of work is still open.</summary>
    public async Task<BootstrapStepResult> BootstrapAsync(BootstrapClubCommand command, CancellationToken cancellationToken)
    {
        var scope = await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            await clubs.LockBootstrapAsync(cancellationToken);
            var club = await clubs.GetClubAsync(cancellationToken);
            if (club is null)
            {
                var created = await TryCreateAsync(command, cancellationToken);
                if (created is not null)
                {
                    return await FinishAsync(scope, created.Value, cancellationToken);
                }

                // Another instance won: release the lock and evaluate the existing club in a new unit of work.
                await scope.RollbackAsync(cancellationToken);
                await scope.DisposeAsync();
                scope = await unitOfWork.BeginAsync(cancellationToken);
                await clubs.LockBootstrapAsync(cancellationToken);
                club = await clubs.GetClubAsync(cancellationToken);
                if (club is null)
                {
                    return await RefuseAsync(scope, cancellationToken);
                }
            }

            return await EvaluateExistingAsync(scope, club, command, cancellationToken);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    // Returns null when a unique violation shows that another instance created the club or account first.
    private async Task<BootstrapOutcome?> TryCreateAsync(BootstrapClubCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ClubDisplayName)
            || string.IsNullOrWhiteSpace(command.FirstClubAdminAccountName)
            || string.IsNullOrEmpty(command.FirstClubAdminInitialPassword))
        {
            return BootstrapOutcome.NotConfigured;
        }

        if (!DisplayName.TryCreate(command.ClubDisplayName, out var displayName, out _)
            || !AccountName.TryCreate(command.FirstClubAdminAccountName, out var accountName, out _)
            || (await credentials.ValidatePasswordAsync(command.FirstClubAdminInitialPassword, cancellationToken)).Count > 0)
        {
            return BootstrapOutcome.InvalidConfiguration;
        }

        try
        {
            var accountId = await credentials.CreateAccountAsync(
                accountName, command.FirstClubAdminInitialPassword, passwordChangeRequired: true, createdByAccountId: null, cancellationToken);
            await members.AssignClubRoleAsync(accountId, ClubRole.ClubAdmin, assignedBy: null, cancellationToken);

            var club = new ClubEntity(Guid.NewGuid(), displayName, accountId, time.GetUtcNow(), 1);
            await clubs.InsertClubAsync(club, cancellationToken);
            await audit.RecordAsync(
                new AuditEvent
                {
                    EventType = "club.bootstrapped",
                    Action = "bootstrap",
                    Outcome = AuditOutcome.Succeeded,
                    Resource = new AuditResource("club", club.Id.ToString()),
                    ActorOverride = new AuditActorOverride(AuditActorKind.System, null),
                },
                cancellationToken);
            return BootstrapOutcome.Created;
        }
        catch (UniqueViolationException)
        {
            return null;
        }
    }

    private async Task<BootstrapStepResult> FinishAsync(IUnitOfWorkScope scope, BootstrapOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome == BootstrapOutcome.Created)
        {
            return new BootstrapStepResult(outcome, scope);
        }

        await scope.RollbackAsync(cancellationToken);
        await scope.DisposeAsync();
        return new BootstrapStepResult(outcome, null);
    }

    private async Task<BootstrapStepResult> EvaluateExistingAsync(
        IUnitOfWorkScope scope,
        ClubEntity club,
        BootstrapClubCommand command,
        CancellationToken cancellationToken)
    {
        // The initial password is never read once a club exists.
        if (string.IsNullOrWhiteSpace(command.FirstClubAdminAccountName))
        {
            return new BootstrapStepResult(BootstrapOutcome.AlreadyEstablished, scope);
        }

        if (AccountName.TryCreate(command.FirstClubAdminAccountName, out var name, out _)
            && await members.FindAccountIdByNameAsync(name, cancellationToken) == club.BootstrapAdminAccountId)
        {
            return new BootstrapStepResult(BootstrapOutcome.AlreadyEstablished, scope);
        }

        return await RefuseAsync(scope, cancellationToken);
    }

    private async Task<BootstrapStepResult> RefuseAsync(IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        await scope.RollbackAsync(cancellationToken);
        await scope.DisposeAsync();
        await audit.RecordIndependentAsync(
            new AuditEvent
            {
                EventType = "club.bootstrap-refused",
                Action = "bootstrap",
                Outcome = AuditOutcome.Refused,
                Resource = new AuditResource("club", null),
                ReasonCode = "bootstrap-conflict",
                ActorOverride = new AuditActorOverride(AuditActorKind.System, null),
            },
            cancellationToken);
        return new BootstrapStepResult(BootstrapOutcome.Conflict, null);
    }
}
