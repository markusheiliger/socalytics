using System.Text;
using System.Text.Json;

namespace SocAlytics.Platform.Domain.Registry;

/// <summary>A refused fact payload schema map: a named code and the kind it concerns (empty for the whole map).</summary>
public sealed record FactPayloadSchemaViolation(string Code, string Kind);

/// <summary>The Domain rules for the fact payload schemas an Analyst manifest declares.</summary>
public static class FactPayloadSchemaLimits
{
    public const int MaxKinds = 64;
    public const int MaxSchemaBytes = 16 * 1024;
    public const int MaxDepth = 8;
    public const int MaxNodes = 256;

    public const string TooManyKinds = "payload-schemas-too-many-kinds";
    public const string InvalidKind = "payload-schema-invalid-kind";
    public const string TooLarge = "payload-schema-too-large";
    public const string RootNotObject = "payload-schema-root-not-object";
    public const string NotASchema = "payload-schema-not-a-schema";
    public const string UnknownKeyword = "payload-schema-unknown-keyword";
    public const string ExternalRef = "payload-schema-external-ref";
    public const string UnresolvedRef = "payload-schema-unresolved-ref";
    public const string CyclicRef = "payload-schema-cyclic-ref";
    public const string TooDeep = "payload-schema-too-deep";
    public const string TooManyNodes = "payload-schema-too-many-nodes";

    private const string RefPrefix = "#/$defs/";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "enum", "const", "properties", "required", "additionalProperties", "items", "minItems", "maxItems",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "$defs", "$ref",
        "title", "description",
    };

    /// <summary>Returns the first violation of the map, or null when every schema is within the rules.</summary>
    public static FactPayloadSchemaViolation? Check(IReadOnlyDictionary<string, JsonElement> schemas)
    {
        if (schemas.Count > MaxKinds)
        {
            return new(TooManyKinds, "");
        }

        foreach (var (kind, schema) in schemas.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var code = CheckSchema(kind, schema);
            if (code is not null)
            {
                return new(code, kind);
            }
        }

        return null;
    }

    /// <summary>The <c>sha-256:&lt;hex&gt;</c> digest of the canonical JSON of the schema map.</summary>
    public static string Digest(IReadOnlyDictionary<string, JsonElement> schemas) =>
        ContentDigest.Of(JsonSerializer.Serialize(schemas)).ToString();

    private static string? CheckSchema(string kind, JsonElement schema)
    {
        if (kind.Length is < 1 or > 100)
        {
            return InvalidKind;
        }

        if (Encoding.UTF8.GetByteCount(CanonicalJson.Canonicalize(schema)) > MaxSchemaBytes)
        {
            return TooLarge;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return NotASchema;
        }

        if (!schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() != "object")
        {
            return RootNotObject;
        }

        var defs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (schema.TryGetProperty("$defs", out var rootDefs) && rootDefs.ValueKind == JsonValueKind.Object)
        {
            foreach (var def in rootDefs.EnumerateObject())
            {
                defs[def.Name] = def.Value;
            }
        }

        var nodes = 0;
        var refs = new List<string>();
        var code = Walk(schema, 1, ref nodes, refs);
        if (code is not null)
        {
            return code;
        }

        if (refs.Any(r => !defs.ContainsKey(r)))
        {
            return UnresolvedRef;
        }

        return HasCycle(defs) ? CyclicRef : null;
    }

    private static string? Walk(JsonElement schema, int depth, ref int nodes, List<string> refs)
    {
        if (depth > MaxDepth)
        {
            return TooDeep;
        }

        if (++nodes > MaxNodes)
        {
            return TooManyNodes;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return NotASchema;
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (!Keywords.Contains(property.Name))
            {
                return UnknownKeyword;
            }

            string? code = null;
            switch (property.Name)
            {
                case "$ref":
                    if (property.Value.ValueKind != JsonValueKind.String
                        || property.Value.GetString() is not { } target
                        || !target.StartsWith(RefPrefix, StringComparison.Ordinal)
                        || target.Length == RefPrefix.Length
                        || target.IndexOf('/', RefPrefix.Length) >= 0)
                    {
                        return ExternalRef;
                    }

                    refs.Add(target[RefPrefix.Length..]);
                    break;
                case "items":
                    code = Walk(property.Value, depth + 1, ref nodes, refs);
                    break;
                case "additionalProperties" when property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    code = Walk(property.Value, depth + 1, ref nodes, refs);
                    break;
                case "properties" or "$defs":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        return NotASchema;
                    }

                    foreach (var child in property.Value.EnumerateObject())
                    {
                        code = Walk(child.Value, depth + 1, ref nodes, refs);
                        if (code is not null)
                        {
                            break;
                        }
                    }

                    break;
            }

            if (code is not null)
            {
                return code;
            }
        }

        return null;
    }

    private static bool HasCycle(Dictionary<string, JsonElement> defs)
    {
        var edges = defs.ToDictionary(d => d.Key, d => CollectRefs(d.Value), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        bool Visit(string name)
        {
            if (state.TryGetValue(name, out var s))
            {
                return s == 1;
            }

            state[name] = 1;
            foreach (var next in edges[name])
            {
                if (edges.ContainsKey(next) && Visit(next))
                {
                    return true;
                }
            }

            state[name] = 2;
            return false;
        }

        return edges.Keys.Any(Visit);
    }

    private static List<string> CollectRefs(JsonElement schema)
    {
        var refs = new List<string>();
        var stack = new Stack<JsonElement>();
        stack.Push(schema);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in current.EnumerateObject())
            {
                if (property.Name == "$ref" && property.Value.GetString() is { } target)
                {
                    refs.Add(target[RefPrefix.Length..]);
                }
                else if (property.Name is "properties" or "$defs")
                {
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        stack.Push(child.Value);
                    }
                }
                else if (property.Name is "items" or "additionalProperties")
                {
                    stack.Push(property.Value);
                }
            }
        }

        return refs;
    }
}
