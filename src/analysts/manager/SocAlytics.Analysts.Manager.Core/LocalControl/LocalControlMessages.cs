using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SocAlytics.Analysts.Manager.LocalControl;

public static class LocalControlProtocol
{
    public const int Version = 1;

    public const int MaxRequestBytes = 16 * 1024;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record LocalControlRequest(int ProtocolVersion, string Command, JsonObject? Arguments = null);

public sealed record LocalControlError(string Code, string Message, string? Remediation = null);

public sealed record LocalControlResponse(int ProtocolVersion, bool Ok, JsonNode? Result = null, LocalControlError? Error = null)
{
    public static LocalControlResponse Success(JsonNode result) => new(LocalControlProtocol.Version, true, result);

    public static LocalControlResponse Failure(string code, string message) =>
        new(LocalControlProtocol.Version, false, null, new LocalControlError(code, message.Length > 500 ? message[..500] : message));
}
