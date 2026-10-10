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

public sealed class MemberAdministrationEndpointTests(PostgresContainerFixture postgres)
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

	[Fact]
	// Quickstart A33
	public async Task EndMemberSessionsEndsEverySession()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		using var s1 = await ApiSession.SignInAsync(factory, "member-1", TestMembers.DefaultPassword, ct);
		using var s2 = await ApiSession.SignInAsync(factory, "member-1", TestMembers.DefaultPassword, ct);

		using var ended = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/members/{targetId}/sessions", null, ct);
		ended.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		foreach (var s in new[] { s1, s2 })
		{
			using var response = await s.SendAsync(HttpMethod.Get, "/api/v1/members", null, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'sessions.ended'", ct)).ShouldBe(1);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.member_session WHERE member_account_id = '{targetId}' AND end_reason = 'ended-by-admin'", ct)).ShouldBe(2);
	}

	[Fact]
	public async Task UnlockLetsCorrectPasswordSignInAndIgnoresStaleIfMatch()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", lockedOut: true, cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var locked = await factory.CreateApiClient().PostAsJsonAsync(
			"/api/v1/session", new { accountName = "member-1", password = TestMembers.DefaultPassword }, ct);
		locked.IsSuccessStatusCode.ShouldBeFalse();

		using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/members/{targetId}/unlock");
		request.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
		request.Headers.TryAddWithoutValidation("If-Match", "\"999\"");
		using var unlocked = await admin.Client.SendAsync(request, ct);
		unlocked.StatusCode.ShouldBe(HttpStatusCode.OK, await unlocked.Content.ReadAsStringAsync(ct));
		(await unlocked.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("lockedOut").GetBoolean().ShouldBeFalse();

		using var member = await ApiSession.SignInAsync(factory, "member-1", TestMembers.DefaultPassword, ct);
		using var again = await admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{targetId}/unlock", null, ct);
		again.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'account.unlocked' AND outcome = 'succeeded'", ct)).ShouldBe(1);
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'account.unlocked' AND outcome = 'unchanged'", ct)).ShouldBe(1);
	}

	[Theory]
	[InlineData("registrar")]
	[InlineData("none")]
	public async Task NonAdminsGetForbiddenOnEveryAdministrationOperation(string callerRole)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		var adminId = await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await TestMembers.SeedAsync(db, "caller-1", clubRoles: callerRole == "none" ? null : [callerRole], cancellationToken: ct);
		using var session = await ApiSession.SignInAsync(factory, "caller-1", TestMembers.DefaultPassword, ct);
		var m = $"/api/v1/members/{adminId}";

		var attempts = new (HttpMethod Method, string Path, object? Body)[]
		{
			(HttpMethod.Post, "/api/v1/members", new { accountName = "someone-new" }),
			(HttpMethod.Put, $"{m}/club-roles/registrar", null),
			(HttpMethod.Delete, $"{m}/club-roles/club-admin", null),
			(HttpMethod.Post, $"{m}/deactivate", null),
			(HttpMethod.Post, $"{m}/reactivate", null),
			(HttpMethod.Post, $"{m}/unlock", null),
			(HttpMethod.Delete, $"{m}/sessions", null),
		};
		foreach (var (method, path, body) in attempts)
		{
			using var response = await session.SendAsync(method, path, body, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
		}

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied'", ct)).ShouldBe(attempts.Length);
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.member_account", ct)).ShouldBe(2);
		(await ScalarAsync<string>(db, $"SELECT membership_status FROM socalytics.member_account WHERE id = '{adminId}'", ct)).ShouldBe("active");
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.club_role_assignment WHERE role = 'club-admin'", ct)).ShouldBe(1);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.member_session WHERE member_account_id = '{adminId}' AND ended_at IS NOT NULL", ct)).ShouldBe(0);
	}
}
