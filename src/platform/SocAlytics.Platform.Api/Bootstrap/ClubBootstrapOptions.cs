namespace SocAlytics.Platform.Api.Bootstrap;

public sealed class ClubBootstrapOptions
{
	public const string SectionName = "ClubBootstrap";

	public string? ClubDisplayName { get; set; }

	public FirstClubAdminOptions FirstClubAdmin { get; set; } = new();

	public override string ToString() => nameof(ClubBootstrapOptions);

	public sealed class FirstClubAdminOptions
	{
		public string? AccountName { get; set; }

		public string? InitialPassword { get; set; }

		public override string ToString() => nameof(FirstClubAdminOptions);
	}
}
