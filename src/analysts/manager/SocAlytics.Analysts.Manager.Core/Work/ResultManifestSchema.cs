using System.Text.Json;
using Json.Schema;

namespace SocAlytics.Analysts.Manager.Work;

/// <summary>Validates a result manifest against the embedded contracts/analysis/result-manifest/v1 schema.</summary>
public static class ResultManifestSchema
{
    public const string SchemaId = "https://socalytics.invalid/contracts/analysis/result-manifest/v1/result-manifest.schema.json";

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
        var assembly = typeof(ResultManifestSchema).Assembly;
        JsonSchema? manifest = null;
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
                manifest = schema;
            }
        }

        // Reference resolution happens lazily on first evaluation and is not thread-safe, so resolve it up front.
        using var probe = JsonDocument.Parse("null");
        foreach (var schema in schemas)
        {
            schema.Evaluate(probe.RootElement, Options);
        }

        return manifest ?? throw new InvalidOperationException("The result-manifest schema is not embedded.");
    }
}
