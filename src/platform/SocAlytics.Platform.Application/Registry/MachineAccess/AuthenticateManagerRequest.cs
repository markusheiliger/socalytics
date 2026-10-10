using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.MachineAccess;

public sealed record AuthenticateManagerCommand(string? AccessToken, string? DPoPProof, string HttpMethod, string RequestPath);

public sealed record ManagerPrincipalData(Guid ManagerId, Guid RegistrationId);

public sealed record ManagerAuthenticationResult(ManagerPrincipalData? Principal, string? Error, string? InactiveState = null)
{
    public const string InvalidToken = "invalid_token";
    public const string InvalidProof = "invalid_dpop_proof";

    public bool Succeeded => Principal is not null;

    public static ManagerAuthenticationResult Failed(string error) => new(null, error);

    public static ManagerAuthenticationResult Inactive(string state) => new(null, InvalidToken, state);
}

public sealed class AuthenticateManagerRequestHandler(
    IUnitOfWork unitOfWork,
    IManagerAccessTokenStore tokens,
    IRegistrationStore registrations,
    IRegistrySecretGenerator secrets,
    IManagerProofValidator validator,
    IOptions<StampOptions> stamp,
    TimeProvider time)
{
    public async Task<ManagerAuthenticationResult> HandleAsync(AuthenticateManagerCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.AccessToken))
        {
            return ManagerAuthenticationResult.Failed(ManagerAuthenticationResult.InvalidToken);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var lookup = await tokens.FindByHashAsync(secrets.Hash(command.AccessToken), cancellationToken);
        var registration = lookup?.Registration;
        if (lookup is null && !string.IsNullOrEmpty(command.DPoPProof))
        {
            var inactive = await FindInactiveAsync(command, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            if (inactive is not null)
            {
                return inactive;
            }
        }

        if (lookup is null || registration?.ManagerId is null
            || lookup.Token.ExpiresAt <= time.GetUtcNow()
            || lookup.Token.Scope != ManagerTokenResult.Scope
            || registration.State is not (RegistrationState.Active or RegistrationState.Unregistering)
            || !string.Equals(registration.StampId, stamp.Value.Id, StringComparison.Ordinal)
            || lookup.Token.Jkt != registration.KeyThumbprint)
        {
            return ManagerAuthenticationResult.Failed(ManagerAuthenticationResult.InvalidToken);
        }

        if (string.IsNullOrEmpty(command.DPoPProof))
        {
            return ManagerAuthenticationResult.Failed(ManagerAuthenticationResult.InvalidProof);
        }

        var proof = await validator.ValidateDPoPProofAsync(
            command.DPoPProof, command.HttpMethod, command.RequestPath, command.AccessToken, true, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return proof.Succeeded && proof.KeyThumbprint == lookup.Token.Jkt
            ? new ManagerAuthenticationResult(new ManagerPrincipalData(registration.ManagerId.Value, registration.Id), null)
            : ManagerAuthenticationResult.Failed(ManagerAuthenticationResult.InvalidProof);
    }

    private async Task<ManagerAuthenticationResult?> FindInactiveAsync(AuthenticateManagerCommand command, CancellationToken cancellationToken)
    {
        var proof = await validator.ValidateDPoPProofAsync(
            command.DPoPProof!, command.HttpMethod, command.RequestPath, command.AccessToken, true, cancellationToken);
        if (!proof.Succeeded || proof.KeyThumbprint is null
            || await registrations.FindNonTerminalByThumbprintAsync(proof.KeyThumbprint, cancellationToken) is not null)
        {
            return null;
        }

        var inactive = await registrations.FindLatestInactiveByThumbprintAsync(proof.KeyThumbprint, cancellationToken);
        return inactive is null ? null : ManagerAuthenticationResult.Inactive(inactive.State.ToApiName());
    }
}
