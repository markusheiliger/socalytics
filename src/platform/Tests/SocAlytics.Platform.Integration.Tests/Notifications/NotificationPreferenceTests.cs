using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Notifications;

public sealed class NotificationPreferenceTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private const string Preferences = "/api/v1/me/notification-preferences";
	private const string Registrations = "/api/v1/analyst-manager-registrations";

	private static readonly Dictionary<string, string?> RegistrySettings = new()
	{
		["Registry:AllowSoftwareKeys"] = "true",
		["Registry:RateLimits:PairingCreationPerMinute"] = "10000",
		["Registry:RateLimits:CodeSubmissionPerMinute"] = "10000",
	};

	private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
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

	private static async Task<Guid> AccountIdAsync(IsolatedDatabase db, string name, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT id FROM socalytics.member_account WHERE account_name = @n", connection);
		command.Parameters.AddWithValue("n", name);
		return (Guid)(await command.ExecuteScalarAsync(ct))!;
	}

	private static async Task VerifyAddressAsync(IsolatedDatabase db, Guid accountId, string address, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, verified_at, set_by, updated_at) " +
			"VALUES (@id, @a, @a, 'verified', now(), @id, now())", connection);
		command.Parameters.AddWithValue("id", accountId);
		command.Parameters.AddWithValue("a", address);
		await command.ExecuteNonQueryAsync(ct);
	}

	private static Task<HttpResponseMessage> SetAsync(ApiSession session, string type, bool enabled, CancellationToken ct) =>
		session.SendAsync(HttpMethod.Put, $"{Preferences}/{type}", new { enabled }, ct);

	private static async Task<JsonElement[]> ListAsync(ApiSession session, CancellationToken ct)
	{
		using var response = await session.SendAsync(HttpMethod.Get, Preferences, null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		return body.GetProperty("items").EnumerateArray().ToArray();
	}

	private static async Task SubmitPairingAsync(EmailApiHarness h, ApiSession registrar, CancellationToken ct)
	{
		using var manager = new TestAnalystManager();
		using var anonymous = h.Factory.CreateApiClient();
		using var pair = await RegistryTestHost.PairAsync(anonymous, RegistryTestHost.PairingBody(manager), ct);
		pair.StatusCode.ShouldBe(HttpStatusCode.Created);
		var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
		using var submitted = await registrar.SendAsync(
			HttpMethod.Post,
			Registrations,
			new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() },
			ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync(ct));
	}

	[Fact]
	// US3 scenarios 1 and 4: types for the roles, on by default, account messages always sent
	public async Task MembersSeeOnlyTheTypesOfTheirRolesAndAccountMessagesAreAlwaysSent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);

		var items = await ListAsync(h.Admin, ct);
		items.Select(i => i.GetProperty("type").GetString()).ShouldBe(["email-confirmation", "registration-pending"], ignoreOrder: true);
		var pending = items.Single(i => i.GetProperty("type").GetString() == "registration-pending");
		pending.GetProperty("enabled").GetBoolean().ShouldBeTrue();
		pending.GetProperty("accountMessage").GetBoolean().ShouldBeFalse();
		var account = items.Single(i => i.GetProperty("type").GetString() == "email-confirmation");
		account.GetProperty("enabled").GetBoolean().ShouldBeTrue();
		account.GetProperty("accountMessage").GetBoolean().ShouldBeTrue();

		await TestMembers.SeedAsync(h.Db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var registrar = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);
		(await ListAsync(registrar, ct)).Select(i => i.GetProperty("type").GetString()).ShouldBe(["email-confirmation"]);
	}

	[Fact]
	// US3 scenarios 1 and 4: refusals
	public async Task AccountMessagesAndTypesOutsideTheRolesCannotBeConfigured()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);
		await TestMembers.SeedAsync(h.Db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var registrar = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);

		foreach (var (session, type) in new[] { (h.Admin, "email-confirmation"), (h.Admin, "unknown-type"), (registrar, "registration-pending") })
		{
			using var response = await SetAsync(session, type, false, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			(await response.Content.ReadAsStringAsync(ct)).ShouldContain("notification-type-not-configurable");
		}

		(await ScalarAsync(h.Db, "SELECT count(*) FROM socalytics.notification_preferences", ct)).ShouldBe(0);
		(await ListAsync(h.Admin, ct)).Length.ShouldBe(2);
	}

	[Fact]
	// US3 scenarios 1 to 3: a row is stored only when it differs from the default; the change is audited
	public async Task TurningATypeOffAndOnStoresOnlyDeviationsAndAuditsTheChange()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct);

		using (var unchanged = await SetAsync(h.Admin, "registration-pending", true, ct))
		{
			unchanged.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		(await ScalarAsync(h.Db, "SELECT count(*) FROM socalytics.notification_preferences", ct)).ShouldBe(0);
		(await ScalarAsync(h.Db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'identity.notification-preference-changed'", ct)).ShouldBe(0);

		using (var off = await SetAsync(h.Admin, "registration-pending", false, ct))
		{
			off.StatusCode.ShouldBe(HttpStatusCode.OK);
			(await off.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("enabled").GetBoolean().ShouldBeFalse();
		}

		(await ListAsync(h.Admin, ct)).Single(i => i.GetProperty("type").GetString() == "registration-pending")
			.GetProperty("enabled").GetBoolean().ShouldBeFalse();
		(await ScalarAsync(h.Db, "SELECT count(*) FROM socalytics.notification_preferences", ct)).ShouldBe(1);

		using (var on = await SetAsync(h.Admin, "registration-pending", true, ct))
		{
			on.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		(await ListAsync(h.Admin, ct)).Single(i => i.GetProperty("type").GetString() == "registration-pending")
			.GetProperty("enabled").GetBoolean().ShouldBeTrue();
		(await ScalarAsync(h.Db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'identity.notification-preference-changed'", ct)).ShouldBe(2);
	}

	[Fact]
	// US3 scenarios 2 and 3: an opted-out Club Admin receives no pending-request message until the type is on again
	public async Task AnOptedOutClubAdminReceivesNoPendingRequestMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await EmailApiHarness.StartAsync(postgres, mailpit, ct, RegistrySettings);
		var address = EmailApiHarness.NewAddress();
		await VerifyAddressAsync(h.Db, await AccountIdAsync(h.Db, EmailApiHarness.AdminName, ct), address, ct);
		await TestMembers.SeedAsync(h.Db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
		using var registrar = await ApiSession.SignInAsync(h.Factory, "reg-1", TestMembers.DefaultPassword, ct);

		using (var off = await SetAsync(h.Admin, "registration-pending", false, ct))
		{
			off.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		await SubmitPairingAsync(h, registrar, ct);
		(await h.MailCountAsync(address, ct)).ShouldBe(0);

		using (var on = await SetAsync(h.Admin, "registration-pending", true, ct))
		{
			on.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		await SubmitPairingAsync(h, registrar, ct);
		(await h.MailCountAsync(address, ct)).ShouldBe(1);
	}
}
