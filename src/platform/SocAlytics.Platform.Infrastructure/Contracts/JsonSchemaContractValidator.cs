using System.Text.Json;
using Json.Schema;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Infrastructure.Contracts;

internal sealed class JsonSchemaContractValidator : IContractValidator
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    public JsonSchemaContractValidator()
    {
        var registry = new SchemaRegistry { Fetch = (_, _) => null };
        var assembly = typeof(JsonSchemaContractValidator).Assembly;
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith("contracts/", StringComparison.Ordinal) && n.EndsWith(".schema.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var schema = JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = registry });
            if (schema.BaseUri is { } id)
            {
                _schemas[id.AbsoluteUri] = schema;
            }
        }

        // Reference resolution happens lazily on first evaluation and is not thread-safe, so resolve it up front.
        using var probe = JsonDocument.Parse("null");
        foreach (var schema in _schemas.Values)
        {
            schema.Evaluate(probe.RootElement, Options);
        }
    }

    public ContractValidationResult Validate(string schemaId, JsonElement payload)
    {
        if (!_schemas.TryGetValue(schemaId, out var schema))
        {
            throw new ArgumentException($"Unknown contract schema '{schemaId}'.", nameof(schemaId));
        }

        var result = schema.Evaluate(payload, Options);
        if (result.IsValid)
        {
            return ContractValidationResult.Success();
        }

        var errors = new List<ContractValidationError>();
        foreach (var detail in result.Details ?? [])
        {
            if (detail.Errors is null)
            {
                continue;
            }

            foreach (var keyword in detail.Errors.Keys)
            {
                errors.Add(new ContractValidationError(detail.InstanceLocation.ToString(), keyword.Length == 0 ? "false" : keyword));
            }
        }

        if (errors.Count == 0)
        {
            errors.Add(new ContractValidationError(string.Empty, "schema"));
        }

        return ContractValidationResult.Failure(errors);
    }
}
