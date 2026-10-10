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

public sealed class ClubRoleEndpointTests(PostgresContainerFixture postgres)
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

	private static async Task ExecuteAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		await command.ExecuteNonQueryAsync(ct);
	}

	private static Task<long> CountAuditAsync(IsolatedDatabase db, string eventType, CancellationToken ct) =>
		ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{eventType}'", ct);

	private static string RolePath(Guid id, string role) => $"/api/v1/members/{id}/club-roles/{role}";

	[Fact]
	public async Task AssigningRegistrarTakesEffectAndRepeatIsUnchanged()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		using var member = await ApiSession.SignInAsync(factory, "member-1", TestMembers.DefaultPassword, ct);

		using var denied = await member.SendAsync(HttpMethod.Get, "/api/v1/members", null, ct);
		denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using var assigned = await admin.SendAsync(HttpMethod.Put, RolePath(targetId, "registrar"), null, ct);
		assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(ct));
		assigned.Headers.ETag.ShouldNotBeNull();
		var json = await assigned.Content.ReadFromJsonAsync<JsonElement>(ct);
		json.GetProperty("clubRoles").EnumerateArray().Select(e => e.GetString()).ShouldBe(["registrar"]);
		(await ScalarAsync<long>(db,
			"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'club-role.assigned' AND outcome = 'succeeded' " +
			$"AND resource_id = '{targetId}' AND details->>'role' = 'registrar' AND actor_account_id IS NOT NULL", ct)).ShouldBe(1);

		using var repeated = await admin.SendAsync(HttpMethod.Put, RolePath(targetId, "registrar"), null, ct);
		repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await ScalarAsync<long>(db,
			"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'club-role.assigned' AND outcome = 'unchanged'", ct)).ShouldBe(1);

		using var invalid = await admin.SendAsync(HttpMethod.Put, RolePath(targetId, "coach"), null, ct);
		invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task StaleIfMatchOnAssignmentIsIgnored()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var request = new HttpRequestMessage(HttpMethod.Put, RolePath(targetId, "registrar"));
		request.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
		request.Headers.TryAddWithoutValidation("If-Match", "\"999\"");
		using var response = await admin.Client.SendAsync(request, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task SoleClubAdminCannotRevokeOwnRole()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		var adminId = await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var response = await admin.SendAsync(HttpMethod.Delete, RolePath(adminId, "club-admin"), null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await response.Content.ReadAsStringAsync(ct)).ShouldContain("last-club-admin");
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.club_role_assignment WHERE role = 'club-admin'", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ConcurrentMutualRevocationNeverLeavesZeroClubAdmins()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;

		for (var round = 0; round < 50; round++)
		{
			var a = await TestMembers.SeedAsync(db, $"admin-a-{round}", clubRoles: ["club-admin"], cancellationToken: ct);
			var b = await TestMembers.SeedAsync(db, $"admin-b-{round}", clubRoles: ["club-admin"], cancellationToken: ct);
			await ExecuteAsync(db,
				$"DELETE FROM socalytics.club_role_assignment WHERE role = 'club-admin' AND member_account_id NOT IN ('{a}', '{b}')", ct);
			using var sessionA = await ApiSession.SignInAsync(factory, $"admin-a-{round}", TestMembers.DefaultPassword, ct);
			using var sessionB = await ApiSession.SignInAsync(factory, $"admin-b-{round}", TestMembers.DefaultPassword, ct);

			var results = await Task.WhenAll(
				sessionA.SendAsync(HttpMethod.Delete, RolePath(b, "club-admin"), null, ct),
				sessionB.SendAsync(HttpMethod.Delete, RolePath(a, "club-admin"), null, ct));
			var statuses = results.Select(r => r.StatusCode).ToList();
			foreach (var r in results)
			{
				r.Dispose();
			}

			statuses.Count(s => s == HttpStatusCode.OK).ShouldBeLessThanOrEqualTo(1);
			statuses.Where(s => s != HttpStatusCode.OK).ShouldAllBe(s => s == HttpStatusCode.Conflict || s == HttpStatusCode.Forbidden);
			(await ScalarAsync<long>(db,
				"SELECT count(*) FROM socalytics.club_role_assignment r JOIN socalytics.member_account m ON m.id = r.member_account_id " +
				"WHERE r.role = 'club-admin' AND m.membership_status = 'active'", ct)).ShouldBeGreaterThanOrEqualTo(1);
		}
	}

	[Fact]
	public async Task RevokingIssuerClubAdminRevokesOpenCredentials()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		var issuerId = await TestMembers.SeedAsync(db, "admin-x", clubRoles: ["club-admin"], cancellationToken: ct);
		await TestMembers.SeedAsync(db, "admin-y", clubRoles: ["club-admin"], cancellationToken: ct);
		using var issuer = await ApiSession.SignInAsync(factory, "admin-x", TestMembers.DefaultPassword, ct);
		using var other = await ApiSession.SignInAsync(factory, "admin-y", TestMembers.DefaultPassword, ct);

		using var created = await issuer.SendAsync(HttpMethod.Post, "/api/v1/members", new { accountName = "new-member" }, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created);
		var credential = (await created.Content.ReadFromJsonAsync<JsonElement>(ct))
			.GetProperty("setPasswordCredential").GetProperty("credential").GetString()!;

		using var revoked = await other.SendAsync(HttpMethod.Delete, RolePath(issuerId, "club-admin"), null, ct);
		revoked.StatusCode.ShouldBe(HttpStatusCode.OK, await revoked.Content.ReadAsStringAsync(ct));

		(await ScalarAsync<string>(db,
			"SELECT revocation_reason FROM socalytics.one_time_credential WHERE revoked_at IS NOT NULL", ct)).ShouldBe("issuer-lost-authority");
		(await CountAuditAsync(db, "credential.revoked", ct)).ShouldBe(1);

		using var redeemed = await factory.CreateApiClient().PostAsJsonAsync(
			"/api/v1/credentials/redeem", new { accountName = "new-member", credential, newPassword = "brand-new-password-1" }, ct);
		redeemed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await redeemed.Content.ReadAsStringAsync(ct)).ShouldContain("credential-invalid");
	}

	[Theory]
	[InlineData("registrar")]
	[InlineData("none")]
	public async Task NonAdminsCannotAssignOrRevokeClubRoles(string callerRole)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		var adminId = await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await TestMembers.SeedAsync(db, "caller-1", clubRoles: callerRole == "none" ? null : [callerRole], cancellationToken: ct);
		using var session = await ApiSession.SignInAsync(factory, "caller-1", TestMembers.DefaultPassword, ct);

		using var assign = await session.SendAsync(HttpMethod.Put, RolePath(adminId, "registrar"), null, ct);
		using var revoke = await session.SendAsync(HttpMethod.Delete, RolePath(adminId, "club-admin"), null, ct);
		assign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		revoke.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await CountAuditAsync(db, "authorization.denied", ct)).ShouldBe(2);
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.club_role_assignment WHERE role = 'club-admin'", ct)).ShouldBe(1);
	}
}
