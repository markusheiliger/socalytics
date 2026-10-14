using System.Text.Json.Nodes;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Registry;

public enum ProfileState
{
    Draft,
    Approved,
    Retired,
}

public static class ProfileStateExtensions
{
    public static string ToPersistedName(this ProfileState state) => state switch
    {
        ProfileState.Draft => "draft",
        ProfileState.Approved => "approved",
        ProfileState.Retired => "retired",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static bool TryParse(string? value, out ProfileState state)
    {
        switch (value)
        {
            case "draft": state = ProfileState.Draft; return true;
            case "approved": state = ProfileState.Approved; return true;
            case "retired": state = ProfileState.Retired; return true;
            default: state = default; return false;
        }
    }

    public static bool CanTransitionTo(this ProfileState from, ProfileState to) =>
        (from, to) is (ProfileState.Draft, ProfileState.Approved)
            or (ProfileState.Draft, ProfileState.Retired)
            or (ProfileState.Approved, ProfileState.Retired);
}

public sealed record AnalystProfileContent(
    string CapabilityId,
    string CapabilityVersion,
    string ImageDigest,
    string ModelDigest,
    IReadOnlyList<string> RuntimeConstraints,
    IReadOnlyList<string> Accelerators,
    IReadOnlyDictionary<string, string> Resources,
    int MaxAttempts,
    int LeaseSeconds,
    int StaleTimeoutSeconds,
    int ExecutionTimeoutSeconds,
    string VideoPolicy,
    IReadOnlyDictionary<string, string> InputSchemaVersions,
    string OutputSchemaVersion)
{
    public string ToCanonicalJson()
    {
        var node = new JsonObject
        {
            ["capabilityId"] = CapabilityId,
            ["capabilityVersion"] = CapabilityVersion,
            ["imageDigest"] = ImageDigest,
            ["modelDigest"] = ModelDigest,
            ["runtimeConstraints"] = SortedArray(RuntimeConstraints),
            ["accelerators"] = SortedArray(Accelerators),
            ["resources"] = ToObject(Resources),
            ["maxAttempts"] = MaxAttempts,
            ["leaseSeconds"] = LeaseSeconds,
            ["staleTimeoutSeconds"] = StaleTimeoutSeconds,
            ["executionTimeoutSeconds"] = ExecutionTimeoutSeconds,
            ["videoPolicy"] = VideoPolicy,
            ["inputSchemaVersions"] = ToObject(InputSchemaVersions),
            ["outputSchemaVersion"] = OutputSchemaVersion,
        };
        return CanonicalJson.Canonicalize(node.ToJsonString());
    }

    public Sha256Digest ComputeDigest() => ContentDigest.OfCanonical(ToCanonicalJson());

    private static JsonArray SortedArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, string> values)
    {
        var obj = new JsonObject();
        foreach (var pair in values)
        {
            obj[pair.Key] = pair.Value;
        }

        return obj;
    }
}

public sealed class InvalidProfileTransitionException(ProfileState from, ProfileState to)
    : InvalidOperationException($"A profile cannot move from {from.ToPersistedName()} to {to.ToPersistedName()}.")
{
    public ProfileState From { get; } = from;

    public ProfileState To { get; } = to;
}

public sealed record AnalystProfile(
    string ProfileId,
    int Version,
    Sha256Digest Digest,
    AnalystProfileContent Content,
    ProfileState State,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    Guid? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null,
    Guid? RetiredBy = null,
    DateTimeOffset? RetiredAt = null)
{
    public static AnalystProfile CreateDraft(string profileId, int version, AnalystProfileContent content, Guid createdBy, DateTimeOffset createdAt) =>
        new(profileId, version, content.ComputeDigest(), content, ProfileState.Draft, createdBy, createdAt);

    public bool IsSelfApproval => ApprovedBy is { } approver && approver == CreatedBy;

    public AnalystProfile Approve(Guid by, DateTimeOffset at)
    {
        RequireTransition(ProfileState.Approved);
        return this with { State = ProfileState.Approved, ApprovedBy = by, ApprovedAt = at };
    }

    public AnalystProfile Retire(Guid by, DateTimeOffset at)
    {
        RequireTransition(ProfileState.Retired);
        return this with { State = ProfileState.Retired, RetiredBy = by, RetiredAt = at };
    }

    private void RequireTransition(ProfileState to)
    {
        if (!State.CanTransitionTo(to))
        {
            throw new InvalidProfileTransitionException(State, to);
        }
    }
}
