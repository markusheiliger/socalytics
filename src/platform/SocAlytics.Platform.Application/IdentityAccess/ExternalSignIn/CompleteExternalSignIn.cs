using System.Security.Cryptography;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;

/// <param name="State">The <c>state</c> query value, if any.</param>
/// <param name="Code">The <c>code</c> query value, if any.</param>
/// <param name="ProviderReportedError">True when the provider answered with <c>error</c>.</param>
/// <param name="BrowserBinding">The binding cookie value the browser presented, if any.</param>
public sealed record CompleteExternalSignInCommand(
    string? State,
    string? Code,
    bool ProviderReportedError,
    string? BrowserBinding,
    string RedirectUri,
    byte[]? PresentedTokenHash = null);

/// <summary>Every failure is the same generic outcome; its cause is only audited.</summary>
public sealed class CompleteExternalSignInHandler(
    IUnitOfWork unitOfWork,
    IOidcProviderClient provider,
    IPendingExternalSignInStore pending,
    IExternalIdentityStore links,
    IMemberAccountStore members,
    ISessionStore sessions,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<SignedInSession>> HandleAsync(CompleteExternalSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrEmpty(command.State))
        {
            return await FailAsync("missing-state", null, cancellationToken);
        }

        PendingExternalSignIn? consumed;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            consumed = await pending.ConsumeAsync(StartExternalSignInHandler.Hash(command.State), time.GetUtcNow(), cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        if (consumed is null)
        {
            return await FailAsync("state-invalid", null, cancellationToken);
        }

        var providerId = consumed.ProviderId;
        if (string.IsNullOrEmpty(command.BrowserBinding)
            || !CryptographicOperations.FixedTimeEquals(StartExternalSignInHandler.Hash(command.BrowserBinding), consumed.BrowserBindingHash))
        {
            return await FailAsync("binding-mismatch", providerId, cancellationToken);
        }

        if (command.ProviderReportedError)
        {
            return await FailAsync("provider-error", providerId, cancellationToken);
        }

        if (string.IsNullOrEmpty(command.Code))
        {
            return await FailAsync("missing-code", providerId, cancellationToken);
        }

        OidcIdentity identity;
        try
        {
            identity = await provider.ExchangeCodeAsync(
                providerId, command.RedirectUri, command.Code, consumed.CodeVerifier, consumed.Nonce, cancellationToken);
        }
        catch (OidcProviderException ex)
        {
            return await FailAsync(ex.Failure switch
            {
                OidcProviderFailure.Unavailable => "provider-unavailable",
                OidcProviderFailure.InvalidToken => "token-invalid",
                _ => "exchange-rejected",
            }, providerId, cancellationToken);
        }

        await using var signInScope = await unitOfWork.BeginAsync(cancellationToken);
        var now = time.GetUtcNow();
        var link = await links.FindByIssuerAndSubjectAsync(identity.Issuer, identity.Subject, cancellationToken);
        if (link is null || !string.Equals(link.ProviderId, providerId, StringComparison.Ordinal))
        {
            return await FailInScopeAsync("unlinked", providerId, signInScope, cancellationToken);
        }

        var binding = await members.GetSessionBindingAsync(link.AccountId, cancellationToken);
        if (binding is null || binding.Status != MembershipStatus.Active)
        {
            return await FailInScopeAsync("membership-inactive", providerId, signInScope, cancellationToken);
        }

        if (command.PresentedTokenHash is { } presented)
        {
            var presentedSession = await sessions.FindByTokenHashAsync(presented, cancellationToken);
            if (presentedSession is { EndedAt: null })
            {
                await sessions.EndAsync(presentedSession.SessionId, "replaced", now, cancellationToken);
            }
        }

        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var created = await sessions.CreateAsync(
            link.AccountId, StartExternalSignInHandler.Hash(rawToken), binding.SecurityStamp, now, cancellationToken);
        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "session.external-sign-in",
                Action = "external-sign-in",
                Outcome = AuditOutcome.Succeeded,
                Resource = new AuditResource("session", created.SessionId.ToString()),
                ActorOverride = new AuditActorOverride(AuditActorKind.Member, link.AccountId),
                Details = new Dictionary<string, string> { ["provider"] = providerId },
            },
            cancellationToken);
        await signInScope.CommitAsync(cancellationToken);
        return new SignedInSession(created.SessionId, rawToken, created.IdleExpiresAt, created.AbsoluteExpiresAt, false, link.AccountId);
    }

    private async Task<OperationResult<SignedInSession>> FailAsync(string cause, string? providerId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        return await FailInScopeAsync(cause, providerId, scope, cancellationToken);
    }

    private async Task<OperationResult<SignedInSession>> FailInScopeAsync(
        string cause, string? providerId, IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string> { ["cause"] = cause };
        if (providerId is not null)
        {
            details["provider"] = providerId;
        }

        await audit.RecordAsync(
            new AuditEvent
            {
                EventType = "session.external-sign-in-failed",
                Action = "external-sign-in",
                Outcome = AuditOutcome.Failed,
                Resource = new AuditResource("session", null),
                ActorOverride = new AuditActorOverride(AuditActorKind.Anonymous, null),
                Details = details,
            },
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return OperationFailure.SignInFailed();
    }
}
