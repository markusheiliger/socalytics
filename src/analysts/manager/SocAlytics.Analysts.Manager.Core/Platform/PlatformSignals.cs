using System.Net;
using System.Text.Json;

namespace SocAlytics.Analysts.Manager.Platform;

public enum PlatformOutcome
{
    Success,
    Inactive,
    Refused,
    Transient,
}

public enum InactiveReason
{
    Revoked,
    Unregistered,
}

public sealed record PlatformSignal(PlatformOutcome Outcome, InactiveReason? Reason = null);

public static class PlatformSignals
{
    public static PlatformSignal ClassifyToken(HttpStatusCode status, string? body)
    {
        if (IsSuccess(status))
        {
            return new(PlatformOutcome.Success);
        }

        if (status == HttpStatusCode.BadRequest && TryParse<OAuthError>(body) is { Error: "invalid_client" } e
            && ParseReason(e.RegistrationState) is { } reason)
        {
            return new(PlatformOutcome.Inactive, reason);
        }

        return Other(status);
    }

    public static PlatformSignal ClassifyResource(HttpStatusCode status, IEnumerable<string> wwwAuthenticate, string? body)
    {
        if (IsSuccess(status))
        {
            return new(PlatformOutcome.Success);
        }

        if (status == HttpStatusCode.Unauthorized
            && wwwAuthenticate.Any(h => h.TrimStart().StartsWith("DPoP", StringComparison.OrdinalIgnoreCase))
            && TryParse<Problem>(body) is { Code: "analyst-manager-registration-inactive" } p
            && p.Type.EndsWith("analyst-manager-registration-inactive", StringComparison.Ordinal)
            && ParseReason(p.RegistrationState) is { } reason)
        {
            return new(PlatformOutcome.Inactive, reason);
        }

        return Other(status);
    }

    private static PlatformSignal Other(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            ? new(PlatformOutcome.Transient)
            : new(PlatformOutcome.Refused);

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static InactiveReason? ParseReason(string? value) => value switch
    {
        "revoked" => InactiveReason.Revoked,
        "unregistered" => InactiveReason.Unregistered,
        _ => null,
    };

    private static T? TryParse<T>(string? body) where T : class
    {
        try
        {
            return string.IsNullOrEmpty(body) ? null : JsonSerializer.Deserialize<T>(body, PlatformJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
