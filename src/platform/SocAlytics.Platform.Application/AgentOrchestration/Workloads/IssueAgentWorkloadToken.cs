using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Abstractions;

namespace SocAlytics.Platform.Application.AgentOrchestration.Workloads;

public sealed record IssueAgentWorkloadTokenCommand(
    string? GrantType, string? ClientAssertionType, string? ClientAssertion, string? ClientId, string? Scope, string? DPoPProof);

public sealed record AgentWorkloadTokenResult(bool Succeeded, string? Error, string? AccessToken = null, int ExpiresIn = 0)
{
    public const string Scope = "agent-workload";

    public static AgentWorkloadTokenResult Failed(string error) => new(false, error);
}

/// <summary>Workload token and access token helpers shared by issuance and request authentication.</summary>
internal static class AgentWorkloadCredentials
{
    public static string HashToken(IRegistrySecretGenerator secrets, string token) =>
        Convert.ToHexString(secrets.Hash(token)).ToLowerInvariant();

    public static AgentOrchestrationOptions.WorkloadClientOptions? FindClient(AgentOrchestrationOptions options, string? clientId) =>
        string.IsNullOrEmpty(clientId)
            ? null
            : options.Workloads.Clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));

    // RFC 7638 thumbprint of an EC public JWK, in the form the DPoP proof validator reports; null when the JWK is unusable.
    public static string? Thumbprint(string publicJwk)
    {
        try
        {
            using var document = JsonDocument.Parse(publicJwk);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "kty", out var kty) || kty != "EC"
                || !TryString(root, "crv", out var crv) || crv != "P-256"
                || !TryString(root, "x", out var x) || !TryString(root, "y", out var y))
            {
                return null;
            }

            var canonical = JsonSerializer.Serialize(new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["crv"] = crv!,
                ["kty"] = kty!,
                ["x"] = x!,
                ["y"] = y!,
            });
            return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrEmpty(value);
    }
}

public sealed class IssueAgentWorkloadTokenHandler(
    IUnitOfWork unitOfWork,
    IAgentWorkloadTokenStore tokens,
    IRegistrySecretGenerator secrets,
    IManagerProofValidator validator,
    IAuditTrail audit,
    IOptions<StampOptions> stamp,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    public async Task<AgentWorkloadTokenResult> HandleAsync(IssueAgentWorkloadTokenCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.GrantType) || string.IsNullOrEmpty(command.ClientAssertionType)
            || string.IsNullOrEmpty(command.ClientAssertion) || string.IsNullOrEmpty(command.ClientId))
        {
            return AgentWorkloadTokenResult.Failed("invalid_request");
        }

        if (command.GrantType != "client_credentials")
        {
            return AgentWorkloadTokenResult.Failed("unsupported_grant_type");
        }

        if (command.ClientAssertionType != AssertionType)
        {
            return AgentWorkloadTokenResult.Failed("invalid_request");
        }

        if (command.Scope is not null && command.Scope != AgentWorkloadTokenResult.Scope)
        {
            return AgentWorkloadTokenResult.Failed("invalid_scope");
        }

        var current = options.CurrentValue;
        var client = AgentWorkloadCredentials.FindClient(current, command.ClientId);
        var expectedThumbprint = client is { Enabled: true } ? AgentWorkloadCredentials.Thumbprint(client.PublicJwk) : null;

        var error = "invalid_client";
        string? reason = null;
        if (client is null)
        {
            reason = "unknown-client";
        }
        else if (!client.Enabled)
        {
            reason = "client-disabled";
        }
        else if (expectedThumbprint is null)
        {
            reason = "client-key-invalid";
        }
        else if (string.IsNullOrEmpty(stamp.Value.Id))
        {
            reason = "stamp-mismatch";
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (reason is null)
        {
            var dpop = ProofValidationResult.Refused(ProofRefusalReasons.Invalid);
            if (!string.IsNullOrEmpty(command.DPoPProof))
            {
                dpop = await validator.ValidateDPoPProofAsync(
                    command.DPoPProof, "POST", stamp.Value.WorkloadTokenUri.AbsolutePath, null, false, cancellationToken);
            }

            if (!dpop.Succeeded || dpop.KeyThumbprint != expectedThumbprint)
            {
                error = "invalid_dpop_proof";
                reason = dpop.RefusalReason == ProofRefusalReasons.Replayed ? ProofRefusalReasons.Replayed : ProofRefusalReasons.Invalid;
            }
            else
            {
                var assertion = await validator.ValidateClientAssertionAsync(
                    command.ClientAssertion, command.ClientId, client!.PublicJwk, cancellationToken, stamp.Value.WorkloadTokenUri);
                if (!assertion.Succeeded)
                {
                    reason = assertion.RefusalReason == ProofRefusalReasons.Replayed ? ProofRefusalReasons.Replayed : ProofRefusalReasons.Invalid;
                }
            }
        }

        if (reason is not null)
        {
            await audit.RecordAsync(AgentOrchestrationAuditEvents.WorkloadCredentialRefused(command.ClientId, reason), cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return AgentWorkloadTokenResult.Failed(error);
        }

        var now = time.GetUtcNow();
        var lifetime = current.Workloads.TokenLifetime;
        var token = secrets.NewAccessToken();
        await tokens.InsertAsync(
            new AgentWorkloadAccessToken(
                AgentWorkloadCredentials.HashToken(secrets, token), command.ClientId, expectedThumbprint!, now, now + lifetime),
            cancellationToken);
        await audit.RecordAsync(AgentOrchestrationAuditEvents.WorkloadCredentialIssued(command.ClientId), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new AgentWorkloadTokenResult(true, null, token, (int)lifetime.TotalSeconds);
    }
}
