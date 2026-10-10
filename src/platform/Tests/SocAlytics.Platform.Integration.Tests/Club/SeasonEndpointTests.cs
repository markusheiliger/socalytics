using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Club;

public sealed class SeasonEndpointTests(PostgresContainerFixture postgres)
{
	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Season Club",
		["FirstClubAdmin:AccountName"] = "season-bootstrap",
		["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
	};

	private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var factory = new PlatformApiFactory(db, Config);
		await factory.WaitUntilHealthyAsync(ct);
		return (db, factory);
	}

	private static async Task<ApiSession> SignInAsync(IsolatedDatabase db, PlatformApiFactory factory, string name, string[]? roles, CancellationToken ct)
	{
		await TestMembers.SeedAsync(db, name, clubRoles: roles, cancellationToken: ct);
		return await ApiSession.SignInAsync(factory, name, TestMembers.DefaultPassword, ct);
	}

	private static async Task<Guid> CreateAsync(ApiSession admin, string name, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		response.Headers.ETag.ShouldNotBeNull();
		response.Headers.Location.ShouldNotBeNull();
		var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		json.GetProperty("state").GetString().ShouldBe("draft");
		return json.GetProperty("id").GetGuid();
	}

	private static async Task<string> StateAsync(ApiSession session, Guid id, CancellationToken ct)
	{
		using var response = await session.SendAsync(HttpMethod.Get, $"/api/v1/seasons/{id}", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("state").GetString()!;
	}

	private static async Task<string> PostAsync(ApiSession session, Guid id, string action, HttpStatusCode expected, CancellationToken ct, string? ifMatch = null)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/seasons/{id}/{action}");
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		using var response = await session.Client.SendAsync(request, ct);
		response.StatusCode.ShouldBe(expected);
		return await response.Content.ReadAsStringAsync(ct);
	}

	[Fact]
	// Quickstart A17
	public async Task LifecycleTransitionsAndConflicts()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var admin = await SignInAsync(db, factory, "season-admin", ["club-admin"], ct);

		var first = await CreateAsync(admin, "2026", ct);
		var second = await CreateAsync(admin, "2027", ct);

		(await PostAsync(admin, first, "archive", HttpStatusCode.Conflict, ct)).ShouldContain("invalid-state-transition");
		(await StateAsync(admin, first, ct)).ShouldBe("draft");

		// A stale If-Match is ignored for lifecycle actions.
		await PostAsync(admin, first, "activate", HttpStatusCode.OK, ct, "\"999\"");
		(await StateAsync(admin, first, ct)).ShouldBe("active");

		(await PostAsync(admin, second, "activate", HttpStatusCode.Conflict, ct)).ShouldContain("season-already-active");
		(await StateAsync(admin, first, ct)).ShouldBe("active");
		(await StateAsync(admin, second, ct)).ShouldBe("draft");

		await PostAsync(admin, first, "archive", HttpStatusCode.OK, ct);
		(await StateAsync(admin, first, ct)).ShouldBe("archived");

		await PostAsync(admin, Guid.NewGuid(), "activate", HttpStatusCode.NotFound, ct);
		using var missing = await admin.SendAsync(HttpMethod.Get, $"/api/v1/seasons/{Guid.NewGuid()}", null, ct);
		missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task ConcurrentActivationYieldsExactlyOneActiveSeason()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var admin = await SignInAsync(db, factory, "race-admin", ["club-admin"], ct);
		var ids = new[] { await CreateAsync(admin, "A", ct), await CreateAsync(admin, "B", ct) };

		var results = await Task.WhenAll(ids.Select(async id =>
		{
			using var response = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{id}/activate", null, ct);
			return response.StatusCode;
		}));

		results.Count(s => s == HttpStatusCode.OK).ShouldBe(1);
		results.Count(s => s == HttpStatusCode.Conflict).ShouldBe(1);
		var states = new List<string>();
		foreach (var id in ids)
		{
			states.Add(await StateAsync(admin, id, ct));
		}

		states.Count(s => s == "active").ShouldBe(1);
	}

	[Theory]
	[InlineData("coach")]
	[InlineData("viewer")]
	[InlineData("registrar")]
	public async Task NonAdminsCannotWriteSeasons(string role)
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var admin = await SignInAsync(db, factory, "owner-admin", ["club-admin"], ct);
		var id = await CreateAsync(admin, "Existing", ct);
		var roles = role == "registrar" ? new[] { "registrar" } : null;
		using var caller = await SignInAsync(db, factory, $"caller-{role}", roles, ct);

		using var create = await caller.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name = "Nope" }, ct);
		create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		await PostAsync(caller, id, "activate", HttpStatusCode.Forbidden, ct);
		await PostAsync(caller, id, "archive", HttpStatusCode.Forbidden, ct);

		(await StateAsync(admin, id, ct)).ShouldBe("draft");
		using var list = await admin.SendAsync(HttpMethod.Get, "/api/v1/seasons", null, ct);
		(await list.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").GetArrayLength().ShouldBe(1);
	}

	[Fact]
	public async Task MemberWithoutRolesListsAndReadsEverySeasonWithPagination()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var admin = await SignInAsync(db, factory, "page-admin", ["club-admin"], ct);
		var a = await CreateAsync(admin, "S1", ct);
		var b = await CreateAsync(admin, "S2", ct);
		var c = await CreateAsync(admin, "S3", ct);
		await PostAsync(admin, a, "activate", HttpStatusCode.OK, ct);
		await PostAsync(admin, a, "archive", HttpStatusCode.OK, ct);
		await PostAsync(admin, b, "activate", HttpStatusCode.OK, ct);

		using var plain = await SignInAsync(db, factory, "plain-member", null, ct);
		var seen = new List<Guid>();
		string? token = null;
		var pages = 0;
		do
		{
			var path = "/api/v1/seasons?pageSize=2" + (token is null ? string.Empty : $"&continuationToken={token}");
			using var response = await plain.SendAsync(HttpMethod.Get, path, null, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.OK);
			var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
			seen.AddRange(json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
			token = json.TryGetProperty("continuationToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
			pages++;
		}
		while (token is not null);

		pages.ShouldBe(2);
		seen.ShouldBe([a, b, c]);
		(await StateAsync(plain, a, ct)).ShouldBe("archived");
		(await StateAsync(plain, b, ct)).ShouldBe("active");
		(await StateAsync(plain, c, ct)).ShouldBe("draft");

		using var bad = await plain.SendAsync(HttpMethod.Get, "/api/v1/seasons?pageSize=1000", null, ct);
		bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task RestrictedSessionCannotListSeasons()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var session = await ApiSession.SignInAsync(factory, "season-bootstrap", "Initial-Admin-Pass-1234", ct);

		using var list = await session.SendAsync(HttpMethod.Get, "/api/v1/seasons", null, ct);
		list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await list.Content.ReadAsStringAsync(ct)).ShouldContain("password-change-required");
	}
}
