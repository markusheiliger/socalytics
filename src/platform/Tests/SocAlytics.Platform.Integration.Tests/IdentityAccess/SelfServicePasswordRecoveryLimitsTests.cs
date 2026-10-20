using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
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
public sealed partial class SelfServicePasswordRecoveryLimitsTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private const string MemberName = "member-1";
	private const string NewPassword = "a-brand-new-password";
	private const string Temporary = "Recovery-Temporary-Pass-5678";

	[GeneratedRegex(@"http://localhost:5173/password-recovery/complete#token=([A-Za-z0-9_-]+)")]
	private static partial Regex LinkPattern();

	private sealed record Started(EmailApiHarness Harness, Guid MemberId, string Address) : IAsyncDisposable
	{
		public ValueTask DisposeAsync() => Harness.DisposeAsync();
	}

	private async Task<Started> StartAsync(CancellationToken ct, bool relaxedRequestLimit = false)
	{
		var settings = new Dictionary<string, string?> { ["PasswordRecovery:MinimumResponseDuration"] = "00:00:00.010" };
		if (relaxedRequestLimit)
		{
			settings["PasswordRecovery:RequestsPerMinute"] = "1000";
		}

		var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct, settings);
		var memberId = await TestMembers.SeedAsync(h.Db, MemberName, cancellationToken: ct);
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

	private static async Task<object?> ScalarAsync(string connectionString, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(connectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		return await command.ExecuteScalarAsync(ct);
	}

	private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters) =>
		Convert.ToInt64(await ScalarAsync(db.AppConnectionString, sql, ct, parameters));

	private static async Task<long> EventsAsync(IsolatedDatabase db, string type, CancellationToken ct, string? outcome = null, string? reason = null) =>
		Convert.ToInt64(await ScalarAsync(
			db.MigratorConnectionString,
			"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = @t AND (@o::text IS NULL OR outcome = @o) AND (@r::text IS NULL OR reason_code = @r)",
			ct,
			("t", type), ("o", (object?)outcome ?? DBNull.Value), ("r", (object?)reason ?? DBNull.Value)));

	private static async Task<HttpResponseMessage> PostAsync(EmailApiHarness h, string path, object body, CancellationToken ct)
	{
		using var anonymous = h.Factory.CreateApiClient();
		// TestServer clients do not propagate trace context, so the host's server activities would start a new trace.
		if (Activity.Current?.Id is { } traceparent)
		{
			anonymous.DefaultRequestHeaders.TryAddWithoutValidation("traceparent", traceparent);
		}

		return await anonymous.PostAsJsonAsync($"/api/v1/password-recovery/{path}", body, ct);
	}

	private static Task<HttpResponseMessage> RequestAsync(EmailApiHarness h, string address, CancellationToken ct) =>
		PostAsync(h, "requests", new { address }, ct);

	private static Task<HttpResponseMessage> CheckAsync(EmailApiHarness h, string token, CancellationToken ct) =>
		PostAsync(h, "checks", new { token }, ct);

	private static Task<HttpResponseMessage> CompleteAsync(EmailApiHarness h, string token, string newPassword, CancellationToken ct) =>
		PostAsync(h, "completions", new { token, newPassword }, ct);

	private static async Task<string[]> BodiesAsync(EmailApiHarness h, string address, int count, CancellationToken ct)
	{
		await h.DeliverAsync(ct);
		var found = await h.Mailpit.WaitForMessagesAsync($"to:{address}", count, TimeSpan.FromSeconds(30), ct);
		var bodies = new List<string>();
		foreach (var message in found)
		{
			bodies.Add(await h.Mailpit.GetTextBodyAsync(message.Id, ct));
		}

		return [.. bodies];
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
		return TokenOf((await BodiesAsync(h, address, 1, ct))[0]);
	}

	private static async Task ShouldBeNeutralFailureAsync(EmailApiHarness h, string token, CancellationToken ct)
	{
		using var check = await CheckAsync(h, token, ct);
		check.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await check.Content.ReadAsStringAsync(ct)).ShouldContain("password-recovery-invalid");
		using var completed = await CompleteAsync(h, token, NewPassword, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await completed.Content.ReadAsStringAsync(ct)).ShouldContain("password-recovery-invalid");
	}

	private static async Task ShouldBeRateLimitedAsync(HttpResponseMessage response, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
		response.Headers.RetryAfter.ShouldNotBeNull();
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		(await response.Content.ReadAsStringAsync(ct)).ShouldContain("password-recovery-rate-limited");
	}

	[Fact]
	// FR-015; SC-006: a fourth request within an hour for one member answers 202 and sends nothing
	public async Task FourthRequestWithinAnHourAnswersAcceptedAndSendsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct, relaxedRequestLimit: true);
		var h = s.Harness;

		for (var i = 0; i < 3; i++)
		{
			using var accepted = await RequestAsync(h, s.Address, ct);
			accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
			await h.DeliverAsync(ct);
		}

		(await h.MailCountAsync(s.Address, ct)).ShouldBe(3);

		using var fourth = await RequestAsync(h, s.Address, ct);
		fourth.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		(await fourth.Content.ReadAsStringAsync(ct)).ShouldBeEmpty();

		(await h.MailCountAsync(s.Address, ct)).ShouldBe(3);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE account_id = @a", ct, ("a", s.MemberId))).ShouldBe(3);
		(await EventsAsync(h.Db, "password-recovery.requested", ct, reason: "rate-limited")).ShouldBe(1);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	// FR-016; SC-006: the sixth request per minute from one client is refused, for known and unknown addresses alike
	public async Task SixthRequestPerMinuteFromOneClientIsRefused(bool known)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct);
		var address = known ? s.Address : EmailApiHarness.NewAddress();

		for (var i = 0; i < 5; i++)
		{
			using var accepted = await RequestAsync(s.Harness, address, ct);
			accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		}

		using var sixth = await RequestAsync(s.Harness, address, ct);
		await ShouldBeRateLimitedAsync(sixth, ct);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	// FR-016; SC-006: the eleventh check or completion per minute from one client is refused, for known and unknown tokens alike
	public async Task EleventhCheckOrCompletionPerMinuteFromOneClientIsRefused(bool known)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct);
		var h = s.Harness;
		var token = known ? await RequestAndReadTokenAsync(h, s.Address, ct) : new string('A', 43);

		for (var i = 0; i < 10; i++)
		{
			using var check = await CheckAsync(h, token, ct);
			check.StatusCode.ShouldBe(known ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
		}

		using var eleventh = await CheckAsync(h, token, ct);
		await ShouldBeRateLimitedAsync(eleventh, ct);
		using var completion = await CompleteAsync(h, token, NewPassword, ct);
		await ShouldBeRateLimitedAsync(completion, ct);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE state = 'used'", ct)).ShouldBe(0);
	}

	[Fact]
	// FR-009: a second accepted request makes the first link fail and the link of the second message work
	public async Task SecondAcceptedRequestMakesTheFirstLinkFail()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct, relaxedRequestLimit: true);
		var h = s.Harness;
		var first = await RequestAndReadTokenAsync(h, s.Address, ct);

		using var again = await RequestAsync(h, s.Address, ct);
		again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		var second = (await BodiesAsync(h, s.Address, 2, ct)).Select(TokenOf).Single(t => t != first);

		await ShouldBeNeutralFailureAsync(h, first, ct);
		using var check = await CheckAsync(h, second, ct);
		check.StatusCode.ShouldBe(HttpStatusCode.OK);
		using var completed = await CompleteAsync(h, second, NewPassword, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
	}

	private static async Task<HttpResponseMessage> AdminSendAsync(EmailApiHarness h, HttpMethod method, string path, object? body, long? version, CancellationToken ct)
	{
		var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		request.Headers.Add("X-CSRF-Token", h.Admin.AntiforgeryToken);
		if (version is { } v)
		{
			request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
		}

		return await h.Admin.Client.SendAsync(request, ct);
	}

	private static async Task<long> AddressVersionAsync(EmailApiHarness h, Guid memberId, CancellationToken ct)
	{
		using var response = await h.Admin.SendAsync(HttpMethod.Get, $"/api/v1/members/{memberId}/email", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("version").GetInt64();
	}

	[Theory]
	[InlineData("password-change")]
	[InlineData("club-admin-reset")]
	[InlineData("address-change")]
	[InlineData("address-removal")]
	[InlineData("deactivation")]
	// FR-009: every account change that affects the credential or the address makes an open link fail with the neutral answer
	public async Task AccountChangeMakesAnOpenLinkFail(string change)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct, relaxedRequestLimit: true);
		var h = s.Harness;
		var token = await RequestAndReadTokenAsync(h, s.Address, ct);

		switch (change)
		{
			case "password-change":
				using (var member = await ApiSession.SignInAsync(h.Factory, MemberName, TestMembers.DefaultPassword, ct))
				{
					using var changed = await member.SendAsync(
						HttpMethod.Post, "/api/v1/me/password", new { currentPassword = TestMembers.DefaultPassword, newPassword = "Changed-Password-91011" }, ct);
					changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
				}

				break;
			case "club-admin-reset":
				using (var issued = await h.Admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{s.MemberId}/credentials", new { purpose = "password-reset" }, ct))
				{
					issued.StatusCode.ShouldBe(HttpStatusCode.Created);
					var credential = (await issued.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("credential").GetString();
					using var anonymous = h.Factory.CreateApiClient();
					using var redeemed = await anonymous.PostAsJsonAsync(
						"/api/v1/credentials/redeem", new { accountName = MemberName, credential, newPassword = "Changed-Password-91011" }, ct);
					redeemed.IsSuccessStatusCode.ShouldBeTrue();
				}

				break;
			case "address-change":
				using (var set = await AdminSendAsync(
					h, HttpMethod.Put, $"/api/v1/members/{s.MemberId}/email", new { address = EmailApiHarness.NewAddress() }, await AddressVersionAsync(h, s.MemberId, ct), ct))
				{
					set.StatusCode.ShouldBe(HttpStatusCode.OK);
				}

				break;
			case "address-removal":
				using (var removed = await AdminSendAsync(
					h, HttpMethod.Delete, $"/api/v1/members/{s.MemberId}/email", null, await AddressVersionAsync(h, s.MemberId, ct), ct))
				{
					removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
				}

				break;
			default:
				using (var deactivated = await h.Admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{s.MemberId}/deactivate", null, ct))
				{
					deactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
				}

				break;
		}

		await ShouldBeNeutralFailureAsync(h, token, ct);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE state = 'used'", ct)).ShouldBe(0);
	}

	[Fact]
	// FR-009: a break-glass directive applied while a link is open makes the link fail with the neutral answer
	public async Task BreakGlassDirectiveMakesAnOpenLinkFail()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(ct, relaxedRequestLimit: true);
		var h = s.Harness;
		var token = await RequestAndReadTokenAsync(h, s.Address, ct);

		var bootstrap = new Dictionary<string, string?>
		{
			["ClubDisplayName"] = "Example FC",
			["FirstClubAdmin:AccountName"] = EmailApiHarness.AdminName,
			["FirstClubAdmin:InitialPassword"] = "Recovery-Initial-Pass-1234",
		};
		var directive = new Dictionary<string, string?>
		{
			["AccountName"] = MemberName,
			["RecoveryId"] = "recovery-0001",
			["TemporaryCredential"] = Temporary,
		};
		await using (var second = new PlatformApiFactory(h.Db, bootstrap, directive))
		{
			using var client = second.CreateApiClient();
			var deadline = DateTime.UtcNow.AddSeconds(60);
			while (await EventsAsync(h.Db, "break-glass-recovery.applied", ct) == 0)
			{
				(DateTime.UtcNow < deadline).ShouldBeTrue();
				await Task.Delay(200, ct);
			}
		}

		await ShouldBeNeutralFailureAsync(h, token, ct);
		(await CountAsync(h.Db, "SELECT count(*) FROM socalytics.password_recoveries WHERE state = 'used'", ct)).ShouldBe(0);
	}

	[Fact]
	// FR-017, FR-018; SC-005: one audit event per step with masked addresses, and no secret in audit events, logs, or telemetry
	public async Task AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var telemetry = new StringBuilder();
		ActivityTraceId testTrace = default;
		using var testSource = new ActivitySource(TestSourceName);
		using var activities = new ActivityListener
		{
			ShouldListenTo = source => IsScopeOfHost(source.Name),
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity =>
			{
				if (activity.TraceId != testTrace)
				{
					return;
				}

				lock (telemetry)
				{
					telemetry.Append(activity.DisplayName).Append(' ').Append(activity.OperationName).Append(' ');
					foreach (var tag in activity.Tags)
					{
						telemetry.Append(tag.Key).Append('=').Append(tag.Value).Append(' ');
					}

					foreach (var tag in activity.TagObjects)
					{
						telemetry.Append(tag.Key).Append('=').Append(tag.Value).Append(' ');
					}

					foreach (var activityEvent in activity.Events)
					{
						telemetry.Append(activityEvent.Name).Append(' ');
						foreach (var tag in activityEvent.Tags)
						{
							telemetry.Append(tag.Key).Append('=').Append(tag.Value).Append(' ');
						}
					}
				}
			},
		};
		ActivitySource.AddActivityListener(activities);
		using var meters = new MeterListener();
		meters.InstrumentPublished = (instrument, listener) =>
		{
			if (IsScopeOfHost(instrument.Meter.Name))
			{
				listener.EnableMeasurementEvents(instrument);
			}
		};
		void Measured(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
		{
			if (Activity.Current?.TraceId != testTrace)
			{
				return;
			}

			AppendTags(telemetry, tags);
		}

		meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Measured(instrument, tags));
		meters.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Measured(instrument, tags));
		meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Measured(instrument, tags));
		meters.Start();

		using var testActivity = testSource.StartActivity("secret-telemetry-test");
		testTrace = testActivity!.TraceId;

		await using var s = await StartAsync(ct, relaxedRequestLimit: true);
		lock (telemetry)
		{
			telemetry.Clear();
		}

		var h = s.Harness;
		var unknown = EmailApiHarness.NewAddress();
		EmailAddress.TryCreate(s.Address, out var known, out _).ShouldBeTrue();
		EmailAddress.TryCreate(unknown, out var unknownAddress, out _).ShouldBeTrue();

		using (var knownRequest = await RequestAsync(h, s.Address, ct))
		{
			knownRequest.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		}

		using (var unknownRequest = await RequestAsync(h, unknown, ct))
		{
			unknownRequest.StatusCode.ShouldBe(HttpStatusCode.Accepted);
		}

		var body = (await BodiesAsync(h, s.Address, 1, ct))[0];
		var token = TokenOf(body);

		using (var weak = await CompleteAsync(h, token, "short", ct))
		{
			weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		using (var completed = await CompleteAsync(h, token, NewPassword, ct))
		{
			completed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		using (var again = await CompleteAsync(h, token, NewPassword, ct))
		{
			again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		(await EventsAsync(h.Db, "password-recovery.requested", ct)).ShouldBe(2);
		(await EventsAsync(h.Db, "password-recovery.requested", ct, reason: "queued")).ShouldBe(1);
		(await EventsAsync(h.Db, "password-recovery.requested", ct, reason: "no-eligible-account")).ShouldBe(1);
		(await EventsAsync(h.Db, "password-recovery.issued", ct)).ShouldBe(1);
		(await EventsAsync(h.Db, "password-recovery.completed", ct)).ShouldBe(3);
		(await EventsAsync(h.Db, "password-recovery.completed", ct, "succeeded")).ShouldBe(1);
		(await EventsAsync(h.Db, "password-recovery.completed", ct, "failed", "password-policy")).ShouldBe(1);
		(await EventsAsync(h.Db, "password-recovery.completed", ct, "failed", "recovery-invalid")).ShouldBe(1);

		const string masked = "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = @t AND details->>'maskedAddress' = @m";
		(await ScalarAsync(h.Db.MigratorConnectionString, masked, ct, ("t", "password-recovery.requested"), ("m", known!.Masked))).ShouldBe(2L);
		unknownAddress!.Masked.ShouldBe(known.Masked);
		(await ScalarAsync(h.Db.MigratorConnectionString, masked, ct, ("t", "password-recovery.issued"), ("m", known.Masked))).ShouldBe(1L);
		(await ScalarAsync(h.Db.MigratorConnectionString, masked, ct, ("t", "password-recovery.completed"), ("m", known.Masked))).ShouldBe(2L);

		var audit = (string?)await ScalarAsync(
			h.Db.MigratorConnectionString,
			"SELECT coalesce(string_agg(row_to_json(e)::text, ' '), '') FROM socalytics.security_audit_event e",
			ct);
		var logs = string.Join(
			' ',
			h.Factory.Logs.Entries.Select(e => $"{e.Message} {e.Exception} {string.Join(' ', e.State.Select(p => $"{p.Key}={p.Value}"))}"));
		string telemetryText;
		lock (telemetry)
		{
			telemetryText = telemetry.ToString();
		}

		var secrets = new[] { s.Address, known.Normalized, unknown, token, NewPassword, "short", TestMembers.DefaultPassword, "password reset was requested", body };
		foreach (var (name, text) in new[] { ("audit", audit!), ("logs", logs), ("telemetry", telemetryText) })
		{
			foreach (var secret in secrets)
			{
				text.ShouldNotContain(secret, Case.Insensitive, $"{name} contains a secret");
			}
		}
	}

	private const string TestSourceName = "SocAlytics.Integration.Tests.SecretTelemetry";

	private static readonly string[] HostScopes = ["Microsoft.AspNetCore", "System.Net.Http", "SocAlytics", "MailKit"];

	// Sources and meters are process-wide statics shared with parallel hosts, so only telemetry of the test's own trace counts.
	private static bool IsScopeOfHost(string name) =>
		!name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) &&
		HostScopes.Any(scope => name.StartsWith(scope, StringComparison.Ordinal));

	private static void AppendTags(StringBuilder sink, ReadOnlySpan<KeyValuePair<string, object?>> tags)
	{
		lock (sink)
		{
			foreach (var tag in tags)
			{
				sink.Append(tag.Key).Append('=').Append(tag.Value).Append(' ');
			}
		}
	}
}
