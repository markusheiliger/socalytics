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
	};
}
