using System.Text.Json;
using System.Text.Json.Serialization;
using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Platform;

public sealed record StampDiscovery(
    string StampId, bool SoftwareKeysAllowed, string PairingEndpoint, string ActivationEndpoint, string TokenEndpoint,
    IReadOnlyList<string>? SoftwareKeysAllowedFor = null);

public sealed record DeviceMetadata(
    string Os, string ClaimedKeyProtection, string OsVersion, string Architecture, string ManagerVersion, string? HostLabel = null,
    string? ClaimedApplicationProfile = null);

public sealed record PairingRequest(string StampId, JsonElement PublicKey, string Nonce, DeviceMetadata DeviceMetadata);

public sealed record PairingIssued(
    string PairingCode, string DeviceFingerprint, string PollingHandle, string VerificationUri,
    DateTimeOffset ExpiresAt, double PollingIntervalSeconds, string StampId);

public sealed record PollingHandleRequest(string PollingHandle);

public sealed record PairingStatus(
    string Status, double PollingIntervalSeconds, DateTimeOffset? ExpiresAt = null,
    string? Challenge = null, DateTimeOffset? ChallengeExpiresAt = null);

public sealed record ActivationRequest(string PollingHandle, string Proof);

public sealed record ActivationResult(string ManagerId, string StampId, string TokenEndpoint, string KeyThumbprint);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

public sealed record OAuthError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription = null,
    [property: JsonPropertyName("registration_state")] string? RegistrationState = null);

public sealed record OwnRegistration(
    string ManagerId, string StampId, string State, string ClubName, string KeyThumbprint,
    DateTimeOffset ActivatedAt, double? StatusCheckIntervalSeconds = null,
    bool? ReportAccepted = null, IReadOnlyList<ReportViolation>? ReportViolations = null);

public sealed record ReportViolation(string Field, string Code);

public sealed record StatusReport(
    string OperatingState, DateTimeOffset? PausedUntil, string? RuntimeUnavailableReason,
    int ActiveAnalysts, int MaxAnalysts, string ManagerVersion);

public sealed record UnregistrationResult(string ManagerId, string State);

public sealed record Problem(
    string Type, string Title, int Status, string Code, string CorrelationId,
    string? Detail = null, string? Instance = null, string? RegistrationState = null);

public static class PlatformJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
