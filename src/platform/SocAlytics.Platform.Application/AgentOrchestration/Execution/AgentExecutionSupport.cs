using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public static class AgentExecutionCodes
{
    public const string ClaimObsolete = "claim-obsolete";
    public const string OperationIdReused = "operation-id-reused";
    public const string AttemptStale = "agent-attempt-stale";
    public const string DispositionField = "disposition";
}

internal static class AgentExecutionSupport
{
    private static readonly JsonSerializerOptions PolicyOptions = new(JsonSerializerDefaults.Web);

    // The invocation's stored policy decides, so later configuration changes never alter a submitted invocation.
    public static ExecutionPolicy ResolvePolicy(string executionPolicyJson, AgentOrchestrationOptions current)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<ExecutionPolicy>(executionPolicyJson, PolicyOptions);
            if (stored is not null)
            {
                return stored;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentOutOfRangeException)
        {
        }

        return current.Execution.ToPolicy();
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string FencingTokenText(long fencingToken) => fencingToken.ToString(CultureInfo.InvariantCulture);

    public static OperationFailure Stale(AgentStaleDisposition disposition) =>
        new(
            OperationFailureKind.Conflict,
            AgentExecutionCodes.AttemptStale,
            [new FieldViolation(AgentExecutionCodes.DispositionField, AgentPersistedNames.ToPersistedName(disposition))],
            null);
}
