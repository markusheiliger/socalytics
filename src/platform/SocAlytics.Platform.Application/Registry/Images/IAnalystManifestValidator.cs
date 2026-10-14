using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Images;

public sealed record AnalystManifestValidationResult(
    bool IsValid,
    IReadOnlyList<ContractValidationError> Errors,
    CapabilityDeclaration? Declaration);

/// <summary>Validates an <c>analyst-manifest.json</c> against the released manifest contract.</summary>
public interface IAnalystManifestValidator
{
    /// <summary>Returns the declaration for <paramref name="imageDigest"/> when <paramref name="manifestJson"/> is valid.</summary>
    AnalystManifestValidationResult Validate(string imageDigest, string manifestJson);
}
