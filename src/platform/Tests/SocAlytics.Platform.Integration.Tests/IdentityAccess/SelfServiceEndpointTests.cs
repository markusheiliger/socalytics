using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class SelfServiceEndpointTests(PostgresContainerFixture postgres)
{
	private const string Initial = "Initial-Admin-Pass-1234";
	private const string Changed = "Changed-Admin-Pass-5678";

	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Self Club",
		["FirstClubAdmin:AccountName"] = "self-admin",
		["FirstClubAdmin:InitialPassword"] = Initial,
	};

	private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var factory = new PlatformApiFactory(db, Config);
		await factory.WaitUntilHealthyAsync(ct);
		return (db, factory);
	}

	[Fact]
	// Quickstart A30, A49
	public async Task PasswordChangeKeepsCurrentSessionAndEndsOthers()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;

		using var first = await ApiSession.SignInAsync(factory, "self-admin", Initial, ct);
		using var second = await ApiSession.SignInAsync(factory, "self-admin", Initial, ct);
		first.Info.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();

		using var me = await first.SendAsync(HttpMethod.Get, "/api/v1/me", null, ct);
		me.StatusCode.ShouldBe(HttpStatusCode.OK);
		var body = await me.Content.ReadFromJsonAsync<JsonElement>(ct);
		body.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
		body.GetProperty("clubRoles").EnumerateArray().Select(e => e.GetString()).ShouldContain("club-admin");

		using var change = await first.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = Initial, newPassword = Changed }, ct);
		change.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var after = await first.SendAsync(HttpMethod.Get, "/api/v1/me", null, ct);
		after.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await after.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();

		using var other = await second.SendAsync(HttpMethod.Get, "/api/v1/me", null, ct);
		other.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		using var anonymous = factory.CreateApiClient();
		using var oldSignIn = await anonymous.PostAsJsonAsync("/api/v1/session", new { accountName = "self-admin", password = Initial }, ct);
		oldSignIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		using var newSignIn = await anonymous.PostAsJsonAsync("/api/v1/session", new { accountName = "self-admin", password = Changed }, ct);
		newSignIn.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task InvalidChangesAreRejectedWithFieldViolations()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var session = await ApiSession.SignInAsync(factory, "self-admin", Initial, ct);

		using var wrong = await session.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = "nope-nope-nope-1", newPassword = Changed }, ct);
		wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await wrong.Content.ReadAsStringAsync(ct)).ShouldContain("currentPassword");

		using var weak = await session.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = Initial, newPassword = "short" }, ct);
		weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await weak.Content.ReadAsStringAsync(ct)).ShouldContain("password-policy");

		using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/password")
		{
			Content = JsonContent.Create(new { currentPassword = Initial, newPassword = Changed }),
		};
		using var noCsrf = await session.Client.SendAsync(request, ct);
		noCsrf.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}
}
