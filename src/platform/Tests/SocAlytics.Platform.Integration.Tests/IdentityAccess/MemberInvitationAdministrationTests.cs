using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

[Collection(MailpitCollection.Name)]
public sealed partial class MemberInvitationAdministrationTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private const string Password = "a-long-enough-password";

	// The tests advance the clock by days, so the admin session must outlive that.
	private static readonly Dictionary<string, string?> LongSessions = new()
	{
		["IdentityAccess:Session:IdleTimeout"] = "30.00:00:00",
		["IdentityAccess:Session:AbsoluteLifetime"] = "30.00:00:00",
	};

	private Task<EmailApiHarness> StartAsync(CancellationToken ct) =>
		EmailApiHarness.StartAsync(postgres, mailpit, ct, LongSessions);

	private static async Task<Guid> CreateTeamAsync(EmailApiHarness h, CancellationToken ct)
	{
		using var season = await h.Admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name = "2026" }, ct);
		var seasonId = (await season.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
		using var team = await h.Admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{seasonId}/teams", new { name = "U17" }, ct);
		team.StatusCode.ShouldBe(HttpStatusCode.Created);
		return (await team.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
	}

	private sealed record Invited(Guid Id, string Address, string AccountName, long Version);

	private static async Task<Invited> InviteAsync(EmailApiHarness h, Guid teamId, string accountName, string? address, CancellationToken ct)
	{
		address ??= EmailApiHarness.NewAddress();
		using var response = await h.Admin.SendAsync(
			HttpMethod.Post,
			"/api/v1/invitations",
			new { address, accountName, teamRoles = new[] { new { teamId, role = "coach" } } },
			ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		return new Invited(body.GetProperty("id").GetGuid(), address, accountName, body.GetProperty("version").GetInt64());
	}

	private static async Task<JsonElement[]> ListAsync(EmailApiHarness h, string? state, CancellationToken ct)
	{
		using var response = await h.Admin.SendAsync(
			HttpMethod.Get, state is null ? "/api/v1/invitations" : $"/api/v1/invitations?state={state}", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return (await response.Content.ReadFromJsonAsync<JsonElement[]>(ct))!;
	}

	private static async Task<JsonElement> GetAsync(EmailApiHarness h, Guid id, CancellationToken ct) =>
		(await ListAsync(h, null, ct)).Single(i => i.GetProperty("id").GetGuid() == id);

	private static Task<HttpResponseMessage> ActAsync(EmailApiHarness h, string action, Guid id, long? version, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invitations/{id}/{action}");
		request.Headers.Add("X-CSRF-Token", h.Admin.AntiforgeryToken);
		if (version is { } v)
		{
			request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
		}

		return h.Admin.Client.SendAsync(request, ct);
	}

	private static async Task<long> ActOkAsync(EmailApiHarness h, string action, Guid id, long version, CancellationToken ct)
	{
		using var response = await ActAsync(h, action, id, version, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("version").GetInt64();
	}

	private static async Task<List<string>> TokensAsync(EmailApiHarness h, string address, int count, CancellationToken ct)
	{
		await h.DeliverAsync(ct);
		var found = await h.Mailpit.WaitForMessagesAsync($"to:{address}", count, TimeSpan.FromSeconds(30), ct);
		var tokens = new List<string>();
		foreach (var message in found)
		{
			var text = await h.Mailpit.GetTextBodyAsync(message.Id, ct);
			tokens.Add(LinkPattern().Match(text).Groups[1].Value);
		}

		return tokens;
	}

	private static async Task<string> TokenAsync(EmailApiHarness h, string address, CancellationToken ct) =>
		(await TokensAsync(h, address, 1, ct))[0];

	private static async Task<HttpResponseMessage> CheckAsync(EmailApiHarness h, string token, CancellationToken ct)
	{
		using var client = h.Factory.CreateApiClient();
		return await client.PostAsJsonAsync("/api/v1/invitations/checks", new { token }, ct);
	}

	private static async Task ShouldBeInvalidAsync(EmailApiHarness h, string token, CancellationToken ct)
	{
		using var check = await CheckAsync(h, token, ct);
		check.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await check.Content.ReadAsStringAsync(ct)).ShouldContain("invitation-invalid");
	}

	private static async Task ShouldBeUsableAsync(EmailApiHarness h, string token, CancellationToken ct)
	{
		using var check = await CheckAsync(h, token, ct);
		check.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Fact]
	// FR-013: newest first, state filter, expired display state, and the delivery state
	public async Task TheListReturnsEveryInvitationNewestFirstFiltersByStateAndShowsExpiryAndDelivery()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);

		var oldest = await InviteAsync(h, teamId, "oldest", null, ct);
		await TokenAsync(h, oldest.Address, ct);
		h.Factory.Time.Advance(TimeSpan.FromDays(8));
		var middle = await InviteAsync(h, teamId, "middle", null, ct);
		h.Factory.Time.Advance(TimeSpan.FromMinutes(1));
		var newest = await InviteAsync(h, teamId, "newest", null, ct);
		await TokenAsync(h, middle.Address, ct);
		await TokenAsync(h, newest.Address, ct);
		await ActOkAsync(h, "revoke", middle.Id, (await GetAsync(h, middle.Id, ct)).GetProperty("version").GetInt64(), ct);

		var all = await ListAsync(h, null, ct);
		all.Select(i => i.GetProperty("id").GetGuid()).ShouldBe([newest.Id, middle.Id, oldest.Id]);
		all.Select(i => i.GetProperty("state").GetString()).ShouldBe(["pending", "revoked", "expired"]);
		all[0].GetProperty("deliveryState").GetString().ShouldBe("sent");
		all[2].GetProperty("deliveryState").GetString().ShouldBe("sent");

		(await ListAsync(h, "pending", ct)).Select(i => i.GetProperty("id").GetGuid()).ShouldBe([newest.Id]);
		(await ListAsync(h, "expired", ct)).Select(i => i.GetProperty("id").GetGuid()).ShouldBe([oldest.Id]);
		(await ListAsync(h, "revoked", ct)).Select(i => i.GetProperty("id").GetGuid()).ShouldBe([middle.Id]);
		(await ListAsync(h, "accepted", ct)).ShouldBeEmpty();

		using var invalid = await h.Admin.SendAsync(HttpMethod.Get, "/api/v1/invitations?state=bogus", null, ct);
		invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	// FR-014: resend of a pending invitation sends a new message and supersedes the first link
	public async Task ResendOfAPendingInvitationSendsANewMessageAndTheFirstLinkFails()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		var invited = await InviteAsync(h, teamId, "pending-resend", null, ct);
		var first = await TokenAsync(h, invited.Address, ct);

		var version = await ActOkAsync(h, "resend", invited.Id, invited.Version, ct);
		version.ShouldBeGreaterThan(invited.Version);

		var tokens = await TokensAsync(h, invited.Address, 2, ct);
		var second = tokens.Single(t => t != first);
		await ShouldBeInvalidAsync(h, first, ct);
		await ShouldBeUsableAsync(h, second, ct);

		var current = await GetAsync(h, invited.Id, ct);
		current.GetProperty("state").GetString().ShouldBe("pending");
		current.GetProperty("resendCount").GetInt32().ShouldBe(1);

		using var accepted = await h.Factory.CreateApiClient()
			.PostAsJsonAsync("/api/v1/invitations/acceptances", new { token = second, password = Password }, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
	}

	[Fact]
	// FR-014: resend of an expired invitation makes it pending again
	public async Task ResendOfAnExpiredInvitationMakesItPending()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		var invited = await InviteAsync(h, teamId, "expired-resend", null, ct);
		var first = await TokenAsync(h, invited.Address, ct);
		h.Factory.Time.Advance(TimeSpan.FromDays(8));

		var expired = await GetAsync(h, invited.Id, ct);
		expired.GetProperty("state").GetString().ShouldBe("expired");
		await ShouldBeInvalidAsync(h, first, ct);

		await ActOkAsync(h, "resend", invited.Id, expired.GetProperty("version").GetInt64(), ct);
		(await GetAsync(h, invited.Id, ct)).GetProperty("state").GetString().ShouldBe("pending");

		var tokens = await TokensAsync(h, invited.Address, 2, ct);
		await ShouldBeUsableAsync(h, tokens.Single(t => t != first), ct);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	// FR-015: revoke of a pending and of an expired invitation ends the link and releases name and address
	public async Task RevokeEndsTheLinkAndReleasesNameAndAddress(bool expire)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		var invited = await InviteAsync(h, teamId, "revoked-one", null, ct);
		var token = await TokenAsync(h, invited.Address, ct);
		if (expire)
		{
			h.Factory.Time.Advance(TimeSpan.FromDays(8));
		}

		var current = await GetAsync(h, invited.Id, ct);
		current.GetProperty("state").GetString().ShouldBe(expire ? "expired" : "pending");
		await ActOkAsync(h, "revoke", invited.Id, current.GetProperty("version").GetInt64(), ct);

		var revoked = await GetAsync(h, invited.Id, ct);
		revoked.GetProperty("state").GetString().ShouldBe("revoked");
		await ShouldBeInvalidAsync(h, token, ct);

		var again = await InviteAsync(h, teamId, invited.AccountName, invited.Address, ct);
		again.Id.ShouldNotBe(invited.Id);
	}

	[Fact]
	// FR-016: resend and revoke of accepted or revoked invitations answer 409 invitation-not-open
	public async Task ResendAndRevokeOfClosedInvitationsAnswerNotOpen()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);

		var accepted = await InviteAsync(h, teamId, "accepted-one", null, ct);
		var token = await TokenAsync(h, accepted.Address, ct);
		using (var accept = await h.Factory.CreateApiClient()
			.PostAsJsonAsync("/api/v1/invitations/acceptances", new { token, password = Password }, ct))
		{
			accept.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		var revoked = await InviteAsync(h, teamId, "revoked-two", null, ct);
		await ActOkAsync(h, "revoke", revoked.Id, revoked.Version, ct);

		foreach (var id in new[] { accepted.Id, revoked.Id })
		{
			var version = (await GetAsync(h, id, ct)).GetProperty("version").GetInt64();
			foreach (var action in new[] { "resend", "revoke" })
			{
				using var response = await ActAsync(h, action, id, version, ct);
				response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
				(await response.Content.ReadAsStringAsync(ct)).ShouldContain("invitation-not-open");
			}
		}
	}

	[Fact]
	// FR-013–FR-016: a stale If-Match answers 412 and a missing one 428
	public async Task AStaleOrMissingIfMatchIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		var invited = await InviteAsync(h, teamId, "versioned", null, ct);
		var version = await ActOkAsync(h, "resend", invited.Id, invited.Version, ct);

		foreach (var action in new[] { "resend", "revoke" })
		{
			using var stale = await ActAsync(h, action, invited.Id, invited.Version, ct);
			stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
			using var missing = await ActAsync(h, action, invited.Id, null, ct);
			missing.StatusCode.ShouldBe((HttpStatusCode)428);
		}

		(await GetAsync(h, invited.Id, ct)).GetProperty("version").GetInt64().ShouldBe(version);
	}

	[Fact]
	// FR-014: a fourth resend within an hour answers 429 with Retry-After
	public async Task AFourthResendWithinAnHourIsRateLimited()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		var invited = await InviteAsync(h, teamId, "limited", null, ct);

		var version = invited.Version;
		for (var i = 0; i < 3; i++)
		{
			version = await ActOkAsync(h, "resend", invited.Id, version, ct);
		}

		using var limited = await ActAsync(h, "resend", invited.Id, version, ct);
		limited.StatusCode.ShouldBe((HttpStatusCode)429);
		limited.Headers.RetryAfter.ShouldNotBeNull();
		limited.Headers.RetryAfter.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 3600);
		(await limited.Content.ReadAsStringAsync(ct)).ShouldContain("invitation-resend-rate-limited");
		(await GetAsync(h, invited.Id, ct)).GetProperty("version").GetInt64().ShouldBe(version);

		h.Factory.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
		await ActOkAsync(h, "resend", invited.Id, version, ct);
	}

	[Fact]
	// SC-006: message delivery does not change the invitation's version
	public async Task MessageDeliveryDoesNotChangeTheInvitationVersion()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		var invited = await InviteAsync(h, teamId, "unchanged", null, ct);

		(await GetAsync(h, invited.Id, ct)).GetProperty("version").GetInt64().ShouldBe(invited.Version);
		await TokenAsync(h, invited.Address, ct);
		var delivered = await GetAsync(h, invited.Id, ct);
		delivered.GetProperty("deliveryState").GetString().ShouldBe("sent");
		delivered.GetProperty("version").GetInt64().ShouldBe(invited.Version);

		await ActOkAsync(h, "resend", invited.Id, invited.Version, ct);
	}

	[GeneratedRegex(@"/invitations/accept#token=([A-Za-z0-9_-]+)")]
	private static partial Regex LinkPattern();
}
