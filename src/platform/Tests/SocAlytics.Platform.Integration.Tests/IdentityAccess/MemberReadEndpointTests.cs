using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class MemberReadEndpointTests(PostgresContainerFixture postgres)
{
	private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return (db, new PlatformApiFactory(db));
	}

	[Fact]
	public async Task PaginationReturnsEveryMemberOnceInAccountNameOrder()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		string[] names = ["member-e", "member-b", "member-d", "member-a", "member-c"];
		foreach (var name in names)
		{
			await TestMembers.SeedAsync(db, name, cancellationToken: ct);
		}

		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var seen = new List<string>();
		string? token = null;
		var pages = 0;
		do
		{
			var url = "/api/v1/members?pageSize=2" + (token is null ? string.Empty : "&continuationToken=" + Uri.EscapeDataString(token));
			using var response = await admin.SendAsync(HttpMethod.Get, url, null, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
			var page = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
			var items = page.GetProperty("items");
			items.GetArrayLength().ShouldBeLessThanOrEqualTo(2);
			seen.AddRange(items.EnumerateArray().Select(i => i.GetProperty("accountName").GetString()!));
			token = page.TryGetProperty("continuationToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
			pages++;
		}
		while (token is not null && pages < 10);

		seen.ShouldBe(["admin-1", "member-a", "member-b", "member-c", "member-d", "member-e"]);
		pages.ShouldBe(3);
	}

	[Fact]
	public async Task InvalidPageSizeOrTokenGetsValidationFailed()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var tooLarge = await admin.SendAsync(HttpMethod.Get, "/api/v1/members?pageSize=201", null, ct);
		tooLarge.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await tooLarge.Content.ReadAsStringAsync(ct)).ShouldContain("validation-failed");

		using var badToken = await admin.SendAsync(HttpMethod.Get, "/api/v1/members?continuationToken=not-a-token", null, ct);
		badToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task GetMemberReturnsDocumentedFieldsAndETag()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var id = await TestMembers.SeedAsync(db, "member-1", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var response = await admin.SendAsync(HttpMethod.Get, $"/api/v1/members/{id}", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		response.Headers.ETag.ShouldNotBeNull();
		var member = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		member.GetProperty("id").GetGuid().ShouldBe(id);
		member.GetProperty("accountName").GetString().ShouldBe("member-1");
		member.GetProperty("membershipStatus").GetString().ShouldBe("active");
		member.GetProperty("passwordSet").GetBoolean().ShouldBeTrue();
		member.GetProperty("lockedOut").GetBoolean().ShouldBeFalse();
		member.GetProperty("twoFactorEnabled").GetBoolean().ShouldBeFalse();
		member.GetProperty("clubRoles").GetArrayLength().ShouldBe(0);
		member.GetProperty("teamRoles").GetArrayLength().ShouldBe(0);
		member.TryGetProperty("createdAt", out var createdAt).ShouldBeTrue();
		member.TryGetProperty("version", out var version).ShouldBeTrue();
	}

	[Fact]
	public async Task UnknownMemberGetsNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

		using var response = await admin.SendAsync(HttpMethod.Get, $"/api/v1/members/{Guid.NewGuid()}", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task NonAdminGetsForbidden()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		var id = await TestMembers.SeedAsync(db, "registrar-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var session = await ApiSession.SignInAsync(factory, "registrar-1", TestMembers.DefaultPassword, ct);

		using var list = await session.SendAsync(HttpMethod.Get, "/api/v1/members", null, ct);
		list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		using var single = await session.SendAsync(HttpMethod.Get, $"/api/v1/members/{id}", null, ct);
		single.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}
}
