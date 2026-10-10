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

public sealed class PasswordRecoveryEndpointTests(PostgresContainerFixture postgres)
{
	private const string NewPassword = "brand-new-password-1";

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

	private static async Task<HttpResponseMessage> IssueAsync(ApiSession admin, Guid memberId, string purpose, CancellationToken ct) =>
		await admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{memberId}/credentials", new { purpose }, ct);

	private static async Task<string> IssueOkAsync(ApiSession admin, Guid memberId, string purpose, CancellationToken ct)
	{
		using var response = await IssueAsync(admin, memberId, purpose, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		json.GetProperty("purpose").GetString().ShouldBe(purpose);
		json.GetProperty("memberId").GetGuid().ShouldBe(memberId);
		return json.GetProperty("credential").GetString()!;
	}

	private static async Task<HttpResponseMessage> RedeemAsync(PlatformApiFactory factory, string name, string credential, CancellationToken ct)
	{
		using var client = factory.CreateApiClient();
		return await client.PostAsJsonAsync(
			"/api/v1/credentials/redeem", new { accountName = name, credential, newPassword = NewPassword }, ct);
	}

	private static async Task<HttpResponseMessage> SignInRawAsync(PlatformApiFactory factory, string name, string password, CancellationToken ct)
	{
		using var client = factory.CreateApiClient();
		return await client.PostAsJsonAsync("/api/v1/session", new { accountName = name, password }, ct);
	}

	[Fact]
	public async Task LockoutRejectsCorrectPasswordUntilClubAdminUnlocks()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		for (var i = 0; i < 5; i++)
		{
			using var wrong = await SignInRawAsync(factory, "member-1", "wrong-password-value", ct);
			wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		using var locked = await SignInRawAsync(factory, "member-1", TestMembers.DefaultPassword, ct);
		locked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'account.locked-out'", ct)).ShouldBeGreaterThan(0);

		using var unlock = await admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{targetId}/unlock", null, ct);
		unlock.StatusCode.ShouldBe(HttpStatusCode.OK);
		using var ok = await SignInRawAsync(factory, "member-1", TestMembers.DefaultPassword, ct);
		ok.IsSuccessStatusCode.ShouldBeTrue();
	}

	[Fact]
	public async Task ResetEndsSessionsAndCredentialIsSingleUse()
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
		var credential = await IssueOkAsync(admin, targetId, "password-reset", ct);

		using var redeemed = await RedeemAsync(factory, "member-1", credential, ct);
		redeemed.IsSuccessStatusCode.ShouldBeTrue(await redeemed.Content.ReadAsStringAsync(ct));
		foreach (var s in new[] { s1, s2 })
		{
			using var response = await s.SendAsync(HttpMethod.Get, "/api/v1/members", null, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		using var second = await RedeemAsync(factory, "member-1", credential, ct);
		second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await second.Content.ReadAsStringAsync(ct)).ShouldContain("credential-invalid");
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'credential.issued'", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ExpiredResetCredentialFailsAndPasswordIsUnchanged()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var credential = await IssueOkAsync(admin, targetId, "password-reset", ct);

		factory.Time.Advance(TimeSpan.FromDays(2));
		using var expired = await RedeemAsync(factory, "member-1", credential, ct);
		expired.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await SignInRawAsync(factory, "member-1", TestMembers.DefaultPassword, ct)).IsSuccessStatusCode.ShouldBeTrue();
	}

	[Fact]
	public async Task PurposeMustMatchPasswordStateAndNewCredentialSupersedesOld()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var withPassword = await TestMembers.SeedAsync(db, "member-1", cancellationToken: ct);
		var withoutPassword = await TestMembers.SeedAsync(db, "member-2", password: null, cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var resetNoPassword = await IssueAsync(admin, withoutPassword, "password-reset", ct);
		resetNoPassword.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await resetNoPassword.Content.ReadAsStringAsync(ct)).ShouldContain("credential-not-applicable");
		using var setWithPassword = await IssueAsync(admin, withPassword, "set-password", ct);
		setWithPassword.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await setWithPassword.Content.ReadAsStringAsync(ct)).ShouldContain("credential-not-applicable");

		var first = await IssueOkAsync(admin, withPassword, "password-reset", ct);
		var second = await IssueOkAsync(admin, withPassword, "password-reset", ct);
		(await RedeemAsync(factory, "member-1", first, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await RedeemAsync(factory, "member-1", second, ct)).IsSuccessStatusCode.ShouldBeTrue();
	}

	[Fact]
	public async Task ResetCredentialIsRevokedWhenIssuerLosesClubAdmin()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		var adminX = await TestMembers.SeedAsync(db, "admin-x", clubRoles: ["club-admin"], cancellationToken: ct);
		await TestMembers.SeedAsync(db, "admin-y", clubRoles: ["club-admin"], cancellationToken: ct);
		var targetId = await TestMembers.SeedAsync(db, "member-1", cancellationToken: ct);
		using var x = await ApiSession.SignInAsync(factory, "admin-x", TestMembers.DefaultPassword, ct);
		using var y = await ApiSession.SignInAsync(factory, "admin-y", TestMembers.DefaultPassword, ct);
		var credential = await IssueOkAsync(x, targetId, "password-reset", ct);

		using var revoke = await y.SendAsync(HttpMethod.Delete, $"/api/v1/members/{adminX}/club-roles/club-admin", null, ct);
		revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await revoke.Content.ReadAsStringAsync(ct));
		(await ScalarAsync<string>(db, $"SELECT revocation_reason FROM socalytics.one_time_credential WHERE member_account_id = '{targetId}'", ct))
			.ShouldBe("issuer-lost-authority");
		(await RedeemAsync(factory, "member-1", credential, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}
}
