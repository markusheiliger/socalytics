using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Application.Segments;

// The identity fields are raw so a missing or malformed field is rejected here instead of failing the caller.
public sealed record RequestSegmentMaterializationCommand(
    Guid MatchId,
    Guid TeamId,
    Guid RecordingSetVersionId,
    Guid RecordingVersionId,
    string? TimelineMappingDigest,
    string? SegmentationPolicyDigest,
    int SegmentNumber,
    int PolicyVersion,
    int DurationSeconds);

public sealed record SegmentMaterializationRequestResult
{
    private SegmentMaterializationRequestResult(MaterializedSegmentRecord? segment, string? rejectionReason)
    {
        Segment = segment;
        RejectionReason = rejectionReason;
    }

    public MaterializedSegmentRecord? Segment { get; }

    public string? RejectionReason { get; }

    public bool IsAccepted => Segment is not null;

    public static SegmentMaterializationRequestResult Accepted(MaterializedSegmentRecord segment) => new(segment, null);

    public static SegmentMaterializationRequestResult Rejected(string reason) => new(null, reason);
}

// Runs inside the caller's unit of work; a rejection writes nothing.
public sealed class RequestSegmentMaterializationHandler(ISegmentStore store, IRecordingLineageQueries lineage)
{
    public const int SupportedPolicyVersion = 1;

    // Requests with a missing or nonpositive identity field.
    public const string IdentityInvalid = "segment-identity-invalid";

    public async Task<SegmentMaterializationRequestResult> HandleAsync(
        RequestSegmentMaterializationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.MatchId == Guid.Empty
            || command.TeamId == Guid.Empty
            || command.RecordingSetVersionId == Guid.Empty
            || command.RecordingVersionId == Guid.Empty
            || command.SegmentNumber <= 0
            || command.DurationSeconds <= 0
            || !Sha256Digest.TryParse(command.TimelineMappingDigest, out var mappingDigest)
            || !Sha256Digest.TryParse(command.SegmentationPolicyDigest, out var policyDigest))
        {
            return SegmentMaterializationRequestResult.Rejected(IdentityInvalid);
        }

        if (command.PolicyVersion != SupportedPolicyVersion)
        {
            return SegmentMaterializationRequestResult.Rejected(SegmentFailureReasons.PolicyUnknown);
        }

        var identity = new MaterializedSegmentIdentity(
            command.RecordingVersionId, mappingDigest, policyDigest, command.SegmentNumber);

        var existing = await store.FindAsync(identity, cancellationToken);
        if (existing is not null
            && (existing.PolicyVersion != command.PolicyVersion || existing.DurationSeconds != command.DurationSeconds))
        {
            return SegmentMaterializationRequestResult.Rejected(SegmentFailureReasons.PolicyConflict);
        }

        var set = await lineage.GetSetVersionAsync(command.MatchId, command.RecordingSetVersionId, cancellationToken);
        var member = set?.Members.FirstOrDefault(m =>
            m.RecordingVersionId == command.RecordingVersionId
            && string.Equals(m.TimelineMappingDigest, mappingDigest.ToString(), StringComparison.Ordinal));
        if (set is null || set.MatchId != command.MatchId || set.TeamId != command.TeamId || member is null)
        {
            return SegmentMaterializationRequestResult.Rejected(SegmentFailureReasons.ScopeMismatch);
        }

        var mapping = await lineage.GetTimelineMappingAsync(command.MatchId, member.TimelineMappingId, cancellationToken);
        if (mapping is null
            || mapping.RecordingVersionId != command.RecordingVersionId
            || !string.Equals(mapping.Digest, mappingDigest.ToString(), StringComparison.Ordinal))
        {
            return SegmentMaterializationRequestResult.Rejected(SegmentFailureReasons.ScopeMismatch);
        }

        var window = SegmentWindowCalculator.Calculate(command.SegmentNumber, command.DurationSeconds, mapping.Spans);
        if (window.IsRejected)
        {
            return SegmentMaterializationRequestResult.Rejected(window.RejectionReason!);
        }

        var segment = await store.DemandAsync(identity, command.PolicyVersion, command.DurationSeconds, cancellationToken);
        return SegmentMaterializationRequestResult.Accepted(segment);
    }
}
