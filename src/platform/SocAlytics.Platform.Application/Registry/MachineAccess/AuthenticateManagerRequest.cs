using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.MachineAccess;

public sealed record AuthenticateManagerCommand(string? AccessToken, string? DPoPProof, string HttpMethod, string RequestPath);

public sealed record ManagerPrincipalData(Guid ManagerId, Guid RegistrationId);

public sealed record ManagerAuthenticationResult(ManagerPrincipalData? Principal, string? Error)
{
    public const string InvalidToken = "invalid_token";
    public const string InvalidProof = "invalid_dpop_proof";

    public bool Succeeded => Principal is not null;

    public static ManagerAuthenticationResult Failed(string error) => new(null, error);
}

public sealed class AuthenticateManagerRequestHandler(
    IUnitOfWork unitOfWork,
    IManagerAccessTokenStore tokens,
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
}
