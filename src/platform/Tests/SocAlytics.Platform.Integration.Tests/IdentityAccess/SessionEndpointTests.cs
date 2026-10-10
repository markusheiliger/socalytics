using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class SessionEndpointTests(PostgresContainerFixture postgres)
{
	private const string Password = TestMembers.DefaultPassword;

	private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return db;
	}

	private static async Task<string> NormalizedFailureAsync(HttpResponseMessage response, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		var json = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(ct);
		json!["code"].GetString().ShouldBe("sign-in-failed");
		json.Remove("correlationId");
		return JsonSerializer.Serialize(json.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value));
	}

	[Fact]
	// Quickstart A6
	public async Task SignInSetsHardenedCookieAndLeaksNoSecrets()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db);

		using var client = factory.CreateApiClient();
		using var response = await client.PostAsJsonAsync("/api/v1/session", new { accountName = "alice", password = Password }, ct);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var cookie = response.Headers.GetValues("Set-Cookie").Single();
		cookie.ShouldStartWith("__Host-socalytics-session=");
		cookie.ToLowerInvariant().ShouldContain("secure");
		cookie.ToLowerInvariant().ShouldContain("httponly");
		cookie.ToLowerInvariant().ShouldContain("samesite=strict");
		cookie.ToLowerInvariant().ShouldContain("path=/");
		cookie.ToLowerInvariant().ShouldNotContain("domain=");
		cookie.ToLowerInvariant().ShouldNotContain("expires=");
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();

		var token = cookie.Split(';')[0].Split('=', 2)[1];
		var body = await response.Content.ReadAsStringAsync(ct);
		body.ShouldNotContain(token);
		body.ToLowerInvariant().ShouldNotContain("password\"");
		body.ToLowerInvariant().ShouldNotContain("hash");
	}

	[Fact]
	// Quickstart A7
	public async Task SignedOutCookieAndMissingSessionGetUnauthorizedWithoutRedirect()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db);

		using var anonymous = factory.CreateApiClient();
		using var none = await anonymous.GetAsync("/api/v1/session", ct);
		none.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		none.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

		using var session = await ApiSession.SignInAsync(factory, "alice", Password, ct);
		using var signOut = await session.SendAsync(HttpMethod.Delete, "/api/v1/session", null, ct);
		signOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var after = await session.Client.GetAsync("/api/v1/session", ct);
		after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		after.Headers.Location.ShouldBeNull();
		var problem = await after.Content.ReadFromJsonAsync<JsonElement>(ct);
		problem.GetProperty("code").GetString().ShouldBe("unauthenticated");
	}

	[Fact]
	public async Task SignOutRequiresValidAntiforgeryToken()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db);
		using var session = await ApiSession.SignInAsync(factory, "alice", Password, ct);

		using var missing = await session.Client.DeleteAsync("/api/v1/session", ct);
		missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await missing.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("antiforgery-failed");

		using var wrong = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/session");
		wrong.Headers.Add("X-CSRF-Token", "wrong");
		using var wrongResponse = await session.Client.SendAsync(wrong, ct);
		wrongResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT count(*) FROM socalytics.member_session WHERE ended_at IS NULL", connection);
		Convert.ToInt64(await command.ExecuteScalarAsync(ct)).ShouldBe(1);
	}

	[Fact]
	// Quickstart A57
	public async Task AntiforgeryTokenIsBoundToItsSession()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db);
		using var a = await ApiSession.SignInAsync(factory, "alice", Password, ct);
		using var b = await ApiSession.SignInAsync(factory, "alice", Password, ct);

		a.AntiforgeryToken.ShouldNotBe(b.AntiforgeryToken);
		using var crossed = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/session");
		crossed.Headers.Add("X-CSRF-Token", a.AntiforgeryToken);
		using var crossedResponse = await b.Client.SendAsync(crossed, ct);
		crossedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using var signOut = await a.SendAsync(HttpMethod.Delete, "/api/v1/session", null, ct);
		signOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		using var reuse = await a.SendAsync(HttpMethod.Delete, "/api/v1/session", null, ct);
		reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		var expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(b.RawToken), "socalytics-csrf"u8))
			.TrimEnd('=').Replace('+', '-').Replace('/', '_');
		b.AntiforgeryToken.ShouldBe(expected);
	}

	[Fact]
	// Quickstart A9
	public async Task AllSignInFailureClassesReturnIdenticalBodies()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "alice", cancellationToken: ct);
		await TestMembers.SeedAsync(db, "locked", lockedOut: true, cancellationToken: ct);
		await TestMembers.SeedAsync(db, "gone", membershipStatus: "deactivated", cancellationToken: ct);
		await TestMembers.SeedAsync(db, "nopw", password: null, cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db);

		var bodies = new List<string>();
		foreach (var (name, password) in new[]
		{
			("ghost", Password), ("alice", "wrong-password-value"), ("locked", Password), ("gone", Password), ("nopw", Password),
		})
		{
			using var client = factory.CreateApiClient();
			using var response = await client.PostAsJsonAsync("/api/v1/session", new { accountName = name, password }, ct);
			bodies.Add(await NormalizedFailureAsync(response, ct));
		}

		bodies.Distinct().Count().ShouldBe(1);
	}

	[Fact]
	public async Task NonJsonSignInGetsUnsupportedMediaType()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = new PlatformApiFactory(db);
		using var client = factory.CreateApiClient();
		using var content = new StringContent("accountName=a&password=b", Encoding.UTF8, "application/x-www-form-urlencoded");

		using var response = await client.PostAsync("/api/v1/session", content, ct);

		response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
		(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("unsupported-media-type");
	}

	[Fact]
	public async Task RestrictedSessionCanReadAndEndItself()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await TestMembers.SeedAsync(db, "fresh", passwordChangeRequired: true, cancellationToken: ct);
		await using var factory = new PlatformApiFactory(db);
		using var session = await ApiSession.SignInAsync(factory, "fresh", Password, ct);

		session.Info.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
		using var get = await session.Client.GetAsync("/api/v1/session", ct);
		get.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await get.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("passwordChangeRequired").GetBoolean().ShouldBeTrue();
		using var signOut = await session.SendAsync(HttpMethod.Delete, "/api/v1/session", null, ct);
		signOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
	}
}
