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

internal static class CoexistenceSupport
{
	public static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	public static async Task LinkAsync(IsolatedDatabase db, Guid accountId, string issuer, string subject, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"INSERT INTO socalytics.external_identity_link (id, account_id, provider_id, issuer, subject, linked_at, linked_by) " +
			"VALUES (gen_random_uuid(), @a, 'test', @issuer, @subject, now(), @a)",
			connection);
		command.Parameters.AddWithValue("a", accountId);
		command.Parameters.AddWithValue("issuer", issuer);
		command.Parameters.AddWithValue("subject", subject);
		await command.ExecuteNonQueryAsync(ct);
	}
}

public sealed class ExternalIdentityCoexistenceTests(PostgresContainerFixture postgres, OidcProviderContainerFixture provider)
	: IClassFixture<OidcProviderContainerFixture>
{
	private const string WebUi = "http://localhost:5173";

	private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return db;
	}

	private Dictionary<string, string?> ProviderSettings() => new()
	{
		["IdentityAccess:ExternalProviders:test:DisplayName"] = "Test provider",
		["IdentityAccess:ExternalProviders:test:Authority"] = provider.Authority,
		["IdentityAccess:ExternalProviders:test:ClientId"] = "socalytics",
		["IdentityAccess:ExternalProviders:test:ClientSecret"] = "provider-client-secret",
		["IdentityAccess:ExternalSignIn:WebUiBaseUrl"] = WebUi,
	};

	private static async Task<HttpResponseMessage> CompleteAsync(HttpClient client, HttpResponseMessage start, CancellationToken ct)
	{
		start.StatusCode.ShouldBe(HttpStatusCode.OK);
		var url = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("authorizationUrl").GetString()!);
		using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
		using var redirect = await browser.GetAsync(url, ct);
		return await client.GetAsync("/api/v1/session/external/callback" + redirect.Headers.Location!.Query, ct);
	}

	private static async Task<HttpResponseMessage> ExternalSignInAsync(HttpClient client, CancellationToken ct)
	{
		using var start = await client.PostAsJsonAsync("/api/v1/session/external", new { provider = "test" }, ct);
		return await CompleteAsync(client, start, ct);
	}

	private static async Task<HttpResponseMessage> LocalSignInAsync(HttpClient client, string name, string password, CancellationToken ct) =>
		await client.PostAsJsonAsync("/api/v1/session", new { accountName = name, password }, ct);

	private static string[] PropertyNames(JsonElement json) => json.EnumerateObject().Select(p => p.Name).Order().ToArray();

	private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		string Get(string name) => json.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;
		return $"{Get("type")}|{Get("title")}|{Get("detail")}|{Get("status")}";
	}

	[Fact]
	// US3 scenario 1
	public async Task ExternalOnlyMemberIsRefusedByTheLocalFormWithTheGenericFailure()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await TestMembers.SeedAsync(db, "wrong-pw", cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db, extraSettings: ProviderSettings());
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var created = await admin.SendAsync(
			HttpMethod.Post, "/api/v1/members", new { accountName = "jdoe", credential = new { kind = "identity-link", provider = "test" } }, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created);
		var json = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
		var credential = json.GetProperty("setPasswordCredential");
		credential.GetProperty("purpose").GetString().ShouldBe("identity-link");

		using var member = factory.CreateApiClient();
		using (var start = await member.PostAsJsonAsync(
			"/api/v1/session/external",
			new { provider = "test", accountName = "jdoe", linkCredential = credential.GetProperty("credential").GetString() },
			ct))
		using (var linked = await CompleteAsync(member, start, ct))
		{
			linked.Headers.Location!.ToString().ShouldBe(WebUi + "/");
		}

		(await CoexistenceSupport.ScalarAsync(
			db, "SELECT count(*) FROM socalytics.member_account WHERE account_name = 'jdoe' AND password_hash IS NULL", ct)).ShouldBe(1);

		using var anonymous = factory.CreateApiClient();
		var wrong = await RefusalAsync(await LocalSignInAsync(anonymous, "wrong-pw", "not-the-password", ct), ct);
		var external = await RefusalAsync(await LocalSignInAsync(anonymous, "jdoe", "any-password-at-all", ct), ct);

		external.ShouldBe(wrong);
		using var again = await ExternalSignInAsync(factory.CreateApiClient(), ct);
		again.Headers.Location!.ToString().ShouldBe(WebUi + "/");
	}

	[Fact]
	// US3 scenario 2
	public async Task MemberWithPasswordAndLinkGetsTheSameKindOfSessionEitherWay()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var id = await TestMembers.SeedAsync(db, "both", cancellationToken: ct);
		await CoexistenceSupport.LinkAsync(db, id, provider.Authority, OidcProviderContainerFixture.Subject, ct);
		await using var factory = new PlatformApiFactory(db, extraSettings: ProviderSettings());

		using var local = factory.CreateApiClient();
		using var localResponse = await LocalSignInAsync(local, "both", TestMembers.DefaultPassword, ct);
		localResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
		var localCookie = localResponse.Headers.GetValues("Set-Cookie").Single().Split('=')[0];
		var localInfo = await localResponse.Content.ReadFromJsonAsync<JsonElement>(ct);

		using var external = factory.CreateApiClient();
		using var callback = await ExternalSignInAsync(external, ct);
		callback.StatusCode.ShouldBe(HttpStatusCode.Found);
		callback.Headers.Location!.ToString().ShouldBe(WebUi + "/");
		var externalCookie = callback.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(localCookie + "=", StringComparison.Ordinal));
		externalCookie.ShouldNotBeNull();
		using var info = await external.GetAsync("/api/v1/session", ct);
		info.StatusCode.ShouldBe(HttpStatusCode.OK);
		var externalInfo = await info.Content.ReadFromJsonAsync<JsonElement>(ct);

		PropertyNames(externalInfo).ShouldBe(PropertyNames(localInfo));
		(await CoexistenceSupport.ScalarAsync(db, "SELECT count(*) FROM socalytics.member_session WHERE ended_at IS NULL", ct)).ShouldBe(2);
	}

	[Fact]
	// US3 scenario 3
	public async Task PasswordChangeAndResetCredentialLeaveTheLinkUnaffected()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var id = await TestMembers.SeedAsync(db, "both", cancellationToken: ct);
		await CoexistenceSupport.LinkAsync(db, id, provider.Authority, OidcProviderContainerFixture.Subject, ct);
		await using var factory = new PlatformApiFactory(db, extraSettings: ProviderSettings());
		const string link = "SELECT count(*) FROM socalytics.external_identity_link WHERE account_id = @a AND subject = 'mock-subject-1'";

		using (var member = await ApiSession.SignInAsync(factory, "both", TestMembers.DefaultPassword, ct))
		{
			using var change = await member.SendAsync(
				HttpMethod.Post, "/api/v1/me/password", new { currentPassword = TestMembers.DefaultPassword, newPassword = "Changed-Password-91011" }, ct);
			change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		(await CoexistenceSupport.ScalarAsync(db, link, ct, ("a", id))).ShouldBe(1);

		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		using var reset = await admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{id}/credentials", new { purpose = "password-reset" }, ct);
		reset.StatusCode.ShouldBe(HttpStatusCode.Created);

		(await CoexistenceSupport.ScalarAsync(db, link, ct, ("a", id))).ShouldBe(1);
		using var callback = await ExternalSignInAsync(factory.CreateApiClient(), ct);
		callback.Headers.Location!.ToString().ShouldBe(WebUi + "/");
	}

	[Fact]
	// US3 scenario 4
	public async Task BreakGlassDirectiveForALinkedClubAdminRecoversLocallyAndKeepsTheLink()
	{
		var ct = TestContext.Current.CancellationToken;
		const string admin = "recovery-admin";
		const string temporary = "Recovery-Temporary-Pass-5678";
		var bootstrap = new Dictionary<string, string?>
		{
			["ClubDisplayName"] = "Recovery Club",
			["FirstClubAdmin:AccountName"] = admin,
			["FirstClubAdmin:InitialPassword"] = "Recovery-Initial-Pass-1234",
		};
		await using var db = await MigratedAsync(ct);
		await using (var first = new PlatformApiFactory(db, bootstrap))
		{
			await first.WaitUntilHealthyAsync(ct);
		}

		Guid accountId;
		await using (var connection = new NpgsqlConnection(db.AppConnectionString))
		{
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand("SELECT id FROM socalytics.member_account WHERE account_name = @n", connection);
			command.Parameters.AddWithValue("n", admin);
			accountId = (Guid)(await command.ExecuteScalarAsync(ct))!;
		}

		await CoexistenceSupport.LinkAsync(db, accountId, "https://unreachable.example", "admin-subject", ct);
		var directive = new Dictionary<string, string?>
		{
			["AccountName"] = admin,
			["RecoveryId"] = "recovery-0002",
			["TemporaryCredential"] = temporary,
		};

		var settings = ProviderSettings();
		settings["IdentityAccess:ExternalProviders:test:Authority"] = "https://unreachable.invalid";
		await using var second = new PlatformApiFactory(db, bootstrap, directive, extraSettings: settings);
		await second.WaitUntilHealthyAsync(ct);

		using var client = second.CreateApiClient();
		using var response = await LocalSignInAsync(client, admin, temporary, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await CoexistenceSupport.ScalarAsync(
			db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'break-glass-recovery.applied'", ct)).ShouldBe(1);
		(await CoexistenceSupport.ScalarAsync(
			db,
			"SELECT count(*) FROM socalytics.external_identity_link WHERE account_id = @a AND subject = 'admin-subject'",
			ct,
			("a", accountId))).ShouldBe(1);
	}
}
