using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Registry.Images;

public sealed record RegisterAnalystImageCommand(string? Reference);

public sealed class RegisterAnalystImageResult
{
    private RegisterAnalystImageResult()
    {
    }

    public StoredCapabilityDeclaration? Declaration { get; private init; }

    /// <summary>False when the image was already registered.</summary>
    public bool Created { get; private init; }

    public OperationFailure? Failure { get; private init; }

    public string? RefusalCode { get; private init; }

    public IReadOnlyList<FieldViolation> Details { get; private init; } = [];

    public bool IsRegistered => Declaration is not null;

    public static RegisterAnalystImageResult Registered(StoredCapabilityDeclaration declaration, bool created) =>
        new() { Declaration = declaration, Created = created };

    public static RegisterAnalystImageResult Failed(OperationFailure failure) => new() { Failure = failure };

    public static RegisterAnalystImageResult Refused(string code, IReadOnlyList<FieldViolation>? details = null) =>
        new() { RefusalCode = code, Details = details ?? [] };
}

public sealed class RegisterAnalystImageHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    ICapabilityDeclarationStore store,
    IOciImageReader reader,
    IAnalystManifestValidator validator,
    IAuditTrail audit,
    IRequestContext context,
    TimeProvider time)
{
    private const string Sha256Prefix = "sha256:";
    private const int MaxAuditedReferenceLength = 300;

    private static readonly string[] RequiredLabels =
    [
        "io.socalytics.analyst-manifest.version",
        "io.socalytics.analyst-manifest.sha256",
        "io.socalytics.capability.id",
        "io.socalytics.capability.version",
        "io.socalytics.model.digest",
    ];

    public async Task<RegisterAnalystImageResult> HandleAsync(RegisterAnalystImageCommand command, CancellationToken cancellationToken)
    {
        var reference = command.Reference?.Trim();
        string? referenceDigest;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var decision = await authorizer.AuthorizeClubAsync(
                ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ImageResourceType, null), cancellationToken);
            if (!decision.IsGranted)
            {
                return RegisterAnalystImageResult.Failed(OperationFailure.Forbidden());
            }

            if (string.IsNullOrEmpty(reference))
            {
                return RegisterAnalystImageResult.Failed(OperationFailure.Validation(new FieldViolation("reference", "required")));
            }

            referenceDigest = DigestOf(reference);
            if (referenceDigest is not null && await store.FindAsync(referenceDigest, cancellationToken) is { } existing)
            {
                return RegisterAnalystImageResult.Registered(existing, false);
            }
        }

        var read = await reader.ReadAsync(reference, cancellationToken);
        if (read.IsRefused)
        {
            return await RefuseAsync(reference, referenceDigest, read.RefusalCode!, [], cancellationToken);
        }

        var content = read.Content!;
        var validation = validator.Validate(content.ImageDigest, content.ManifestJson);
        if (!validation.IsValid)
        {
            return await RefuseAsync(
                reference, content.ImageDigest, ImageImportRefusalCodes.ManifestInvalid,
                validation.Errors.Select(e => new FieldViolation(e.Field, e.Keyword)).ToList(), cancellationToken);
        }

        var declaration = validation.Declaration!;
        var labelViolations = CheckLabels(content, declaration, ManifestVersionOf(content.ManifestJson));
        if (labelViolations.Count > 0)
        {
            return await RefuseAsync(reference, content.ImageDigest, ImageImportRefusalCodes.LabelsMismatch, labelViolations, cancellationToken);
        }

        await using var writeScope = await unitOfWork.BeginAsync(cancellationToken);
        var stored = new StoredCapabilityDeclaration(
            declaration, RepositoryOf(reference), Sha256Prefix + content.ManifestSha256, content.ManifestJson,
            "image", time.GetUtcNow(), context.MemberAccountId);
        if (!await store.TryInsertAsync(stored, cancellationToken))
        {
            var concurrent = await store.FindAsync(content.ImageDigest, cancellationToken);
            return concurrent is null
                ? RegisterAnalystImageResult.Failed(OperationFailure.Conflict("analyst-image-conflict"))
                : RegisterAnalystImageResult.Registered(concurrent, false);
        }

        await audit.RecordAsync(RegistryAuditEvents.AnalystImageRegistered(content.ImageDigest, new Dictionary<string, string>
        {
            ["imageDigest"] = content.ImageDigest,
            ["capabilityId"] = declaration.CapabilityId,
            ["capabilityVersion"] = declaration.CapabilityVersion,
            ["modelDigest"] = declaration.ModelDigest,
        }), cancellationToken);
        await writeScope.CommitAsync(cancellationToken);
        return RegisterAnalystImageResult.Registered(stored, true);
    }

    private async Task<RegisterAnalystImageResult> RefuseAsync(
        string reference, string? imageDigest, string code, IReadOnlyList<FieldViolation> details, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var auditDetails = new Dictionary<string, string>
        {
            ["reference"] = reference.Length > MaxAuditedReferenceLength ? reference[..MaxAuditedReferenceLength] : reference,
        };
        if (imageDigest is not null)
        {
            auditDetails["imageDigest"] = imageDigest;
        }

        await audit.RecordAsync(RegistryAuditEvents.AnalystImageRefused(imageDigest, code, auditDetails), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return RegisterAnalystImageResult.Refused(code, details);
    }

    private static List<FieldViolation> CheckLabels(OciImageContent content, Domain.Registry.CapabilityDeclaration declaration, string? manifestVersion)
    {
        var violations = new List<FieldViolation>();
        var expected = new Dictionary<string, string?>
        {
            ["io.socalytics.analyst-manifest.version"] = manifestVersion,
            ["io.socalytics.capability.id"] = declaration.CapabilityId,
            ["io.socalytics.capability.version"] = declaration.CapabilityVersion,
            ["io.socalytics.model.digest"] = declaration.ModelDigest,
        };

        foreach (var label in RequiredLabels)
        {
            if (!content.Labels.TryGetValue(label, out var actual) || string.IsNullOrWhiteSpace(actual))
            {
                violations.Add(new FieldViolation(label, "missing"));
                continue;
            }

            var matches = label == "io.socalytics.analyst-manifest.sha256"
                ? string.Equals(actual.StartsWith(Sha256Prefix, StringComparison.Ordinal) ? actual[Sha256Prefix.Length..] : actual, content.ManifestSha256, StringComparison.OrdinalIgnoreCase)
                : string.Equals(actual, expected[label], StringComparison.Ordinal);
            if (!matches)
            {
                violations.Add(new FieldViolation(label, "mismatch"));
            }
        }

        return violations;
    }

    private static string? ManifestVersionOf(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        return document.RootElement.TryGetProperty("manifestVersion", out var version) && version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
    }

    private static string? DigestOf(string reference)
    {
        var at = reference.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        var digest = reference[(at + 1)..];
        return digest.Length == Sha256Prefix.Length + 64
            && digest.StartsWith(Sha256Prefix, StringComparison.Ordinal)
            && digest.AsSpan(Sha256Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0
                ? digest
                : null;
    }

    private static string RepositoryOf(string reference)
    {
        var name = reference[..reference.LastIndexOf('@')];
        var slash = name.IndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }
}

public sealed class ListAnalystImagesHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, ICapabilityDeclarationStore store)
{
    public async Task<OperationResult<IReadOnlyList<StoredCapabilityDeclaration>>> HandleAsync(
        string? capabilityId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ImageResourceType, null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var all = await store.ListAsync(cancellationToken);
        IReadOnlyList<StoredCapabilityDeclaration> items = string.IsNullOrEmpty(capabilityId)
            ? all
            : all.Where(d => string.Equals(d.Declaration.CapabilityId, capabilityId, StringComparison.Ordinal)).ToList();
        return OperationResult<IReadOnlyList<StoredCapabilityDeclaration>>.Success(items);
    }
}

public sealed class GetAnalystImageHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, ICapabilityDeclarationStore store)
{
    public async Task<OperationResult<StoredCapabilityDeclaration>> HandleAsync(string digest, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ImageResourceType, digest), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var found = await store.FindAsync(digest, cancellationToken);
        return found is null ? OperationFailure.NotFound() : found;
    }
}
