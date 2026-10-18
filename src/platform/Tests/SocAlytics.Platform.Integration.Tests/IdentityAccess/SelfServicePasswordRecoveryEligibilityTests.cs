using System.Diagnostics;
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
public sealed partial class SelfServicePasswordRecoveryEligibilityTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private const string NewPassword = "a-brand-new-password";
	private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(500);

	[GeneratedRegex(@"http://localhost:5173/password-recovery/complete#token=([A-Za-z0-9_-]+)")]
	private static partial Regex LinkPattern();

	private Task<EmailApiHarness> StartAsync(CancellationToken ct) =>
		EmailApiHarness.StartAsync(
			postgres,
			mailpit,
			ct,
			new Dictionary<string, string?>
			{
				["PasswordRecovery:MinimumResponseDuration"] = MinimumDuration.ToString("c"),
				["PasswordRecovery:RequestsPerMinute"] = "1000",
				["PasswordRecovery:MaxMessagesPerHour"] = "1000",
				["PasswordRecovery:TokenAttemptsPerMinute"] = "1000",
			});

	private static async Task<Guid> SeedMemberAsync(
		EmailApiHarness h,
		string name,
		string? address,
		CancellationToken ct,
		bool verified = true,
		string? password = TestMembers.DefaultPassword,
		string membershipStatus = "active",
		bool lockedOut = false)
	{
		var id = await TestMembers.SeedAsync(h.Db, name, password, membershipStatus, lockedOut: lockedOut, cancellationToken: ct);
		if (address is not null)
		{
			EmailAddress.TryCreate(address, out var email, out _).ShouldBeTrue();
			await ExecuteAsync(
				h.Db,
				"INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, verified_at, set_by, updated_at) " +
				"VALUES (@id, @a, @n, @s, CASE WHEN @s = 'verified' THEN now() ELSE NULL END, @id, now())",
				ct,
				("id", id), ("a", email!.Value), ("n", email.Normalized), ("s", verified ? "verified" : "unverified"));
		}

		return id;
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

	private static Task<HttpResponseMessage> RequestAsync(EmailApiHarness h, string? address, CancellationToken ct) =>
		PostAsync(h, "requests", new { address }, ct);

	private static Task<HttpResponseMessage> CheckAsync(EmailApiHarness h, string token, CancellationToken ct) =>
		PostAsync(h, "checks", new { token }, ct);

	private static Task<HttpResponseMessage> CompleteAsync(EmailApiHarness h, string token, string newPassword, CancellationToken ct) =>
		PostAsync(h, "completions", new { token, newPassword }, ct);

	private static async Task<HttpResponseMessage> SignInRawAsync(EmailApiHarness h, string name, string password, CancellationToken ct)
	{
		using var client = h.Factory.CreateApiClient();
		return await client.PostAsJsonAsync("/api/v1/session", new { accountName = name, password }, ct);
	}

	private static async Task<string[]> TokensAsync(EmailApiHarness h, string address, int count, CancellationToken ct)
	{
		await h.DeliverAsync(ct);
		var found = await h.Mailpit.WaitForMessagesAsync($"to:{address}", count, TimeSpan.FromSeconds(30), ct);
		var tokens = new List<string>();
		foreach (var message in found)
		{
			var match = LinkPattern().Match(await h.Mailpit.GetTextBodyAsync(message.Id, ct));
			match.Success.ShouldBeTrue();
			tokens.Add(match.Groups[1].Value);
		}

		return [.. tokens];
	}

	private static async Task<string> RequestAndReadTokenAsync(EmailApiHarness h, string address, CancellationToken ct)
	{
		using var accepted = await RequestAsync(h, address, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		return (await TokensAsync(h, address, 1, ct))[0];
	}

	// Problem bodies of the same failure only differ in per-request values.
	private static async Task<string> ProblemShapeAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		var shape = body.EnumerateObject()
			.Where(p => p.Name is not "correlationId")
			.Select(p => $"{p.Name}={p.Value.GetRawText()}");
		return string.Join(";", shape);
	}

	private sealed record Answer(HttpStatusCode Status, string Body, string? CacheControl, TimeSpan Elapsed);

	private static async Task<Answer> TimedRequestAsync(EmailApiHarness h, string? address, CancellationToken ct)
	{
		var watch = Stopwatch.StartNew();
		using var response = await RequestAsync(h, address, ct);
		var body = await response.Content.ReadAsStringAsync(ct);
		watch.Stop();
		return new Answer(response.StatusCode, body, response.Headers.CacheControl?.ToString(), watch.Elapsed);
	}

	[Fact]
	// FR-002, FR-003, FR-005; SC-002: every address gets the same answer in status and body, none faster than the minimum, and only the eligible member receives a message
	public async Task AnswersAreIdenticalAndTakeTheMinimumDurationAndOnlyTheEligibleMemberReceivesAMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var unknown = EmailApiHarness.NewAddress();
		var unverified = EmailApiHarness.NewAddress();
		var deactivated = EmailApiHarness.NewAddress();
		var providerOnly = EmailApiHarness.NewAddress();
		var unredeemed = EmailApiHarness.NewAddress();
		var eligible = EmailApiHarness.NewAddress();
		await SeedMemberAsync(h, "unverified-1", unverified, ct, verified: false);
		await SeedMemberAsync(h, "deactivated-1", deactivated, ct, membershipStatus: "deactivated");
		var providerId = await SeedMemberAsync(h, "provider-1", providerOnly, ct, password: null);
		await ExecuteAsync(
			h.Db,
			"INSERT INTO socalytics.external_identity_link (id, account_id, provider_id, issuer, subject, linked_at, linked_by) " +
			"VALUES (gen_random_uuid(), @a, 'test', 'https://issuer.example', 'subject-1', now(), @a)",
			ct,
			("a", providerId));
		await SeedMemberAsync(h, "unredeemed-1", unredeemed, ct, password: null);
		await SeedMemberAsync(h, "eligible-1", eligible, ct);

		var cases = new (string Name, string Address)[]
		{
			("unknown", unknown),
			("unverified", unverified),
			("deactivated", deactivated),
			("provider-only", providerOnly),
			("unredeemed", unredeemed),
			("eligible", eligible),
		};

		foreach (var (_, address) in cases)
		{
			await TimedRequestAsync(h, address, ct);
		}

		const int rounds = 4;
		var elapsed = cases.ToDictionary(c => c.Name, _ => new List<TimeSpan>());
		Answer? reference = null;
		for (var round = 0; round < rounds; round++)
		{
			foreach (var (name, address) in cases)
			{
				var answer = await TimedRequestAsync(h, address, ct);
				answer.Status.ShouldBe(HttpStatusCode.Accepted, name);
				answer.Body.ShouldBeEmpty(name);
				answer.CacheControl.ShouldBe("no-store", name);
				answer.Elapsed.ShouldBeGreaterThanOrEqualTo(MinimumDuration - TimeSpan.FromMilliseconds(20), name);
				reference ??= answer;
				(answer.Status, answer.Body, answer.CacheControl).ShouldBe((reference.Status, reference.Body, reference.CacheControl), name);
				elapsed[name].Add(answer.Elapsed);
			}
		}

		var medians = elapsed.ToDictionary(
			e => e.Key,
			e => e.Value.OrderBy(v => v).ElementAt(e.Value.Count / 2).TotalMilliseconds);
		var eligibleMedian = medians["eligible"];
		foreach (var (name, median) in medians.Where(m => m.Key != "eligible"))
		{
			(eligibleMedian - median).ShouldBeLessThan(250, $"eligible was consistently slower than {name}");
			(median - eligibleMedian).ShouldBeLessThan(250, $"{name} was consistently slower than eligible");
		}

		foreach (var (name, address) in cases.Where(c => c.Name != "eligible"))
		{
			(await h.MailCountAsync(address, ct)).ShouldBe(0, name);
		}

		(await h.MailCountAsync(eligible, ct)).ShouldBeGreaterThan(0);
		(await CountAsync(
			h.Db,
			"SELECT count(*) FROM socalytics.password_recoveries r JOIN socalytics.member_account a ON a.id = r.account_id WHERE a.account_name <> 'eligible-1'",
			ct)).ShouldBe(0);
	}

	[Theory]
	[InlineData(null, "required")]
	[InlineData("", "required")]
	[InlineData("   ", "required")]
	[InlineData("no-at-sign", "invalid")]
	[InlineData("two@@example.test", "invalid")]
	// FR-006: an empty or malformed address fails field validation, and the failure is also delayed
	public async Task EmptyOrMalformedAddressFailsFieldValidation(string? address, string code)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);

		var watch = Stopwatch.StartNew();
		using var response = await RequestAsync(h, address, ct);
		watch.Stop();

		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		var body = await response.Content.ReadAsStringAsync(ct);
		body.ShouldContain("address");
		body.ShouldContain(code);
		watch.Elapsed.ShouldBeGreaterThanOrEqualTo(MinimumDuration - TimeSpan.FromMilliseconds(20));
	}

	[Fact]
	// FR-007: a shared address sends one message per eligible member and each link applies only to that member
	public async Task SharedAddressSendsOneMessagePerEligibleMemberAndEachLinkAppliesOnlyToThatMember()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var shared = EmailApiHarness.NewAddress();
		await SeedMemberAsync(h, "parent-1", shared, ct);
		await SeedMemberAsync(h, "child-1", shared, ct);
		await SeedMemberAsync(h, "inactive-1", shared, ct, membershipStatus: "deactivated");

		using var accepted = await RequestAsync(h, shared, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		var tokens = await TokensAsync(h, shared, 2, ct);
		(await h.MailCountAsync(shared, ct)).ShouldBe(2);
		tokens.Distinct().Count().ShouldBe(2);

		var names = new List<string>();
		foreach (var token in tokens)
		{
			using var check = await CheckAsync(h, token, ct);
			check.StatusCode.ShouldBe(HttpStatusCode.OK);
			names.Add((await check.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accountName").GetString()!);
		}

		names.Order().ShouldBe(["child-1", "parent-1"]);

		var parentToken = tokens[names.IndexOf("parent-1")];
		using var completed = await CompleteAsync(h, parentToken, NewPassword, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		using var parentNew = await SignInRawAsync(h, "parent-1", NewPassword, ct);
		parentNew.IsSuccessStatusCode.ShouldBeTrue();
		using var parentOld = await SignInRawAsync(h, "parent-1", TestMembers.DefaultPassword, ct);
		parentOld.IsSuccessStatusCode.ShouldBeFalse();
		using var childOld = await SignInRawAsync(h, "child-1", TestMembers.DefaultPassword, ct);
		childOld.IsSuccessStatusCode.ShouldBeTrue();
		using var childNew = await SignInRawAsync(h, "child-1", NewPassword, ct);
		childNew.IsSuccessStatusCode.ShouldBeFalse();
	}

	[Fact]
	// FR-004, FR-012: a member deactivated after the message was sent gets the neutral failure and stays deactivated
	public async Task MemberDeactivatedAfterTheMessageGetsTheNeutralFailureAndStaysDeactivated()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var address = EmailApiHarness.NewAddress();
		var id = await SeedMemberAsync(h, "member-1", address, ct);
		var token = await RequestAndReadTokenAsync(h, address, ct);
		var hash = await ScalarTextAsync(h.Db, "SELECT password_hash FROM socalytics.member_account WHERE id = @a", ct, ("a", id));

		await ExecuteAsync(h.Db, "UPDATE socalytics.member_account SET membership_status = 'deactivated' WHERE id = @a", ct, ("a", id));

		using var check = await CheckAsync(h, token, ct);
		check.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await check.Content.ReadAsStringAsync(ct)).ShouldContain("password-recovery-invalid");
		using var completed = await CompleteAsync(h, token, NewPassword, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await completed.Content.ReadAsStringAsync(ct)).ShouldContain("password-recovery-invalid");

		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.member_account WHERE id = @a AND membership_status = 'deactivated'", ct, ("a", id))).ShouldBe(1);
		(await ScalarTextAsync(h.Db, "SELECT password_hash FROM socalytics.member_account WHERE id = @a", ct, ("a", id))).ShouldBe(hash);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE state = 'used'", ct)).ShouldBe(0);
	}

	private static async Task<string?> ScalarTextAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		return await command.ExecuteScalarAsync(ct) as string;
	}

	[Fact]
	// FR-013: unknown, expired, and used tokens all fail with the same password-recovery-invalid answer
	public async Task UnknownExpiredAndUsedTokensFailWithTheSameAnswer()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var expiredAddress = EmailApiHarness.NewAddress();
		var usedAddress = EmailApiHarness.NewAddress();
		await SeedMemberAsync(h, "expiring-1", expiredAddress, ct);
		await SeedMemberAsync(h, "using-1", usedAddress, ct);

		var expiredToken = await RequestAndReadTokenAsync(h, expiredAddress, ct);
		var usedToken = await RequestAndReadTokenAsync(h, usedAddress, ct);
		using (var completed = await CompleteAsync(h, usedToken, NewPassword, ct))
		{
			completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		h.Factory.Time.Advance(TimeSpan.FromHours(2));
		var unknownToken = new string('A', 43);

		var shapes = new HashSet<string>();
		foreach (var token in new[] { unknownToken, expiredToken, usedToken })
		{
			using var check = await CheckAsync(h, token, ct);
			check.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			shapes.Add(await ProblemShapeAsync(check, ct));
			using var complete = await CompleteAsync(h, token, NewPassword, ct);
			complete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			var shape = await ProblemShapeAsync(complete, ct);
			shape.ShouldContain("password-recovery-invalid");
			shapes.Add(shape);
		}

		// One shape for the checks and one for the completions, whatever the reason.
		shapes.Count.ShouldBeLessThanOrEqualTo(2);
		using var oldPassword = await SignInRawAsync(h, "expiring-1", TestMembers.DefaultPassword, ct);
		oldPassword.IsSuccessStatusCode.ShouldBeTrue();
	}

	[Fact]
	// FR-012: a locked-out member completes a recovery and remains locked until the lockout ends
	public async Task LockedOutMemberCompletesARecoveryAndRemainsLockedUntilTheLockoutEnds()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var address = EmailApiHarness.NewAddress();
		var id = await SeedMemberAsync(h, "locked-1", address, ct, lockedOut: true);
		var token = await RequestAndReadTokenAsync(h, address, ct);

		using var completed = await CompleteAsync(h, token, NewPassword, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.member_account WHERE id = @a AND lockout_end IS NOT NULL", ct, ("a", id))).ShouldBe(1);
		using var locked = await SignInRawAsync(h, "locked-1", NewPassword, ct);
		locked.IsSuccessStatusCode.ShouldBeFalse();

		h.Factory.Time.Advance(TimeSpan.FromDays(800));
		using var unlocked = await SignInRawAsync(h, "locked-1", NewPassword, ct);
		unlocked.IsSuccessStatusCode.ShouldBeTrue();
	}

	[Fact]
	// FR-003, FR-012: recovery never creates a password for a member without one
	public async Task RecoveryNeverCreatesAPasswordForAMemberWithoutOne()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var withoutAddress = EmailApiHarness.NewAddress();
		var withoutId = await SeedMemberAsync(h, "no-password-1", withoutAddress, ct, password: null);
		var losingAddress = EmailApiHarness.NewAddress();
		var losingId = await SeedMemberAsync(h, "losing-1", losingAddress, ct);

		using var accepted = await RequestAsync(h, withoutAddress, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		(await h.MailCountAsync(withoutAddress, ct)).ShouldBe(0);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE account_id = @a", ct, ("a", withoutId))).ShouldBe(0);

		var token = await RequestAndReadTokenAsync(h, losingAddress, ct);
		await ExecuteAsync(h.Db, "UPDATE socalytics.member_account SET password_hash = NULL WHERE id = @a", ct, ("a", losingId));

		using var completed = await CompleteAsync(h, token, NewPassword, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await completed.Content.ReadAsStringAsync(ct)).ShouldContain("password-recovery-invalid");

		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.member_account WHERE id = ANY(@ids) AND password_hash IS NOT NULL", ct, ("ids", new[] { withoutId, losingId })))
			.ShouldBe(0);
	}
}
