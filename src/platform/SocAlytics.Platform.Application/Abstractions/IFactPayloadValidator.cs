using System.Text.Json;

namespace SocAlytics.Platform.Application.Abstractions;

public enum FactPayloadValidationOutcome
{
    Valid,
    Invalid,
    KindUndeclared,
    BoundReached,
}

/// <summary>One fact of a result in result order: its kind and its payload.</summary>
public sealed record FactPayloadCandidate(string Kind, JsonElement Payload);

/// <summary>
/// The verdict for one result. For <see cref="FactPayloadValidationOutcome.Valid" /> every locator part is empty.
/// Otherwise <see cref="Kind" />, <see cref="FactIndex" /> (zero-based position in result order), and
/// <see cref="Pointer" /> (JSON pointer of the first violation, empty for an undeclared kind or a reached bound)
/// describe the first violation, and <see cref="ViolationCount" /> counts the nonconforming facts examined.
/// No payload value is ever part of the verdict.
/// </summary>
public sealed record FactPayloadValidationResult(
    FactPayloadValidationOutcome Outcome,
    string? Kind,
    int? FactIndex,
    string? Pointer,
    int ViolationCount)
{
    private static readonly FactPayloadValidationResult ValidResult = new(FactPayloadValidationOutcome.Valid, null, null, null, 0);

    public bool IsValid => Outcome == FactPayloadValidationOutcome.Valid;

    public static FactPayloadValidationResult Success() => ValidResult;

    public static FactPayloadValidationResult Invalid(string kind, int factIndex, string pointer, int violationCount) =>
        new(FactPayloadValidationOutcome.Invalid, kind, factIndex, pointer, violationCount);

    public static FactPayloadValidationResult KindUndeclared(string kind, int factIndex, int violationCount) =>
        new(FactPayloadValidationOutcome.KindUndeclared, kind, factIndex, string.Empty, violationCount);

    public static FactPayloadValidationResult BoundReached(string? kind, int? factIndex, int violationCount) =>
        new(FactPayloadValidationOutcome.BoundReached, kind, factIndex, string.Empty, violationCount);
}

/// <summary>
/// Validates the fact payloads of one result against the fact payload schemas pinned to the node
/// (kind to JSON Schema), without asserting formats.
/// </summary>
public interface IFactPayloadValidator
{
    /// <summary>The time bound after which validation of one result stops with <see cref="FactPayloadValidationOutcome.BoundReached" />.</summary>
    TimeSpan MaxDuration { get; }

    /// <summary>
    /// Compiles the pinned schemas once and validates every fact in result order, within <see cref="MaxDuration" />.
    /// A fact whose kind has no pinned schema is <see cref="FactPayloadValidationOutcome.KindUndeclared" />; a payload
    /// deeper than the instance depth limit is <see cref="FactPayloadValidationOutcome.BoundReached" />.
    /// </summary>
    FactPayloadValidationResult Validate(
        IReadOnlyDictionary<string, JsonElement> schemasByKind,
        IReadOnlyList<FactPayloadCandidate> facts);
}
