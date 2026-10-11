using System.Text.Json;

namespace SocAlytics.Platform.Application.Abstractions;

public sealed record ContractValidationError(string Field, string Keyword);

public sealed class ContractValidationResult
{
    private static readonly ContractValidationResult ValidResult = new([]);

    private ContractValidationResult(IReadOnlyList<ContractValidationError> errors) => Errors = errors;

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<ContractValidationError> Errors { get; }

    public static ContractValidationResult Success() => ValidResult;

    public static ContractValidationResult Failure(IReadOnlyList<ContractValidationError> errors) => new(errors);
}

public interface IContractValidator
{
    ContractValidationResult Validate(string schemaId, JsonElement payload);
}
