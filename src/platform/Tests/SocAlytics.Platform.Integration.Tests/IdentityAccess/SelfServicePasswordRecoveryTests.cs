using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

[Collection(MailpitCollection.Name)]
public sealed partial class SelfServicePasswordRecoveryTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private const string MemberName = "member-1";
	private const string NewPassword = "a-brand-new-password";
	private const string LinkPrefix = "http://localhost:5173/password-recovery/complete#token=";

	[GeneratedRegex(@"http://localhost:5173/password-recovery/complete#token=([A-Za-z0-9_-]+)")]
	private static partial Regex LinkPattern();

	private sealed record Started(EmailApiHarness Harness, Guid MemberId, string Address) : IAsyncDisposable
	{
		public ValueTask DisposeAsync() => Harness.DisposeAsync();
	}

	private async Task<Started> StartAsync(CancellationToken ct, bool passwordChangeRequired = false)
	{
		var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		var memberId = await TestMembers.SeedAsync(h.Db, MemberName, passwordChangeRequired: passwordChangeRequired, cancellationToken: ct);
		var address = EmailApiHarness.NewAddress();
		EmailAddress.TryCreate(address, out var email, out _).ShouldBeTrue();
		await ExecuteAsync(
			h.Db,
			"INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, verified_at, set_by, updated_at) " +
			"VALUES (@id, @a, @n, 'verified', now(), @id, now())",
			ct,
			("id", memberId), ("a", email!.Value), ("n", email.Normalized));
		return new Started(h, memberId, address);
	}

	private static async Task<int> ExecuteAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		return await command.ExecuteNonQueryAsync(ct);
	}

	private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	private static async Task<HttpResponseMessage> PostAsync(EmailApiHarness h, string path, object body, CancellationToken ct)
	{
		using var anonymous = h.Factory.CreateApiClient();
		return await anonymous.PostAsJsonAsync($"/api/v1/password-recovery/{path}", body, ct);
	}

	private static Task<HttpResponseMessage> RequestAsync(EmailApiHarness h, string address, CancellationToken ct) =>
		PostAsync(h, "requests", new { address }, ct);

	private static Task<HttpResponseMessage> CheckAsync(EmailApiHarness h, string token, CancellationToken ct) =>
		PostAsync(h, "checks", new { token }, ct);

	private static Task<HttpResponseMessage> CompleteAsync(EmailApiHarness h, string token, string newPassword, CancellationToken ct) =>
		PostAsync(h, "completions", new { token, newPassword }, ct);

	private static async Task<(string Subject, string Text)> MessageAsync(EmailApiHarness h, string address, CancellationToken ct)
	{
		await h.DeliverAsync(ct);
		var found = await h.Mailpit.WaitForMessagesAsync($"to:{address}", 1, TimeSpan.FromSeconds(30), ct);
		return (found[0].Subject, await h.Mailpit.GetTextBodyAsync(found[0].Id, ct));
	}

	private static string TokenOf(string text)
	{
		var match = LinkPattern().Match(text);
		match.Success.ShouldBeTrue(text);
		return match.Groups[1].Value;
	}

	private static async Task<string> RequestAndReadTokenAsync(EmailApiHarness h, string address, CancellationToken ct)
	{
		using var accepted = await RequestAsync(h, address, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		return TokenOf((await MessageAsync(h, address, ct)).Text);
	}

	[Fact]
	// FR-002–FR-004, FR-019, FR-020: an eligible request is accepted and produces one message with the token only in the fragment
	public async Task EligibleRequestProducesOneMessageWithTheTokenInTheFragment()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct);
		var h = s.Harness;

		using var response = await RequestAsync(h, s.Address, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		(await response.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();

		var (subject, text) = await MessageAsync(h, s.Address, ct);
		subject.ShouldBe("Reset your SocAlytics password");
		text.ShouldContain("password reset was requested");
		text.ShouldContain("ignore this message");
		text.ShouldContain("valid for 1 hour");
		text.ShouldContain(LinkPrefix);
		TokenOf(text).ShouldNotBeNullOrEmpty();
		text.ShouldNotContain(MemberName);
		text.ShouldNotContain(TestMembers.DefaultPassword);
		text.ShouldNotContain("club-admin");
		text.ShouldNotContain("?token=");

		(await h.MailCountAsync(s.Address, ct)).ShouldBe(1);
	}

	[Fact]
	// FR-010: the check names the account and leaves the recovery usable
	public async Task CheckReturnsTheAccountNameAndLeavesTheRecoveryUsable()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct);
		var h = s.Harness;
		var token = await RequestAndReadTokenAsync(h, s.Address, ct);

		for (var i = 0; i < 2; i++)
		{
			using var check = await CheckAsync(h, token, ct);
			check.StatusCode.ShouldBe(HttpStatusCode.OK);
			check.Headers.CacheControl!.NoStore.ShouldBeTrue();
			(await check.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accountName").GetString().ShouldBe(MemberName);
		}

		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE state = 'issued' AND used_at IS NULL", ct)).ShouldBe(1);

		using var completed = await CompleteAsync(h, token, NewPassword, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
	}

	[Fact]
	// FR-013: a policy violation or missing password leaves the token usable
	public async Task PolicyViolationLeavesTheTokenUsable()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct);
		var h = s.Harness;
		var token = await RequestAndReadTokenAsync(h, s.Address, ct);

		using var weak = await CompleteAsync(h, token, "short", ct);
		weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		var body = await weak.Content.ReadAsStringAsync(ct);
		body.ShouldContain("newPassword");
		body.ShouldContain("password-policy");

		using var missing = await CompleteAsync(h, token, "", ct);
		missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await missing.Content.ReadAsStringAsync(ct)).ShouldContain("required");

		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE state = 'issued'", ct)).ShouldBe(1);

		using var completed = await CompleteAsync(h, token, NewPassword, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
	}

	[Fact]
	// FR-011, FR-012, FR-014: completion replaces the password, uses the token, ends sessions, creates none, and keeps identity links
	public async Task CompletionReplacesThePasswordUsesTheTokenAndEndsEverySession()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct);
		var h = s.Harness;
		await ExecuteAsync(
			h.Db,
			"INSERT INTO socalytics.external_identity_link (id, account_id, provider_id, issuer, subject, linked_at, linked_by) " +
			"VALUES (gen_random_uuid(), @a, 'test', 'https://issuer.example', 'subject-1', now(), @a)",
			ct,
			("a", s.MemberId));
		using (var earlier = await ApiSession.SignInAsync(h.Factory, MemberName, TestMembers.DefaultPassword, ct))
		{
			await ExecuteAsync(h.Db, "UPDATE socalytics.member_account SET password_change_required = true WHERE id = @a", ct, ("a", s.MemberId));
			(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.member_session WHERE member_account_id = @a AND ended_at IS NULL", ct, ("a", s.MemberId)))
				.ShouldBe(1);

			var token = await RequestAndReadTokenAsync(h, s.Address, ct);
			using var completed = await CompleteAsync(h, token, NewPassword, ct);
			completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
			completed.Headers.CacheControl!.NoStore.ShouldBeTrue();
			completed.Headers.Contains("Set-Cookie").ShouldBeFalse();

			using var again = await CompleteAsync(h, token, NewPassword, ct);
			again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			(await again.Content.ReadAsStringAsync(ct)).ShouldContain("password-recovery-invalid");
			using var checkAgain = await CheckAsync(h, token, ct);
			checkAgain.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.member_session WHERE member_account_id = @a", ct, ("a", s.MemberId))).ShouldBe(1);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.member_session WHERE member_account_id = @a AND ended_at IS NULL", ct, ("a", s.MemberId)))
			.ShouldBe(0);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.member_session WHERE member_account_id = @a AND end_reason = 'password-reset'", ct, ("a", s.MemberId)))
			.ShouldBe(1);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE state = 'used' AND used_at IS NOT NULL", ct)).ShouldBe(1);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.member_account WHERE id = @a AND password_change_required", ct, ("a", s.MemberId))).ShouldBe(0);
		(await CountAsync(
			h.Db,
			"SELECT count(*) FROM socalytics.external_identity_link WHERE account_id = @a AND issuer = 'https://issuer.example' AND subject = 'subject-1'",
			ct,
			("a", s.MemberId))).ShouldBe(1);

		using var client = h.Factory.CreateApiClient();
		using var oldPassword = await client.PostAsJsonAsync("/api/v1/session", new { accountName = MemberName, password = TestMembers.DefaultPassword }, ct);
		oldPassword.IsSuccessStatusCode.ShouldBeFalse();
		using var fresh = await ApiSession.SignInAsync(h.Factory, MemberName, NewPassword, ct);
		fresh.Info.ValueKind.ShouldBe(JsonValueKind.Object);
	}

	[Fact]
	// FR-020: the message is an account message that members cannot turn off
	public async Task RecoveryMessageIsAnAccountMessageThatCannotBeConfigured()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct);
		using var member = await ApiSession.SignInAsync(s.Harness.Factory, MemberName, TestMembers.DefaultPassword, ct);

		using var list = await member.SendAsync(HttpMethod.Get, "/api/v1/me/notification-preferences", null, ct);
		list.StatusCode.ShouldBe(HttpStatusCode.OK);
		var items = (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().ToArray();
		var item = items.Single(i => i.GetProperty("type").GetString() == "password-recovery");
		item.GetProperty("accountMessage").GetBoolean().ShouldBeTrue();
		item.GetProperty("enabled").GetBoolean().ShouldBeTrue();

		using var refused = await member.SendAsync(HttpMethod.Put, "/api/v1/me/notification-preferences/password-recovery", new { enabled = false }, ct);
		refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await refused.Content.ReadAsStringAsync(ct)).ShouldContain("notification-type-not-configurable");
	}
}
