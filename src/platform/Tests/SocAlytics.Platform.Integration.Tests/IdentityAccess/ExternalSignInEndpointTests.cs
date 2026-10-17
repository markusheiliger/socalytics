using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class ExternalSignInEndpointTests(PostgresContainerFixture postgres, OidcProviderContainerFixture provider)
	: IClassFixture<OidcProviderContainerFixture>
{
	private const string WebUi = "http://localhost:5173";
	private const string FailedRedirect = WebUi + "/sign-in?external=failed";

	private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return db;
	}

	private PlatformApiFactory CreateFactory(IsolatedDatabase db, bool configured = true) => new(
		db,
		extraSettings: configured
			? new Dictionary<string, string?>
			{
				["IdentityAccess:ExternalProviders:test:DisplayName"] = "Test provider",
				["IdentityAccess:ExternalProviders:test:Authority"] = provider.Authority,
				["IdentityAccess:ExternalProviders:test:ClientId"] = "socalytics",
				["IdentityAccess:ExternalProviders:test:ClientSecret"] = "provider-client-secret",
				["IdentityAccess:ExternalSignIn:WebUiBaseUrl"] = WebUi,
			}
			: null);

	private async Task LinkAsync(IsolatedDatabase db, Guid accountId, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"INSERT INTO socalytics.external_identity_link (id, account_id, provider_id, issuer, subject, linked_at, linked_by) " +
			"VALUES (@id, @a, 'test', @issuer, @subject, now(), @a)",
			connection);
		command.Parameters.AddWithValue("id", Guid.NewGuid());
		command.Parameters.AddWithValue("a", accountId);
		command.Parameters.AddWithValue("issuer", provider.Authority);
		command.Parameters.AddWithValue("subject", OidcProviderContainerFixture.Subject);
		await command.ExecuteNonQueryAsync(ct);
	}

	private static async Task<Uri> StartAsync(HttpClient client, CancellationToken ct)
	{
		using var response = await client.PostAsJsonAsync("/api/v1/session/external", new { provider = "test" }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var cookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
		cookie.ShouldStartWith("__host-socalytics-external=");
		cookie.ShouldContain("secure");
		cookie.ShouldContain("httponly");
		cookie.ShouldContain("samesite=lax");
		var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		return new Uri(json.GetProperty("authorizationUrl").GetString()!);
	}

	// Plays the browser's visit to the provider; returns the callback query the provider redirects to.
	private static async Task<string> ProviderRedirectQueryAsync(Uri authorizationUrl, CancellationToken ct)
	{
		using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
		using var response = await browser.GetAsync(authorizationUrl, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Found);
		return response.Headers.Location!.Query;
	}

	private static async Task<HttpResponseMessage> CallbackAsync(HttpClient client, string query, CancellationToken ct) =>
		await client.GetAsync("/api/v1/session/external/callback" + query, ct);

	private static void ShouldFailGenerically(HttpResponseMessage response)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.Found);
		response.Headers.Location!.ToString().ShouldBe(FailedRedirect);
		response.Headers.GetValues("Set-Cookie").ShouldNotContain(c => c.StartsWith("__Host-socalytics-session=", StringComparison.Ordinal) && !c.Contains("expires=Thu, 01 Jan 1970", StringComparison.Ordinal));
	}

	private static async Task<IReadOnlyList<(string EventType, string Details)>> AuditAsync(IsolatedDatabase db, string likeType, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT event_type, details::text FROM socalytics.security_audit_event WHERE event_type LIKE @t ORDER BY occurred_at", connection);
		command.Parameters.AddWithValue("t", likeType);
		var rows = new List<(string, string)>();
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			rows.Add((reader.GetString(0), reader.GetString(1)));
		}

		return rows;
	}

	[Fact]
	public async Task ProviderListingIsEmptyWithoutProvidersAndShowsOnlyIdAndNameWhenConfigured()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);

		await using (var none = CreateFactory(db, configured: false))
		{
			using var client = none.CreateApiClient();
			var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity-providers", ct);
			json.GetProperty("providers").GetArrayLength().ShouldBe(0);
		}

		await using var factory = CreateFactory(db);
		using var configured = factory.CreateApiClient();
		using var response = await configured.GetAsync("/api/v1/identity-providers", ct);
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		var body = await response.Content.ReadAsStringAsync(ct);
		var providers = JsonDocument.Parse(body).RootElement.GetProperty("providers");
		providers.GetArrayLength().ShouldBe(1);
		providers[0].GetProperty("id").GetString().ShouldBe("test");
		providers[0].GetProperty("displayName").GetString().ShouldBe("Test provider");
		body.ShouldNotContain("provider-client-secret");
		body.ShouldNotContain(provider.Authority);
	}

	[Fact]
	public async Task StartRejectsUnknownProviderAndMalformedBody()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();

		using var unknown = await client.PostAsJsonAsync("/api/v1/session/external", new { provider = "nope" }, ct);
		unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		using var empty = await client.PostAsJsonAsync("/api/v1/session/external", new { }, ct);
		empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task LinkedMemberSignsInWithRolesAndTheFlowLeaksNoProviderSecrets()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var alice = await TestMembers.SeedAsync(db, "alice", password: null, clubRoles: ["club-admin"], cancellationToken: ct);
		await LinkAsync(db, alice, ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();

		var url = await StartAsync(client, ct);
		var query = await ProviderRedirectQueryAsync(url, ct);
		var code = HttpUtility.ParseQueryString(query)["code"]!;
		using var callback = await CallbackAsync(client, query, ct);

		callback.StatusCode.ShouldBe(HttpStatusCode.Found);
		callback.Headers.Location!.ToString().ShouldBe(WebUi + "/");
		var cookies = callback.Headers.GetValues("Set-Cookie").ToArray();
		cookies.ShouldContain(c => c.StartsWith("__Host-socalytics-session=", StringComparison.Ordinal));
		cookies.ShouldAllBe(c => c.StartsWith("__Host-socalytics-session=", StringComparison.Ordinal)
			|| c.StartsWith("__Host-socalytics-external=;", StringComparison.Ordinal));
		(await callback.Content.ReadAsStringAsync(ct)).ShouldNotContain(code);

		using var session = await client.GetAsync("/api/v1/session", ct);
		session.StatusCode.ShouldBe(HttpStatusCode.OK);
		var json = await session.Content.ReadFromJsonAsync<JsonElement>(ct);
		json.GetProperty("member").GetProperty("accountName").GetString().ShouldBe("alice");
		json.GetProperty("member").GetProperty("clubRoles").EnumerateArray().Select(e => e.GetString()).ShouldContain("club-admin");

		var audit = await AuditAsync(db, "session.external-sign-in%", ct);
		audit.Select(a => a.EventType).ShouldBe(["session.external-sign-in"]);
		foreach (var (_, details) in audit)
		{
			details.ShouldNotContain(code);
			details.ShouldNotContain("provider-client-secret");
		}

		foreach (var entry in factory.Logs.Entries)
		{
			entry.Message.ShouldNotContain(code);
			entry.Message.ShouldNotContain("provider-client-secret");
			(entry.Exception ?? string.Empty).ShouldNotContain("provider-client-secret");
		}
	}

	[Fact]
	public async Task ExistingSessionIsReplacedBySuccessfulExternalSignIn()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var alice = await TestMembers.SeedAsync(db, "alice", clubRoles: ["club-admin"], cancellationToken: ct);
		await LinkAsync(db, alice, ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();
		(await client.PostAsJsonAsync("/api/v1/session", new { accountName = "alice", password = TestMembers.DefaultPassword }, ct))
			.StatusCode.ShouldBe(HttpStatusCode.OK);

		var url = await StartAsync(client, ct);
		using var callback = await CallbackAsync(client, await ProviderRedirectQueryAsync(url, ct), ct);
		callback.Headers.Location!.ToString().ShouldBe(WebUi + "/");

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT count(*) FROM socalytics.member_session WHERE end_reason = 'replaced'", connection);
		((long)(await command.ExecuteScalarAsync(ct))!).ShouldBe(1);
	}

	[Fact]
	public async Task UnlinkedIdentityFailsGenerically()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();

		var url = await StartAsync(client, ct);
		using var callback = await CallbackAsync(client, await ProviderRedirectQueryAsync(url, ct), ct);

		ShouldFailGenerically(callback);
		(await AuditAsync(db, "session.external-sign-in-failed", ct)).Single().Details.ShouldContain("unlinked");
	}

	[Fact]
	public async Task DeactivatedMemberFailsGenerically()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var alice = await TestMembers.SeedAsync(db, "alice", membershipStatus: "deactivated", cancellationToken: ct);
		await LinkAsync(db, alice, ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();

		var url = await StartAsync(client, ct);
		using var callback = await CallbackAsync(client, await ProviderRedirectQueryAsync(url, ct), ct);

		ShouldFailGenerically(callback);
		(await AuditAsync(db, "session.external-sign-in-failed", ct)).Single().Details.ShouldContain("membership-inactive");
	}

	[Fact]
	public async Task ProviderErrorFailsGenerically()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();

		var url = await StartAsync(client, ct);
		var state = HttpUtility.ParseQueryString(url.Query)["state"]!;
		using var callback = await CallbackAsync(client, $"?error=access_denied&state={Uri.EscapeDataString(state)}", ct);

		ShouldFailGenerically(callback);
		(await AuditAsync(db, "session.external-sign-in-failed", ct)).Single().Details.ShouldContain("provider-error");
	}

	[Fact]
	public async Task TamperedAndMissingStateFailGenerically()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var alice = await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await LinkAsync(db, alice, ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();

		var url = await StartAsync(client, ct);
		var query = await ProviderRedirectQueryAsync(url, ct);
		var code = HttpUtility.ParseQueryString(query)["code"]!;

		ShouldFailGenerically(await CallbackAsync(client, $"?code={Uri.EscapeDataString(code)}&state=tampered", ct));
		ShouldFailGenerically(await CallbackAsync(client, $"?code={Uri.EscapeDataString(code)}", ct));
		ShouldFailGenerically(await CallbackAsync(client, string.Empty, ct));
	}

	[Fact]
	public async Task MissingOrForeignBindingCookieFailsGenerically()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var alice = await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await LinkAsync(db, alice, ct);
		await using var factory = CreateFactory(db);
		using var victim = factory.CreateApiClient();
		using var attacker = factory.CreateApiClient();
		using var cookieless = factory.CreateApiClient();

		var attackerQuery = await ProviderRedirectQueryAsync(await StartAsync(attacker, ct), ct);
		await StartAsync(victim, ct);
		ShouldFailGenerically(await CallbackAsync(victim, attackerQuery, ct));

		var secondQuery = await ProviderRedirectQueryAsync(await StartAsync(attacker, ct), ct);
		ShouldFailGenerically(await CallbackAsync(cookieless, secondQuery, ct));

		(await AuditAsync(db, "session.external-sign-in-failed", ct)).ShouldAllBe(a => a.Details.Contains("binding-mismatch"));
	}

	[Fact]
	public async Task ExpiredCallbackFailsGenerically()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var alice = await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await LinkAsync(db, alice, ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();

		var query = await ProviderRedirectQueryAsync(await StartAsync(client, ct), ct);
		factory.Time.Advance(TimeSpan.FromMinutes(6));

		ShouldFailGenerically(await CallbackAsync(client, query, ct));
	}

	[Fact]
	public async Task ReplayedCallbackFailsGenerically()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var alice = await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await LinkAsync(db, alice, ct);
		await using var factory = CreateFactory(db);
		using var client = factory.CreateApiClient();

		var query = await ProviderRedirectQueryAsync(await StartAsync(client, ct), ct);
		using var first = await CallbackAsync(client, query, ct);
		first.Headers.Location!.ToString().ShouldBe(WebUi + "/");

		ShouldFailGenerically(await CallbackAsync(client, query, ct));
	}
}
