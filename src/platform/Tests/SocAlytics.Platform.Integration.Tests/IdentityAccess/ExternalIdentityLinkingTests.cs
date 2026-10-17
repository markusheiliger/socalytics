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

public sealed class ExternalIdentityLinkingTests(PostgresContainerFixture postgres, OidcProviderContainerFixture provider)
	: IClassFixture<OidcProviderContainerFixture>
{
	private const string WebUi = "http://localhost:5173";
	private const string LinkFailed = WebUi + "/link-account?external=failed";

	private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return db;
	}

	private PlatformApiFactory CreateFactory(IsolatedDatabase db) => new(
		db,
		extraSettings: new Dictionary<string, string?>
		{
			["IdentityAccess:ExternalProviders:test:DisplayName"] = "Test provider",
			["IdentityAccess:ExternalProviders:test:Authority"] = provider.Authority,
			["IdentityAccess:ExternalProviders:test:ClientId"] = "socalytics",
			["IdentityAccess:ExternalProviders:test:ClientSecret"] = "provider-client-secret",
			["IdentityAccess:ExternalSignIn:WebUiBaseUrl"] = WebUi,
		});

	private static async Task<(Guid Id, string Credential)> CreateLinkMemberAsync(ApiSession admin, string name, CancellationToken ct)
	{
		using var response = await admin.SendAsync(
			HttpMethod.Post, "/api/v1/members", new { accountName = name, credential = new { kind = "identity-link", provider = "test" } }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		var credential = json.GetProperty("setPasswordCredential");
		credential.GetProperty("purpose").GetString().ShouldBe("identity-link");
		return (json.GetProperty("member").GetProperty("id").GetGuid(), credential.GetProperty("credential").GetString()!);
	}

	private static async Task<string> IssueLinkCredentialAsync(ApiSession admin, Guid memberId, CancellationToken ct)
	{
		using var response = await admin.SendAsync(
			HttpMethod.Post, $"/api/v1/members/{memberId}/credentials", new { purpose = "identity-link", provider = "test" }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("credential").GetString()!;
	}

	private static async Task<HttpResponseMessage> StartLinkAsync(HttpClient client, string accountName, string credential, CancellationToken ct) =>
		await client.PostAsJsonAsync("/api/v1/session/external", new { provider = "test", accountName, linkCredential = credential }, ct);

	private static async Task<HttpResponseMessage> CompleteAsync(HttpClient client, HttpResponseMessage start, CancellationToken ct)
	{
		start.StatusCode.ShouldBe(HttpStatusCode.OK);
		var url = new Uri((await start.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("authorizationUrl").GetString()!);
		using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
		using var redirect = await browser.GetAsync(url, ct);
		return await client.GetAsync("/api/v1/session/external/callback" + redirect.Headers.Location!.Query, ct);
	}

	private static async Task<HttpResponseMessage> RedeemAsync(HttpClient client, string accountName, string credential, CancellationToken ct)
	{
		using var start = await StartLinkAsync(client, accountName, credential, ct);
		return await CompleteAsync(client, start, ct);
	}

	private static async Task<HttpResponseMessage> ExternalSignInAsync(HttpClient client, CancellationToken ct)
	{
		using var start = await client.PostAsJsonAsync("/api/v1/session/external", new { provider = "test" }, ct);
		return await CompleteAsync(client, start, ct);
	}

	private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return (long)(await command.ExecuteScalarAsync(ct))!;
	}

	private static async Task<string> AuditTextAsync(IsolatedDatabase db, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT coalesce(string_agg(event_type || ' ' || details::text || ' ' || coalesce(reason_code, ''), E'\\n'), '') FROM socalytics.security_audit_event",
			connection);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}

	private static Task<long> AuditCountAsync(IsolatedDatabase db, string eventType, CancellationToken ct) =>
		ScalarAsync(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{eventType}'", ct);

	private static async Task<JsonElement> GetMemberAsync(ApiSession admin, Guid id, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Get, $"/api/v1/members/{id}", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
	}

	private static Task<HttpResponseMessage> UnlinkAsync(ApiSession session, Guid memberId, string providerId, string? ifMatch, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/members/{memberId}/external-identities/{providerId}");
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		if (ifMatch is not null)
		{
			request.Headers.Add("If-Match", ifMatch);
		}

		return session.Client.SendAsync(request, ct);
	}

	[Fact]
	public async Task MemberCreatedWithLinkCredentialRedeemsItAndSignsInThroughTheProvider()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await using var factory = CreateFactory(db);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var (memberId, credential) = await CreateLinkMemberAsync(admin, "jdoe", ct);

		using var member = factory.CreateApiClient();
		using var callback = await RedeemAsync(member, "jdoe", credential, ct);

		callback.StatusCode.ShouldBe(HttpStatusCode.Found);
		callback.Headers.Location!.ToString().ShouldBe(WebUi + "/");
		using var session = await member.GetAsync("/api/v1/session", ct);
		session.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.external_identity_link", ct)).ShouldBe(1);
		(await AuditCountAsync(db, "identity.linked", ct)).ShouldBe(1);
		(await AuditCountAsync(db, "credential.issued", ct)).ShouldBe(1);

		// The credential is used up, and the linked member now signs in through the provider only.
		using var replay = await StartLinkAsync(factory.CreateApiClient(), "jdoe", credential, ct);
		replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		using var signedIn = await ExternalSignInAsync(factory.CreateApiClient(), ct);
		signedIn.Headers.Location!.ToString().ShouldBe(WebUi + "/");
		using var local = await factory.CreateApiClient().PostAsJsonAsync(
			"/api/v1/session", new { accountName = "jdoe", password = "anything-at-all-1" }, ct);
		local.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		var audit = await AuditTextAsync(db, ct);
		audit.ShouldNotContain(credential);
		audit.ShouldNotContain(OidcProviderContainerFixture.Subject);
		foreach (var entry in factory.Logs.Entries)
		{
			entry.Message.ShouldNotContain(credential);
		}

		var json = await GetMemberAsync(admin, memberId, ct);
		var identity = json.GetProperty("externalIdentities").EnumerateArray().Single();
		identity.GetProperty("provider").GetString().ShouldBe("test");
		identity.GetProperty("displayName").GetString().ShouldBe("Test provider");
		identity.GetProperty("providerConfigured").GetBoolean().ShouldBeTrue();
		identity.TryGetProperty("linkedAt", out _).ShouldBeTrue();
		var text = json.GetRawText();
		text.ShouldNotContain(OidcProviderContainerFixture.Subject);
		text.ShouldNotContain("issuer");
	}

	[Fact]
	public async Task CreationDefaultsToSetPasswordAndRejectsUnknownChoices()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await using var factory = CreateFactory(db);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var plain = await admin.SendAsync(HttpMethod.Post, "/api/v1/members", new { accountName = "plain" }, ct);
		plain.StatusCode.ShouldBe(HttpStatusCode.Created);
		(await plain.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("setPasswordCredential").GetProperty("purpose").GetString()
			.ShouldBe("set-password");
		using var explicitChoice = await admin.SendAsync(
			HttpMethod.Post, "/api/v1/members", new { accountName = "explicit", credential = new { kind = "set-password" } }, ct);
		explicitChoice.StatusCode.ShouldBe(HttpStatusCode.Created);
		using var unknownProvider = await admin.SendAsync(
			HttpMethod.Post, "/api/v1/members", new { accountName = "other", credential = new { kind = "identity-link", provider = "nope" } }, ct);
		unknownProvider.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		using var unknownKind = await admin.SendAsync(
			HttpMethod.Post, "/api/v1/members", new { accountName = "other", credential = new { kind = "magic" } }, ct);
		unknownKind.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		using var unknownIssue = await admin.SendAsync(
			HttpMethod.Post, $"/api/v1/members/{Guid.NewGuid()}/credentials", new { purpose = "identity-link", provider = "nope" }, ct);
		unknownIssue.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.member_account WHERE account_name = 'other'", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task InvalidLinkCredentialsConsumeNothingAndFailGenerically()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var admin2Id = await TestMembers.SeedAsync(db, "admin-2", clubRoles: ["club-admin"], cancellationToken: ct);
		await using var factory = CreateFactory(db);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		using var admin2 = await ApiSession.SignInAsync(factory, "admin-2", TestMembers.DefaultPassword, ct);

		var (_, expired) = await CreateLinkMemberAsync(admin, "expired-member", ct);
		var (deactivatedId, deactivated) = await CreateLinkMemberAsync(admin, "deactivated-member", ct);
		var (_, orphaned) = await CreateLinkMemberAsync(admin2, "orphaned-member", ct);
		using (var response = await admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{deactivatedId}/deactivate", null, ct))
		{
			response.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var revoke = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/members/{admin2Id}/club-roles/club-admin", null, ct))
		{
			revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		factory.Time.Advance(TimeSpan.FromDays(30));
		using var client = factory.CreateApiClient();
		var attempts = new[]
		{
			("expired-member", expired), ("deactivated-member", deactivated), ("orphaned-member", orphaned),
			("expired-member", "wrong"), ("nobody", "wrong"),
		};
		foreach (var (name, value) in attempts)
		{
			using var response = await StartLinkAsync(client, name, value, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.pending_external_sign_in", ct)).ShouldBe(0);
		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.external_identity_link", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task IdentityAlreadyLinkedToAnotherMemberIsRefusedAndAudited()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await using var factory = CreateFactory(db);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var (_, first) = await CreateLinkMemberAsync(admin, "first", ct);
		var (_, second) = await CreateLinkMemberAsync(admin, "second", ct);
		using (var ok = await RedeemAsync(factory.CreateApiClient(), "first", first, ct))
		{
			ok.Headers.Location!.ToString().ShouldBe(WebUi + "/");
		}

		using var refused = await RedeemAsync(factory.CreateApiClient(), "second", second, ct);

		refused.StatusCode.ShouldBe(HttpStatusCode.Found);
		refused.Headers.Location!.ToString().ShouldBe(LinkFailed);
		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.external_identity_link", ct)).ShouldBe(1);
		(await AuditCountAsync(db, "identity.link-refused", ct)).ShouldBe(1);
		(await AuditTextAsync(db, ct)).ShouldContain("already-linked");
	}

	[Fact]
	public async Task MemberAlreadyLinkedToTheProviderCannotLinkAnotherIdentity()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await using var factory = CreateFactory(db);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var (memberId, first) = await CreateLinkMemberAsync(admin, "jdoe", ct);
		using (await RedeemAsync(factory.CreateApiClient(), "jdoe", first, ct))
		{
		}

		var again = await IssueLinkCredentialAsync(admin, memberId, ct);
		await using (var connection = new NpgsqlConnection(db.AppConnectionString))
		{
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand("UPDATE socalytics.external_identity_link SET subject = 'someone-else'", connection);
			await command.ExecuteNonQueryAsync(ct);
		}

		// The provider returns the original subject, which is now unlinked, but the member-provider pair is taken.
		using var refused = await RedeemAsync(factory.CreateApiClient(), "jdoe", again, ct);

		refused.Headers.Location!.ToString().ShouldBe(LinkFailed);
		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.external_identity_link", ct)).ShouldBe(1);
		(await AuditCountAsync(db, "identity.link-refused", ct)).ShouldBe(1);
		(await ScalarAsync(
			db,
			"SELECT count(*) FROM socalytics.one_time_credential WHERE purpose = 'identity-link' AND consumed_at IS NULL AND revoked_at IS NULL",
			ct)).ShouldBe(1);
	}

	[Fact]
	public async Task UnlinkEndsSessionsAndRefusesLaterProviderSignIns()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await using var factory = CreateFactory(db);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var (memberId, credential) = await CreateLinkMemberAsync(admin, "jdoe", ct);
		using var memberClient = factory.CreateApiClient();
		using (await RedeemAsync(memberClient, "jdoe", credential, ct))
		{
		}

		(await memberClient.GetAsync("/api/v1/session", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		var version = (await GetMemberAsync(admin, memberId, ct)).GetProperty("version").GetInt64();

		(await UnlinkAsync(admin, memberId, "test", null, ct)).StatusCode.ShouldBe((HttpStatusCode)428);
		(await UnlinkAsync(admin, memberId, "test", $"\"{version + 100}\"", ct)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
		(await UnlinkAsync(admin, memberId, "other", $"\"{version}\"", ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using var removed = await UnlinkAsync(admin, memberId, "test", $"\"{version}\"", ct);

		removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		(await memberClient.GetAsync("/api/v1/session", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.external_identity_link", ct)).ShouldBe(0);
		(await AuditCountAsync(db, "identity.unlinked", ct)).ShouldBe(1);
		using var again = await ExternalSignInAsync(factory.CreateApiClient(), ct);
		again.Headers.Location!.ToString().ShouldBe(WebUi + "/sign-in?external=failed");
		(await GetMemberAsync(admin, memberId, ct)).GetProperty("externalIdentities").GetArrayLength().ShouldBe(0);
	}

	[Fact]
	public async Task NonAdminsCannotIssueLinkCredentialsOrUnlink()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var target = await TestMembers.SeedAsync(db, "target", cancellationToken: ct);
		await TestMembers.SeedAsync(db, "plain", cancellationToken: ct);
		await using var factory = CreateFactory(db);
		using var plain = await ApiSession.SignInAsync(factory, "plain", TestMembers.DefaultPassword, ct);

		using var issue = await plain.SendAsync(
			HttpMethod.Post, $"/api/v1/members/{target}/credentials", new { purpose = "identity-link", provider = "test" }, ct);
		issue.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		using var create = await plain.SendAsync(
			HttpMethod.Post, "/api/v1/members", new { accountName = "x", credential = new { kind = "identity-link", provider = "test" } }, ct);
		create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await UnlinkAsync(plain, target, "test", "\"1\"", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task LinksOfAProviderThatIsNoLongerConfiguredAreShownAsSuch()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var admin = await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		await using (var connection = new NpgsqlConnection(db.AppConnectionString))
		{
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand(
				"INSERT INTO socalytics.external_identity_link (id, account_id, provider_id, issuer, subject, linked_at, linked_by) " +
				"VALUES (gen_random_uuid(), @a, 'gone', 'https://gone.example', 's', now(), @a)",
				connection);
			command.Parameters.AddWithValue("a", admin);
			await command.ExecuteNonQueryAsync(ct);
		}

		await using var factory = CreateFactory(db);
		using var session = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var identity = (await GetMemberAsync(session, admin, ct)).GetProperty("externalIdentities").EnumerateArray().Single();

		identity.GetProperty("provider").GetString().ShouldBe("gone");
		identity.GetProperty("providerConfigured").GetBoolean().ShouldBeFalse();
	}
}
