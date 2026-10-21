using System.Text.Json;
using Json.Schema;

namespace SocAlytics.Analysts.Manager.Platform;

/// <summary>Validates a segment media response against the embedded contracts/analysis/segment-media/v1 schema.</summary>
public static class SegmentMediaSchema
{
    public const string SchemaId = "https://socalytics.invalid/contracts/analysis/segment-media/v1/segment-media.schema.json";

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private static readonly Lazy<JsonSchema> Schema = new(Load);

    public static bool IsValid(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            return Schema.Value.Evaluate(document.RootElement, Options).IsValid;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonSchema Load()
    {
        var registry = new SchemaRegistry { Fetch = (_, _) => null };
        var assembly = typeof(SegmentMediaSchema).Assembly;
        JsonSchema? media = null;
        var schemas = new List<JsonSchema>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("contracts/", StringComparison.Ordinal) && n.EndsWith(".schema.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var schema = JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = registry });
            schemas.Add(schema);
            if (schema.BaseUri?.AbsoluteUri == SchemaId)
            {
                media = schema;
            }
        }

        // Reference resolution happens lazily on first evaluation and is not thread-safe, so resolve it up front.
        using var probe = JsonDocument.Parse("null");
        foreach (var schema in schemas)
        {
            schema.Evaluate(probe.RootElement, Options);
        }

        return media ?? throw new InvalidOperationException("The segment-media schema is not embedded.");
    }
}
