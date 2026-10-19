using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;

namespace SocAlytics.Platform.Application.AgentOrchestration.Workloads;

public sealed record AuthenticateAgentWorkloadCommand(string? AccessToken, string? DPoPProof, string HttpMethod, string RequestPath);

public sealed record AgentWorkloadPrincipalData(string ClientId);

public sealed record AgentWorkloadAuthenticationResult(AgentWorkloadPrincipalData? Principal, string? Error)
{
    public const string InvalidToken = "invalid_token";
    public const string InvalidProof = "invalid_dpop_proof";

    public bool Succeeded => Principal is not null;

    public static AgentWorkloadAuthenticationResult Failed(string error) => new(null, error);
}

public sealed class AuthenticateAgentWorkloadRequestHandler(
    IUnitOfWork unitOfWork,
    IAgentWorkloadTokenStore tokens,
    IRegistrySecretGenerator secrets,
    IManagerProofValidator validator,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public async Task<AgentWorkloadAuthenticationResult> HandleAsync(
        AuthenticateAgentWorkloadCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.AccessToken))
        {
            return AgentWorkloadAuthenticationResult.Failed(AgentWorkloadAuthenticationResult.InvalidToken);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var token = await tokens.FindByHashAsync(AgentWorkloadCredentials.HashToken(secrets, command.AccessToken), cancellationToken);

        // The client's current configuration decides on every request, so disabling a client stops its tokens at once.
        var client = token is null ? null : AgentWorkloadCredentials.FindClient(options.CurrentValue, token.WorkloadClientId);
        if (token is null || token.ExpiresAt <= time.GetUtcNow() || client is not { Enabled: true }
            || AgentWorkloadCredentials.Thumbprint(client.PublicJwk) != token.KeyThumbprint)
        {
            return AgentWorkloadAuthenticationResult.Failed(AgentWorkloadAuthenticationResult.InvalidToken);
        }

        if (string.IsNullOrEmpty(command.DPoPProof))
        {
            return AgentWorkloadAuthenticationResult.Failed(AgentWorkloadAuthenticationResult.InvalidProof);
        }

        var proof = await validator.ValidateDPoPProofAsync(
            command.DPoPProof, command.HttpMethod, command.RequestPath, command.AccessToken, true, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return proof.Succeeded && proof.KeyThumbprint == token.KeyThumbprint
            ? new AgentWorkloadAuthenticationResult(new AgentWorkloadPrincipalData(token.WorkloadClientId), null)
            : AgentWorkloadAuthenticationResult.Failed(AgentWorkloadAuthenticationResult.InvalidProof);
    }
}
