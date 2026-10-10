namespace SocAlytics.Platform.Api.Bootstrap;

public sealed class BreakGlassRecoveryOptions
{
	public const string SectionName = "BreakGlassRecovery";

	public string? AccountName { get; set; }

	/// <summary>8 to 128 characters; single use.</summary>
	public string? RecoveryId { get; set; }

	public string? TemporaryCredential { get; set; }

	// Any key present means a directive was given; incomplete ones are refused by the handler.
	public bool IsConfigured =>
		!string.IsNullOrWhiteSpace(AccountName) || !string.IsNullOrWhiteSpace(RecoveryId) || !string.IsNullOrEmpty(TemporaryCredential);

	public override string ToString() => nameof(BreakGlassRecoveryOptions);
}
