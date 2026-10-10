using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class MembershipLifecycleEndpointTests(PostgresContainerFixture postgres)
{
	private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return (db, new PlatformApiFactory(db));
	}

	private static async Task<T> ScalarAsync<T>(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return (T)(await command.ExecuteScalarAsync(ct))!;
	}

	private static string Path(Guid id, string action) => $"/api/v1/members/{id}/{action}";

	private static async Task<string> CreateWithCredentialAsync(ApiSession admin, string name, CancellationToken ct)
	{
		using var created = await admin.SendAsync(HttpMethod.Post, "/api/v1/members", new { accountName = name }, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created);
		return (await created.Content.ReadFromJsonAsync<JsonElement>(ct))
			.GetProperty("setPasswordCredential").GetProperty("credential").GetString()!;
	}

	private static async Task<HttpResponseMessage> RedeemAsync(PlatformApiFactory factory, string name, string credential, CancellationToken ct)
	{
		using var client = factory.CreateApiClient();
		return await client.PostAsJsonAsync(
			"/api/v1/credentials/redeem", new { accountName = name, credential, newPassword = "brand-new-password-1" }, ct);
	}

	[Fact]
	// Quickstart A12
	public async Task DeactivationEndsSessionsRemovesRolesAndBlocksSignIn()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		using var s1 = await ApiSession.SignInAsync(factory, "member-1", TestMembers.DefaultPassword, ct);
		using var s2 = await ApiSession.SignInAsync(factory, "member-1", TestMembers.DefaultPassword, ct);

		using var deactivated = await admin.SendAsync(HttpMethod.Post, Path(targetId, "deactivate"), null, ct);
		deactivated.StatusCode.ShouldBe(HttpStatusCode.OK, await deactivated.Content.ReadAsStringAsync(ct));
		deactivated.Headers.ETag.ShouldNotBeNull();
		var json = await deactivated.Content.ReadFromJsonAsync<JsonElement>(ct);
		json.GetProperty("membershipStatus").GetString().ShouldBe("deactivated");
		json.GetProperty("clubRoles").GetArrayLength().ShouldBe(0);

		foreach (var s in new[] { s1, s2 })
		{
			using var response = await s.SendAsync(HttpMethod.Get, "/api/v1/members", null, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		using var signIn = await factory.CreateApiClient().PostAsJsonAsync(
			"/api/v1/session", new { accountName = "member-1", password = TestMembers.DefaultPassword }, ct);
		signIn.IsSuccessStatusCode.ShouldBeFalse();
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.member_session WHERE ended_at IS NULL AND end_reason IS NULL AND member_account_id = '" + targetId + "'", ct)).ShouldBe(0);
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'member.deactivated'", ct)).ShouldBe(1);
	}

	[Fact]
	// Quickstart A13
	public async Task OnlyClubAdminCannotDeactivateSelfAndReactivationRestoresNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		var adminId = await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var self = await admin.SendAsync(HttpMethod.Post, Path(adminId, "deactivate"), null, ct);
		self.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await self.Content.ReadAsStringAsync(ct)).ShouldContain("last-club-admin");

		using var notDeactivated = await admin.SendAsync(HttpMethod.Post, Path(targetId, "reactivate"), null, ct);
		notDeactivated.StatusCode.ShouldBe(HttpStatusCode.Conflict);

		(await admin.SendAsync(HttpMethod.Post, Path(targetId, "deactivate"), null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		using var reactivated = await admin.SendAsync(HttpMethod.Post, Path(targetId, "reactivate"), null, ct);
		reactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
		var json = await reactivated.Content.ReadFromJsonAsync<JsonElement>(ct);
		json.GetProperty("membershipStatus").GetString().ShouldBe("active");
		json.GetProperty("clubRoles").GetArrayLength().ShouldBe(0);

		using var again = await admin.SendAsync(HttpMethod.Post, Path(targetId, "reactivate"), null, ct);
		again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await again.Content.ReadAsStringAsync(ct)).ShouldContain("invalid-state-transition");
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.member_session WHERE member_account_id = '" + targetId + "' AND ended_at IS NULL", ct)).ShouldBe(0);
	}

	[Fact]
	// Quickstart A15
	public async Task DeactivationRacingRoleAssignmentAlwaysEndsDeactivatedWithoutRoles()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		for (var round = 0; round < 10; round++)
		{
			var targetId = await TestMembers.SeedAsync(db, $"member-{round}", cancellationToken: ct);
			var assign = admin.SendAsync(HttpMethod.Put, $"/api/v1/members/{targetId}/club-roles/registrar", null, ct);
			var deactivate = admin.SendAsync(HttpMethod.Post, Path(targetId, "deactivate"), null, ct);
			if (round % 2 == 1)
			{
				(assign, deactivate) = (deactivate, assign);
			}

			var responses = await Task.WhenAll(assign, deactivate);
			foreach (var response in responses)
			{
				response.StatusCode.ShouldNotBe(HttpStatusCode.PreconditionFailed);
				response.StatusCode.ShouldNotBe(HttpStatusCode.PreconditionRequired);
				response.Dispose();
			}

			(await ScalarAsync<string>(db, $"SELECT membership_status FROM socalytics.member_account WHERE id = '{targetId}'", ct)).ShouldBe("deactivated");
			(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.club_role_assignment WHERE member_account_id = '{targetId}'", ct)).ShouldBe(0);
		}
	}

	[Fact]
	// Quickstart A52, A53
	public async Task CredentialsAreRevokedOnDeactivation()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var issuerId = await TestMembers.SeedAsync(db, "admin-x", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		using var issuer = await ApiSession.SignInAsync(factory, "admin-x", TestMembers.DefaultPassword, ct);

		var issued = await CreateWithCredentialAsync(issuer, "issued-member", ct);
		var own = await CreateWithCredentialAsync(admin, "target-member", ct);
		var targetId = await ScalarAsync<Guid>(db, "SELECT id FROM socalytics.member_account WHERE account_name = 'target-member'", ct);

		(await admin.SendAsync(HttpMethod.Post, Path(issuerId, "deactivate"), null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		(await ScalarAsync<string>(db,
			"SELECT revocation_reason FROM socalytics.one_time_credential c JOIN socalytics.member_account a ON a.id = c.member_account_id WHERE a.account_name = 'issued-member'", ct))
			.ShouldBe("issuer-lost-authority");

		(await admin.SendAsync(HttpMethod.Post, Path(targetId, "deactivate"), null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		(await ScalarAsync<string>(db,
			$"SELECT revocation_reason FROM socalytics.one_time_credential WHERE member_account_id = '{targetId}'", ct)).ShouldBe("target-deactivated");
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'credential.revoked'", ct)).ShouldBe(2);

		using var before = await RedeemAsync(factory, "target-member", own, ct);
		before.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await admin.SendAsync(HttpMethod.Post, Path(targetId, "reactivate"), null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		using var after = await RedeemAsync(factory, "target-member", own, ct);
		after.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await after.Content.ReadAsStringAsync(ct)).ShouldContain("credential-invalid");
		(await RedeemAsync(factory, "issued-member", issued, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	// Quickstart A54
	public async Task DeactivationRacingRedemptionNeverLeavesDeactivatedAccountWithPasswordAndSession()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		for (var round = 0; round < 50; round++)
		{
			var name = $"racer-{round}";
			var credential = await CreateWithCredentialAsync(admin, name, ct);
			var targetId = await ScalarAsync<Guid>(db, $"SELECT id FROM socalytics.member_account WHERE account_name = '{name}'", ct);
			var deactivate = admin.SendAsync(HttpMethod.Post, Path(targetId, "deactivate"), null, ct);
			var redeem = RedeemAsync(factory, name, credential, ct);
			using var responses = await Task.WhenAll(deactivate, redeem).ContinueWith(t => new CompositeDisposable(t.Result), ct);

			(await ScalarAsync<string>(db, $"SELECT membership_status FROM socalytics.member_account WHERE id = '{targetId}'", ct)).ShouldBe("deactivated");
			(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.member_session WHERE member_account_id = '{targetId}' AND ended_at IS NULL", ct)).ShouldBe(0);
		}
	}

	private sealed class CompositeDisposable(IEnumerable<IDisposable> items) : IDisposable
	{
		public void Dispose()
		{
			foreach (var item in items)
			{
				item.Dispose();
			}
		}
	}
}
