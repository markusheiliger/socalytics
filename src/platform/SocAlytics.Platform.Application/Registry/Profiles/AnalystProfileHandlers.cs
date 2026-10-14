using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Profiles;

public sealed record CreateAnalystProfileCommand(string? ProfileId, string? ImageDigest, AnalystProfileContent? Content);

public sealed class CreateAnalystProfileResult
{
    private CreateAnalystProfileResult()
    {
    }

    public StoredAnalystProfile? Profile { get; private init; }

    /// <summary>False when the same content already exists.</summary>
    public bool Created { get; private init; }

    public OperationFailure? Failure { get; private init; }

    public IReadOnlyList<FieldViolation> Contradictions { get; private init; } = [];

    public static CreateAnalystProfileResult Success(StoredAnalystProfile profile, bool created) =>
        new() { Profile = profile, Created = created };

    public static CreateAnalystProfileResult Failed(OperationFailure failure) => new() { Failure = failure };

    public static CreateAnalystProfileResult Contradicts(IReadOnlyList<FieldViolation> violations) => new() { Contradictions = violations };
}

public sealed class CreateAnalystProfileHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAnalystProfileStore profiles,
    ICapabilityDeclarationStore declarations,
    IAuditTrail audit,
    IRequestContext context,
    TimeProvider time)
{
    public const string ImageNotRegistered = "image-not-registered";
    public const string ProfileContradictsDeclaration = "profile-contradicts-declaration";
    public const string ProfileContentExists = "profile-content-exists";

    public async Task<CreateAnalystProfileResult> HandleAsync(CreateAnalystProfileCommand command, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ProfileResourceType, null), cancellationToken);
        if (!decision.IsGranted)
        {
            return CreateAnalystProfileResult.Failed(OperationFailure.Forbidden());
        }

        var violations = new List<FieldViolation>();
        if (string.IsNullOrWhiteSpace(command.ProfileId))
        {
            violations.Add(new FieldViolation("profileId", "required"));
        }

        if (string.IsNullOrWhiteSpace(command.ImageDigest))
        {
            violations.Add(new FieldViolation("imageDigest", "required"));
        }

        if (command.Content is null)
        {
            violations.Add(new FieldViolation("content", "required"));
        }

        if (violations.Count > 0)
        {
            return CreateAnalystProfileResult.Failed(OperationFailure.Validation([.. violations]));
        }

        var profileId = command.ProfileId!.Trim();
        var declaration = await declarations.FindAsync(command.ImageDigest!.Trim(), cancellationToken);
        if (declaration is null)
        {
            return CreateAnalystProfileResult.Failed(new OperationFailure(OperationFailureKind.NotFound, ImageNotRegistered, [], null));
        }

        var content = command.Content! with { ImageDigest = declaration.Declaration.ImageDigest };
        var contradictions = ProfileValidation.Validate(profileId, content, declaration.Declaration);
        if (contradictions.Count > 0)
        {
            return CreateAnalystProfileResult.Contradicts(contradictions.Select(v => new FieldViolation(v.Field, v.Message)).ToList());
        }

        var digest = content.ComputeDigest();
        if (await profiles.FindByDigestAsync(digest.ToString(), cancellationToken) is { } existing)
        {
            return existing.Profile.ProfileId == profileId && existing.Profile.State != ProfileState.Retired
                ? CreateAnalystProfileResult.Success(existing, false)
                : CreateAnalystProfileResult.Failed(OperationFailure.Conflict(ProfileContentExists));
        }

        var latest = (await profiles.ListAsync(cancellationToken))
            .Where(p => p.Profile.ProfileId == profileId)
            .Select(p => p.Profile.Version)
            .DefaultIfEmpty(0)
            .Max();
        var profile = AnalystProfile.CreateDraft(profileId, latest + 1, content, context.MemberAccountId ?? Guid.Empty, time.GetUtcNow());
        if (!await profiles.TryInsertAsync(profile, cancellationToken))
        {
            return CreateAnalystProfileResult.Failed(OperationFailure.Conflict(ProfileContentExists));
        }

        await audit.RecordAsync(RegistryAuditEvents.AnalystProfileCreated(profile.ProfileId, profile.Version, new Dictionary<string, string>
        {
            ["profileId"] = profile.ProfileId,
            ["profileVersion"] = profile.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["profileDigest"] = profile.Digest.ToString(),
            ["imageDigest"] = content.ImageDigest,
            ["capabilityId"] = content.CapabilityId,
            ["capabilityVersion"] = content.CapabilityVersion,
            ["modelDigest"] = content.ModelDigest,
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return CreateAnalystProfileResult.Success(new StoredAnalystProfile(profile, 1), true);
    }
}

public sealed record ProfileTransitionCommand(string ProfileId, int Version, long ExpectedVersion);

public sealed class ApproveAnalystProfileHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAnalystProfileStore profiles,
    IAuditTrail audit,
    IRequestContext context,
    TimeProvider time)
{
    public const string ProfileNotDraft = "profile-not-draft";

    public async Task<OperationResult<StoredAnalystProfile>> HandleAsync(ProfileTransitionCommand command, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer,
            new AuditResource(RegistryAuditEvents.ProfileResourceType, RegistryAuditEvents.ProfileResourceId(command.ProfileId, command.Version)),
            cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var stored = await profiles.FindAsync(command.ProfileId, command.Version, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        if (stored.Profile.State != ProfileState.Draft)
        {
            return OperationFailure.Conflict(ProfileNotDraft);
        }

        if (stored.Version != command.ExpectedVersion)
        {
            return OperationFailure.VersionMismatch(stored.Version);
        }

        var approved = stored.Profile.Approve(context.MemberAccountId ?? Guid.Empty, time.GetUtcNow());
        var write = await profiles.UpdateStateAsync(approved, command.ExpectedVersion, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return write.Outcome == VersionedWriteOutcome.NotFound
                ? OperationFailure.NotFound()
                : OperationFailure.VersionMismatch(write.Version);
        }

        await audit.RecordAsync(RegistryAuditEvents.AnalystProfileApproved(approved.ProfileId, approved.Version, new Dictionary<string, string>
        {
            ["profileId"] = approved.ProfileId,
            ["profileVersion"] = approved.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["profileDigest"] = approved.Digest.ToString(),
            ["selfApproval"] = approved.IsSelfApproval ? "true" : "false",
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new StoredAnalystProfile(approved, write.Version ?? stored.Version);
    }
}

public sealed class RetireAnalystProfileHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAnalystProfileStore profiles,
    IWorkflowDefinitionStore workflows,
    IAuditTrail audit,
    IRequestContext context,
    TimeProvider time)
{
    public const string ProfileInDefaultWorkflow = "profile-in-default-workflow";
    public const string ProfileAlreadyRetired = "profile-already-retired";

    public async Task<OperationResult<StoredAnalystProfile>> HandleAsync(ProfileTransitionCommand command, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer,
            new AuditResource(RegistryAuditEvents.ProfileResourceType, RegistryAuditEvents.ProfileResourceId(command.ProfileId, command.Version)),
            cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var stored = await profiles.FindAsync(command.ProfileId, command.Version, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        if (stored.Profile.State == ProfileState.Retired)
        {
            return OperationFailure.Conflict(ProfileAlreadyRetired);
        }

        if (stored.Version != command.ExpectedVersion)
        {
            return OperationFailure.VersionMismatch(stored.Version);
        }

        if (await DefaultWorkflowUsesAsync(stored.Profile, cancellationToken))
        {
            return OperationFailure.Conflict(ProfileInDefaultWorkflow);
        }

        var retired = stored.Profile.Retire(context.MemberAccountId ?? Guid.Empty, time.GetUtcNow());
        var write = await profiles.UpdateStateAsync(retired, command.ExpectedVersion, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return write.Outcome == VersionedWriteOutcome.NotFound
                ? OperationFailure.NotFound()
                : OperationFailure.VersionMismatch(write.Version);
        }

        await audit.RecordAsync(RegistryAuditEvents.AnalystProfileRetired(retired.ProfileId, retired.Version, new Dictionary<string, string>
        {
            ["profileId"] = retired.ProfileId,
            ["profileVersion"] = retired.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["profileDigest"] = retired.Digest.ToString(),
            ["fromState"] = stored.Profile.State.ToPersistedName(),
            ["toState"] = retired.State.ToPersistedName(),
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new StoredAnalystProfile(retired, write.Version ?? stored.Version);
    }

    private async Task<bool> DefaultWorkflowUsesAsync(AnalystProfile profile, CancellationToken cancellationToken)
    {
        var current = await workflows.GetDefaultAsync(cancellationToken);
        if (current is null)
        {
            return false;
        }

        var definition = await workflows.FindAsync(current.WorkflowId, current.WorkflowVersion, cancellationToken);
        if (definition is null)
        {
            return false;
        }

        using var document = JsonDocument.Parse(definition.DefinitionJson);
        if (!document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var digest = profile.Digest.ToString();
        return nodes.EnumerateArray().Any(node =>
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("profileDigest", out var value)
            && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), digest, StringComparison.Ordinal));
    }
}

public sealed class ListAnalystProfilesHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IAnalystProfileStore profiles)
{
    public async Task<OperationResult<IReadOnlyList<StoredAnalystProfile>>> HandleAsync(
        string? state, string? capabilityId, CancellationToken cancellationToken)
    {
        ProfileState wanted = default;
        if (!string.IsNullOrEmpty(state) && !ProfileStateExtensions.TryParse(state, out wanted))
        {
            return OperationFailure.Validation(new FieldViolation("state", "invalid-value"));
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ProfileResourceType, null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        IEnumerable<StoredAnalystProfile> items = await profiles.ListAsync(cancellationToken);
        if (!string.IsNullOrEmpty(state))
        {
            items = items.Where(p => p.Profile.State == wanted);
        }

        if (!string.IsNullOrEmpty(capabilityId))
        {
            items = items.Where(p => string.Equals(p.Profile.Content.CapabilityId, capabilityId, StringComparison.Ordinal));
        }

        return OperationResult<IReadOnlyList<StoredAnalystProfile>>.Success(items.ToList());
    }
}

public sealed class GetAnalystProfileHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IAnalystProfileStore profiles)
{
    public async Task<OperationResult<StoredAnalystProfile>> HandleAsync(string profileId, int version, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer,
            new AuditResource(RegistryAuditEvents.ProfileResourceType, RegistryAuditEvents.ProfileResourceId(profileId, version)),
            cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var found = await profiles.FindAsync(profileId, version, cancellationToken);
        return found is null ? OperationFailure.NotFound() : found;
    }
}
