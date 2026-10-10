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

public sealed class MemberCreationEndpointTests(PostgresContainerFixture postgres)
{
	private const string NewPassword = "brand-new-password-1";

	private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return (db, new PlatformApiFactory(db));
	}

	private static async Task<long> CountAuditAsync(IsolatedDatabase db, string eventType, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = @t", connection);
		command.Parameters.AddWithValue("t", eventType);
		return (long)(await command.ExecuteScalarAsync(ct))!;
	}

	private static async Task<string> CreateAsync(ApiSession admin, string name, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Post, "/api/v1/members", new { accountName = name }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		return json.GetProperty("setPasswordCredential").GetProperty("credential").GetString()!;
	}

	private static Task<HttpResponseMessage> RedeemAsync(PlatformApiFactory factory, string name, string credential, string password, CancellationToken ct) =>
		factory.CreateApiClient().PostAsJsonAsync("/api/v1/credentials/redeem", new { accountName = name, credential, newPassword = password }, ct);

	private static async Task AssertCredentialInvalidAsync(HttpResponseMessage response, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await response.Content.ReadAsStringAsync(ct)).ShouldContain("\"credential-invalid\"");
	}

	[Fact]
	public async Task ClubAdminCreatesMemberWhoRedeemsCredentialAndSignsIn()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var response = await admin.SendAsync(HttpMethod.Post, "/api/v1/members", new { accountName = "new-member" }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		response.Headers.Location.ShouldNotBeNull();
		response.Headers.ETag.ShouldNotBeNull();
		var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		var member = json.GetProperty("member");
		member.GetProperty("clubRoles").GetArrayLength().ShouldBe(0);
		member.GetProperty("passwordSet").GetBoolean().ShouldBeFalse();
		var credential = json.GetProperty("setPasswordCredential").GetProperty("credential").GetString()!;
		(await CountAuditAsync(db, "member.created", ct)).ShouldBe(1);
		(await CountAuditAsync(db, "credential.issued", ct)).ShouldBe(1);

		using var duplicate = await admin.SendAsync(HttpMethod.Post, "/api/v1/members", new { accountName = "NEW-MEMBER" }, ct);
		duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await duplicate.Content.ReadAsStringAsync(ct)).ShouldContain("account-name-taken");

		using var redeemed = await RedeemAsync(factory, "new-member", credential, NewPassword, ct);
		redeemed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		(await CountAuditAsync(db, "credential.redeemed", ct)).ShouldBe(1);
		using var signedIn = await ApiSession.SignInAsync(factory, "new-member", NewPassword, ct);

		using var again = await RedeemAsync(factory, "new-member", credential, "another-new-password-2", ct);
		await AssertCredentialInvalidAsync(again, ct);
		using var stillWorks = await ApiSession.SignInAsync(factory, "new-member", NewPassword, ct);
	}

	[Fact]
	public async Task CredentialForAnotherAccountOrUnknownAccountIsInvalid()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var first = await CreateAsync(admin, "member-a", ct);
		await CreateAsync(admin, "member-b", ct);

		using var other = await RedeemAsync(factory, "member-b", first, NewPassword, ct);
		await AssertCredentialInvalidAsync(other, ct);
		using var unknown = await RedeemAsync(factory, "nobody-here", first, NewPassword, ct);
		await AssertCredentialInvalidAsync(unknown, ct);
		(await CountAuditAsync(db, "credential.redeemed", ct)).ShouldBe(2);
	}

	[Fact]
	public async Task ExpiredCredentialIsInvalidAndPolicyViolationIsValidationFailure()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var credential = await CreateAsync(admin, "member-a", ct);

		using var weak = await RedeemAsync(factory, "member-a", credential, "short", ct);
		weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		var body = await weak.Content.ReadAsStringAsync(ct);
		body.ShouldContain("validation-failed");
		body.ShouldContain("password-policy");

		factory.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));

		using var expired = await RedeemAsync(factory, "member-a", credential, NewPassword, ct);
		await AssertCredentialInvalidAsync(expired, ct);
		using var rejected = await factory.CreateApiClient().PostAsJsonAsync(
			"/api/v1/session", new { accountName = "member-a", password = NewPassword }, ct);
		rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task RegistrarCannotCreateMembers()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "registrar-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var session = await ApiSession.SignInAsync(factory, "registrar-1", TestMembers.DefaultPassword, ct);

		using var response = await session.SendAsync(HttpMethod.Post, "/api/v1/members", new { accountName = "new-member" }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await response.Content.ReadAsStringAsync(ct)).ShouldContain("\"forbidden\"");
		(await CountAuditAsync(db, "authorization.denied", ct)).ShouldBe(1);
		(await CountAuditAsync(db, "member.created", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task RedeemRejectsNonJson()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;

		using var client = factory.CreateApiClient();
		using var content = new StringContent("accountName=x", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
		using var response = await client.PostAsync("/api/v1/credentials/redeem", content, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
	}
}
