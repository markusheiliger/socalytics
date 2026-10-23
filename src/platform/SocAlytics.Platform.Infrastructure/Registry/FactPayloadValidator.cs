using System.Diagnostics;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Results;

namespace SocAlytics.Platform.Infrastructure.Registry;

/// <summary>
/// JSON Schema 2020-12 validation of fact payloads against the schemas pinned to a node. Formats are not asserted,
/// instance depth is limited, the work is bounded by <see cref="MaxDuration" />, payloads are never modified, and
/// nothing is retained after <see cref="Validate" /> returns. Callers validate only payloads that are already inside
/// the per-result facts size limit.
/// </summary>
internal sealed class FactPayloadValidator : IFactPayloadValidator
{
    public const int MaxInstanceDepth = 32;

    private static readonly EvaluationOptions EvaluationOptions = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = false,
    };

    private readonly IOptions<AnalysisResultOptions> _options;

    public FactPayloadValidator(IOptions<AnalysisResultOptions> options)
    {
        _options = options;
    }

    public TimeSpan MaxDuration => _options.Value.PayloadValidation.MaxDuration;

    public FactPayloadValidationResult Validate(
        IReadOnlyDictionary<string, JsonElement> schemasByKind,
        IReadOnlyList<FactPayloadCandidate> facts)
    {
        var started = Stopwatch.GetTimestamp();
        var maxDuration = MaxDuration;
        var compiled = new Dictionary<string, JsonSchema?>(StringComparer.Ordinal);
        FactPayloadValidationResult? first = null;
        var violations = 0;

        for (var index = 0; index < facts.Count; index++)
        {
            var fact = facts[index];
            if (Stopwatch.GetElapsedTime(started) > maxDuration || ExceedsDepth(fact.Payload))
            {
                return first is null
                    ? FactPayloadValidationResult.BoundReached(fact.Kind, index, violations)
                    : first with { ViolationCount = violations };
            }

            if (!compiled.TryGetValue(fact.Kind, out var schema))
            {
                schema = schemasByKind.TryGetValue(fact.Kind, out var raw) ? Compile(raw) : null;
                compiled[fact.Kind] = schema;
            }

            if (schema is null)
            {
                violations++;
                first ??= FactPayloadValidationResult.KindUndeclared(fact.Kind, index, 0);
                continue;
            }

            var evaluation = schema.Evaluate(fact.Payload, EvaluationOptions);
            if (evaluation.IsValid)
            {
                continue;
            }

            violations++;
            first ??= FactPayloadValidationResult.Invalid(fact.Kind, index, FirstViolationPointer(evaluation), 0);
        }

        return first is null
            ? FactPayloadValidationResult.Success()
            : first with { ViolationCount = violations };
    }

    private static JsonSchema Compile(JsonElement raw)
    {
        // Only local references exist in pinned schemas, so nothing is ever fetched.
        var schema = JsonSchema.FromText(raw.GetRawText(), new BuildOptions { SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => null } });

        // Reference resolution happens lazily on first evaluation, so resolve it before the timed work.
        using var probe = JsonDocument.Parse("null");
        schema.Evaluate(probe.RootElement, EvaluationOptions);
        return schema;
    }

    private static string FirstViolationPointer(EvaluationResults evaluation)
    {
        foreach (var detail in evaluation.Details ?? [])
        {
            if (!detail.IsValid && detail.Errors is { Count: > 0 })
            {
                var pointer = detail.InstanceLocation.ToString();

                // A refused additional property is reported at its object, as the Python SDK does.
                if (detail.EvaluationPath.ToString().EndsWith("/additionalProperties", StringComparison.Ordinal))
                {
                    pointer = pointer[..pointer.LastIndexOf('/')];
                }

                return pointer;
            }
        }

        return string.Empty;
    }

    private static bool ExceedsDepth(JsonElement payload)
    {
        if (payload.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return false;
        }

        var stack = new Stack<(JsonElement Element, int Depth)>();
        stack.Push((payload, 1));
        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (depth > MaxInstanceDepth)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        stack.Push((property.Value, depth + 1));
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        stack.Push((item, depth + 1));
                    }
                }
            }
        }

        return false;
    }
}
