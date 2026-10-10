using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class AccessAuthorizer(
    IRequestContext requestContext,
    IMemberAccountStore accounts,
    ITeamScopeResolver scopeResolver,
    IAuditTrail auditTrail) : IAccessAuthorizer
{
    private static readonly AuditResource ClubResource = new("club", null);

    public Task<AccessDecision> AuthorizeClubAsync(ClubPermission permission, CancellationToken cancellationToken) =>
        AuthorizeClubAsync(permission, ClubResource, cancellationToken);

    public async Task<AccessDecision> AuthorizeClubAsync(
        ClubPermission permission,
        AuditResource resource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var (snapshot, inactiveReason) = await LoadAsync(cancellationToken);
        if (snapshot is null)
        {
            return await DenyAsync(AccessDecision.Forbidden, resource, null, inactiveReason!, cancellationToken);
        }

        var granted = permission switch
        {
            ClubPermission.Administer => snapshot.ClubRoles.Contains(ClubRole.ClubAdmin),
            ClubPermission.Register => snapshot.ClubRoles.Contains(ClubRole.ClubAdmin)
                || snapshot.ClubRoles.Contains(ClubRole.Registrar),
            _ => false,
        };

        return granted
            ? AccessDecision.Granted()
            : await DenyAsync(AccessDecision.Forbidden, resource, null, "insufficient-role", cancellationToken);
    }

    public async Task<AccessDecision> AuthorizeTeamResourceAsync(
        TeamOwnedResource resource,
        TeamPermission permission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var audited = new AuditResource(resource.Kind, resource.Id.ToString());
        var (snapshot, inactiveReason) = await LoadAsync(cancellationToken);
        if (snapshot is null)
        {
            return await DenyAsync(AccessDecision.NotFound, audited, null, inactiveReason!, cancellationToken);
        }

        var scope = await scopeResolver.ResolveAsync(resource, cancellationToken);
        if (scope is null)
        {
            return await DenyAsync(AccessDecision.NotFound, audited, null, "unknown-resource", cancellationToken);
        }

        if (snapshot.ClubRoles.Contains(ClubRole.ClubAdmin))
        {
            return AccessDecision.Granted(scope);
        }

        if (!snapshot.TeamRoles.TryGetValue(scope.TeamId, out var role))
        {
            return await DenyAsync(AccessDecision.NotVisible, audited, scope.TeamId, "not-visible", cancellationToken);
        }

        if (permission == TeamPermission.Write && role != TeamRole.Coach)
        {
            return await DenyAsync(
                AccessDecision.Forbidden, audited, scope.TeamId, "insufficient-role", cancellationToken);
        }

        return AccessDecision.Granted(scope);
    }

    public async Task<TeamVisibility> GetVisibleTeamsAsync(CancellationToken cancellationToken)
    {
        var (snapshot, _) = await LoadAsync(cancellationToken);
        if (snapshot is null)
        {
            return new TeamVisibility(false, new HashSet<Guid>());
        }

        return snapshot.ClubRoles.Contains(ClubRole.ClubAdmin)
            ? new TeamVisibility(true, new HashSet<Guid>(await accounts.ListAllTeamIdsAsync(cancellationToken)))
            : new TeamVisibility(false, new HashSet<Guid>(snapshot.TeamRoles.Keys));
    }

    // Returns a snapshot only for an active account without a pending password change.
    private async Task<(MemberAccessSnapshot? Snapshot, string? DenialReason)> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (requestContext.MemberAccountId is not { } accountId)
        {
            return (null, "inactive-membership");
        }

        var snapshot = await accounts.GetAccessSnapshotAsync(accountId, cancellationToken);
        if (snapshot is null || snapshot.Status != MembershipStatus.Active)
        {
            return (null, "inactive-membership");
        }

        return snapshot.PasswordChangeRequired ? (null, "password-change-required") : (snapshot, null);
    }

    private async Task<AccessDecision> DenyAsync(
        AccessDecision decision,
        AuditResource resource,
        Guid? teamId,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        await auditTrail.RecordIndependentAsync(
            new AuditEvent
            {
                EventType = "authorization.denied",
                Action = "authorize",
                Outcome = AuditOutcome.Denied,
                Resource = resource,
                TeamId = teamId,
                ReasonCode = reasonCode,
            },
            cancellationToken);
        return decision;
    }
}
