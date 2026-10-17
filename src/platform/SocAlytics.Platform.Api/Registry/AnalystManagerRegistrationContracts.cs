using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SocAlytics.Platform.Api.Registry;

internal sealed record PairingCodeSubmissionBody(string? PairingCode, string? DeviceFingerprint);

internal sealed record ReasonRequest(string? Reason);

internal sealed record AnalystManagerDeviceMetadataDto(
	string Os,
	string OsVersion,
	string Architecture,
	string ManagerVersion,
	string ClaimedKeyProtection,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClaimedApplicationProfile,
	string? HostLabel);

internal sealed record AnalystManagerPairingOriginDto(string? SourceAddress, DateTimeOffset RequestedAt);

internal sealed record AnalystManagerStatusDto(
	string OperatingState,
	DateTimeOffset? PausedUntil,
	string? RuntimeUnavailableReason,
	int ActiveAnalysts,
	int MaxAnalysts,
	string ManagerVersion,
	DateTimeOffset ReportedAt);

// Status and Connectivity belong to the Club Admin fleet view: both are omitted unless IncludesOperations is set
// (see RegistrationDtoJson), and Status is written as null for a registration that has not reported yet.
internal sealed record AnalystManagerRegistrationDto(
	Guid RegistrationId,
	Guid? ManagerId,
	string StampId,
	string State,
	string KeyThumbprint,
	string DeviceFingerprint,
	AnalystManagerDeviceMetadataDto DeviceMetadata,
	AnalystManagerPairingOriginDto PairingOrigin,
	string? ApprovalNotice,
	Guid? SubmittedBy,
	DateTimeOffset? SubmittedAt,
	Guid? DecidedBy,
	DateTimeOffset? DecidedAt,
	string? DecisionReason,
	DateTimeOffset? ActivatedAt,
	DateTimeOffset? EndedAt,
	string? EndReason,
	Guid? RevokedBy,
	DateTimeOffset PairingExpiresAt,
	DateTimeOffset? ApprovalExpiresAt,
	DateTimeOffset? ActivationExpiresAt,
	DateTimeOffset CreatedAt,
	long Version)
{
	public AnalystManagerStatusDto? Status { get; init; }

	public string? Connectivity { get; init; }

	[JsonIgnore]
	internal bool IncludesOperations { get; init; }
}

internal sealed record AnalystManagerRegistrationPageDto(
	IReadOnlyList<AnalystManagerRegistrationDto> Items,
	string? ContinuationToken);

internal static class RegistrationDtoJson
{
	public static void OmitOperationsUnlessIncluded(JsonTypeInfo typeInfo)
	{
		if (typeInfo.Type != typeof(AnalystManagerRegistrationDto))
		{
			return;
		}

		foreach (var property in typeInfo.Properties)
		{
			if (property.Name is "status" or "connectivity")
			{
				property.ShouldSerialize = static (instance, _) => ((AnalystManagerRegistrationDto)instance).IncludesOperations;
			}
		}
	}
}
