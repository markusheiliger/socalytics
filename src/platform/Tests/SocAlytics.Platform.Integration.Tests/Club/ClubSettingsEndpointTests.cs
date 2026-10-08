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

public sealed class ClubSettingsEndpointTests(PostgresContainerFixture postgres)
{
	private const string Initial = "Initial-Admin-Pass-1234";
	private const string Changed = "Changed-Admin-Pass-5678";

	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Settings Club",
		["FirstClubAdmin:AccountName"] = "settings-admin",
		["FirstClubAdmin:InitialPassword"] = Initial,
	};

	private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var factory = new PlatformApiFactory(db, Config);
		await factory.WaitUntilHealthyAsync(ct);
		return (db, factory);
	}

	private static Task<HttpResponseMessage> PutAsync(ApiSession session, string? ifMatch, string displayName, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/club") { Content = JsonContent.Create(new { displayName }) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return session.Client.SendAsync(request, ct);
	}

	private static async Task<long> CountAuditAsync(IsolatedDatabase db, string eventType, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = @t", connection);
		command.Parameters.AddWithValue("t", eventType);
		return (long)(await command.ExecuteScalarAsync(ct))!;
	}

	private static async Task<string> ReadDisplayNameAsync(ApiSession session, CancellationToken ct)
	{
		using var response = await session.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("displayName").GetString()!;
	}

	[Fact]
	public async Task RestrictedSessionIsRejectedUntilPasswordIsChanged()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var session = await ApiSession.SignInAsync(factory, "settings-admin", Initial, ct);

		using var get = await session.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct);
		get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await get.Content.ReadAsStringAsync(ct)).ShouldContain("password-change-required");

		using var put = await PutAsync(session, "\"1\"", "Renamed", ct);
		put.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await put.Content.ReadAsStringAsync(ct)).ShouldContain("password-change-required");

		using var change = await session.SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword = Initial, newPassword = Changed }, ct);
		change.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var after = await session.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct);
		after.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	public async Task MemberWithoutRolesCanReadClub()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "plain-member", cancellationToken: ct);
		using var session = await ApiSession.SignInAsync(factory, "plain-member", TestMembers.DefaultPassword, ct);

		(await ReadDisplayNameAsync(session, ct)).ShouldBe("Settings Club");
	}

	[Fact]
	public async Task UpdateRequiresCurrentVersion()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "club-boss", clubRoles: ["club-admin"], cancellationToken: ct);
		using var session = await ApiSession.SignInAsync(factory, "club-boss", TestMembers.DefaultPassword, ct);

		using var read = await session.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct);
		var etag = read.Headers.ETag!.Tag;

		using var updated = await PutAsync(session, etag, "Renamed Club", ct);
		updated.StatusCode.ShouldBe(HttpStatusCode.OK);
		updated.Headers.ETag!.Tag.ShouldNotBe(etag);
		(await ReadDisplayNameAsync(session, ct)).ShouldBe("Renamed Club");
		(await CountAuditAsync(db, "club.settings-updated", ct)).ShouldBe(1);

		using var stale = await PutAsync(session, etag, "Stale Name", ct);
		stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

		using var missing = await PutAsync(session, null, "No Header", ct);
		missing.StatusCode.ShouldBe((HttpStatusCode)428);

		using var star = await PutAsync(session, "*", "Star", ct);
		star.StatusCode.ShouldBe((HttpStatusCode)428);

		(await ReadDisplayNameAsync(session, ct)).ShouldBe("Renamed Club");
		(await CountAuditAsync(db, "club.settings-updated", ct)).ShouldBe(1);

		using var post = await session.SendAsync(HttpMethod.Post, "/api/v1/club", new { displayName = "x" }, ct);
		post.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
	}

	[Fact]
	public async Task RegistrarCannotUpdateClub()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "registrar-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var session = await ApiSession.SignInAsync(factory, "registrar-1", TestMembers.DefaultPassword, ct);

		using var read = await session.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct);
		using var denied = await PutAsync(session, read.Headers.ETag!.Tag, "Nope", ct);
		denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await denied.Content.ReadAsStringAsync(ct)).ShouldContain("\"forbidden\"");
		(await CountAuditAsync(db, "authorization.denied", ct)).ShouldBe(1);
		(await ReadDisplayNameAsync(session, ct)).ShouldBe("Settings Club");
	}
}
