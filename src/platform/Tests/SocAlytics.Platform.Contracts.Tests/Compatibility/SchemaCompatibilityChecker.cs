using System.Text.Json.Nodes;

namespace SocAlytics.Platform.Contracts.Tests.Compatibility;

/// <summary>
/// Rule-based structural diff of a released schema (baseline) against its current revision.
/// Every document accepted by the baseline must remain acceptable to the current schema.
/// </summary>
public sealed class SchemaCompatibilityChecker
{
    private static readonly string[] LowerBounds = ["minimum", "exclusiveMinimum", "minLength", "minItems", "minProperties", "minContains"];
    private static readonly string[] UpperBounds = ["maximum", "exclusiveMaximum", "maxLength", "maxItems", "maxProperties", "maxContains"];
    private static readonly string[] SubschemaKeywords = ["items", "additionalProperties", "unevaluatedProperties", "unevaluatedItems", "contains", "propertyNames"];

    private readonly IReadOnlyDictionary<string, JsonNode> _baselineDocuments;
    private readonly IReadOnlyDictionary<string, JsonNode> _currentDocuments;
    private readonly List<string> _violations = [];
    private readonly HashSet<string> _visited = [];

    private SchemaCompatibilityChecker(IReadOnlyDictionary<string, JsonNode> baselineDocuments, IReadOnlyDictionary<string, JsonNode> currentDocuments)
    {
        _baselineDocuments = baselineDocuments;
        _currentDocuments = currentDocuments;
    }

    /// <summary>Checks a standalone baseline and revision (no cross-document references).</summary>
    public static IReadOnlyList<string> Check(JsonNode baseline, JsonNode current) =>
        Check(baseline, current, new Dictionary<string, JsonNode>(), new Dictionary<string, JsonNode>());

    /// <param name="baselineDocuments">Baseline registry for one released version, keyed by declared <c>$id</c>.</param>
    /// <param name="currentDocuments">Current schemas keyed by <c>$id</c>; used when the baseline has no copy of a referenced schema.</param>
    public static IReadOnlyList<string> Check(
        JsonNode baseline,
        JsonNode current,
        IReadOnlyDictionary<string, JsonNode> baselineDocuments,
        IReadOnlyDictionary<string, JsonNode> currentDocuments)
    {
        var checker = new SchemaCompatibilityChecker(baselineDocuments, currentDocuments);
        checker.Compare(baseline, current, Id(baseline), Id(current), "#");
        return checker._violations;
    }

    private static string? Id(JsonNode node) => (node as JsonObject)?["$id"]?.GetValue<string>();

    private void Compare(JsonNode? b, JsonNode? c, string? bBase, string? cBase, string path)
    {
        if (b is null)
        {
            return;
        }

        if (c is null)
        {
            _violations.Add($"{path}: subschema was removed");
            return;
        }

        if (b is JsonValue bv && bv.TryGetValue<bool>(out var bb))
        {
            if (bb && c is JsonValue cv && cv.TryGetValue<bool>(out var cb) && !cb)
            {
                _violations.Add($"{path}: schema was closed to every value");
            }

            return;
        }

        if (c is JsonValue cv2 && cv2.TryGetValue<bool>(out var cb2))
        {
            if (!cb2)
            {
                _violations.Add($"{path}: schema was closed to every value");
            }

            return;
        }

        var bo = (JsonObject)b;
        var co = (JsonObject)c;

        if (bo["$ref"] is not null || co["$ref"] is not null)
        {
            var (rb, rbBase) = Resolve(bo, bBase, _baselineDocuments, _currentDocuments);
            var (rc, rcBase) = Resolve(co, cBase, _currentDocuments, _currentDocuments);
            // The comparison of a resolved pair does not depend on the path, so recursive references stop here.
            var key = $"{rbBase}|{rcBase}|{bo["$ref"]}|{co["$ref"]}";
            if (rb is not null && rc is not null && _visited.Add(key))
            {
                Compare(rb, rc, rbBase, rcBase, path + "/$ref");
            }
            else if (rb is null || rc is null)
            {
                _violations.Add($"{path}: reference {bo["$ref"] ?? co["$ref"]} could not be resolved");
            }
        }

        CompareType(bo, co, path);
        CompareValues(bo, co, path);
        CompareBounds(bo, co, path);
        CompareProperties(bo, co, bBase, cBase, path);

        foreach (var keyword in SubschemaKeywords)
        {
            if (bo[keyword] is { } bs)
            {
                if (co[keyword] is { } cs)
                {
                    Compare(bs, cs, bBase, cBase, $"{path}/{keyword}");
                }
                else if (keyword is "contains" or "propertyNames")
                {
                    continue;
                }
            }
            else if (co[keyword] is JsonValue closed && closed.TryGetValue<bool>(out var open) && !open)
            {
                _violations.Add($"{path}: {keyword} was newly closed");
            }
            else if (keyword is "items" or "contains" or "propertyNames" && co[keyword] is JsonObject)
            {
                _violations.Add($"{path}: {keyword} was newly constrained");
            }
        }

        if (bo["prefixItems"] is JsonArray bp && co["prefixItems"] is JsonArray cp)
        {
            for (var i = 0; i < bp.Count && i < cp.Count; i++)
            {
                Compare(bp[i], cp[i], bBase, cBase, $"{path}/prefixItems/{i}");
            }
        }

        CompareComposition(bo, co, bBase, cBase, path);
    }

