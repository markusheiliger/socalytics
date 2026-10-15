using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

[Collection(MailpitCollection.Name)]
public sealed class MemberEmailTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private static async Task<Guid> AccountIdAsync(IsolatedDatabase db, string name, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT id FROM socalytics.member_account WHERE account_name = @n", connection);
		command.Parameters.AddWithValue("n", name);
		return (Guid)(await command.ExecuteScalarAsync(ct))!;
	}

	private static Task<HttpResponseMessage> SendAsync(ApiSession session, HttpMethod method, string path, object? body, long? version, CancellationToken ct)
	{
		var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		}

		if (version is { } v)
		{
			request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
		}

		return session.Client.SendAsync(request, ct);
	}

	private static async Task<(EmailApiHarness Harness, ApiSession Member, Guid MemberId)> StartAsync(
		PostgresContainerFixture postgres, MailpitContainerFixture mailpit, CancellationToken ct)
	{
		var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var memberId = await TestMembers.SeedAsync(h.Db, "member-1", cancellationToken: ct);
		var member = await ApiSession.SignInAsync(h.Factory, "member-1", TestMembers.DefaultPassword, ct);
		return (h, member, memberId);
	}

	[Fact]
	// US4 scenarios 1 and 2: a Club Admin sets an address; it is unverified, confirmed by the member, and visible in the member views
	public async Task ClubAdminSetsAnAddressThatTheMemberConfirms()
	{
		var ct = TestContext.Current.CancellationToken;
		var (h, member, memberId) = await StartAsync(postgres, mailpit, ct);
		await using var _ = h;
		using var __ = member;
		var path = $"/api/v1/members/{memberId}/email";

		using var none = await h.Admin.SendAsync(HttpMethod.Get, path, null, ct);
		none.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		var address = EmailApiHarness.NewAddress();
		using var set = await SendAsync(h.Admin, HttpMethod.Put, path, new { address }, null, ct);
		set.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await set.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("state").GetString().ShouldBe("unverified");

		var token = await h.TokenFromMailAsync(address, 1, ct);
		(await h.ConfirmAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var own = await member.SendAsync(HttpMethod.Get, "/api/v1/me/email", null, ct);
		(await own.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("state").GetString().ShouldBe("verified");

		using var detail = await h.Admin.SendAsync(HttpMethod.Get, $"/api/v1/members/{memberId}", null, ct);
		var email = (await detail.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("email");
		email.GetProperty("address").GetString().ShouldBe(address);
		email.GetProperty("state").GetString().ShouldBe("verified");

		using var list = await h.Admin.SendAsync(HttpMethod.Get, "/api/v1/members", null, ct);
		var items = (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().ToArray();
		items.Single(i => i.GetProperty("id").GetGuid() == memberId).GetProperty("email").GetProperty("address").GetString().ShouldBe(address);
		items.Where(i => i.GetProperty("id").GetGuid() != memberId)
			.All(i => !i.TryGetProperty("email", out var e) || e.ValueKind == JsonValueKind.Null).ShouldBeTrue();

		var audit = await AuditDetailsAsync(h.Db, "identity.email-address-set", ct);
		audit.ShouldNotContain(address);
		await using var connection = new NpgsqlConnection(h.Db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT set_by FROM socalytics.member_email_addresses WHERE account_id = @id", connection);
		command.Parameters.AddWithValue("id", memberId);
		((Guid)(await command.ExecuteScalarAsync(ct))!).ShouldBe(await AccountIdAsync(h.Db, EmailApiHarness.AdminName, ct));
	}

	[Fact]
	// US4 scenario 2: changing needs the current version, resets verification, and confirms the new address
	public async Task ClubAdminChangesAnAddressWithTheCurrentVersion()
	{
		var ct = TestContext.Current.CancellationToken;
		var (h, member, memberId) = await StartAsync(postgres, mailpit, ct);
		await using var _ = h;
		using var __ = member;
		var path = $"/api/v1/members/{memberId}/email";
		var first = EmailApiHarness.NewAddress();
		using var set = await SendAsync(h.Admin, HttpMethod.Put, path, new { address = first }, null, ct);
		var version = (await set.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("version").GetInt64();

		using var missing = await SendAsync(h.Admin, HttpMethod.Put, path, new { address = EmailApiHarness.NewAddress() }, null, ct);
		missing.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);

		var second = EmailApiHarness.NewAddress();
		using var changed = await SendAsync(h.Admin, HttpMethod.Put, path, new { address = second }, version, ct);
		changed.StatusCode.ShouldBe(HttpStatusCode.OK);
		await h.TokenFromMailAsync(second, 1, ct);

		using var invalid = await SendAsync(h.Admin, HttpMethod.Put, path, new { address = "not-an-address" }, version + 1, ct);
		invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	// US4 scenario 3: removing leaves the member without an address
	public async Task ClubAdminRemovesAnAddress()
	{
		var ct = TestContext.Current.CancellationToken;
		var (h, member, memberId) = await StartAsync(postgres, mailpit, ct);
		await using var _ = h;
		using var __ = member;
		var path = $"/api/v1/members/{memberId}/email";
		var address = EmailApiHarness.NewAddress();
		using var set = await SendAsync(h.Admin, HttpMethod.Put, path, new { address }, null, ct);
		var version = (await set.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("version").GetInt64();

		using var removed = await SendAsync(h.Admin, HttpMethod.Delete, path, null, version, ct);
		removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var own = await member.SendAsync(HttpMethod.Get, "/api/v1/me/email", null, ct);
		own.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		using var detail = await h.Admin.SendAsync(HttpMethod.Get, $"/api/v1/members/{memberId}", null, ct);
		var body = await detail.Content.ReadFromJsonAsync<JsonElement>(ct);
		(!body.TryGetProperty("email", out var email) || email.ValueKind == JsonValueKind.Null).ShouldBeTrue();
		(await AuditDetailsAsync(h.Db, "identity.email-address-removed", ct)).ShouldNotContain(address);
		(await h.MailCountAsync(address, ct)).ShouldBe(0);
	}

	[Fact]
	// US4 scenario 4: other members are refused and nothing changes
	public async Task NonAdminsCannotManageOtherMembersAddresses()
	{
		var ct = TestContext.Current.CancellationToken;
		var (h, member, _) = await StartAsync(postgres, mailpit, ct);
		await using var _ = h;
		using var __ = member;
		var otherId = await TestMembers.SeedAsync(h.Db, "member-2", cancellationToken: ct);
		var path = $"/api/v1/members/{otherId}/email";

		using var get = await member.SendAsync(HttpMethod.Get, path, null, ct);
		get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		using var put = await SendAsync(member, HttpMethod.Put, path, new { address = EmailApiHarness.NewAddress() }, null, ct);
		put.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		using var delete = await SendAsync(member, HttpMethod.Delete, path, null, 1, ct);
		delete.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using var asAdmin = await h.Admin.SendAsync(HttpMethod.Get, path, null, ct);
		asAdmin.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task UnknownMembersAreNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		using var response = await SendAsync(h.Admin, HttpMethod.Put, $"/api/v1/members/{Guid.NewGuid()}/email", new { address = EmailApiHarness.NewAddress() }, null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	internal static async Task<string> AuditDetailsAsync(IsolatedDatabase db, string eventType, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT coalesce(string_agg(details::text, ' '), '') FROM socalytics.security_audit_event WHERE event_type = @t", connection);
		command.Parameters.AddWithValue("t", eventType);
		var text = (string)(await command.ExecuteScalarAsync(ct))!;
		text.ShouldNotBeEmpty();
		return text;
	}
}
