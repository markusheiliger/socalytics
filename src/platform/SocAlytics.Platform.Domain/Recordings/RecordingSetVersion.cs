using System.Diagnostics.CodeAnalysis;

namespace SocAlytics.Platform.Domain.Recordings;

public sealed record RecordingSetViolation(string Code, string Field, string Message);

public sealed class RecordingSetVersion
{
    public const string EmptyCode = "recording-set-empty";
    public const string TooLargeCode = "recording-set-too-large";
    public const string MemberInvalidCode = "recording-set-member-invalid";
    public const string DuplicateRecordingCode = "recording-set-duplicate-recording";

    private RecordingSetVersion(
        Guid id, Guid matchId, DateTimeOffset finalizedAt, IReadOnlyList<RecordingSetMember> members)
    {
        Id = id;
        MatchId = matchId;
        FinalizedAt = finalizedAt;
        Members = members;
    }

    public Guid Id { get; }

    public Guid MatchId { get; }

    public DateTimeOffset FinalizedAt { get; }

    public IReadOnlyList<RecordingSetMember> Members { get; }

    public int MemberCount => Members.Count;

    // matchMappings maps each recording version of the route Match to its timeline mapping ids; anything
    // else, including ids of other Matches, is reported identically as an invalid member.
    public static bool TryCreate(
        Guid id,
        Guid matchId,
        DateTimeOffset finalizedAt,
        IReadOnlyList<(Guid RecordingVersionId, Guid TimelineMappingId)> requested,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> matchMappings,
        int maxMembers,
        [NotNullWhen(true)] out RecordingSetVersion? set,
        out IReadOnlyList<RecordingSetViolation> violations)
    {
        set = null;
        var errors = new List<RecordingSetViolation>();
        violations = errors;

        if (requested.Count == 0)
        {
            errors.Add(new RecordingSetViolation(EmptyCode, "members", "A recording set has at least one member."));
            return false;
        }

        if (requested.Count > maxMembers)
        {
            errors.Add(new RecordingSetViolation(
                TooLargeCode, "members", $"A recording set has at most {maxMembers} members."));
            return false;
        }

        var seen = new HashSet<Guid>();
        var members = new List<RecordingSetMember>(requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            var (recordingVersionId, mappingId) = requested[i];
            var field = $"members[{i}]";

            if (!matchMappings.TryGetValue(recordingVersionId, out var mappings) || !mappings.Contains(mappingId))
            {
                errors.Add(new RecordingSetViolation(
                    MemberInvalidCode, field, "The recording version and timeline mapping are not valid for this match."));
            }
            else if (!seen.Add(recordingVersionId))
            {
                errors.Add(new RecordingSetViolation(
                    DuplicateRecordingCode, field, "A recording version appears at most once in a recording set."));
            }
            else
            {
                members.Add(new RecordingSetMember(i + 1, recordingVersionId, mappingId));
            }
        }

        if (errors.Count > 0)
        {
            return false;
        }

        set = new RecordingSetVersion(id, matchId, finalizedAt, members.AsReadOnly());
        return true;
    }
}
