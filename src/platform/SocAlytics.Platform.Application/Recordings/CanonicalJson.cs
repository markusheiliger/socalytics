using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings;

/// <summary>Deterministic UTF-8 JSON: properties in ordinal order, no whitespace.</summary>
public static class CanonicalJson
{
    public static byte[] Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, node);
        }

        return stream.ToArray();
    }

    public static Sha256Digest Digest(JsonNode? node) => Sha256Digest.FromBytes(SHA256.HashData(Serialize(node)));

    /// <summary>Digest over the operation, its route targets, and the normalized body (including every part digest).</summary>
    public static Sha256Digest Digest(RecordingRetryOperation operation, IReadOnlyDictionary<string, string> routeTargets, JsonNode? body)
    {
        ArgumentNullException.ThrowIfNull(routeTargets);
        var targets = new JsonObject();
        foreach (var (key, value) in routeTargets)
        {
            targets[key] = value;
        }

        return Digest(new JsonObject
        {
            ["operation"] = operation.ToWireValue(),
            ["route"] = targets,
            ["body"] = body?.DeepClone(),
        });
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
