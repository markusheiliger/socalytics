namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

internal static class TestIdentityAccessSettings
{
	public static IReadOnlyDictionary<string, string?> Values { get; } = new Dictionary<string, string?>
	{
		["IdentityAccess:Session:IdleTimeout"] = "00:30:00",
		["IdentityAccess:Session:AbsoluteLifetime"] = "08:00:00",
		["IdentityAccess:Lockout:MaxFailedAccessAttempts"] = "5",
		["IdentityAccess:Lockout:LockoutDuration"] = "00:15:00",
		["IdentityAccess:Password:RequiredLength"] = "12",
		["IdentityAccess:OneTimeCredential:Lifetime"] = "1.00:00:00",
		["ObjectStorage:ServiceUrl"] = "http://127.0.0.1:9",
		["ObjectStorage:AccessKey"] = "test-access",
		["ObjectStorage:SecretKey"] = "test-secret",
		["ObjectStorage:Bucket"] = "test-bucket",
		["ObjectStorage:EnsureBucketOnStartup"] = "false",
		["Recordings:Upload:SessionLifetime"] = "00:30:00",
		["Recordings:Upload:GrantLifetime"] = "00:15:00",
		["Recordings:Upload:ExpirySweepInterval"] = "00:01:00",
		["Recordings:Upload:MaxObjectSizeBytes"] = "5497558138880",
		["Recordings:Upload:MinPartSizeBytes"] = "5242880",
		["Recordings:Upload:MaxPartSizeBytes"] = "5368709120",
		["Recordings:Upload:MaxPartCount"] = "10000",
		["Recordings:Upload:MaxGrantsPerRequest"] = "1000",
		["Recordings:Upload:AllowedContentTypes:0"] = "video/mp4",
		["Recordings:Upload:AllowedContentTypes:1"] = "video/quicktime",
	};
}
