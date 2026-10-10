using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;

namespace SocAlytics.Platform.Api.Recordings;

internal static class RecordingProblemCodes
{
	public const string UploadDeclarationInvalid = "upload-declaration-invalid";
	public const string PartNumbersInvalid = "part-numbers-invalid";
	public const string ContentTypeNotAllowed = "content-type-not-allowed";
	public const string TimelineMappingInvalid = "timeline-mapping-invalid";
	public const string RecordingSetEmpty = "recording-set-empty";
	public const string RecordingSetTooLarge = "recording-set-too-large";
	public const string RecordingSetMemberInvalid = "recording-set-member-invalid";
	public const string RecordingSetDuplicateRecording = "recording-set-duplicate-recording";
	public const string SeasonArchived = "season-archived";
	public const string UploadSessionCompleted = "upload-session-completed";
	public const string UploadSessionExpired = "upload-session-expired";
	public const string UploadPartsIncomplete = "upload-parts-incomplete";
	public const string UploadPartMismatch = "upload-part-mismatch";
	public const string UploadObjectMismatch = "upload-object-mismatch";
	public const string IntegrityEvidenceUnavailable = "integrity-evidence-unavailable";
	public const string ObjectStorageUnavailable = "object-storage-unavailable";

	public static int StatusOf(string code) => code switch
	{
		UploadDeclarationInvalid or PartNumbersInvalid or ContentTypeNotAllowed or TimelineMappingInvalid
			or RecordingSetEmpty or RecordingSetTooLarge or RecordingSetMemberInvalid or RecordingSetDuplicateRecording
			=> StatusCodes.Status400BadRequest,
		SeasonArchived or UploadSessionCompleted or UploadSessionExpired or UploadPartsIncomplete
			or UploadPartMismatch or UploadObjectMismatch or IntegrityEvidenceUnavailable
			=> StatusCodes.Status409Conflict,
		ObjectStorageUnavailable => StatusCodes.Status503ServiceUnavailable,
		_ => throw new ArgumentOutOfRangeException(nameof(code)),
	};
}

internal sealed class ObjectStorageUnavailableFilter : IEndpointFilter
{
	public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		try
		{
			return await next(context);
		}
		catch (ObjectStorageUnavailableException)
		{
			return ProblemResults.Problem(
				RecordingProblemCodes.StatusOf(RecordingProblemCodes.ObjectStorageUnavailable),
				RecordingProblemCodes.ObjectStorageUnavailable);
		}
	}
}
