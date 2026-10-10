using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Api.Recordings;

internal sealed record RecordingDescriptorDto(string? DisplayName, string? Description, string? ContentType);

internal sealed record StartUploadRequest(RecordingDescriptorDto? Recording, long TotalSizeBytes, long PartSizeBytes, string[]? PartDigests);

internal sealed record UploadSessionDto(
	string Id,
	string MatchId,
	string TeamId,
	string State,
	RecordingDescriptorDto Recording,
	long TotalSizeBytes,
	long PartSizeBytes,
	int PartCount,
	string ContentDigest,
	DateTimeOffset CreatedAt,
	DateTimeOffset ExpiresAt,
	DateTimeOffset? CompletedAt,
	DateTimeOffset? ExpiredAt,
	long Version)
{
	public static UploadSessionDto From(UploadSession s, UploadSessionState state) => new(
		s.Id.ToString(),
		s.MatchId.ToString(),
		s.TeamId.ToString(),
		state switch
		{
			UploadSessionState.Pending => "pending",
			UploadSessionState.Completed => "completed",
			_ => "expired",
		},
		new RecordingDescriptorDto(s.Descriptor.DisplayName, s.Descriptor.Description, s.Descriptor.ContentType),
		s.Declaration.TotalSizeBytes,
		s.Declaration.PartSizeBytes,
		s.Declaration.PartCount,
		s.Declaration.ExpectedDigest.ToString(),
		s.CreatedAt,
		s.ExpiresAt,
		s.CompletedAt,
		s.ExpiredAt,
		s.Version);
}

internal sealed record PartGrantDto(int PartNumber, string Method, string Url, IReadOnlyDictionary<string, string> RequiredHeaders)
{
	public static PartGrantDto From(PartUploadGrant g) => new(g.PartNumber, g.Method, g.Url.ToString(), g.RequiredHeaders);
}

internal sealed record PartGrantBatchDto(DateTimeOffset ExpiresAt, IReadOnlyList<PartGrantDto> Parts)
{
	public static PartGrantBatchDto? From(IReadOnlyList<PartUploadGrant> grants) =>
		grants.Count == 0 ? null : new(grants.Min(g => g.ExpiresAt), [.. grants.Select(PartGrantDto.From)]);
}

internal sealed record PartGrantRequest(int[]? PartNumbers);

internal sealed record UploadSessionWithGrantsDto(UploadSessionDto Session, PartGrantBatchDto? Grants);