    private void CompareType(JsonObject bo, JsonObject co, string path)
    {
        var ct = Types(co["type"]);
        if (ct is null)
        {
            return;
        }

        var bt = Types(bo["type"]);
        if (bt is null || !bt.All(t => ct.Contains(t) || (t == "integer" && ct.Contains("number"))))
        {
            _violations.Add($"{path}: type was narrowed to [{string.Join(", ", ct)}]");
        }
    }

    private static HashSet<string>? Types(JsonNode? node) => node switch
    {
        null => null,
        JsonArray a => a.Select(n => n!.GetValue<string>()).ToHashSet(),
        _ => [node.GetValue<string>()],
    };

    private void CompareValues(JsonObject bo, JsonObject co, string path)
    {
        if (co["enum"] is JsonArray ce)
        {
            var allowed = bo["enum"] is JsonArray be
                ? be.All(v => ce.Any(x => JsonNode.DeepEquals(v, x)))
                : bo["const"] is { } bc && ce.Any(x => JsonNode.DeepEquals(bc, x));
            if (!allowed)
            {
                _violations.Add($"{path}: enum was narrowed");
            }
        }

        if (co["const"] is { } cc)
        {
            var same = bo["const"] is { } bc2 && JsonNode.DeepEquals(bc2, cc)
                || bo["enum"] is JsonArray { Count: 1 } be1 && JsonNode.DeepEquals(be1[0], cc);
            if (!same)
            {
                _violations.Add($"{path}: const was narrowed");
            }
        }

        foreach (var keyword in new[] { "pattern", "format" })
        {
            if (co[keyword] is { } cv && !JsonNode.DeepEquals(bo[keyword], cv))
            {
                _violations.Add($"{path}: {keyword} was narrowed or changed");
            }
        }
    }

    private void CompareBounds(JsonObject bo, JsonObject co, string path)
    {
        foreach (var keyword in LowerBounds)
        {
            if (co[keyword] is { } cv && (bo[keyword] is not { } bv || cv.GetValue<decimal>() > bv.GetValue<decimal>()))
            {
                _violations.Add($"{path}: {keyword} was tightened");
            }
        }

        foreach (var keyword in UpperBounds)
        {
            if (co[keyword] is { } cv && (bo[keyword] is not { } bv || cv.GetValue<decimal>() < bv.GetValue<decimal>()))
            {
                _violations.Add($"{path}: {keyword} was tightened");
            }
        }
    }

    private void CompareProperties(JsonObject bo, JsonObject co, string? bBase, string? cBase, string path)
    {
        var bRequired = Names(bo["required"]);
        foreach (var name in Names(co["required"]).Where(n => !bRequired.Contains(n)))
        {
            _violations.Add($"{path}: property '{name}' became required");
        }

        if (bo["properties"] is not JsonObject bProps)
        {
            return;
        }

        var cProps = co["properties"] as JsonObject;
        foreach (var (name, bSchema) in bProps)
        {
            if (cProps is null || !cProps.TryGetPropertyValue(name, out var cSchema))
            {
                _violations.Add($"{path}: property '{name}' was removed or renamed");
                continue;
            }

            Compare(bSchema, cSchema, bBase, cBase, $"{path}/properties/{name}");
        }
    }

    private void CompareComposition(JsonObject bo, JsonObject co, string? bBase, string? cBase, string path)
    {
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (bo[keyword] is JsonArray ba && co[keyword] is JsonArray ca)
            {
                if (ca.Count < ba.Count && keyword != "allOf")
                {
                    _violations.Add($"{path}: {keyword} lost alternatives");
                }

                for (var i = 0; i < ba.Count && i < ca.Count; i++)
                {
                    Compare(ba[i], ca[i], bBase, cBase, $"{path}/{keyword}/{i}");
                }
            }
            else if (co[keyword] is JsonArray && bo[keyword] is null && keyword == "allOf")
            {
                _violations.Add($"{path}: allOf was newly added");
            }
        }

        foreach (var keyword in new[] { "then", "else" })
        {
            if (bo[keyword] is { } bs && co[keyword] is { } cs)
            {
                Compare(bs, cs, bBase, cBase, $"{path}/{keyword}");
            }
        }
    }

    private static HashSet<string> Names(JsonNode? node) =>
        node is JsonArray a ? a.Select(n => n!.GetValue<string>()).ToHashSet() : [];

    private static (JsonNode? Node, string? Base) Resolve(
        JsonObject schema,
        string? baseId,
        IReadOnlyDictionary<string, JsonNode> primary,
        IReadOnlyDictionary<string, JsonNode> fallback)
    {
        if (schema["$ref"]?.GetValue<string>() is not { } reference)
        {
            return (schema, baseId);
        }

        var target = baseId is null ? new Uri(reference, UriKind.RelativeOrAbsolute) : new Uri(new Uri(baseId), reference);
        var documentId = target.GetLeftPart(UriPartial.Path);
        var fragment = Uri.UnescapeDataString(target.Fragment.TrimStart('#'));
        if (!primary.TryGetValue(documentId, out var document) && !fallback.TryGetValue(documentId, out document))
        {
            return (null, null);
        }

        var node = document;
        foreach (var segment in fragment.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = segment.Replace("~1", "/").Replace("~0", "~");
            node = node is JsonArray arr ? arr[int.Parse(key)] : node?[key];
        }

        return (node, documentId);
    }
}
