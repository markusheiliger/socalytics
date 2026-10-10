using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.MachineAccess;

public sealed record IssueManagerTokenCommand(
    string? GrantType, string? ClientAssertionType, string? ClientAssertion, string? ClientId, string? Scope, string? DPoPProof);

public sealed record ManagerTokenResult(bool Succeeded, string? Error, string? AccessToken = null, int ExpiresIn = 0)
{
    public const string Scope = "analyst-manager";

    public static ManagerTokenResult Failed(string error) => new(false, error);
}

public sealed class IssueManagerTokenHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore registrations,
    IManagerAccessTokenStore tokens,
    IRegistrySecretGenerator secrets,
    IManagerProofValidator validator,
    IAuditTrail audit,
    IOptions<StampOptions> stamp,
    IOptions<RegistryOptions> registry,
    TimeProvider time)
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    public async Task<ManagerTokenResult> HandleAsync(IssueManagerTokenCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.GrantType) || string.IsNullOrEmpty(command.ClientAssertionType)
            || string.IsNullOrEmpty(command.ClientAssertion) || string.IsNullOrEmpty(command.ClientId))
        {
            return ManagerTokenResult.Failed("invalid_request");
        }

        if (command.GrantType != "client_credentials")
        {
            return ManagerTokenResult.Failed("unsupported_grant_type");
        }

        if (command.ClientAssertionType != AssertionType)
        {
            return ManagerTokenResult.Failed("invalid_request");
        }

        if (command.Scope is not null && command.Scope != ManagerTokenResult.Scope)
        {
            return ManagerTokenResult.Failed("invalid_scope");
        }

        if (!Guid.TryParse(command.ClientId, out var managerId))
        {
            return ManagerTokenResult.Failed("invalid_client");
        }

        var now = time.GetUtcNow();
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await registrations.FindByManagerIdAsync(managerId, cancellationToken);
        if (registration is null)
        {
            return ManagerTokenResult.Failed("invalid_client");
        }

        var error = "invalid_client";
        string? reason = null;
        ProofValidationResult dpop = ProofValidationResult.Refused(ProofRefusalReasons.Invalid);
        if (!string.IsNullOrEmpty(command.DPoPProof))
        {
            dpop = await validator.ValidateDPoPProofAsync(
                command.DPoPProof, "POST", stamp.Value.TokenUri.AbsolutePath, null, false, cancellationToken);
        }

        if (!dpop.Succeeded || dpop.KeyThumbprint != registration.KeyThumbprint)
        {
            error = "invalid_dpop_proof";
            reason = dpop.RefusalReason == ProofRefusalReasons.Replayed ? ProofRefusalReasons.Replayed : ProofRefusalReasons.Invalid;
        }
        else
        {
            var assertion = await validator.ValidateClientAssertionAsync(
                command.ClientAssertion, command.ClientId, registration.PublicJwk, cancellationToken);
            if (!assertion.Succeeded)
            {
                reason = assertion.RefusalReason == ProofRefusalReasons.Replayed ? ProofRefusalReasons.Replayed : ProofRefusalReasons.Invalid;
            }
            else if (registration.State is not (RegistrationState.Active or RegistrationState.Unregistering))
            {
                reason = "registration-inactive";
            }
            else if (!string.Equals(registration.StampId, stamp.Value.Id, StringComparison.Ordinal))
            {
                reason = "stamp-mismatch";
            }
        }

        if (reason is not null)
        {
            await audit.RecordAsync(RegistryAuditEvents.CredentialRefused(registration.Id, reason), cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return ManagerTokenResult.Failed(error);
        }

        var lifetime = registry.Value.Tokens.Lifetime;
        var token = secrets.NewAccessToken();
        await tokens.InsertAsync(
            new ManagerAccessToken(secrets.Hash(token), registration.Id, registration.KeyThumbprint, ManagerTokenResult.Scope, now, now + lifetime),
            cancellationToken);
        await audit.RecordAsync(RegistryAuditEvents.CredentialIssued(registration.Id, new Dictionary<string, string>
        {
            ["managerId"] = managerId.ToString(),
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new ManagerTokenResult(true, null, token, (int)lifetime.TotalSeconds);
    }
}
