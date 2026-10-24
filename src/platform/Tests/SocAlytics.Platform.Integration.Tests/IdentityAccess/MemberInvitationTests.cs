using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

[Collection(MailpitCollection.Name)]
public sealed partial class MemberInvitationTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private const string AccountName = "jdoe";
	private const string Password = "a-long-enough-password";

	private static async Task<Guid> CreateTeamAsync(ApiSession admin, CancellationToken ct)
	{
		using var season = await admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name = "2026" }, ct);
		var seasonId = (await season.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
		using var team = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{seasonId}/teams", new { name = "U17" }, ct);
		team.StatusCode.ShouldBe(HttpStatusCode.Created);
		return (await team.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
	}

	private static Task<HttpResponseMessage> InviteAsync(EmailApiHarness h, string address, Guid teamId, CancellationToken ct) =>
		h.Admin.SendAsync(
			HttpMethod.Post,
			"/api/v1/invitations",
			new { address, accountName = AccountName, teamRoles = new[] { new { teamId, role = "coach" } } },
			ct);

	private static async Task<long> CountAsync(EmailApiHarness h, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(h.Db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		return (long)(await command.ExecuteScalarAsync(ct))!;
	}

	private static Task<long> MembersNamedAsync(EmailApiHarness h, CancellationToken ct) =>
		CountAsync(h, "SELECT count(*) FROM socalytics.member_account WHERE normalized_account_name = @n", ct, ("n", AccountName.ToUpperInvariant()));

	private static async Task<string> TokenAsync(EmailApiHarness h, string address, CancellationToken ct)
	{
		await h.DeliverAsync(ct);
		var found = await h.Mailpit.WaitForMessagesAsync($"to:{address}", 1, TimeSpan.FromSeconds(30), ct);
		var text = await h.Mailpit.GetTextBodyAsync(found[0].Id, ct);
		return LinkPattern().Match(text).Groups[1].Value;
	}

	private static Task<HttpResponseMessage> PostAnonymousAsync(EmailApiHarness h, string path, object body, CancellationToken ct)
	{
		var client = h.Factory.CreateApiClient();
		return client.PostAsJsonAsync(path, body, ct);
	}

	[Fact]
	// FR-001, FR-020, FR-021: one message with the fixed content and the token only in the fragment
	public async Task InvitingQueuesOneMessageWhoseLinkCarriesTheTokenInTheFragment()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var teamId = await CreateTeamAsync(h.Admin, ct);
		var address = EmailApiHarness.NewAddress();

		using var created = await InviteAsync(h, address, teamId, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created);
		created.Headers.ETag.ShouldNotBeNull();

		(await h.MailCountAsync(address, ct)).ShouldBe(1);
		var found = await h.Mailpit.SearchAsync($"to:{address}", ct);
		var text = await h.Mailpit.GetTextBodyAsync(found[0].Id, ct);
		found[0].Subject.ShouldContain("Example FC");
		text.ShouldContain("Example FC");
		text.ShouldContain("ignore");
		text.ShouldContain("valid for");
		LinkPattern().Match(text).Success.ShouldBeTrue();
		text.ShouldContain("http://localhost:5173/invitations/accept#token=");
		text.ShouldNotContain("?token");
		foreach (var forbidden in new[] { AccountName, EmailApiHarness.AdminName, "coach", "U17", "2026", Password })
		{
			text.ShouldNotContain(forbidden);
		}
	}

	[Fact]
	// FR-012: nothing exists before acceptance
	public async Task NoMemberAccountRoleOrAddressExistsBeforeAcceptance()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var teamId = await CreateTeamAsync(h.Admin, ct);
		var address = EmailApiHarness.NewAddress();
		using var created = await InviteAsync(h, address, teamId, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created);
		await TokenAsync(h, address, ct);

		(await MembersNamedAsync(h, ct)).ShouldBe(0);
		(await CountAsync(h, "SELECT count(*) FROM socalytics.team_role_assignment WHERE team_id = @t", ct, ("t", teamId))).ShouldBe(0);
		(await CountAsync(h, "SELECT count(*) FROM socalytics.member_email_addresses WHERE lower(address) = @a", ct, ("a", address.ToLowerInvariant()))).ShouldBe(0);
	}

	[Fact]
	// FR-007, FR-008: the check and a policy violation leave the link usable
	public async Task CheckingAndAPolicyViolationLeaveTheLinkUsable()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var teamId = await CreateTeamAsync(h.Admin, ct);
		var address = EmailApiHarness.NewAddress();
		using var created = await InviteAsync(h, address, teamId, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created);
		var token = await TokenAsync(h, address, ct);

		for (var i = 0; i < 2; i++)
		{
			using var check = await PostAnonymousAsync(h, "/api/v1/invitations/checks", new { token }, ct);
			check.StatusCode.ShouldBe(HttpStatusCode.OK);
			var body = await check.Content.ReadFromJsonAsync<JsonElement>(ct);
			body.GetProperty("clubName").GetString().ShouldBe("Example FC");
			body.GetProperty("accountName").GetString().ShouldBe(AccountName);
		}

		using var weak = await PostAnonymousAsync(h, "/api/v1/invitations/acceptances", new { token, password = "short" }, ct);
		weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await weak.Content.ReadAsStringAsync(ct)).ShouldContain("password-policy");
		(await MembersNamedAsync(h, ct)).ShouldBe(0);

		using var again = await PostAnonymousAsync(h, "/api/v1/invitations/checks", new { token }, ct);
		again.StatusCode.ShouldBe(HttpStatusCode.OK);
		using var accepted = await PostAnonymousAsync(h, "/api/v1/invitations/acceptances", new { token, password = Password }, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
	}

	[Fact]
	// FR-009–FR-011: acceptance creates one active member, uses the link, and the member signs in
	public async Task AcceptanceCreatesOneActiveMemberAndUsesTheLink()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var teamId = await CreateTeamAsync(h.Admin, ct);
		var address = EmailApiHarness.NewAddress();
		using var created = await InviteAsync(h, address, teamId, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created);
		var token = await TokenAsync(h, address, ct);

		using var accepted = await PostAnonymousAsync(h, "/api/v1/invitations/acceptances", new { token, password = Password }, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		accepted.Headers.Contains("Set-Cookie").ShouldBeFalse();

		(await MembersNamedAsync(h, ct)).ShouldBe(1);
		(await CountAsync(
			h,
			"SELECT count(*) FROM socalytics.member_account WHERE normalized_account_name = @n AND membership_status = 'active' AND NOT password_change_required",
			ct,
			("n", AccountName.ToUpperInvariant()))).ShouldBe(1);
		(await CountAsync(
			h,
			"SELECT count(*) FROM socalytics.member_email_addresses e JOIN socalytics.member_account a ON a.id = e.account_id " +
			"WHERE a.normalized_account_name = @n AND lower(e.address) = @a AND e.state = 'verified' AND e.verified_at IS NOT NULL",
			ct,
			("n", AccountName.ToUpperInvariant()), ("a", address.ToLowerInvariant()))).ShouldBe(1);
		(await CountAsync(
			h,
			"SELECT count(*) FROM socalytics.team_role_assignment r JOIN socalytics.member_account a ON a.id = r.member_account_id " +
			"WHERE a.normalized_account_name = @n AND r.team_id = @t AND r.role = 'coach'",
			ct,
			("n", AccountName.ToUpperInvariant()), ("t", teamId))).ShouldBe(1);

		using var second = await PostAnonymousAsync(h, "/api/v1/invitations/acceptances", new { token, password = Password }, ct);
		second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await second.Content.ReadAsStringAsync(ct)).ShouldContain("invitation-invalid");
		using var check = await PostAnonymousAsync(h, "/api/v1/invitations/checks", new { token }, ct);
		check.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await MembersNamedAsync(h, ct)).ShouldBe(1);

		using var member = await ApiSession.SignInAsync(h.Factory, AccountName, Password, ct);
		member.Info.GetProperty("passwordChangeRequired").GetBoolean().ShouldBeFalse();
	}

	[Fact]
	// FR-021: the invitation message is not a member preference
	public async Task TheInvitationMessageIsNotAnAdjustableNotificationPreference()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);

		using var list = await h.Admin.SendAsync(HttpMethod.Get, "/api/v1/me/notification-preferences", null, ct);
		list.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await list.Content.ReadAsStringAsync(ct)).ShouldNotContain("member-invitation");

		using var set = await h.Admin.SendAsync(
			HttpMethod.Put, "/api/v1/me/notification-preferences/member-invitation", new { enabled = false }, ct);
		set.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await set.Content.ReadAsStringAsync(ct)).ShouldContain("notification-type-not-configurable");
	}

	[GeneratedRegex(@"/invitations/accept#token=([A-Za-z0-9_-]+)")]
	private static partial Regex LinkPattern();
}
